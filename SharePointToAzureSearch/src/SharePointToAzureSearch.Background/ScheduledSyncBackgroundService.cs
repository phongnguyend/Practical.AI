using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

namespace SharePointToAzureSearch.Background;

/// <summary>
/// Runs the SharePoint delta synchronization on a fixed interval so changes are still picked up when a
/// Graph notification is never delivered. Passes are serialized with the Service Bus worker by
/// <see cref="ISharePointChangeProcessor"/> itself.
/// </summary>
public sealed class ScheduledSyncBackgroundService(
    ISharePointChangeProcessor changeProcessor,
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
