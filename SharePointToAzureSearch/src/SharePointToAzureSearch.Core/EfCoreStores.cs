using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// Keeps the Microsoft Graph delta link for each drive in SQL Server, together with the reconciliation
/// round it belongs to. The link is the worker's only checkpoint: it is written after a whole delta page
/// has been indexed, so a pass that fails is repeated from where the last successful one ended.
/// </summary>
public sealed class EfDeltaStateStore(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IDeltaStateStore
{
    public async Task<DeltaCheckpoint?> GetAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DeltaState
            .AsNoTracking()
            .Where(x => x.DriveId == driveId)
            .Select(x => new DeltaCheckpoint(x.DeltaLink, x.ScanId, x.SweptScanId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string driveId, DeltaCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await context.DeltaState.FindAsync([driveId], cancellationToken);
        if (row is null)
        {
            context.DeltaState.Add(new DeltaStateEntity
            {
                DriveId = driveId,
                DeltaLink = checkpoint.DeltaLink,
                ScanId = checkpoint.ScanId,
                SweptScanId = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            // SweptScanId is deliberately left alone, so a recorded sweep survives the passes after it.
            row.DeltaLink = checkpoint.DeltaLink;
            row.ScanId = checkpoint.ScanId;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSweptAsync(string driveId, Guid scanId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DeltaState
            .Where(x => x.DriveId == driveId && x.ScanId == scanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.SweptScanId, scanId), cancellationToken);
    }

    public async Task ClearAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.DeltaState.Where(x => x.DriveId == driveId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Tracks what was last indexed for each SharePoint file in SQL Server.
/// </summary>
public sealed class EfFileMetadataStore(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IFileMetadataStore
{
    public async Task<FileIndexRecord?> GetAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.IndexedFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.DriveId == driveId && x.ItemId == itemId, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task SaveAsync(FileIndexRecord record, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A single delta pass at a time owns a file, so reading the row and writing it back is enough.
        var row = await context.IndexedFiles.FindAsync([record.DriveId, record.ItemId], cancellationToken);
        if (row is null)
        {
            row = new IndexedFileEntity { DriveId = record.DriveId, ItemId = record.ItemId };
            context.IndexedFiles.Add(row);
        }

        // Only descriptive columns are truncated, so an over-long SharePoint value cannot fail the write;
        // the key and change-detection columns are never shortened.
        row.FileName = Truncate(record.Name, 400)!;
        row.ParentPath = Truncate(record.ParentPath, 1000);
        row.WebUrl = Truncate(record.WebUrl, 2000);
        row.MimeType = Truncate(record.MimeType, 200);
        row.SizeBytes = record.Size;
        row.LastModifiedUtc = record.LastModifiedUtc;
        row.ETag = Truncate(record.ETag, 200);
        row.CTag = Truncate(record.CTag, 200);
        row.PermissionsHash = record.PermissionsHash;
        row.IndexFingerprint = Truncate(record.IndexFingerprint, 200)!;
        row.ChunkCount = record.ChunkCount;
        row.ScanId = record.ScanId;
        row.IndexedAtUtc = record.IndexedAtUtc;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSeenAsync(string driveId, string itemId, Guid scanId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.IndexedFiles
            .Where(x => x.DriveId == driveId && x.ItemId == itemId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ScanId, scanId), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListItemsOutsideScanAsync(string driveId, Guid scanId, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.IndexedFiles
            .AsNoTracking()
            .Where(x => x.DriveId == driveId && x.ScanId != scanId)
            .Select(x => x.ItemId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.IndexedFiles
            .Where(x => x.DriveId == driveId && x.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal static FileIndexRecord ToRecord(IndexedFileEntity row) => new(
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
        row.IndexedAtUtc);

    private static string? Truncate(string? value, int length) =>
        value is not null && value.Length > length ? value[..length] : value;
}
