using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

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
    DateTimeOffset IndexedAtUtc);

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

/// <summary>
/// Read-only access to the two tables the worker keeps its state in, for operator-facing views. Nothing
/// here writes, and a table that does not exist yet reads as empty rather than failing, so the views work
/// before the worker's first pass has run.
/// </summary>
public interface IIndexStateReader
{
    Task<PagedResult<IndexedFileRow>> ListFilesAsync(IndexedFileQuery query, CancellationToken cancellationToken);
    Task<IndexedFileRow?> GetFileAsync(string driveId, string itemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeltaStateRow>> ListDeltaStateAsync(CancellationToken cancellationToken);
    Task<IndexStateSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public sealed class SqlIndexStateReader(IOptions<SqlServerOptions> options) : IIndexStateReader
{
    private static readonly IndexStateSummary EmptySummary = new(0, 0, null, 0, 0, 0, null, null, []);

    /// <summary>
    /// Sortable columns by the name the client uses. Anything not in here is ignored, which is what keeps
    /// the ORDER BY clause free of client input.
    /// </summary>
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "FileName",
        ["path"] = "ParentPath",
        ["mimeType"] = "MimeType",
        ["size"] = "SizeBytes",
        ["lastModifiedUtc"] = "LastModifiedUtc",
        ["chunkCount"] = "ChunkCount",
        ["indexedAtUtc"] = "IndexedAtUtc"
    };

    private readonly SqlServerOptions _options = options.Value;

    private string FilesTable => QualifiedName(_options.FileMetadataTableName);

    private string DeltaTable => QualifiedName(_options.DeltaStateTableName);

    public async Task<PagedResult<IndexedFileRow>> ListFilesAsync(IndexedFileQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, FilesTable, cancellationToken))
        {
            return new PagedResult<IndexedFileRow>(0, []);
        }

        var sortColumn = query.Sort is not null && SortColumns.TryGetValue(query.Sort, out var column) ? column : "IndexedAtUtc";
        var direction = query.Descending ? "DESC" : "ASC";

