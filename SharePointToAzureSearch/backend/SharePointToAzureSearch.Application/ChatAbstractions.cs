using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Application;

public interface IChatStore
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

public interface IAgentStore
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

public interface IFoundrySessionStore
{
    Task<string?> GetAsync(Guid conversationId, string endpoint, CancellationToken cancellationToken);
    Task SaveAsync(Guid conversationId, string endpoint, string sessionId, CancellationToken cancellationToken);
}
