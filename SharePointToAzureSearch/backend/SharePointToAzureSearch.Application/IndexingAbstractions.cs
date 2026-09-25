using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Application;

/// <summary>Turns a downloaded SharePoint file into the plain text that gets chunked and embedded.</summary>
public interface IContentExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

public interface ISearchIndexStore
{
    Task EnsureIndexAsync(CancellationToken cancellationToken);
    Task ReplaceItemAsync(string driveId, string itemId, IReadOnlyList<SearchChunkDocument> chunks, CancellationToken cancellationToken);
    Task DeleteItemAsync(string driveId, string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Merges file and permission fields onto chunks that are already indexed, leaving their content and
    /// vectors untouched. Returns false when the index does not hold every chunk, so the caller can fall
    /// back to a full rebuild instead of leaving the file half-updated.
    /// </summary>
    Task<bool> TryMergeItemMetadataAsync(IReadOnlyList<SearchChunkMetadataDocument> chunks, CancellationToken cancellationToken);
}

public interface ISearchQueryStore
{
    Task<SearchQueryResults> SearchAsync(SearchQueryMode mode, SearchQueryRequest request, CancellationToken cancellationToken);
}

public interface ISharePointChangeProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken);
    Task<FileIndexRecord?> ReindexAsync(string driveId, string itemId, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only access to the two tables the worker keeps its state in, for operator-facing views. Nothing
/// here writes, so the views can be pointed at a read replica; they read the same model the worker
/// writes through, and work before its first pass has run.
/// </summary>
public interface IIndexStateReader
{
    Task<PagedResult<IndexedFileRow>> ListFilesAsync(IndexedFileQuery query, CancellationToken cancellationToken);
    Task<IndexedFileRow?> GetFileAsync(string driveId, string itemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeltaStateRow>> ListDeltaStateAsync(CancellationToken cancellationToken);
    Task<IndexStateSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public interface IWebhookSubscriptionStore
{
    Task<IReadOnlyList<WebhookSubscriptionDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition> CreateAsync(
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        string? clientState,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> UpdateAsync(
        Guid id,
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        string? clientState,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task DeleteAsync(string graphSubscriptionId, CancellationToken cancellationToken);
    Task<bool> SetAutoRenewAsync(Guid id, bool enabled, CancellationToken cancellationToken);
}
