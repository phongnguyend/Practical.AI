using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public sealed record ChatConversation(
    Guid Id,
    string Title,
    string? UserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount);

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
    Task<ChatConversation> CreateConversationAsync(string title, string? userId, CancellationToken cancellationToken);
    Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken);

    /// <summary>Removes a conversation and every message in it. Returns false when it was already gone.</summary>
    Task<bool> DeleteConversationAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken);
    Task<ChatMessageRecord> AppendMessageAsync(
        Guid conversationId,
        ChatMessageRole role,
        string content,
        IReadOnlyList<ChatCitation> citations,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records, or with a null <paramref name="feedback"/> clears, what a reader thought of a message.
    /// Returns false when there is no such message.
    /// </summary>
    Task<bool> SetFeedbackAsync(Guid messageId, ChatFeedback? feedback, CancellationToken cancellationToken);

    /// <summary>
    /// Every answer a reader has rated, newest first. <paramref name="feedback"/> narrows to one
    /// rating and <paramref name="search"/> matches the answer, the question, or the conversation
    /// title; both are optional.
    /// </summary>
    Task<FeedbackPage> ListFeedbackAsync(
        ChatFeedback? feedback,
        string? search,
        int skip,
        int top,
        CancellationToken cancellationToken);
}

/// <summary>
/// Conversations and messages in the same SQL Server database the worker uses for its own state. Two
/// tables, created on first use like the others, so the chat survives a restart of the API.
/// </summary>
public sealed class SqlChatStore : IChatStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqlTable _conversations;
    private readonly SqlTable _messages;

    public SqlChatStore(IOptions<SqlServerOptions> options, ILogger<SqlChatStore> logger)
    {
        var value = options.Value;
        _conversations = new SqlTable(value, logger, value.ChatConversationTableName,
            """
                        Id UNIQUEIDENTIFIER NOT NULL,
                        Title NVARCHAR(200) NOT NULL,
                        UserId NVARCHAR(200) NULL,
                        CreatedAtUtc DATETIMEOFFSET(7) NOT NULL,
                        UpdatedAtUtc DATETIMEOFFSET(7) NOT NULL
            """,
            "Id");

        // Sequence orders the turns within a conversation; timestamps alone would tie when a user
        // message and its answer are written in the same instant.
        _messages = new SqlTable(value, logger, value.ChatMessageTableName,
            """
                        Id UNIQUEIDENTIFIER NOT NULL,
                        ConversationId UNIQUEIDENTIFIER NOT NULL,
                        Sequence INT NOT NULL,
                        Role NVARCHAR(20) NOT NULL,
                        Content NVARCHAR(MAX) NOT NULL,
                        CitationsJson NVARCHAR(MAX) NULL,
                        CreatedAtUtc DATETIMEOFFSET(7) NOT NULL
            """,
            "Id",
            [("Feedback", "NVARCHAR(10) NULL")]);
    }

    public async Task<IReadOnlyList<ChatConversation>> ListConversationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _conversations.OpenAsync(cancellationToken);
        await EnsureMessagesTableAsync(connection, cancellationToken);

        await using var command = _conversations.CreateCommand(connection, $"""
            SELECT c.Id, c.Title, c.UserId, c.CreatedAtUtc, c.UpdatedAtUtc,
                   (SELECT COUNT(*) FROM {_messages.Name} m WHERE m.ConversationId = c.Id) AS MessageCount
              FROM {_conversations.Name} c
             ORDER BY c.UpdatedAtUtc DESC;
            """);

        var rows = new List<ChatConversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadConversation(reader));
        }
        return rows;
    }

    public async Task<ChatConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _conversations.OpenAsync(cancellationToken);
        await EnsureMessagesTableAsync(connection, cancellationToken);

        await using var command = _conversations.CreateCommand(connection, $"""
            SELECT c.Id, c.Title, c.UserId, c.CreatedAtUtc, c.UpdatedAtUtc,
                   (SELECT COUNT(*) FROM {_messages.Name} m WHERE m.ConversationId = c.Id) AS MessageCount
              FROM {_conversations.Name} c
             WHERE c.Id = @id;
            """);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    public async Task<ChatConversation> CreateConversationAsync(string title, string? userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ChatConversation(Guid.NewGuid(), title, userId, now, now, 0);

        await using var connection = await _conversations.OpenAsync(cancellationToken);
        await using var command = _conversations.CreateCommand(connection, $"""
            INSERT INTO {_conversations.Name} (Id, Title, UserId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@id, @title, @userId, @createdAtUtc, @updatedAtUtc);
            """);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = conversation.Id;
        command.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = Truncate(title, 200);
        command.Parameters.Add("@userId", SqlDbType.NVarChar, 200).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@createdAtUtc", SqlDbType.DateTimeOffset).Value = now;
        command.Parameters.Add("@updatedAtUtc", SqlDbType.DateTimeOffset).Value = now;
        await command.ExecuteNonQueryAsync(cancellationToken);

        return conversation;
    }

    public async Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken)
    {
        await using var connection = await _conversations.OpenAsync(cancellationToken);
        await using var command = _conversations.CreateCommand(connection,
            $"UPDATE {_conversations.Name} SET Title = @title WHERE Id = @id;");
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = Truncate(title, 200);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _conversations.OpenAsync(cancellationToken);
        await EnsureMessagesTableAsync(connection, cancellationToken);

        // The messages go first, so a failure between the two statements leaves orphaned messages
        // rather than a conversation that cannot be opened.
        await using var command = _conversations.CreateCommand(connection, $"""
            DELETE FROM {_messages.Name} WHERE ConversationId = @id;
            DELETE FROM {_conversations.Name} WHERE Id = @id;
            SELECT @@ROWCOUNT;
            """);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        return (int?)await command.ExecuteScalarAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await _messages.OpenAsync(cancellationToken);
        await using var command = _messages.CreateCommand(connection, $"""
            SELECT Id, ConversationId, Role, Content, CitationsJson, Feedback, CreatedAtUtc
              FROM {_messages.Name}
             WHERE ConversationId = @conversationId
             ORDER BY Sequence ASC;
            """);
        command.Parameters.Add("@conversationId", SqlDbType.UniqueIdentifier).Value = conversationId;

        var rows = new List<ChatMessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChatMessageRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                Enum.Parse<ChatMessageRole>(reader.GetString(2), ignoreCase: true),
                reader.GetString(3),
                reader.IsDBNull(4) ? [] : JsonSerializer.Deserialize<List<ChatCitation>>(reader.GetString(4), Json) ?? [],
                reader.IsDBNull(5) ? null : Enum.Parse<ChatFeedback>(reader.GetString(5), ignoreCase: true),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return rows;
    }

    public async Task<ChatMessageRecord> AppendMessageAsync(
        Guid conversationId,
        ChatMessageRole role,
        string content,
        IReadOnlyList<ChatCitation> citations,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ChatMessageRecord(Guid.NewGuid(), conversationId, role, content, citations, null, now);

        await using var connection = await _messages.OpenAsync(cancellationToken);
        await using var command = _messages.CreateCommand(connection, $"""
            INSERT INTO {_messages.Name} (Id, ConversationId, Sequence, Role, Content, CitationsJson, CreatedAtUtc)
            SELECT @id, @conversationId,
                   ISNULL((SELECT MAX(Sequence) FROM {_messages.Name} WHERE ConversationId = @conversationId), 0) + 1,
                   @role, @content, @citationsJson, @createdAtUtc;

            UPDATE {_conversations.Name} SET UpdatedAtUtc = @createdAtUtc WHERE Id = @conversationId;
            """);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = record.Id;
        command.Parameters.Add("@conversationId", SqlDbType.UniqueIdentifier).Value = conversationId;
        command.Parameters.Add("@role", SqlDbType.NVarChar, 20).Value = role.ToString();
        command.Parameters.Add("@content", SqlDbType.NVarChar, -1).Value = content;
        command.Parameters.Add("@citationsJson", SqlDbType.NVarChar, -1).Value =
            citations.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(citations, Json);
        command.Parameters.Add("@createdAtUtc", SqlDbType.DateTimeOffset).Value = now;
        await command.ExecuteNonQueryAsync(cancellationToken);

        return record;
    }

    public async Task<bool> SetFeedbackAsync(Guid messageId, ChatFeedback? feedback, CancellationToken cancellationToken)
    {
        await using var connection = await _messages.OpenAsync(cancellationToken);
        await using var command = _messages.CreateCommand(connection,
            $"UPDATE {_messages.Name} SET Feedback = @feedback WHERE Id = @id;");
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = messageId;
        command.Parameters.Add("@feedback", SqlDbType.NVarChar, 10).Value =
            feedback is null ? DBNull.Value : feedback.Value.ToString();
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<FeedbackPage> ListFeedbackAsync(
        ChatFeedback? feedback,
        string? search,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        await using var connection = await _messages.OpenAsync(cancellationToken);
        await _conversations.EnsureAsync(connection, cancellationToken);

        // The question is the last user turn before the answer, which is what makes a rating
        // interpretable; the totals ignore the rating filter so the tiles stay put while it changes.
        await using var command = _messages.CreateCommand(connection, $"""
            WITH rated AS (
                SELECT m.Id, m.ConversationId, m.Sequence, m.Feedback, m.Content, m.CitationsJson, m.CreatedAtUtc,
                       c.Title
                  FROM {_messages.Name} m
                  JOIN {_conversations.Name} c ON c.Id = m.ConversationId
                 WHERE m.Feedback IS NOT NULL
                   AND (@search IS NULL
                        OR m.Content LIKE @search ESCAPE '\'
                        OR c.Title LIKE @search ESCAPE '\')
            )
            SELECT
                SUM(CASE WHEN Feedback = 'Like' THEN 1 ELSE 0 END),
                SUM(CASE WHEN Feedback = 'Dislike' THEN 1 ELSE 0 END)
              FROM rated;

            WITH rated AS (
                SELECT m.Id, m.ConversationId, m.Sequence, m.Feedback, m.Content, m.CitationsJson, m.CreatedAtUtc,
                       c.Title
                  FROM {_messages.Name} m
                  JOIN {_conversations.Name} c ON c.Id = m.ConversationId
                 WHERE m.Feedback IS NOT NULL
                   AND (@search IS NULL
                        OR m.Content LIKE @search ESCAPE '\'
                        OR c.Title LIKE @search ESCAPE '\')
                   AND (@feedback IS NULL OR m.Feedback = @feedback)
            )
            SELECT r.Id, r.ConversationId, r.Title, r.Feedback, r.Content, r.CitationsJson, r.CreatedAtUtc,
                   (SELECT TOP 1 q.Content
                      FROM {_messages.Name} q
                     WHERE q.ConversationId = r.ConversationId
                       AND q.Sequence < r.Sequence
                       AND q.Role = 'User'
                     ORDER BY q.Sequence DESC) AS Question,
                   COUNT_BIG(*) OVER () AS TotalCount
              FROM rated r
             ORDER BY r.CreatedAtUtc DESC
            OFFSET @skip ROWS FETCH NEXT @top ROWS ONLY;
            """);
        command.Parameters.Add("@feedback", SqlDbType.NVarChar, 10).Value =
            feedback is null ? DBNull.Value : feedback.Value.ToString();
        command.Parameters.Add("@search", SqlDbType.NVarChar, 400).Value = (object?)ToLikePattern(search) ?? DBNull.Value;
        command.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, skip);
        command.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(top, 1, 100);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        long liked = 0;
        long disliked = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            liked = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            disliked = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }

        var items = new List<FeedbackEntry>();
        long total = 0;
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new FeedbackEntry(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    Enum.Parse<ChatFeedback>(reader.GetString(3), ignoreCase: true),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? [] : JsonSerializer.Deserialize<List<ChatCitation>>(reader.GetString(5), Json) ?? [],
                    reader.GetFieldValue<DateTimeOffset>(6)));
                total = reader.GetInt64(8);
            }
        }

        return new FeedbackPage(total, liked, disliked, items);
    }

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

    /// <summary>
    /// The message table is created through its own <see cref="SqlTable"/>, which only runs on its
    /// first use; a query that joins the two has to make sure both exist first.
    /// </summary>
    private Task EnsureMessagesTableAsync(SqlConnection connection, CancellationToken cancellationToken) =>
        _messages.EnsureAsync(connection, cancellationToken);

    private static ChatConversation ReadConversation(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt32(5));

    private static string Truncate(string value, int length) =>
        value.Length > length ? value[..length] : value;
}
