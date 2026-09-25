using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Persistence;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

using SearchOptions = SharePointToAzureSearch.Application.SearchOptions;

namespace SharePointToAzureSearch.Infrastructure;

public sealed class ChatMessageAttachmentFileService(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    BlobServiceClient blobService,
    SearchIndexClient indexClient,
    MarkItDownClient markItDown,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<UploadOptions> uploadOptions,
    IOptions<SearchOptions> searchOptions,
    ILogger<ChatMessageAttachmentFileService> logger)
{
    private readonly UploadOptions _uploads = uploadOptions.Value;
    private readonly SearchOptions _search = searchOptions.Value;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    private BlobContainerClient Container => blobService.GetBlobContainerClient(_uploads.ContainerName);
    private SearchClient Search => indexClient.GetSearchClient(_search.UploadIndexName);

    public async Task<AttachmentFileRecord> CreateAsync(
        string fileName,
        string? contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentException("The uploaded file is empty.", nameof(sizeBytes));
        }
        if (sizeBytes > _uploads.MaxFileBytes)
        {
            throw new UploadTooLargeException(_uploads.MaxFileBytes);
        }

        var id = Guid.NewGuid();
        var safeName = Path.GetFileName(fileName);
        var blobName = $"{id:N}/{safeName}";
        var now = DateTimeOffset.UtcNow;
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Container.GetBlobClient(blobName).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.ChatMessageAttachmentFiles.Add(new ChatMessageAttachmentFileEntity
            {
                Id = id,
                FileName = safeName,
                BlobName = blobName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                Status = UploadIndexStatus.NotStarted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        return await IndexAsync(id, cancellationToken);
    }

    public async Task<AttachmentFileRecord?> ReindexAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.ChatMessageAttachmentFiles.AnyAsync(x => x.Id == id, cancellationToken))
        {
            return null;
        }
        return await IndexAsync(id, cancellationToken);
    }

    public async Task<AttachmentFilePage> ListAsync(string? search, int skip, int top, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ChatMessageAttachmentFiles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.FileName.Contains(term));
        }
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip(skip).Take(top)
            .Select(x => new AttachmentFileRecord(
                x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.ChunkCount, x.EmbeddingTokenCount,
                x.ErrorMessage, x.CreatedAtUtc, x.UpdatedAtUtc, x.IndexedAtUtc,
                x.ChatMessageAttachmentId,
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.MessageId
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.MessageId).FirstOrDefault(),
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.Message!.ConversationId
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.Message!.ConversationId).FirstOrDefault(),
                x.ChatMessageAttachment != null
                    ? x.ChatMessageAttachment.Message!.Conversation!.Title
                    : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => a.Message!.Conversation!.Title).FirstOrDefault(),
                !x.MessageAttachments.Any()))
            .ToListAsync(cancellationToken);
        return new AttachmentFilePage(total, rows);
    }

    public async Task<AttachmentFileDownload?> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var response = await Container.GetBlobClient(row.BlobName).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return new AttachmentFileDownload(response.Value.Content, row.FileName, row.ContentType ?? "application/octet-stream");
    }

    public async Task<string?> ConvertToMarkdownAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        if (row.SizeBytes > _uploads.MaxFileBytes)
        {
            throw new UploadTooLargeException(_uploads.MaxFileBytes);
        }

        var blob = await Container.GetBlobClient(row.BlobName).DownloadContentAsync(cancellationToken);
        var bytes = blob.Value.Content.ToArray();
        if (bytes.Length > _uploads.MaxFileBytes)
        {
            throw new UploadTooLargeException(_uploads.MaxFileBytes);
        }
        return await markItDown.ConvertAsync(row.FileName, bytes, row.ContentType, cancellationToken);
    }

    public async Task<bool> DeleteOrphanAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var row = await context.ChatMessageAttachmentFiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (row.ChatMessageAttachmentId is not null
            || await context.ChatMessageAttachments.AnyAsync(x => x.AttachmentFileId == id, cancellationToken))
        {
            throw new AttachmentFileIsLinkedException();
        }

        await EnsureInfrastructureAsync(cancellationToken);
        await DeleteIndexDocumentsAsync(id, cancellationToken);
        await Container.GetBlobClient(row.BlobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        context.ChatMessageAttachmentFiles.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ValidateReadyAsync(
        IReadOnlyCollection<Guid> uploadIds,
        CancellationToken cancellationToken)
    {
        if (uploadIds.Count == 0)
        {
            return;
        }

        var distinctIds = uploadIds.Distinct().ToArray();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ready = await context.ChatMessageAttachmentFiles.AsNoTracking()
            .Where(x => distinctIds.Contains(x.Id)
                        && x.Status == UploadIndexStatus.Indexed
                        && x.ChatMessageAttachmentId == null
                        && !x.MessageAttachments.Any())
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (ready.Count != distinctIds.Length)
        {
            throw new InvalidOperationException(
                "Every attachment file must be indexed successfully and not already linked to a message.");
        }
    }

    public async Task<IReadOnlyList<ConversationAttachmentReference>> ListConversationAttachmentsAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.ChatMessageAttachments.AsNoTracking()
            .Where(x => x.Message!.ConversationId == conversationId
                        && x.AttachmentFile!.Status == UploadIndexStatus.Indexed)
            .Select(x => new { AttachmentId = x.AttachmentFileId, FileName = x.AttachmentFile!.FileName })
            .Distinct()
            .OrderBy(x => x.FileName)
            .ThenBy(x => x.AttachmentId)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new ConversationAttachmentReference(x.AttachmentId, x.FileName)).ToArray();
    }

    public async Task<IReadOnlyList<AttachmentSearchHit>> SearchConversationAsync(
        Guid conversationId,
        string query,
        int top,
        Guid? attachmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // The conversation ID comes from the server's current chat turn, not from the tool arguments.
        // Only files linked to messages in that conversation may contribute search results.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var linkedAttachments = context.ChatMessageAttachments.AsNoTracking()
            .Where(x => x.Message!.ConversationId == conversationId
                        && x.AttachmentFile!.Status == UploadIndexStatus.Indexed);
        if (attachmentId is { } selectedId)
        {
            linkedAttachments = linkedAttachments.Where(x => x.AttachmentFileId == selectedId);
        }
        var attachmentIds = await linkedAttachments
            .Select(x => x.AttachmentFileId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (attachmentIds.Count == 0)
        {
            return [];
        }

        await EnsureInfrastructureAsync(cancellationToken);
        var count = Math.Clamp(top, 1, 10);
        var vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        var hits = new List<AttachmentSearchHit>();
        foreach (var batch in attachmentIds.Chunk(100))
        {
            var values = string.Join(',', batch.Select(id => id.ToString("D")));
            var options = new Azure.Search.Documents.SearchOptions
            {
                Filter = $"search.in(uploadId, '{values}', ',')",
                Size = count,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = count, Fields = { "contentVector" } } }
                }
            };
            options.Select.Add("uploadId");
            options.Select.Add("name");
            options.Select.Add("chunkNumber");
            options.Select.Add("content");

            var response = await Search.SearchAsync<UploadChunkDocument>(query, options, cancellationToken);
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (Guid.TryParse(result.Document.UploadId, out var id) && batch.Contains(id))
                {
                    hits.Add(new AttachmentSearchHit(
                        id, result.Document.Name, result.Document.ChunkNumber,
                        result.Document.Content, result.Score));
                }
            }
        }

        return hits.OrderByDescending(hit => hit.Score).Take(count).ToArray();
    }

    private async Task<AttachmentFileRecord> IndexAsync(Guid id, CancellationToken cancellationToken)
    {
        ChatMessageAttachmentFileEntity row;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            row = await context.ChatMessageAttachmentFiles.SingleAsync(x => x.Id == id, cancellationToken);
            row.Status = UploadIndexStatus.Indexing;
            row.ErrorMessage = null;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await EnsureInfrastructureAsync(cancellationToken);
            var blob = await Container.GetBlobClient(row.BlobName).DownloadContentAsync(cancellationToken);
            var bytes = blob.Value.Content.ToArray();
            var markdown = await markItDown.ConvertAsync(row.FileName, bytes, row.ContentType, cancellationToken);
            var texts = TextChunker.Split(markdown, _uploads.ChunkSizeCharacters, _uploads.ChunkOverlapCharacters);
            var documents = new List<UploadChunkDocument>(texts.Count);
            long? embeddingTokenCount = 0;
            for (var index = 0; index < texts.Count; index++)
            {
                var generated = await embeddings.GenerateAsync([texts[index]], cancellationToken: cancellationToken);
                var vector = generated[0].Vector;
                var tokens = generated.Usage?.TotalTokenCount ?? generated.Usage?.InputTokenCount;
                embeddingTokenCount = embeddingTokenCount.HasValue && tokens.HasValue
                    ? embeddingTokenCount.Value + tokens.Value
                    : null;
                documents.Add(new UploadChunkDocument
                {
                    Id = SearchChunkKey.For("upload", id.ToString("D"), index),
                    UploadId = id.ToString("D"),
                    Name = row.FileName,
                    MimeType = row.ContentType,
                    ChunkNumber = index,
                    Content = texts[index],
                    ContentVector = vector.ToArray(),
                });
            }

            await DeleteIndexDocumentsAsync(id, cancellationToken);
            if (documents.Count > 0)
            {
                foreach (var batch in documents.Chunk(1000))
                {
                    await Search.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
                }
            }
            await SetOutcomeAsync(id, UploadIndexStatus.Indexed, documents.Count, embeddingTokenCount, null, cancellationToken);
            logger.LogInformation("Indexed attachment {UploadId} ({FileName}) as {ChunkCount} chunks using {EmbeddingTokenCount} embedding tokens.", id, row.FileName, documents.Count, embeddingTokenCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing upload {UploadId} ({FileName}) failed.", id, row.FileName);
            await SetOutcomeAsync(id, UploadIndexStatus.Failed, 0, null, ex.Message, cancellationToken);
        }

        await using var resultContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await ListByIdAsync(resultContext, id, cancellationToken))!;
    }

    private async Task SetOutcomeAsync(Guid id, UploadIndexStatus status, int chunks, long? embeddingTokenCount, string? error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.ChatMessageAttachmentFiles.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ChunkCount, chunks)
            .SetProperty(x => x.EmbeddingTokenCount, embeddingTokenCount)
            .SetProperty(x => x.ErrorMessage, error == null ? null : error.Length <= 4000 ? error : error[..4000])
            .SetProperty(x => x.UpdatedAtUtc, now)
            .SetProperty(x => x.IndexedAtUtc, status == UploadIndexStatus.Indexed ? now : null), cancellationToken);
    }

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var fields = new List<SearchField>
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("uploadId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("name") { IsFilterable = true },
                new SimpleField("mimeType", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("chunkNumber", SearchFieldDataType.Int32) { IsSortable = true },
                new SearchableField("content"),
                new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = _search.VectorDimensions,
                    VectorSearchProfileName = "content-vector-profile"
                }
            };
            var definition = new SearchIndex(_search.UploadIndexName, fields)
            {
                VectorSearch = new VectorSearch
                {
                    Algorithms = { new HnswAlgorithmConfiguration("content-hnsw") },
                    Profiles = { new VectorSearchProfile("content-vector-profile", "content-hnsw") }
                }
            };
            await indexClient.CreateOrUpdateIndexAsync(
                definition,
                allowIndexDowntime: false,
                cancellationToken: cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task DeleteIndexDocumentsAsync(Guid id, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        for (var skip = 0; ; skip += 1000)
        {
            var response = await Search.SearchAsync<UploadChunkDocument>("*", new Azure.Search.Documents.SearchOptions
            {
                Filter = $"uploadId eq '{id:D}'",
                Size = 1000,
                Skip = skip,
                Select = { "id" }
            }, cancellationToken);
            var pageCount = 0;
            await foreach (var result in response.Value.GetResultsAsync())
            {
                ids.Add(result.Document.Id);
                pageCount++;
            }
            if (pageCount < 1000) break;
        }
        foreach (var batch in ids.Chunk(1000))
        {
            await Search.DeleteDocumentsAsync("id", batch, cancellationToken: cancellationToken);
        }
    }

    private static Task<AttachmentFileRecord?> ListByIdAsync(
        SharePointIndexDbContext context,
        Guid id,
        CancellationToken cancellationToken) => context.ChatMessageAttachmentFiles.AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new AttachmentFileRecord(
            x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.ChunkCount, x.EmbeddingTokenCount,
            x.ErrorMessage, x.CreatedAtUtc, x.UpdatedAtUtc, x.IndexedAtUtc,
            x.ChatMessageAttachmentId,
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.MessageId
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.MessageId).FirstOrDefault(),
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.Message!.ConversationId
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => (Guid?)a.Message!.ConversationId).FirstOrDefault(),
            x.ChatMessageAttachment != null
                ? x.ChatMessageAttachment.Message!.Conversation!.Title
                : x.MessageAttachments.OrderBy(a => a.CreatedAtUtc).Select(a => a.Message!.Conversation!.Title).FirstOrDefault(),
            !x.MessageAttachments.Any()))
        .SingleOrDefaultAsync(cancellationToken);
}
