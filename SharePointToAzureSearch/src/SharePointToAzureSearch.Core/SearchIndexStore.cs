using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

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

public sealed class AzureSearchIndexStore(
    SearchIndexClient indexClient,
    SearchClient searchClient,
    IOptions<SearchOptions> options) : ISearchIndexStore
{
    private readonly SearchOptions _options = options.Value;

    public async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("driveId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("itemId", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("name") { IsFilterable = true },
            new SearchableField("path") { IsFilterable = true },
            new SimpleField("webUrl", SearchFieldDataType.String),
            new SimpleField("mimeType", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("size", SearchFieldDataType.Int64) { IsFilterable = true, IsSortable = true },
            new SimpleField("lastModifiedUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
            new SimpleField("eTag", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunkNumber", SearchFieldDataType.Int32) { IsSortable = true },
            new SearchableField("content"),
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = _options.VectorDimensions,
                VectorSearchProfileName = "content-vector-profile"
            },
            new SimpleField("allowedPrincipals", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
            new SimpleField("permissionRoles", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
            new SimpleField("hasAnonymousAccess", SearchFieldDataType.Boolean) { IsFilterable = true }
        };
        var index = new SearchIndex(_options.IndexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration("content-hnsw") },
                Profiles = { new VectorSearchProfile("content-vector-profile", "content-hnsw") }
            }
        };
        await indexClient.CreateOrUpdateIndexAsync(index, allowIndexDowntime: false, cancellationToken: cancellationToken);
    }

    public async Task ReplaceItemAsync(string driveId, string itemId, IReadOnlyList<SearchChunkDocument> chunks, CancellationToken cancellationToken)
    {
        await DeleteItemAsync(driveId, itemId, cancellationToken);
        if (chunks.Count > 0)
        {
            await searchClient.MergeOrUploadDocumentsAsync(chunks, cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> TryMergeItemMetadataAsync(IReadOnlyList<SearchChunkMetadataDocument> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return false;
        }

        var merged = true;

        // Merge, not merge-or-upload: a chunk that is missing from the index must fail here rather than be
        // created with no content or vector. Azure AI Search takes at most 1000 documents per request.
        foreach (var batch in chunks.Chunk(1000))
        {
            var response = await searchClient.MergeDocumentsAsync(batch, cancellationToken: cancellationToken);
            merged &= response.Value.Results.All(result => result.Succeeded);
        }
        return merged;
    }

    public async Task DeleteItemAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        var filter = $"driveId eq '{Escape(driveId)}' and itemId eq '{Escape(itemId)}'";
        var results = await searchClient.SearchAsync<SearchDocument>("*", new Azure.Search.Documents.SearchOptions
        {
            Filter = filter,
            Select = { "id" }
        }, cancellationToken);
        var ids = new List<string>();
        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document["id"].ToString()!);
        }

        if (ids.Count > 0)
        {
            await searchClient.DeleteDocumentsAsync("id", ids, cancellationToken: cancellationToken);
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
