using System.Text.Json.Serialization;

namespace SharePointToAzureSearch.Core;

public sealed record SharePointChangeSignal(
    string DriveId,
    string SubscriptionId,
    string ChangeType,
    DateTimeOffset ReceivedAtUtc);

public sealed class ChangeNotificationEnvelope
{
    [JsonPropertyName("value")]
    public List<ChangeNotification> Value { get; set; } = [];
}

public sealed class ChangeNotification
{
    [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; set; } = "";
    [JsonPropertyName("clientState")] public string? ClientState { get; set; }
    [JsonPropertyName("changeType")] public string ChangeType { get; set; } = "updated";
    [JsonPropertyName("resource")] public string? Resource { get; set; }
}

/// <summary>
/// One item from the drive delta feed. <paramref name="ETag"/> changes when either the content or the
/// metadata of the item changes; <paramref name="CTag"/> changes only when the content does, so the two
/// together separate a content edit from a rename, a move, or a column update.
/// </summary>
public sealed record DriveItemChange(
    string Id,
    string Name,
    string? WebUrl,
    string? MimeType,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    string? CTag,
    bool IsFile,
    bool IsDeleted,
    string? ParentPath);

public sealed record DeltaPage(IReadOnlyList<DriveItemChange> Items, string? NextLink, string? DeltaLink);

public sealed record PermissionSnapshot(
    IReadOnlyList<string> AllowedPrincipals,
    IReadOnlyList<string> Roles,
    bool HasAnonymousAccess);

/// <summary>
/// Where the next delta pass resumes from, and which reconciliation round it belongs to. A round begins
/// whenever the worker has to walk the whole drive — the first pass, or the one after an expired delta
/// token — and every incremental pass that follows keeps the same <see cref="ScanId"/>.
/// <para>
/// A checkpoint exists only once a round has walked the drive end to end, so <see cref="SweptScanId"/>
/// records the round whose orphan sweep has already run and keeps it from running a second time.
/// </para>
/// </summary>
public sealed record DeltaCheckpoint(string DeltaLink, Guid ScanId, Guid? SweptScanId = null);

/// <summary>
/// What was last written to the search index for one file. <see cref="CTag"/> identifies the content that
/// was extracted, <see cref="PermissionsHash"/> the permission snapshot that was stored on its chunks, and
/// <see cref="IndexFingerprint"/> the chunking and embedding settings that produced them, so a later pass
/// can decide whether anything has to be done at all. <see cref="ScanId"/> is the reconciliation round the
/// file was last seen in, which identifies the files a full scan did and did not reach.
/// </summary>
public sealed record FileIndexRecord(
    string DriveId,
    string ItemId,
    string Name,
    string? ParentPath,
    string? WebUrl,
    string? MimeType,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    string? CTag,
    string PermissionsHash,
    string IndexFingerprint,
    int ChunkCount,
    Guid ScanId,
    DateTimeOffset IndexedAtUtc);

/// <summary>
/// The file and permission fields of a chunk, without its content or vector. Merging this onto an
/// already indexed chunk refreshes a rename, a move, or a permission change without extracting the
/// file's text or generating an embedding again.
/// </summary>
public sealed class SearchChunkMetadataDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("path")]
    public string? Path { get; init; }
    [JsonPropertyName("webUrl")]
    public string? WebUrl { get; init; }
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }
    [JsonPropertyName("size")]
    public long? Size { get; init; }
    [JsonPropertyName("lastModifiedUtc")]
    public DateTimeOffset? LastModifiedUtc { get; init; }
    [JsonPropertyName("eTag")]
    public string? ETag { get; init; }
    [JsonPropertyName("allowedPrincipals")]
    public IReadOnlyList<string> AllowedPrincipals { get; init; } = [];
    [JsonPropertyName("permissionRoles")]
    public IReadOnlyList<string> PermissionRoles { get; init; } = [];
    [JsonPropertyName("hasAnonymousAccess")]
    public bool HasAnonymousAccess { get; init; }
}

/// <summary>
/// Search document keys are derived from the drive, item, and chunk number rather than stored, so chunks
/// that are already in the index can be addressed again — to merge metadata onto them, for example —
/// without querying the index for their keys first.
/// </summary>
public static class SearchChunkKey
{
    public static string For(string driveId, string itemId, int chunkNumber)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{driveId}:{itemId}:{chunkNumber}");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed class SearchChunkDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";
    [JsonPropertyName("driveId")]
    public string DriveId { get; init; } = "";
    [JsonPropertyName("itemId")]
    public string ItemId { get; init; } = "";
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("path")]
    public string? Path { get; init; }
    [JsonPropertyName("webUrl")]
    public string? WebUrl { get; init; }
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }
    [JsonPropertyName("size")]
    public long? Size { get; init; }
    [JsonPropertyName("lastModifiedUtc")]
    public DateTimeOffset? LastModifiedUtc { get; init; }
    [JsonPropertyName("eTag")]
    public string? ETag { get; init; }
    [JsonPropertyName("chunkNumber")]
    public int ChunkNumber { get; init; }
    [JsonPropertyName("content")]
    public string Content { get; init; } = "";
    [JsonPropertyName("contentVector")]
    public IReadOnlyList<float> ContentVector { get; init; } = [];
    [JsonPropertyName("allowedPrincipals")]
    public IReadOnlyList<string> AllowedPrincipals { get; init; } = [];
    [JsonPropertyName("permissionRoles")]
    public IReadOnlyList<string> PermissionRoles { get; init; } = [];
    [JsonPropertyName("hasAnonymousAccess")]
    public bool HasAnonymousAccess { get; init; }
}
