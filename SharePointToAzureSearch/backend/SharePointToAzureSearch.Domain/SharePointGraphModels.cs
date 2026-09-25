namespace SharePointToAzureSearch.Domain;

public sealed record GraphSubscription(string Id, string Resource, string NotificationUrl, DateTimeOffset ExpirationUtc, string? ClientState);

/// <summary>
/// The drive item as SharePoint holds it after an upload. <paramref name="ETag"/> and
/// <paramref name="CTag"/> belong to the version just created, so they differ from the ones the worker
/// last indexed — which is what makes the next delta pass re-index the file.
/// </summary>
public sealed record UploadedFileVersion(
    string ItemId,
    string Name,
    string? WebUrl,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    string? CTag);

/// <summary>
/// A SharePoint file as it now sits on local disk. <see cref="AlreadyOnDisk"/> is true when a previous
/// download was reused, so nothing was fetched from Microsoft Graph this time.
/// </summary>
public sealed record DownloadedFile(string LocalPath, string FileName, long SizeBytes, bool AlreadyOnDisk);

public sealed class GraphDeltaTokenExpiredException : Exception;

public sealed class FileTooLargeException(long actual, long maximum) : Exception($"File is {actual} bytes; the configured limit is {maximum} bytes.");

public sealed class FileNoLongerIndexableException(string fileName)
    : InvalidOperationException($"SharePoint file '{fileName}' is no longer an indexable file.");
