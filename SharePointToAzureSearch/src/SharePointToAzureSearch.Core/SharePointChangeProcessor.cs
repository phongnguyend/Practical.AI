using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public interface ISharePointChangeProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken);
}

public sealed class SharePointChangeProcessor(
    SharePointClient sharePointClient,
    IDeltaStateStore state,
    IFileMetadataStore metadata,
    ISearchIndexStore search,
    IContentExtractor extractor,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<ProcessorOptions> processorOptions,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<SearchOptions> searchOptions,
    ILogger<SharePointChangeProcessor> logger) : ISharePointChangeProcessor
{
    // How many orphaned files the sweep claims at a time, so a large clean-up does not read the whole
    // backlog into memory at once.
    private const int SweepBatchSize = 500;

    private readonly ProcessorOptions _processor = processorOptions.Value;
    private readonly HashSet<string> _allowedExtensions = BuildAllowedExtensions(processorOptions.Value.AllowedFileExtensions);

    // Identifies the pipeline that produced the stored chunks. Changing the chunk geometry or the embedding
    // model makes every tracked file stale, so files are rebuilt rather than reported as unchanged.
    private readonly string _indexFingerprint = string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"chunk={processorOptions.Value.ChunkSizeCharacters}/{processorOptions.Value.ChunkOverlapCharacters};embedding={openAiOptions.Value.EmbeddingDeployment};dimensions={searchOptions.Value.VectorDimensions}");

    // Serializes every trigger (Service Bus signals, the scheduled poll, and the startup sync) so a
    // single delta cursor is never advanced by two passes at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            logger.LogInformation("Another SharePoint delta synchronization is already running; waiting for it to finish.");
            await _gate.WaitAsync(cancellationToken);
        }

        try
        {
            var driveId = await sharePointClient.GetDriveIdAsync(cancellationToken);
            var checkpoint = await state.GetAsync(driveId, cancellationToken);
            var scanId = StartOrContinueScan(checkpoint);
            try
            {
                await ProcessDeltaAsync(driveId, scanId, checkpoint?.DeltaLink, cancellationToken);
            }
            catch (GraphDeltaTokenExpiredException)
            {
                logger.LogWarning("The Microsoft Graph delta token expired. A full drive reconciliation will be performed.");
                await state.ClearAsync(driveId, cancellationToken);
                checkpoint = null;
                scanId = StartOrContinueScan(null);
                await ProcessDeltaAsync(driveId, scanId, null, cancellationToken);
            }

            // Reaching here means the pass wrote a checkpoint, which only happens once a round has walked
            // the drive end to end: every live file now carries this round's scan ID, so a tracked file
            // that does not is one the drive no longer returns. The recorded sweep keeps this to a single
            // run per round — the incremental passes that follow share the round and skip it.
            if (checkpoint?.SweptScanId != scanId)
            {
                await SweepItemsOutsideScanAsync(driveId, scanId, cancellationToken);
                await state.MarkSweptAsync(driveId, scanId, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A missing or empty delta link means this pass walks the whole drive — the first scan, or the one
    /// after an expired token — so it opens a new reconciliation round. A checkpoint that still holds a
    /// delta link continues the round it was written in.
    /// </summary>
    private Guid StartOrContinueScan(DeltaCheckpoint? checkpoint)
    {
        if (checkpoint is { DeltaLink.Length: > 0 })
        {
            return checkpoint.ScanId;
        }

        var scanId = Guid.NewGuid();
        logger.LogInformation("Walking the whole drive as reconciliation round {ScanId}.", scanId);
        return scanId;
    }

    /// <summary>
    /// Removes the files a completed round never returned: deletions that arrived while the worker was
    /// down, or anything else that has left the drive without a delta notification the worker saw.
    /// <para>
    /// The caller decides when this is safe to run. It is only ever correct after a round has walked the
    /// whole drive, because until then a file the pass has not reached yet looks exactly like a file that
    /// is gone. A pass that throws never gets here, so a partial walk cannot delete anything.
    /// </para>
    /// </summary>
    private async Task SweepItemsOutsideScanAsync(string driveId, Guid scanId, CancellationToken cancellationToken)
    {
        var removed = 0;
        while (true)
        {
            var staleItemIds = await metadata.ListItemsOutsideScanAsync(driveId, scanId, SweepBatchSize, cancellationToken);
            if (staleItemIds.Count == 0)
            {
                break;
            }

            foreach (var itemId in staleItemIds)
            {
                // Each removal deletes the tracked row as well, so the next batch cannot return it again.
                await RemoveAsync(driveId, itemId, cancellationToken);
                removed++;
                logger.LogInformation("Removed SharePoint item {ItemId} from the search index; reconciliation round {ScanId} did not return it.", itemId, scanId);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Reconciliation round {ScanId} removed {RemovedCount} files that are no longer in the document library.", scanId, removed);
        }
    }

    private async Task ProcessDeltaAsync(string driveId, Guid scanId, string? url, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await sharePointClient.GetDeltaPageAsync(url, cancellationToken);
            foreach (var item in page.Items)
            {
                if (item.IsDeleted)
                {
                    await RemoveAsync(driveId, item.Id, cancellationToken);
                    logger.LogInformation("Removed deleted SharePoint item {ItemId} from the search index.", item.Id);
                }
                else if (item.IsFile)
                {
                    if (IsIndexable(item.Name))
                    {
                        await IndexFileAsync(driveId, scanId, item, cancellationToken);
                    }
                    else
                    {
                        // Clears anything indexed before the file was renamed or the allow list narrowed.
                        // Costs no more than the delete that always precedes a re-index.
                        await RemoveAsync(driveId, item.Id, cancellationToken);
                        logger.LogDebug("Skipped {FileName}; its extension is not in Processor:AllowedFileExtensions.", item.Name);
                    }
                }
            }

            if (page.NextLink is not null)
            {
                url = page.NextLink;
                continue;
            }
            if (page.DeltaLink is null)
            {
                throw new InvalidDataException("Microsoft Graph delta response did not contain a delta link.");
            }

            await state.SetAsync(driveId, new DeltaCheckpoint(page.DeltaLink, scanId), cancellationToken);
            return;
        }
    }

    /// <summary>
    /// Brings one file's search documents up to date, doing as little as the tracked metadata allows: nothing
    /// at all when the file is unchanged, a metadata merge when only its properties or permissions moved, and
    /// a full extract/embed/replace otherwise.
    /// </summary>
    private async Task IndexFileAsync(string driveId, Guid scanId, DriveItemChange item, CancellationToken cancellationToken)
    {
        try
        {
            var tracked = await metadata.GetAsync(driveId, item.Id, cancellationToken);
            if (HasSameContent(tracked, item) && await TryRefreshWithoutReindexAsync(driveId, scanId, item, tracked!, cancellationToken))
            {
                return;
            }

            await ReindexFileAsync(driveId, scanId, item, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await RemoveAsync(driveId, item.Id, cancellationToken);
            logger.LogInformation("SharePoint item {ItemId} disappeared while processing; removed it from the index.", item.Id);
        }
        catch (FileTooLargeException ex)
        {
            await RemoveAsync(driveId, item.Id, cancellationToken);
            logger.LogWarning(ex, "Removed SharePoint file {FileName} from the index because it exceeds the configured size limit.", item.Name);
        }
    }

    /// <summary>
    /// Handles a file whose content is already indexed. Permissions are read because they change without
    /// changing the item's tags; everything else is compared against the tracked record. Returns false when
    /// the index turns out not to hold the tracked chunks, so the caller rebuilds the file.
    /// </summary>
    private async Task<bool> TryRefreshWithoutReindexAsync(string driveId, Guid scanId, DriveItemChange item, FileIndexRecord tracked, CancellationToken cancellationToken)
    {
        var permissions = await sharePointClient.GetPermissionsAsync(item.Id, cancellationToken);
        var permissionsHash = HashPermissions(permissions);
        if (string.Equals(tracked.PermissionsHash, permissionsHash, StringComparison.Ordinal) && HasSameProperties(tracked, item))
        {
            // Nothing to index, but the round still has to record that it reached the file. Incremental
            // passes carry the round the file was already stamped with, so they write nothing at all.
            if (tracked.ScanId != scanId)
            {
                await metadata.MarkSeenAsync(driveId, item.Id, scanId, cancellationToken);
            }

            logger.LogInformation("Skipped {FileName} ({ItemId}); it is unchanged since it was indexed at {IndexedAtUtc:u}.", item.Name, item.Id, tracked.IndexedAtUtc);
            return true;
        }

        var chunks = Enumerable.Range(0, tracked.ChunkCount).Select(number => new SearchChunkMetadataDocument
        {
            Id = SearchChunkKey.For(driveId, item.Id, number),
            Name = item.Name,
            Path = item.ParentPath,
            WebUrl = item.WebUrl,
            MimeType = item.MimeType,
            Size = item.Size,
            LastModifiedUtc = item.LastModifiedUtc,
            ETag = item.ETag,
            AllowedPrincipals = permissions.AllowedPrincipals,
            PermissionRoles = permissions.Roles,
            HasAnonymousAccess = permissions.HasAnonymousAccess
        }).ToArray();

        if (!await search.TryMergeItemMetadataAsync(chunks, cancellationToken))
        {
            logger.LogWarning("The search index does not hold all {ChunkCount} tracked chunks of {FileName} ({ItemId}); rebuilding it.", tracked.ChunkCount, item.Name, item.Id);
            return false;
        }

        await metadata.SaveAsync(Track(driveId, scanId, item, permissionsHash, tracked.ChunkCount), cancellationToken);
        logger.LogInformation("Updated the metadata and permissions of {FileName} ({ItemId}) across {ChunkCount} chunks; its content was unchanged, so it was not extracted or embedded again.", item.Name, item.Id, tracked.ChunkCount);
        return true;
    }

    private async Task ReindexFileAsync(string driveId, Guid scanId, DriveItemChange item, CancellationToken cancellationToken)
    {
        var contentTask = sharePointClient.DownloadContentAsync(item.Id, _processor.MaxFileBytes, cancellationToken);
        var permissionsTask = sharePointClient.GetPermissionsAsync(item.Id, cancellationToken);
        await Task.WhenAll(contentTask, permissionsTask);
        var text = await extractor.ExtractAsync(item, contentTask.Result, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
        }

        var textChunks = TextChunker.Split(text, _processor.ChunkSizeCharacters, _processor.ChunkOverlapCharacters);
        var chunks = new List<SearchChunkDocument>(textChunks.Count);
        for (var index = 0; index < textChunks.Count; index++)
        {
            var vector = await embeddings.GenerateVectorAsync(textChunks[index], cancellationToken: cancellationToken);
            chunks.Add(new SearchChunkDocument
            {
                Id = SearchChunkKey.For(driveId, item.Id, index),
                DriveId = driveId,
                ItemId = item.Id,
                Name = item.Name,
                Path = item.ParentPath,
                WebUrl = item.WebUrl,
                MimeType = item.MimeType,
                Size = item.Size,
                LastModifiedUtc = item.LastModifiedUtc,
                ETag = item.ETag,
                ChunkNumber = index,
                Content = textChunks[index],
                ContentVector = vector.ToArray(),
                AllowedPrincipals = permissionsTask.Result.AllowedPrincipals,
                PermissionRoles = permissionsTask.Result.Roles,
                HasAnonymousAccess = permissionsTask.Result.HasAnonymousAccess
            });
        }
        await search.ReplaceItemAsync(driveId, item.Id, chunks, cancellationToken);

        // Tracked only after the index write succeeds, so a failed pass reindexes the file on its retry.
        await metadata.SaveAsync(Track(driveId, scanId, item, HashPermissions(permissionsTask.Result), chunks.Count), cancellationToken);
        logger.LogInformation("Indexed {FileName} ({ItemId}) as {ChunkCount} chunks.", item.Name, item.Id, chunks.Count);
    }

    private async Task RemoveAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await search.DeleteItemAsync(driveId, itemId, cancellationToken);
        await metadata.DeleteAsync(driveId, itemId, cancellationToken);
    }

    private FileIndexRecord Track(string driveId, Guid scanId, DriveItemChange item, string permissionsHash, int chunkCount) => new(
        driveId,
        item.Id,
        item.Name,
        item.ParentPath,
        item.WebUrl,
        item.MimeType,
        item.Size,
        item.LastModifiedUtc,
        item.ETag,
        item.CTag,
        permissionsHash,
        _indexFingerprint,
        chunkCount,
        scanId,
        DateTimeOffset.UtcNow);

    /// <summary>
    /// True when the indexed chunks were built from the content the drive item holds now, by the pipeline
    /// that is configured now. The cTag covers the content alone; when Graph omits it, the eTag stands in
    /// and also rules out a metadata change.
    /// </summary>
    private bool HasSameContent(FileIndexRecord? tracked, DriveItemChange item)
    {
        if (tracked is null
            || tracked.ChunkCount <= 0
            || !string.Equals(tracked.IndexFingerprint, _indexFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return item.CTag is { Length: > 0 } cTag
            ? string.Equals(tracked.CTag, cTag, StringComparison.Ordinal)
            : item.ETag is { Length: > 0 } eTag && string.Equals(tracked.ETag, eTag, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when every file property copied onto the chunks still matches, so a rename, a move, or a
    /// property edit is not missed by a content comparison that cannot see it.
    /// </summary>
    private static bool HasSameProperties(FileIndexRecord tracked, DriveItemChange item) =>
        string.Equals(tracked.Name, item.Name, StringComparison.Ordinal)
        && string.Equals(tracked.ParentPath, item.ParentPath, StringComparison.Ordinal)
        && string.Equals(tracked.WebUrl, item.WebUrl, StringComparison.Ordinal)
        && string.Equals(tracked.MimeType, item.MimeType, StringComparison.Ordinal)
        && string.Equals(tracked.ETag, item.ETag, StringComparison.Ordinal)
        && tracked.Size == item.Size
        && IsSameInstant(tracked.LastModifiedUtc, item.LastModifiedUtc);

    private static bool IsSameInstant(DateTimeOffset? tracked, DateTimeOffset? current) =>
        tracked.HasValue == current.HasValue && (!tracked.HasValue || tracked.Value.UtcTicks == current!.Value.UtcTicks);

    /// <summary>
    /// Hashes the effective sharing snapshot, because a permission change alters neither the eTag nor the
    /// cTag of the item and would otherwise look like no change at all.
    /// </summary>
    private static string HashPermissions(PermissionSnapshot permissions)
    {
        var canonical = new StringBuilder()
            .Append(permissions.HasAnonymousAccess ? '1' : '0')
            .Append('\n')
            .AppendJoin('\n', permissions.AllowedPrincipals.Order(StringComparer.Ordinal))
            .Append("\n\n")
            .AppendJoin('\n', permissions.Roles.Order(StringComparer.Ordinal));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private bool IsIndexable(string fileName) => _allowedExtensions.Contains(Path.GetExtension(fileName));

    private static HashSet<string> BuildAllowedExtensions(IList<string> configured)
    {
        return configured
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
