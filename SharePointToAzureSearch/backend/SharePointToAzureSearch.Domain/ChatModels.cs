using System.Text.Json.Serialization;

namespace SharePointToAzureSearch.Domain;

public sealed record ChatConversation(
    Guid Id,
    string Title,
    string? UserId,
    Guid? AgentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount);

[JsonConverter(typeof(JsonStringEnumConverter<ChatMessageRole>))]
public enum ChatMessageRole
{
    User,
    Assistant
}

/// <summary>
/// One document the assistant retrieved while answering. Kept with the message so a conversation can
/// be reopened and still show what the answer was based on.
/// </summary>
public sealed record ChatCitation(
    string Name,
    string? Path,
    string? WebUrl,
    int ChunkNumber,
    double? Score);

/// <summary>What a reader thought of an answer. Absent until they say.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatFeedback>))]
public enum ChatFeedback
{
    Like,
    Dislike
}

public sealed record ChatMessageRecord(
    Guid Id,
    Guid ConversationId,
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ChatCitation> Citations,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    string? ModelId,
    ChatFeedback? Feedback,
    IReadOnlyList<ChatMessageAttachment> Attachments,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatMessageAttachment(Guid Id, string FileName, string? ContentType, long SizeBytes);

/// <summary>
/// One rated answer, with the question that prompted it and the conversation it came from — the three
/// things needed to judge whether the rating was fair without opening the chat.
/// </summary>
public sealed record FeedbackEntry(
    Guid MessageId,
    Guid ConversationId,
    string ConversationTitle,
    ChatFeedback Feedback,
    string? Question,
    string Answer,
    IReadOnlyList<ChatCitation> Citations,
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    string? ModelId,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A page of rated answers. <see cref="Liked"/> and <see cref="Disliked"/> count everything the search
/// term matches, not just the page or the selected rating, so the totals hold still while the rating
/// filter is toggled.
/// </summary>
public sealed record FeedbackPage(
    long TotalCount,
    long Liked,
    long Disliked,
    IReadOnlyList<FeedbackEntry> Items);

/// <summary>One assistant turn: what it said, what it retrieved, and the model usage it incurred.</summary>
public sealed record ChatTurn(
    string Text,
    IReadOnlyList<ChatCitation> Citations,
    ChatTokenUsage Usage,
    string? ModelId);

/// <summary>Tokens consumed across every model request in an agent turn, including tool round trips.</summary>
public sealed record ChatTokenUsage(long InputTokens, long OutputTokens, long TotalTokens);
