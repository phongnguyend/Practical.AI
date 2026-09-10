using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SharePointToAzureSearch.Core.Data;

/// <summary>
/// Brings the database up to the current migration as the application starts, so a fresh deployment
/// works without a separate schema step. Registered only when <c>SqlServer:AutoMigrate</c> is true;
/// where the login has no DDL rights, turn it off and apply migrations from the pipeline instead.
/// <para>
/// The API and the worker both do this and may start together. Applying a migration takes a SQL Server
/// application lock, so whichever gets there second waits and then finds nothing left to do.
/// </para>
/// </summary>
public sealed class DatabaseMigrationHostedService(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("The database schema is up to date.");
            return;
        }

        logger.LogInformation("Applying {Count} database migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));
        await context.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
