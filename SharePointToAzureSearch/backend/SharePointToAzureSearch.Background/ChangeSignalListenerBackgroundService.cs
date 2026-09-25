using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

namespace SharePointToAzureSearch.Background;

/// <summary>
/// Prepares the search index, optionally runs a startup synchronization, then runs the SharePoint delta
/// synchronization in response to change signals published by the webhook API. Passes are serialized
/// with <see cref="ScheduledSyncBackgroundService"/> by <see cref="ISharePointChangeProcessor"/> itself.
/// </summary>
public sealed class ChangeSignalListenerBackgroundService(
    ServiceBusClient serviceBus,
    ISharePointChangeProcessor changeProcessor,
    ISearchIndexStore search,
    SharePointClient sharePointClient,
    IOptions<ServiceBusOptions> serviceBusOptions,
    IOptions<ProcessorOptions> processorOptions,
    ILogger<ChangeSignalListenerBackgroundService> logger) : BackgroundService
{
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!processorOptions.Value.ChangeSignalListenerEnabled)
        {
            logger.LogInformation("The SharePoint change signal listener is disabled; relying on the scheduled synchronization instead.");
            return;
        }

        await search.EnsureIndexAsync(stoppingToken);
        if (processorOptions.Value.SyncOnStartup)
        {
            logger.LogInformation("Running SharePoint delta synchronization on startup.");
            await changeProcessor.ProcessAsync(stoppingToken);
        }

        var bus = serviceBusOptions.Value;
        _processor = serviceBus.CreateProcessor(bus.TopicName, bus.SubscriptionName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10),
            PrefetchCount = 0
        });
        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;
        await _processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var signal = args.Message.Body.ToObjectFromJson<SharePointChangeSignal>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var driveId = await sharePointClient.GetDriveIdAsync(args.CancellationToken);
        if (signal is null || !string.Equals(signal.DriveId, driveId, StringComparison.Ordinal))
        {
            logger.LogWarning("Dead-lettering a change signal for an unexpected drive.");
            await args.DeadLetterMessageAsync(args.Message, "InvalidDrive", "The message driveId does not match the configured SharePoint drive.");
            return;
        }

        logger.LogInformation("Processing SharePoint change signal from subscription {SubscriptionId}.", signal.SubscriptionId);
        await changeProcessor.ProcessAsync(args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Service Bus processor error. Entity: {EntityPath}; source: {ErrorSource}.", args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
