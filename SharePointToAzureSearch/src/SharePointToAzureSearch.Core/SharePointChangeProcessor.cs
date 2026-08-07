using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public interface ISharePointChangeProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken);
}

public sealed class SharePointChangeProcessor(
    GraphApiClient graph,
    IDeltaStateStore state,
    ISearchIndexStore search,
    IContentExtractor extractor,
    IEmbeddingClient embeddings,
    IOptions<SharePointOptions> sharePointOptions,
    IOptions<ProcessorOptions> processorOptions,
    ILogger<SharePointChangeProcessor> logger) : ISharePointChangeProcessor
{
    private readonly SharePointOptions _sharePoint = sharePointOptions.Value;
    private readonly ProcessorOptions _processor = processorOptions.Value;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var deltaUrl = await state.GetAsync(_sharePoint.DriveId, cancellationToken);
        try
        {
            await ProcessDeltaAsync(deltaUrl, cancellationToken);
        }
        catch (GraphDeltaTokenExpiredException)
        {
            logger.LogWarning("The Microsoft Graph delta token expired. A full drive reconciliation will be performed.");
            await state.ClearAsync(_sharePoint.DriveId, cancellationToken);
            await ProcessDeltaAsync(null, cancellationToken);
        }
    }

    private async Task ProcessDeltaAsync(string? url, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await graph.GetDeltaPageAsync(url, cancellationToken);
            foreach (var item in page.Items)
            {
                if (item.IsDeleted)
                {
                    await search.DeleteItemAsync(_sharePoint.DriveId, item.Id, cancellationToken);
                    logger.LogInformation("Removed deleted SharePoint item {ItemId} from the search index.", item.Id);
                }
                else if (item.IsFile)
                {
                    await IndexFileAsync(item, cancellationToken);
                }
            }

            if (page.NextLink is not null)
            {
                url = page.NextLink;
                continue;
            }
            if (page.DeltaLink is null) throw new InvalidDataException("Microsoft Graph delta response did not contain a delta link.");
            await state.SetAsync(_sharePoint.DriveId, page.DeltaLink, cancellationToken);
            return;
        }
    }

    private async Task IndexFileAsync(DriveItemChange item, CancellationToken cancellationToken)
    {
        try
        {
            var contentTask = graph.DownloadContentAsync(item.Id, _processor.MaxFileBytes, cancellationToken);
            var permissionsTask = graph.GetPermissionsAsync(item.Id, cancellationToken);
            await Task.WhenAll(contentTask, permissionsTask);
            var text = await extractor.ExtractAsync(item, contentTask.Result, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                text = $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
            var textChunks = TextChunker.Split(text, _processor.ChunkSizeCharacters, _processor.ChunkOverlapCharacters);
            var chunks = new List<SearchChunkDocument>(textChunks.Count);
            for (var index = 0; index < textChunks.Count; index++)
            {
                var vector = await embeddings.CreateAsync(textChunks[index], cancellationToken);
                chunks.Add(new SearchChunkDocument
                {
                    Id = EncodeKey($"{_sharePoint.DriveId}:{item.Id}:{index}"),
                    DriveId = _sharePoint.DriveId,
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
                    ContentVector = vector,
                    AllowedPrincipals = permissionsTask.Result.AllowedPrincipals,
                    PermissionRoles = permissionsTask.Result.Roles,
                    HasAnonymousAccess = permissionsTask.Result.HasAnonymousAccess
                });
            }
            await search.ReplaceItemAsync(_sharePoint.DriveId, item.Id, chunks, cancellationToken);
            logger.LogInformation("Indexed {FileName} ({ItemId}) as {ChunkCount} chunks.", item.Name, item.Id, chunks.Count);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await search.DeleteItemAsync(_sharePoint.DriveId, item.Id, cancellationToken);
            logger.LogInformation("SharePoint item {ItemId} disappeared while processing; removed it from the index.", item.Id);
        }
        catch (FileTooLargeException ex)
        {
            await search.DeleteItemAsync(_sharePoint.DriveId, item.Id, cancellationToken);
            logger.LogWarning(ex, "Removed SharePoint file {FileName} from the index because it exceeds the configured size limit.", item.Name);
        }
    }

    private static string EncodeKey(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
