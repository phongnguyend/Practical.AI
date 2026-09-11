using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

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
    DateTimeOffset CreatedAtUtc);

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

/// <summary>
/// Conversations and messages in the same SQL Server database the worker uses for its own state, so the
/// chat survives a restart of the API.
/// </summary>
public sealed class EfChatStore(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IChatStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The character <see cref="ToLikePattern"/> escapes wildcards with.</summary>
    private const string LikeEscape = "\\";

    public async Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatConversations
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Select(c => new ChatConversation(
                c.Id, c.Title, c.UserId, c.AgentId, c.CreatedAtUtc, c.UpdatedAtUtc, c.Messages.Count,
                c.InputTokenCount, c.OutputTokenCount, c.TotalTokenCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatConversations
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ChatConversation(
                c.Id, c.Title, c.UserId, c.AgentId, c.CreatedAtUtc, c.UpdatedAtUtc, c.Messages.Count,
                c.InputTokenCount, c.OutputTokenCount, c.TotalTokenCount))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ChatConversation> CreateConversationAsync(
        string title,
        string? userId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ChatConversation(
            Guid.NewGuid(), Truncate(title, 200), userId, agentId, now, now, 0, 0, 0, 0);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.ChatConversations.Add(new ChatConversationEntity
        {
            Id = conversation.Id,
            Title = conversation.Title,
            UserId = userId,
            AgentId = agentId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken);

        return conversation;
    }

    public async Task<ChatConversation?> BranchConversationAsync(
        Guid conversationId,
        Guid throughMessageId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await context.ChatConversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        var throughSequence = await context.ChatMessages
            .AsNoTracking()
            .Where(m => m.Id == throughMessageId && m.ConversationId == conversationId)
            .Select(m => (int?)m.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (throughSequence is null)
        {
            return null;
        }

        var sourceMessages = await context.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Sequence <= throughSequence.Value)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var branchId = Guid.NewGuid();
        var inputTokens = sourceMessages.Sum(m => m.InputTokenCount);
        var outputTokens = sourceMessages.Sum(m => m.OutputTokenCount);
        var totalTokens = sourceMessages.Sum(m => m.TotalTokenCount);

        context.ChatConversations.Add(new ChatConversationEntity
        {
            Id = branchId,
            Title = source.Title,
            UserId = source.UserId,
            AgentId = source.AgentId,
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
            TotalTokenCount = totalTokens,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        context.ChatMessages.AddRange(sourceMessages.Select(message => new ChatMessageEntity
        {
            ConversationId = branchId,
            Sequence = message.Sequence,
            Role = message.Role,
            Content = message.Content,
            CitationsJson = message.CitationsJson,
            InputTokenCount = message.InputTokenCount,
            OutputTokenCount = message.OutputTokenCount,
            TotalTokenCount = message.TotalTokenCount,
            ModelId = message.ModelId,
            Feedback = null,
            CreatedAtUtc = message.CreatedAtUtc,
        }));
        await context.SaveChangesAsync(cancellationToken);

        return new ChatConversation(
            branchId,
            source.Title,
            source.UserId,
            source.AgentId,
            now,
            now,
            sourceMessages.Count,
            inputTokens,
            outputTokens,
            totalTokens);
    }

    public async Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        var trimmed = Truncate(title, 200);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.ChatConversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(c => c.SetProperty(p => p.Title, trimmed), cancellationToken);
    }

    public async Task<bool> DeleteConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The messages go with it: the foreign key cascades, so the two tables cannot be left disagreeing.
        return await context.ChatConversations.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToRecord)];
    }

    public async Task<ChatMessageRecord> AppendMessageAsync(
        Guid conversationId,
        ChatMessageRole role,
        string content,
        IReadOnlyList<ChatCitation> citations,
        ChatTokenUsage? usage,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var inputTokens = usage?.InputTokens ?? 0;
        var outputTokens = usage?.OutputTokens ?? 0;
        var totalTokens = usage?.TotalTokens ?? 0;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Reading the last sequence and inserting the next one are two statements, so a transaction keeps
        // the unique (ConversationId, Sequence) index from rejecting two turns appended at once.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var lastSequence = await context.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (int?)m.Sequence, cancellationToken) ?? 0;

        var entity = new ChatMessageEntity
        {
            ConversationId = conversationId,
            Sequence = lastSequence + 1,
            Role = role,
            Content = content,
            CitationsJson = citations.Count == 0 ? null : JsonSerializer.Serialize(citations, Json),
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
            TotalTokenCount = totalTokens,
            ModelId = modelId,
            CreatedAtUtc = now
        };
        context.ChatMessages.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        var record = new ChatMessageRecord(
            entity.Id, conversationId, role, content, citations,
            inputTokens, outputTokens, totalTokens, modelId, null, now);

        // The conversation list is ordered by this, so it moves to the top on every turn.
        await context.ChatConversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(p => p.UpdatedAtUtc, now)
                .SetProperty(p => p.InputTokenCount, p => p.InputTokenCount + inputTokens)
                .SetProperty(p => p.OutputTokenCount, p => p.OutputTokenCount + outputTokens)
                .SetProperty(p => p.TotalTokenCount, p => p.TotalTokenCount + totalTokens),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<bool> SetFeedbackAsync(Guid messageId, ChatFeedback? feedback, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ChatMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(m => m.SetProperty(p => p.Feedback, feedback), cancellationToken) > 0;
    }

    public async Task<FeedbackPage> ListFeedbackAsync(
        ChatFeedback? feedback,
        string? search,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pattern = ToLikePattern(search);
        var rated = context.ChatMessages
            .AsNoTracking()
            .Where(m => m.Feedback != null)
            .Where(m => pattern == null
                        || EF.Functions.Like(m.Content, pattern, LikeEscape)
                        || EF.Functions.Like(m.Conversation!.Title, pattern, LikeEscape));

        // The totals ignore the rating filter, so the tiles stay put while it is toggled.
        var totals = await rated
            .GroupBy(m => m.Feedback)
            .Select(g => new { Feedback = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var liked = totals.FirstOrDefault(t => t.Feedback == ChatFeedback.Like)?.Count ?? 0;
        var disliked = totals.FirstOrDefault(t => t.Feedback == ChatFeedback.Dislike)?.Count ?? 0;

        var page = rated.Where(m => feedback == null || m.Feedback == feedback);
        var total = await page.LongCountAsync(cancellationToken);

        // The question is the last user turn before the answer, which is what makes a rating interpretable.
        var rows = await page
            .OrderByDescending(m => m.CreatedAtUtc)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(top, 1, 100))
            .Select(m => new
            {
                m.Id,
                m.ConversationId,
                ConversationTitle = m.Conversation!.Title,
                m.Feedback,
                Answer = m.Content,
                m.CitationsJson,
                m.InputTokenCount,
                m.OutputTokenCount,
                m.TotalTokenCount,
                m.ModelId,
                m.CreatedAtUtc,
                Question = context.ChatMessages
                    .Where(q => q.ConversationId == m.ConversationId
                                && q.Sequence < m.Sequence
                                && q.Role == ChatMessageRole.User)
                    .OrderByDescending(q => q.Sequence)
                    .Select(q => q.Content)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new FeedbackEntry(
                r.Id,
                r.ConversationId,
                r.ConversationTitle,
                r.Feedback!.Value,
                r.Question,
                r.Answer,
                ReadCitations(r.CitationsJson),
                r.InputTokenCount,
                r.OutputTokenCount,
                r.TotalTokenCount,
                r.ModelId,
                r.CreatedAtUtc))
            .ToList();

        return new FeedbackPage(total, liked, disliked, items);
    }

    private static ChatMessageRecord ToRecord(ChatMessageEntity row) => new(
        row.Id,
        row.ConversationId,
        row.Role,
        row.Content,
        ReadCitations(row.CitationsJson),
        row.InputTokenCount,
        row.OutputTokenCount,
        row.TotalTokenCount,
        row.ModelId,
        row.Feedback,
        row.CreatedAtUtc);

    private static IReadOnlyList<ChatCitation> ReadCitations(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<ChatCitation>>(json, Json) ?? [];

    /// <summary>
    /// Turns a free-text term into a contains pattern with the wildcards escaped, so a term such as
    /// <c>100%</c> matches literally instead of matching everything.
    /// </summary>
    private static string? ToLikePattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var escaped = search.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static string Truncate(string value, int length) =>
        value.Length > length ? value[..length] : value;
}

