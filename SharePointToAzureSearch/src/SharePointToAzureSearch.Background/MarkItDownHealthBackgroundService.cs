using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

namespace SharePointToAzureSearch.Background;

/// <summary>
/// Probes the MarkItDown service as the worker starts and every <see cref="MarkItDownOptions.HealthCheckMinutes"/>
/// afterwards, so an unreachable or misconfigured service is visible in the logs rather than surfacing as the
/// first failed DOCX, PPTX, or XLSX conversion. The probe only reports; indexing is not gated on it, because a
/// conversion failure already leaves its Service Bus message unsettled for retry.
/// </summary>
public sealed class MarkItDownHealthBackgroundService(
    MarkItDownClient markItDown,
    IOptions<MarkItDownOptions> options,
    ILogger<MarkItDownHealthBackgroundService> logger) : BackgroundService
{
    private readonly MarkItDownOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HealthCheckEnabled)
        {
            logger.LogInformation("The MarkItDown health check is disabled.");
            return;
        }

        // Only transitions are logged after the first probe, so a healthy service stays quiet.
        bool? wasHealthy = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.HealthCheckMinutes));
        try
        {
            do
            {
                wasHealthy = await ProbeAsync(wasHealthy, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<bool> ProbeAsync(bool? wasHealthy, CancellationToken stoppingToken)
    {
        try
        {
            await markItDown.CheckHealthAsync(stoppingToken);
            if (wasHealthy != true)
            {
                logger.LogInformation("MarkItDown at {Endpoint} is healthy.", _options.Endpoint);
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (wasHealthy != false)
            {
                logger.LogWarning(ex, "MarkItDown at {Endpoint} is not healthy; DOCX, PPTX, and XLSX conversions will fail until it recovers.", _options.Endpoint);
            }

            return false;
        }
    }
}
