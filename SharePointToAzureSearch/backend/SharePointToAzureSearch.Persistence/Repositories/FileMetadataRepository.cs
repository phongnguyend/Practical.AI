using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Persistence.Repositories;

/// <summary>
/// Tracks what was last indexed for each SharePoint file in SQL Server.
/// </summary>
public sealed class FileMetadataRepository(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IFileMetadataRepository
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
        row.EmbeddingTokenCount = record.EmbeddingTokenCount;
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
        row.IndexedAtUtc,
        row.EmbeddingTokenCount);

    private static string? Truncate(string? value, int length) =>
        value is not null && value.Length > length ? value[..length] : value;
}
