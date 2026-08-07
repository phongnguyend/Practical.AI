using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public interface IChangeSignalPublisher
{
    Task PublishAsync(SharePointChangeSignal signal, CancellationToken cancellationToken);
}

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

public interface IDeltaStateStore
{
    Task<string?> GetAsync(string driveId, CancellationToken cancellationToken);
    Task SetAsync(string driveId, string deltaLink, CancellationToken cancellationToken);
    Task ClearAsync(string driveId, CancellationToken cancellationToken);
}

public sealed class BlobDeltaStateStore(BlobContainerClient container) : IDeltaStateStore
{
    public async Task<string?> GetAsync(string driveId, CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(BlobName(driveId));
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var download = await blob.DownloadContentAsync(cancellationToken);
        return download.Value.Content.ToString();
    }

    public async Task SetAsync(string driveId, string deltaLink, CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient(BlobName(driveId)).UploadAsync(BinaryData.FromString(deltaLink), overwrite: true, cancellationToken);
    }

    public async Task ClearAsync(string driveId, CancellationToken cancellationToken)
    {
        await container.GetBlobClient(BlobName(driveId)).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private static string BlobName(string driveId) => $"delta/{Uri.EscapeDataString(driveId)}.txt";
}
