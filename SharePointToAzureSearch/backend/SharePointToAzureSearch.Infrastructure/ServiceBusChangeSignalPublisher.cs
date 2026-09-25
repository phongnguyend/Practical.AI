using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Infrastructure;

public sealed class ServiceBusChangeSignalPublisher(ServiceBusClient client, IOptions<ServiceBusOptions> options)
    : IChangeSignalPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.TopicName);

    public async Task PublishAsync(SharePointChangeSignal signal, CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(signal))
        {
            ContentType = "application/json",
            Subject = "sharepoint.drive.changed",
            MessageId = Guid.NewGuid().ToString("N")
        };
        message.ApplicationProperties["driveId"] = signal.DriveId;
        await _sender.SendMessageAsync(message, cancellationToken);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
