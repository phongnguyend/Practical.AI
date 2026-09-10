using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// A SharePoint file as it now sits on local disk. <see cref="AlreadyOnDisk"/> is true when a previous
/// download was reused, so nothing was fetched from Microsoft Graph this time.
/// </summary>
public sealed record DownloadedFile(string LocalPath, string FileName, long SizeBytes, bool AlreadyOnDisk);

/// <summary>
/// The local copies of SharePoint files: one folder per drive item under the configured root, filled by
/// <see cref="DownloadAsync"/> and sent back by <see cref="UploadAsync"/>. It owns where a drive item
/// lives on disk, so both directions agree on the path without either caller working it out.
/// <para>
/// The cache is keyed by item ID alone, so a file whose content changes in SharePoint after it has been
/// downloaded keeps the copy that was taken first. That is what makes repeated requests for the same
/// document free; <see cref="RefreshAsync"/> takes the current one when that matters.
/// </para>
/// </summary>
public sealed class SharePointFileCache(
    SharePointClient sharePointClient,
    IOptions<DownloadOptions> options,
    ILogger<SharePointFileCache> logger)
{
    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly DownloadOptions _options = options.Value;

    /// <summary>The local copy of a drive item, or null when it has not been downloaded.</summary>
    public DownloadedFile? Find(string itemId, string fileName)
    {
        var file = new FileInfo(ResolveLocalPath(itemId, fileName));
        return file.Exists
            ? new DownloadedFile(file.FullName, file.Name, file.Length, AlreadyOnDisk: true)
            : null;
    }

    /// <summary>
    /// The local copy of a drive item, downloading it only if it is not there already.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(string itemId, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("A drive item ID is required.", nameof(itemId));
        }

        if (Find(itemId, fileName) is { } existing)
        {
            logger.LogInformation("Reused the local copy of {FileName} at {LocalPath}.", existing.FileName, existing.LocalPath);
            return existing;
        }

        return await FetchAsync(itemId, fileName, cancellationToken);
    }

    /// <summary>
    /// Downloads a drive item as SharePoint holds it now, replacing any local copy. This is how a file
    /// whose content changed after it was cached — or one edited locally and not uploaded — is brought
    /// back in line with the library, at the cost of whatever the old copy held.
    /// </summary>
    public async Task<DownloadedFile> RefreshAsync(string itemId, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("A drive item ID is required.", nameof(itemId));
        }

        var replaced = Find(itemId, fileName) is not null;
        var file = await FetchAsync(itemId, fileName, cancellationToken);

        logger.LogInformation(
            replaced
                ? "Refreshed {FileName} from SharePoint, replacing the local copy at {LocalPath}."
                : "Refreshed {FileName} from SharePoint; there was no local copy at {LocalPath}.",
            file.FileName,
            file.LocalPath);

        return file;
    }

    /// <summary>
    /// Fetches the item's content and puts it at its local path, over whatever was there.
    /// </summary>
    private async Task<DownloadedFile> FetchAsync(string itemId, string fileName, CancellationToken cancellationToken)
    {
        var localPath = ResolveLocalPath(itemId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        // Streamed to a name beside the target and moved into place, so the file never sits in memory,
        // a cancelled or failed download leaves nothing a later existence check would mistake for a
        // complete copy, and a refresh keeps the copy it is replacing until the new one is whole.
        var stagingPath = $"{localPath}.{Guid.NewGuid():N}.part";
        long size;
        try
        {
            size = await sharePointClient.DownloadToFileAsync(itemId, stagingPath, _options.MaxFileBytes, cancellationToken);
            File.Move(stagingPath, localPath, overwrite: true);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }

        logger.LogInformation(
            "Downloaded {FileName} ({Bytes} bytes) from SharePoint to {LocalPath}.",
            Path.GetFileName(localPath),
            size,
            localPath);

        return new DownloadedFile(localPath, Path.GetFileName(localPath), size, AlreadyOnDisk: false);
    }

    /// <summary>
    /// Sends the local copy of a drive item back, replacing the document in SharePoint with it as a new
    /// version. The local copy stays where it is, so the item's next download reuses it rather than
    /// fetching the version just uploaded.
    /// </summary>
    /// <exception cref="FileNotFoundException">The item has not been downloaded, so there is nothing to send.</exception>
    public async Task<UploadedFileVersion> UploadAsync(string itemId, string fileName, CancellationToken cancellationToken)
    {
        if (Find(itemId, fileName) is not { } local)
        {
            throw new FileNotFoundException($"'{fileName}' has not been downloaded, so there is no local copy to upload.");
        }

        var version = await sharePointClient.UploadFileAsync(itemId, local.LocalPath, _options.MaxFileBytes, cancellationToken);

        logger.LogInformation(
            "Uploaded {LocalPath} ({Bytes} bytes) back to SharePoint as a new version of {FileName}.",
            local.LocalPath,
            local.SizeBytes,
            version.Name);

        return version;
    }

    /// <summary>
    /// Builds the path a drive item is cached at. Both segments are sanitized and the result is checked to
    /// be inside the configured root, because the file name reaches this method from a model's tool call
    /// and a name such as <c>..\..\appsettings.json</c> must not escape the download directory.
    /// </summary>
    private string ResolveLocalPath(string itemId, string fileName)
    {
        var root = _options.ResolvedDirectory;
        var localPath = Path.GetFullPath(Path.Combine(root, Sanitize(itemId), Sanitize(Path.GetFileName(fileName))));

        if (!localPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{fileName}' does not resolve to a path inside the download directory.", nameof(fileName));
        }

        return localPath;
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string((string.IsNullOrWhiteSpace(value) ? "file" : value)
            .Select(c => InvalidNameChars.Contains(c) ? '_' : c)
            .ToArray())
            .Trim('.', ' ');

        return sanitized.Length == 0 ? "file" : sanitized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
