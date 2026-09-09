using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

namespace SharePointToAzureSearch.Background;

/// <summary>
/// Prepares the search index, optionally runs a startup synchronization, then runs the SharePoint delta
/// synchronization on a fixed interval so changes are still picked up when a Graph notification is never
/// delivered. Passes are serialized with the Service Bus worker by
/// <see cref="ISharePointChangeProcessor"/> itself, so the startup pass is safe to request from both.
/// </summary>
public sealed class ScheduledSyncBackgroundService(
    ISharePointChangeProcessor changeProcessor,
    ISearchIndexStore search,
    IOptions<ProcessorOptions> processorOptions,
    ILogger<ScheduledSyncBackgroundService> logger) : BackgroundService
{
    private readonly ProcessorOptions _processor = processorOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_processor.ScheduledSyncEnabled)
        {
            logger.LogInformation("Scheduled SharePoint delta synchronization is disabled.");
            return;
        }

        await search.EnsureIndexAsync(stoppingToken);
        if (_processor.SyncOnStartup)
        {
            logger.LogInformation("Running SharePoint delta synchronization on startup.");
            try
            {
                await changeProcessor.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Startup SharePoint delta synchronization failed. Retrying at the next interval.");
            }
        }

        var interval = TimeSpan.FromMinutes(_processor.ScheduledSyncMinutes);
        logger.LogInformation("Scheduled SharePoint delta synchronization runs every {Interval}.", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await changeProcessor.ProcessAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduled SharePoint delta synchronization failed. Retrying at the next interval.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
