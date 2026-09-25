using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Persistence.Repositories;

/// <summary>
/// Keeps the Microsoft Graph delta link for each drive in SQL Server, together with the reconciliation
/// round it belongs to. The link is the worker's only checkpoint: it is written after a whole delta page
/// has been indexed, so a pass that fails is repeated from where the last successful one ended.
/// </summary>
public sealed class DeltaStateRepository(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IDeltaStateRepository
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

    public async Task<bool> ResetAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var affected = await context.DeltaState
            .Where(x => x.DriveId == driveId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.DeltaLink, string.Empty)
                .SetProperty(x => x.SweptScanId, (Guid?)null)
                .SetProperty(x => x.UpdatedAtUtc, updatedAtUtc), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var affected = await context.DeltaState
            .Where(x => x.DriveId == driveId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected > 0;
    }
}
