namespace SharePointToAzureSearch.Core.Data;

public sealed class AgentDefinitionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// One row of the delta-checkpoint table: where the next delta pass for a drive resumes from. Maps
/// <see cref="DeltaCheckpoint"/> plus the timestamp the worker last wrote it.
/// </summary>
public sealed class DeltaStateEntity
{
    public string DriveId { get; set; } = "";
    public string DeltaLink { get; set; } = "";
    public Guid ScanId { get; set; }
    public Guid? SweptScanId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// One row of the indexed-file table: what was last written to the search index for a SharePoint file.
/// Maps <see cref="FileIndexRecord"/>.
/// </summary>
public sealed class IndexedFileEntity
{
    public string DriveId { get; set; } = "";
    public string ItemId { get; set; } = "";

    /// <summary>Kept as <c>FileName</c> in the database, because <c>Name</c> reads poorly in ad-hoc queries.</summary>
    public string FileName { get; set; } = "";

    public string? ParentPath { get; set; }
    public string? WebUrl { get; set; }
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public string? ETag { get; set; }
    public string? CTag { get; set; }
    public string PermissionsHash { get; set; } = "";
    public string IndexFingerprint { get; set; } = "";
    public int ChunkCount { get; set; }
    public Guid ScanId { get; set; }
    public DateTimeOffset IndexedAtUtc { get; set; }
}

public sealed class ChatConversationEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? UserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long InputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long TotalTokenCount { get; set; }

    public ICollection<ChatMessageEntity> Messages { get; set; } = [];
}

public sealed class ChatMessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Orders the turns within a conversation; timestamps alone would tie when a user message and its
    /// answer are written in the same instant.
    /// </summary>
    public int Sequence { get; set; }

    public ChatMessageRole Role { get; set; }
    public string Content { get; set; } = "";

    /// <summary>
    /// The retrieved documents behind an answer, serialized as JSON, or null when there were none. Kept
    /// as text rather than a mapped collection: citations are only ever read back whole, with the message.
    /// </summary>
    public string? CitationsJson { get; set; }

    /// <summary>Model usage for this response. User messages and historical rows have zeroes.</summary>
    public long InputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long TotalTokenCount { get; set; }

    public ChatFeedback? Feedback { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public ChatConversationEntity? Conversation { get; set; }
}
