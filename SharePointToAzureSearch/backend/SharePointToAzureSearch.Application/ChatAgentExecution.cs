using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Application;

/// <summary>Only persisted identifiers cross the hosting boundary; SQL owns the conversation.</summary>
public sealed record ChatAgentRequest(Guid ConversationId, Guid QuestionId);

public interface IChatAgentExecutor
{
    Task<ChatTurn> RunStreamingAsync(
        ChatAgentRequest request,
        Func<string, CancellationToken, ValueTask> onText,
        Func<string, CancellationToken, ValueTask> onStatus,
        CancellationToken cancellationToken);
}

public sealed record ChatAgentContext(
    ChatConversation Conversation,
    AgentDefinition Agent,
    IReadOnlyList<ChatMessageRecord> History,
    ChatMessageRecord Question);

/// <summary>Loads the same bounded, database-backed context for local and hosted execution.</summary>
public sealed class ChatAgentContextLoader(IChatRepository chats, IAgentRepository agents)
{
    public const int MaxHistoryMessages = 40;

    public async Task<ChatAgentContext> LoadAsync(ChatAgentRequest request, CancellationToken cancellationToken)
    {
        var conversation = await chats.GetConversationAsync(request.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation no longer exists.");
        var agent = conversation.AgentId is { } id && id != Guid.Empty
            ? await agents.GetAsync(id, cancellationToken)
            : await agents.GetByNameAsync(AgentDefaults.Name, cancellationToken);
        if (agent is null)
        {
            throw new InvalidOperationException("The agent assigned to this conversation is unavailable.");
        }

        var messages = await chats.ListMessagesAsync(request.ConversationId, cancellationToken);
        var question = messages.SingleOrDefault(m => m.Id == request.QuestionId && m.Role == ChatMessageRole.User)
            ?? throw new InvalidOperationException("The saved question does not belong to this conversation.");
        // The API saves the question before invoking us. Never replay it twice, or include later turns.
        var history = messages.TakeWhile(m => m.Id != question.Id).TakeLast(MaxHistoryMessages).ToArray();
        return new(conversation, agent, history, question);
    }
}
