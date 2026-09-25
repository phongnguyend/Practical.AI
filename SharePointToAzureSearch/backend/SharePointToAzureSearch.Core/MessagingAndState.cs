using Azure.Messaging.ServiceBus;
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

/// <summary>
/// Holds the Microsoft Graph delta link that each pass resumes from, and the reconciliation round it
/// belongs to. See <see cref="EfDeltaStateStore"/>.
/// </summary>
public interface IDeltaStateStore
{
    Task<DeltaCheckpoint?> GetAsync(string driveId, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the checkpoint. <see cref="DeltaCheckpoint.SweptScanId"/> is left as it is, so a recorded
    /// sweep survives the passes that follow it.
    /// </summary>
    Task SetAsync(string driveId, DeltaCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the orphan sweep for a round has finished, so no later pass repeats it.
    /// </summary>
    Task MarkSweptAsync(string driveId, Guid scanId, CancellationToken cancellationToken);

    Task ClearAsync(string driveId, CancellationToken cancellationToken);
}

/// <summary>
/// Records what was last indexed for each SharePoint file. The delta feed returns an item whenever
/// anything about it changes — and returns every item after a delta token expires — so without this
/// record every pass would download, extract, embed, and re-upload files that never changed. See
/// <see cref="EfFileMetadataStore"/>.
/// </summary>
public interface IFileMetadataStore
{
    Task<FileIndexRecord?> GetAsync(string driveId, string itemId, CancellationToken cancellationToken);
    Task SaveAsync(FileIndexRecord record, CancellationToken cancellationToken);
    Task DeleteAsync(string driveId, string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a reconciliation round reached a file that needed no work, so the round's
    /// <see cref="FileIndexRecord.ScanId"/> covers every file it saw and not only the ones it rewrote.
    /// </summary>
    Task MarkSeenAsync(string driveId, string itemId, Guid scanId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="limit"/> tracked files that a completed round did not reach. Call it
    /// only once the round has walked the whole drive; until then, files it has simply not got to yet are
    /// indistinguishable from files that are gone.
    /// </summary>
    Task<IReadOnlyList<string>> ListItemsOutsideScanAsync(string driveId, Guid scanId, int limit, CancellationToken cancellationToken);
}
