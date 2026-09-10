using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

namespace SharePointToAzureSearch.Background;

public sealed class SubscriptionRenewalBackgroundService(
    SubscriptionManager subscriptions,
    IOptions<SharePointOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SubscriptionRenewalBackgroundService> logger) : BackgroundService
{
    private readonly SharePointOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SubscriptionRenewalEnabled)
        {
            logger.LogInformation("Microsoft Graph webhook subscriptions are disabled; relying on the scheduled synchronization instead.");
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationLifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureSubscriptionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to create or renew the Microsoft Graph webhook subscription. Retrying later.");
            }
            await Task.Delay(TimeSpan.FromHours(_options.RenewalCheckHours), stoppingToken);
        }
    }

    private async Task EnsureSubscriptionAsync(CancellationToken cancellationToken)
    {
        var (action, subscription) = await subscriptions.EnsureAsync(cancellationToken);
        var message = action switch
        {
            SubscriptionAction.Created => "Created Microsoft Graph subscription {SubscriptionId}, expiring {ExpirationUtc}.",
            SubscriptionAction.Renewed => "Renewed Microsoft Graph subscription {SubscriptionId} until {ExpirationUtc}.",
            _ => "Microsoft Graph subscription {SubscriptionId} is active until {ExpirationUtc}."
        };
        logger.LogInformation(message, subscription.Id, subscription.ExpirationUtc);
    }
}