        await using var command = CreateCommand(connection, $"""
            SELECT DriveId, ItemId, FileName, ParentPath, WebUrl, MimeType, SizeBytes, LastModifiedUtc,
                   ETag, CTag, PermissionsHash, IndexFingerprint, ChunkCount, ScanId, IndexedAtUtc,
                   COUNT_BIG(*) OVER () AS TotalCount
              FROM {FilesTable}
             WHERE (@driveId IS NULL OR DriveId = @driveId)
               AND (@search IS NULL
                    OR FileName LIKE @search ESCAPE '\'
                    OR ParentPath LIKE @search ESCAPE '\'
                    OR WebUrl LIKE @search ESCAPE '\'
                    OR MimeType LIKE @search ESCAPE '\'
                    OR ItemId LIKE @search ESCAPE '\')
             ORDER BY {sortColumn} {direction}, ItemId ASC
            OFFSET @skip ROWS FETCH NEXT @top ROWS ONLY;
            """);
        command.Parameters.Add("@driveId", SqlDbType.NVarChar, 200).Value = OrNull(NullIfBlank(query.DriveId));
        command.Parameters.Add("@search", SqlDbType.NVarChar, 400).Value = OrNull(ToLikePattern(query.Search));
        command.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, query.Skip);
        command.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(query.Top, 1, 200);

        var items = new List<IndexedFileRow>();
        long total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadFileRow(reader));
            total = reader.GetInt64(15);
        }
        return new PagedResult<IndexedFileRow>(total, items);
    }

    public async Task<IndexedFileRow?> GetFileAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, FilesTable, cancellationToken))
        {
            return null;
        }

        await using var command = CreateCommand(connection, $"""
            SELECT DriveId, ItemId, FileName, ParentPath, WebUrl, MimeType, SizeBytes, LastModifiedUtc,
                   ETag, CTag, PermissionsHash, IndexFingerprint, ChunkCount, ScanId, IndexedAtUtc
              FROM {FilesTable}
             WHERE DriveId = @driveId AND ItemId = @itemId;
            """);
        command.Parameters.Add("@driveId", SqlDbType.NVarChar, 200).Value = driveId;
        command.Parameters.Add("@itemId", SqlDbType.NVarChar, 200).Value = itemId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFileRow(reader) : null;
    }

    public async Task<IReadOnlyList<DeltaStateRow>> ListDeltaStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, DeltaTable, cancellationToken))
        {
            return [];
        }

        await using var command = CreateCommand(connection, $"""
            SELECT DriveId, DeltaLink, ScanId, SweptScanId, UpdatedAtUtc
              FROM {DeltaTable}
             ORDER BY UpdatedAtUtc DESC;
            """);

        var rows = new List<DeltaStateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DeltaStateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }

    public async Task<IndexStateSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, FilesTable, cancellationToken))
        {
            return EmptySummary;
        }

        // Files outside the current round can only be counted when the checkpoint table exists; before the
        // worker's first pass it does not, so that part of the query is left out rather than joined to
        // nothing.
        var hasDeltaTable = await TableExistsAsync(connection, DeltaTable, cancellationToken);
        var outsideScan = hasDeltaTable
            ? $"""
               SELECT COUNT_BIG(*)
                 FROM {FilesTable} f
                 LEFT JOIN {DeltaTable} d ON d.DriveId = f.DriveId
                WHERE d.ScanId IS NULL OR f.ScanId <> d.ScanId;
               """
            : "SELECT CAST(0 AS BIGINT);";

        await using var command = CreateCommand(connection, $"""
            SELECT COUNT_BIG(*),
                   SUM(CAST(ChunkCount AS BIGINT)),
                   SUM(SizeBytes),
                   COUNT(DISTINCT DriveId),
                   COUNT(DISTINCT IndexFingerprint),
                   MIN(IndexedAtUtc),
                   MAX(IndexedAtUtc)
              FROM {FilesTable};

            {outsideScan}

            SELECT TOP (12) MimeType,
                   COUNT_BIG(*) AS FileCount,
                   SUM(CAST(ChunkCount AS BIGINT)) AS ChunkCount,
                   SUM(SizeBytes) AS SizeBytes
              FROM {FilesTable}
             GROUP BY MimeType
             ORDER BY FileCount DESC;
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return EmptySummary;
        }

        var totalFiles = reader.GetInt64(0);
        var totalChunks = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var totalSize = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        var distinctDrives = reader.GetInt32(3);
        var distinctFingerprints = reader.GetInt32(4);
        var oldest = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5);
        var newest = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);

        long filesOutsideScan = 0;
        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            filesOutsideScan = reader.GetInt64(0);
        }

        var byMimeType = new List<MimeTypeCount>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                byMimeType.Add(new MimeTypeCount(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
        }

        return new IndexStateSummary(
            totalFiles, totalChunks, totalSize, distinctDrives, filesOutsideScan,
            distinctFingerprints, oldest, newest, byMimeType);
    }

    private static IndexedFileRow ReadFileRow(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ReadString(reader, 3),
        ReadString(reader, 4),
        ReadString(reader, 5),
        ReadNullable<long>(reader, 6),
        ReadNullable<DateTimeOffset>(reader, 7),
        ReadString(reader, 8),
        ReadString(reader, 9),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetInt32(12),
        reader.GetGuid(13),
        reader.GetFieldValue<DateTimeOffset>(14));

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private SqlCommand CreateCommand(SqlConnection connection, string text) =>
        new(text, connection) { CommandTimeout = _options.CommandTimeoutSeconds };

    private async Task<bool> TableExistsAsync(SqlConnection connection, string qualifiedName, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;");
        command.Parameters.Add("@name", SqlDbType.NVarChar, 400).Value = qualifiedName;
        return (int?)await command.ExecuteScalarAsync(cancellationToken) == 1;
    }

    private string QualifiedName(string tableName) => $"{QuoteName(_options.SchemaName)}.{QuoteName(tableName)}";

    private static string QuoteName(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Turns a free-text term into a contains pattern, escaping the wildcards so a term such as
    /// <c>100%</c> matches literally instead of matching everything.
    /// </summary>
    private static string? ToLikePattern(string? search)
    {
        var term = NullIfBlank(search);
        if (term is null)
        {
            return null;
        }

        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static string? ReadString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T? ReadNullable<T>(SqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static object OrNull(object? value) => value ?? DBNull.Value;
}
