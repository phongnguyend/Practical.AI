namespace SharePointToAzureSearch.Domain;

/// <summary>One page of rows, together with the number of rows the query matched in total.</summary>
public sealed record PagedResult<T>(long TotalCount, IReadOnlyList<T> Items);

/// <summary>
/// A row of the indexed-file table as it is shown to an operator. This mirrors
/// <see cref="FileIndexRecord"/> but is produced by a read-only listing query rather than by a
/// single-item lookup, so it carries the same fields for a whole page at a time.
/// </summary>
public sealed record IndexedFileRow(
    string DriveId,
    string ItemId,
    string Name,
    string? ParentPath,
    string? WebUrl,
    string? MimeType,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    string? CTag,
    string PermissionsHash,
    string IndexFingerprint,
    int ChunkCount,
    Guid ScanId,
    DateTimeOffset IndexedAtUtc,
    long? EmbeddingTokenCount);

/// <summary>A row of the delta-checkpoint table, including the timestamp the worker last wrote it.</summary>
public sealed record DeltaStateRow(
    string DriveId,
    string DeltaLink,
    Guid ScanId,
    Guid? SweptScanId,
    DateTimeOffset UpdatedAtUtc);

public sealed record MimeTypeCount(string? MimeType, long FileCount, long ChunkCount, long? SizeBytes);

/// <summary>
/// Aggregates over the indexed-file table. <see cref="FilesOutsideCurrentScan"/> counts the files whose
/// <c>ScanId</c> is not the round recorded in the delta checkpoint — the orphan candidates a completed
/// round would sweep — so a viewer can see reconciliation progress without reading every row.
/// </summary>
public sealed record IndexStateSummary(
    long TotalFiles,
    long TotalChunks,
    long? TotalSizeBytes,
    int DistinctDrives,
    long FilesOutsideCurrentScan,
    int DistinctIndexFingerprints,
    DateTimeOffset? OldestIndexedAtUtc,
    DateTimeOffset? NewestIndexedAtUtc,
    IReadOnlyList<MimeTypeCount> ByMimeType);

/// <summary>
/// How a page of the indexed-file table is selected. <see cref="Sort"/> is matched against a fixed set of
/// column names, so an unknown value falls back to the default rather than reaching the query.
/// </summary>
public sealed record IndexedFileQuery(
    string? Search = null,
    string? DriveId = null,
    string? Sort = null,
    bool Descending = true,
    int Skip = 0,
    int Top = 25);
