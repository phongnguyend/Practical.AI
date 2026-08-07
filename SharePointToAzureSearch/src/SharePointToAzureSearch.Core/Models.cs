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

public sealed record DriveItemChange(
    string Id,
    string Name,
    string? WebUrl,
    string? MimeType,
    long? Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    bool IsFile,
    bool IsDeleted,
    string? ParentPath);

public sealed record DeltaPage(IReadOnlyList<DriveItemChange> Items, string? NextLink, string? DeltaLink);

public sealed record PermissionSnapshot(
    IReadOnlyList<string> AllowedPrincipals,
    IReadOnlyList<string> Roles,
    bool HasAnonymousAccess);

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
