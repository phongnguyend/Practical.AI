using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

/// <summary>Operator actions for a drive's delta checkpoint.</summary>
public sealed class DeltaStateAdminService(IDbContextFactory<SharePointIndexDbContext> contextFactory)
{
    /// <summary>
    /// Keeps the row visible but clears its cursor. The worker treats an empty link as a new full scan.
    /// </summary>
    public async Task<bool> ResetAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var affected = await context.DeltaState
            .Where(row => row.DriveId == driveId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.DeltaLink, string.Empty)
                .SetProperty(row => row.SweptScanId, (Guid?)null)
                .SetProperty(row => row.UpdatedAtUtc, updatedAtUtc), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var affected = await context.DeltaState
            .Where(row => row.DriveId == driveId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected > 0;
    }
}
