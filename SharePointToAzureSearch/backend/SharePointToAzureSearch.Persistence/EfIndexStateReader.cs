using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Persistence;

public sealed class EfIndexStateReader(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IIndexStateReader
{
    private static readonly IndexStateSummary EmptySummary = new(0, 0, null, 0, 0, 0, null, null, []);

    /// <summary>The character <see cref="ToLikePattern"/> escapes wildcards with.</summary>
    private const string LikeEscape = "\\";

    public async Task<PagedResult<IndexedFileRow>> ListFilesAsync(IndexedFileQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var driveId = NullIfBlank(query.DriveId);
        var pattern = ToLikePattern(query.Search);

        var files = context.IndexedFiles
            .AsNoTracking()
            .Where(f => driveId == null || f.DriveId == driveId)
            .Where(f => pattern == null
                        || EF.Functions.Like(f.FileName, pattern, LikeEscape)
                        || (f.ParentPath != null && EF.Functions.Like(f.ParentPath, pattern, LikeEscape))
                        || (f.WebUrl != null && EF.Functions.Like(f.WebUrl, pattern, LikeEscape))
                        || (f.MimeType != null && EF.Functions.Like(f.MimeType, pattern, LikeEscape))
                        || EF.Functions.Like(f.ItemId, pattern, LikeEscape));

        var total = await files.LongCountAsync(cancellationToken);

        var rows = await OrderBy(files, query)
            .Skip(Math.Max(0, query.Skip))
            .Take(Math.Clamp(query.Top, 1, 200))
            .ToListAsync(cancellationToken);

        return new PagedResult<IndexedFileRow>(total, [.. rows.Select(ToRow)]);
    }

    public async Task<IndexedFileRow?> GetFileAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.IndexedFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.DriveId == driveId && f.ItemId == itemId, cancellationToken);
        return row is null ? null : ToRow(row);
    }

    public async Task<IReadOnlyList<DeltaStateRow>> ListDeltaStateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DeltaState
            .AsNoTracking()
            .OrderByDescending(d => d.UpdatedAtUtc)
            .Select(d => new DeltaStateRow(d.DriveId, d.DeltaLink, d.ScanId, d.SweptScanId, d.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IndexStateSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var files = context.IndexedFiles.AsNoTracking();

        var totals = await files
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalFiles = g.LongCount(),
                TotalChunks = (long?)g.Sum(f => (long)f.ChunkCount),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Oldest = g.Min(f => (DateTimeOffset?)f.IndexedAtUtc),
                Newest = g.Max(f => (DateTimeOffset?)f.IndexedAtUtc)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (totals is null)
        {
            return EmptySummary;
        }

        var distinctDrives = await files.Select(f => f.DriveId).Distinct().CountAsync(cancellationToken);
        var distinctFingerprints = await files.Select(f => f.IndexFingerprint).Distinct().CountAsync(cancellationToken);

        // A file whose drive has no checkpoint yet counts as outside the current round, the same as one
        // the round has not reached.
        var filesOutsideScan = await files
            .Where(f => !context.DeltaState.Any(d => d.DriveId == f.DriveId && d.ScanId == f.ScanId))
            .LongCountAsync(cancellationToken);

        // Grouped into an anonymous type rather than straight into MimeTypeCount: ordering by a member of
        // a projected record is not something the SQL translator can follow back to the aggregate.
        var mimeTypes = await files
            .GroupBy(f => f.MimeType)
            .Select(g => new
            {
                MimeType = g.Key,
                FileCount = g.LongCount(),
                ChunkCount = g.Sum(f => (long)f.ChunkCount),
                SizeBytes = g.Sum(f => f.SizeBytes)
            })
            .OrderByDescending(x => x.FileCount)
            .Take(12)
            .ToListAsync(cancellationToken);
        var byMimeType = mimeTypes
            .Select(x => new MimeTypeCount(x.MimeType, x.FileCount, x.ChunkCount, x.SizeBytes))
            .ToList();

        return new IndexStateSummary(
            totals.TotalFiles,
            totals.TotalChunks ?? 0,
            totals.TotalSizeBytes,
            distinctDrives,
            filesOutsideScan,
            distinctFingerprints,
            totals.Oldest,
            totals.Newest,
            byMimeType);
    }

    /// <summary>
    /// Applies the requested sort. The client sends a name rather than a column, and an unknown one falls
    /// back to the default, so nothing it sends reaches the query. <c>ItemId</c> breaks ties, which keeps
    /// paging stable when many rows share a sort value.
    /// </summary>
    private static IQueryable<IndexedFileEntity> OrderBy(IQueryable<IndexedFileEntity> files, IndexedFileQuery query)
    {
        var descending = query.Descending;
        var ordered = query.Sort?.ToLowerInvariant() switch
        {
            "name" => Direction(files, f => f.FileName, descending),
            "path" => Direction(files, f => f.ParentPath, descending),
            "mimetype" => Direction(files, f => f.MimeType, descending),
            "size" => Direction(files, f => f.SizeBytes, descending),
            "lastmodifiedutc" => Direction(files, f => f.LastModifiedUtc, descending),
            "chunkcount" => Direction(files, f => f.ChunkCount, descending),
            "embeddingtokencount" => Direction(files, f => f.EmbeddingTokenCount, descending),
            _ => Direction(files, f => f.IndexedAtUtc, descending)
        };
        return ordered.ThenBy(f => f.ItemId);
    }

    private static IOrderedQueryable<IndexedFileEntity> Direction<TKey>(
        IQueryable<IndexedFileEntity> files,
        System.Linq.Expressions.Expression<Func<IndexedFileEntity, TKey>> key,
        bool descending) =>
        descending ? files.OrderByDescending(key) : files.OrderBy(key);

    private static IndexedFileRow ToRow(IndexedFileEntity row) => new(
        row.DriveId,
        row.ItemId,
        row.FileName,
        row.ParentPath,
        row.WebUrl,
        row.MimeType,
        row.SizeBytes,
        row.LastModifiedUtc,
        row.ETag,
        row.CTag,
        row.PermissionsHash,
        row.IndexFingerprint,
        row.ChunkCount,
        row.ScanId,
        row.IndexedAtUtc,
        row.EmbeddingTokenCount);

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
}
