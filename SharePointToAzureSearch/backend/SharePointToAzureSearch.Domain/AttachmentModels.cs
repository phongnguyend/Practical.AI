using System.Text.Json.Serialization;

namespace SharePointToAzureSearch.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<UploadIndexStatus>))]
public enum UploadIndexStatus
{
    NotStarted,
    Indexing,
    Indexed,
    Failed
}

public sealed record AttachmentFileRecord(
    Guid Id,
    string FileName,
    string? ContentType,
    long SizeBytes,
    UploadIndexStatus Status,
    int ChunkCount,
    long? EmbeddingTokenCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? IndexedAtUtc,
    Guid? ChatMessageAttachmentId,
    Guid? MessageId,
    Guid? ConversationId,
    string? ConversationTitle,
    bool IsOrphan);

public sealed record AttachmentFilePage(long TotalCount, IReadOnlyList<AttachmentFileRecord> Items);

public sealed record AttachmentFileDownload(Stream Content, string FileName, string ContentType);

public sealed record AttachmentSearchHit(Guid AttachmentId, string FileName, int ChunkNumber, string Content, double? Score);

public sealed record ConversationAttachmentReference(Guid AttachmentId, string FileName);

public sealed class UploadTooLargeException(long maximumBytes)
    : InvalidOperationException($"The file exceeds the {maximumBytes:N0}-byte upload limit.");

public sealed class AttachmentFileIsLinkedException()
    : InvalidOperationException("Only orphan attachment files can be deleted.");

/// <summary>One indexed chunk of a conversation attachment, as the upload index stores it.</summary>
public sealed class UploadChunkDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("uploadId")] public string UploadId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("chunkNumber")] public int ChunkNumber { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("contentVector")] public IReadOnlyList<float> ContentVector { get; init; } = [];
}
