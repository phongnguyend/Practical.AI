using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IOptions<OpenAiOptions> openAiOptions,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    private readonly string _defaultModelId = openAiOptions.Value.ChatDeployment;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("The database schema is up to date.");
        }
        else
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));
            await context.Database.MigrateAsync(cancellationToken);
        }

        await BackfillAgentModelIdsAsync(context, cancellationToken);
        await SeedDefaultAgentAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BackfillAgentModelIdsAsync(
        SharePointIndexDbContext context,
        CancellationToken cancellationToken)
    {
        var updated = await context.AgentDefinitions
            .Where(agent => agent.ModelId == null || agent.ModelId == "")
            .ExecuteUpdateAsync(
                update => update.SetProperty(agent => agent.ModelId, _defaultModelId),
                cancellationToken);
        if (updated > 0)
        {
            logger.LogInformation(
                "Assigned the configured default model to {Count} existing agent(s).",
                updated);
        }
    }

    private async Task SeedDefaultAgentAsync(
        SharePointIndexDbContext context,
        CancellationToken cancellationToken)
    {
        if (await context.AgentDefinitions.AnyAsync(a => a.Name == AgentDefaults.Name, cancellationToken))
        {
            logger.LogInformation("The default agent already exists.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        context.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Name = AgentDefaults.Name,
            ModelId = _defaultModelId,
            Instructions = ChatAgentService.GetDefaultInstructions(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created the default agent.");
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The unique index closes the race when multiple application instances start together.
            logger.LogInformation("The default agent was created by another application instance.");
        }
    }
}
