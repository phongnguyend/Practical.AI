using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public sealed class SubscriptionRenewalService(
    GraphApiClient graph,
    IOptions<SharePointOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SubscriptionRenewalService> logger) : BackgroundService
{
    private readonly SharePointOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        var resource = $"drives/{_options.DriveId}/root";
        var subscriptions = await graph.ListSubscriptionsAsync(cancellationToken);
        var existing = subscriptions.FirstOrDefault(x =>
            string.Equals(x.Resource.TrimStart('/'), resource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.NotificationUrl, _options.NotificationUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ClientState, _options.ClientState, StringComparison.Ordinal));
        var expiration = DateTimeOffset.UtcNow.AddDays(_options.SubscriptionLifetimeDays);
        if (existing is null)
        {
            var created = await graph.CreateSubscriptionAsync(expiration, cancellationToken);
            logger.LogInformation("Created Microsoft Graph subscription {SubscriptionId}, expiring {ExpirationUtc}.", created.Id, created.ExpirationUtc);
        }
        else if (existing.ExpirationUtc < DateTimeOffset.UtcNow.AddDays(3))
        {
            await graph.RenewSubscriptionAsync(existing.Id, expiration, cancellationToken);
            logger.LogInformation("Renewed Microsoft Graph subscription {SubscriptionId} until {ExpirationUtc}.", existing.Id, expiration);
        }
        else
        {
            logger.LogInformation("Microsoft Graph subscription {SubscriptionId} is active until {ExpirationUtc}.", existing.Id, existing.ExpirationUtc);
        }
    }
}
