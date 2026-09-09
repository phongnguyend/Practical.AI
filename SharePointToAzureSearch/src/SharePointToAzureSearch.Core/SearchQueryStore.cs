using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using AzureSearchOptions = Azure.Search.Documents.SearchOptions;

namespace SharePointToAzureSearch.Core;

public enum SearchQueryMode
{
    FullText,
    Vector,
    Hybrid
}

public sealed record SearchQueryRequest(string Query, string? UserId, int Top, int Skip);

public sealed record SearchQueryResults(long? TotalCount, IReadOnlyList<SearchQueryHit> Items);

public sealed record SearchQueryHit(
    string Id,
    string DriveId,
    string ItemId,
    string Name,
    string? Path,
    string? WebUrl,
    string? MimeType,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    int ChunkNumber,
    string Content,
    double? Score);

public interface ISearchQueryStore
{
    Task<SearchQueryResults> SearchAsync(SearchQueryMode mode, SearchQueryRequest request, CancellationToken cancellationToken);
}

public sealed class AzureSearchQueryStore(
    SearchClient searchClient,
    IEmbeddingClient embeddings,
    GraphApiClient graph,
    ILogger<AzureSearchQueryStore> logger) : ISearchQueryStore
{
    private static readonly string[] ProjectedFields =
    [
        "id", "driveId", "itemId", "name", "path", "webUrl",
        "mimeType", "size", "lastModifiedUtc", "chunkNumber", "content"
    ];

    public async Task<SearchQueryResults> SearchAsync(SearchQueryMode mode, SearchQueryRequest request, CancellationToken cancellationToken)
    {
        var options = new AzureSearchOptions
        {
            Filter = await BuildPermissionFilterAsync(request.UserId, cancellationToken),
            Size = request.Top,
            Skip = request.Skip,
            IncludeTotalCount = true
        };
        foreach (var field in ProjectedFields) options.Select.Add(field);

        if (mode is SearchQueryMode.Vector or SearchQueryMode.Hybrid)
        {
            var vector = await embeddings.CreateAsync(request.Query, cancellationToken);
            options.VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(vector.ToArray())
                    {
                        // Retrieve enough neighbours to still fill the requested page after skipping.
                        KNearestNeighborsCount = request.Top + request.Skip,
                        Fields = { "contentVector" }
                    }
                }
            };
        }

        // Pure vector search must not send query text, otherwise the request becomes a hybrid one.
        var searchText = mode is SearchQueryMode.Vector ? null : request.Query;
        var response = await searchClient.SearchAsync<SearchChunkDocument>(searchText, options, cancellationToken);

        var items = new List<SearchQueryHit>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var document = result.Document;
            items.Add(new SearchQueryHit(
                document.Id,
                document.DriveId,
                document.ItemId,
                document.Name,
                document.Path,
                document.WebUrl,
                document.MimeType,
                document.Size,
                document.LastModifiedUtc,
                document.ChunkNumber,
                document.Content,
                result.Score));
        }

        logger.LogInformation("{Mode} search returned {Count} chunks for user {UserId}.", mode, items.Count, request.UserId ?? "(unfiltered)");
        return new SearchQueryResults(response.Value.TotalCount, items);
    }

    /// <summary>
    /// Restricts results to chunks the user can view. Without a user ID no restriction is applied, so the
    /// caller is responsible for only omitting it on trusted, non-user-facing calls.
    /// </summary>
    private async Task<string?> BuildPermissionFilterAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var principals = await graph.GetUserPrincipalsAsync(userId, cancellationToken);
        if (principals.Count == 0) return "hasAnonymousAccess eq true";

        var values = string.Join(',', principals.Select(Escape));
        return $"hasAnonymousAccess eq true or allowedPrincipals/any(p: search.in(p, '{values}', ','))";
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
