namespace SharePointToAzureSearch.Domain;

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
