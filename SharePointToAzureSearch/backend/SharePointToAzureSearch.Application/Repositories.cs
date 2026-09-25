using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Application;

// The application's view of its own database. Each interface here is the whole contract for one
// aggregate — conversations, agents, indexed files, delta checkpoints, webhook subscriptions — and the
// persistence layer implements it in SharePointToAzureSearch.Persistence/Repositories. Nothing outside
// that project knows the schema, the provider, or that Entity Framework Core is involved at all.
//
// Abstractions over other people's stores — Azure AI Search, Service Bus — are not repositories and
// stay in IndexingAbstractions.cs.

public interface IChatRepository
{
    Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken);
    Task<ChatConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken);
    Task<ChatConversation> CreateConversationAsync(
        string title,
        string? userId,
        Guid agentId,
        CancellationToken cancellationToken);
    Task<ChatConversation?> BranchConversationAsync(
        Guid conversationId,
        Guid throughMessageId,
        CancellationToken cancellationToken);
    Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken);

    /// <summary>Removes a conversation and every message in it. Returns false when it was already gone.</summary>
    Task<bool> DeleteConversationAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken);
    Task<ChatMessageRecord> AppendMessageAsync(
        Guid conversationId,
        ChatMessageRole role,
        string content,
        IReadOnlyList<ChatCitation> citations,
        ChatTokenUsage? usage,
        string? modelId,
        IReadOnlyCollection<Guid> attachmentFileIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records, or with a null <paramref name="feedback"/> clears, what a reader thought of a message.
    /// Returns false when there is no such message.
    /// </summary>
    Task<bool> SetFeedbackAsync(Guid messageId, ChatFeedback? feedback, CancellationToken cancellationToken);

    /// <summary>
    /// Every answer a reader has rated, newest first. <paramref name="feedback"/> narrows to one
    /// rating and <paramref name="search"/> matches the answer or the conversation title; both are
    /// optional.
    /// </summary>
    Task<FeedbackPage> ListFeedbackAsync(
        ChatFeedback? feedback,
        string? search,
        int skip,
        int top,
        CancellationToken cancellationToken);
}

public interface IAgentRepository
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<AgentDefinition?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken);
    Task<AgentDefinition> CreateAsync(
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken);
    Task<AgentDefinition?> UpdateAsync(
        Guid id,
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken);
}

/// <summary>Which Foundry sandbox a conversation is bound to, so the binding survives an API restart.</summary>
public interface IFoundrySessionRepository
{
    Task<string?> GetAsync(Guid conversationId, string endpoint, CancellationToken cancellationToken);
    Task SaveAsync(Guid conversationId, string endpoint, string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Holds the Microsoft Graph delta link that each pass resumes from, and the reconciliation round it
/// belongs to. The worker advances the checkpoint; an operator resets or removes it.
/// </summary>
public interface IDeltaStateRepository
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

    /// <summary>
    /// Keeps the row visible but clears its cursor. The worker treats an empty link as a new full scan,
    /// so this is how an operator asks for one without losing sight of the drive. Returns false when
    /// there is no such row.
    /// </summary>
    Task<bool> ResetAsync(string driveId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the checkpoint entirely. The worker does this when Microsoft Graph rejects its delta
    /// token and the next pass has to walk the whole drive; an operator can do it to start a drive over.
    /// Returns false when there was nothing to remove, which the worker has no use for.
    /// </summary>
    Task<bool> DeleteAsync(string driveId, CancellationToken cancellationToken);
}

/// <summary>
/// Records what was last indexed for each SharePoint file. The delta feed returns an item whenever
/// anything about it changes — and returns every item after a delta token expires — so without this
/// record every pass would download, extract, embed, and re-upload files that never changed.
/// </summary>
public interface IFileMetadataRepository
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

/// <summary>
/// Read-only access to the two tables the worker keeps its state in, for operator-facing views. Nothing
/// here writes, so the views can be pointed at a read replica; they read the same model the worker
/// writes through, and work before its first pass has run.
/// </summary>
public interface IIndexStateRepository
{
    Task<PagedResult<IndexedFileRow>> ListFilesAsync(IndexedFileQuery query, CancellationToken cancellationToken);
    Task<IndexedFileRow?> GetFileAsync(string driveId, string itemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeltaStateRow>> ListDeltaStateAsync(CancellationToken cancellationToken);
    Task<IndexStateSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

public interface IWebhookSubscriptionRepository
{
    Task<IReadOnlyList<WebhookSubscriptionDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition> CreateAsync(
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        string? clientState,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> UpdateAsync(
        Guid id,
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        string? clientState,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task DeleteAsync(string graphSubscriptionId, CancellationToken cancellationToken);
    Task<bool> SetAutoRenewAsync(Guid id, bool enabled, CancellationToken cancellationToken);
}
