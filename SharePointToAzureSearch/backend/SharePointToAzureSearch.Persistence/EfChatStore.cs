using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Persistence;

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
            .Include(m => m.Attachments)
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
            Attachments = [.. message.Attachments.Select(attachment => new ChatMessageAttachmentEntity
            {
                AttachmentFileId = attachment.AttachmentFileId,
                CreatedAtUtc = now,
            })],
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
            .Include(m => m.Attachments)
            .ThenInclude(a => a.AttachmentFile)
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
        IReadOnlyCollection<Guid> attachmentFileIds,
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

        var distinctFileIds = attachmentFileIds.Distinct().ToArray();
        var attachedFiles = distinctFileIds.Length == 0
            ? []
            : await context.ChatMessageAttachmentFiles
                .Where(x => distinctFileIds.Contains(x.Id) && x.ChatMessageAttachmentId == null)
                .ToListAsync(cancellationToken);
        if (attachedFiles.Count != distinctFileIds.Length)
        {
            throw new InvalidOperationException("One or more attachment files do not exist or are already linked to a message.");
        }
        entity.Attachments = [.. attachedFiles.Select(file => new ChatMessageAttachmentEntity
        {
            AttachmentFileId = file.Id,
            CreatedAtUtc = now,
        })];
        await context.SaveChangesAsync(cancellationToken);

        // The link ID is database-generated, so the file can only point back to it after the first save.
        // Keeping this second write in the transaction means a file is never committed as linked unless
        // its ChatMessageAttachment row was successfully created too.
        foreach (var file in attachedFiles)
        {
            var attachmentId = entity.Attachments
                .Single(attachment => attachment.AttachmentFileId == file.Id).Id;
            var claimed = await context.ChatMessageAttachmentFiles
                .Where(candidate => candidate.Id == file.Id && candidate.ChatMessageAttachmentId == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ChatMessageAttachmentId, attachmentId),
                    cancellationToken);
            if (claimed != 1)
            {
                throw new InvalidOperationException(
                    $"Attachment file '{file.FileName}' was linked to another message while this message was being saved.");
            }
        }

        var record = new ChatMessageRecord(
            entity.Id, conversationId, role, content, citations,
            inputTokens, outputTokens, totalTokens, modelId, null,
            [.. attachedFiles.Select(ToAttachment)], now);

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
        [.. row.Attachments.Where(x => x.AttachmentFile is not null).Select(x => ToAttachment(x.AttachmentFile!))],
        row.CreatedAtUtc);

    private static ChatMessageAttachment ToAttachment(ChatMessageAttachmentFileEntity row) =>
        new(row.Id, row.FileName, row.ContentType, row.SizeBytes);

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

