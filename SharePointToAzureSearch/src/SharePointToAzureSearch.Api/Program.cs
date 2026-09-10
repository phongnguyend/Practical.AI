using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

const string FrontendCorsPolicy = "frontend";
const string NotificationUrlError =
    "'notificationUrl' must be an absolute HTTPS URL; Microsoft Graph calls it to validate the subscription before creating it.";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebhookServices(builder.Configuration);
builder.Services.AddSearchQueryServices(builder.Configuration);
builder.Services.AddIndexStateServices(builder.Configuration);
builder.Services.AddChatServices(builder.Configuration);

// The viewer front end is served from its own origin during development. Origins are configured rather
// than wildcarded, because these endpoints are unauthenticated and expose the whole index.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors(FrontendCorsPolicy);

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/sharepoint/webhook", async (
    HttpRequest request,
    IChangeSignalPublisher publisher,
    SharePointClient sharePointClient,
    IOptions<SharePointOptions> options,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (request.Query.TryGetValue("validationToken", out var validationToken))
    {
        return Results.Text(validationToken.ToString(), "text/plain", Encoding.UTF8);
    }

    var envelope = await request.ReadFromJsonAsync<ChangeNotificationEnvelope>(cancellationToken);
    if (envelope is null)
    {
        return Results.BadRequest();
    }

    var driveId = await sharePointClient.GetDriveIdAsync(cancellationToken);

    foreach (var notification in envelope.Value)
    {
        if (!SecureEquals(notification.ClientState, options.Value.ClientState))
        {
            logger.LogWarning("Ignored a SharePoint notification with an invalid clientState.");
            continue;
        }

        await publisher.PublishAsync(new SharePointChangeSignal(
            driveId,
            notification.SubscriptionId,
            notification.ChangeType,
            DateTimeOffset.UtcNow), cancellationToken);
    }
    return Results.Accepted();
});

app.MapPost("/api/search/fulltext", (
    SearchPayload payload,
    ISearchQueryStore store,
    CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.FullText, payload, store, cancellationToken));

app.MapPost("/api/search/vector", (
    SearchPayload payload,
    ISearchQueryStore store,
    CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.Vector, payload, store, cancellationToken));

app.MapPost("/api/search/hybrid", (
    SearchPayload payload,
    ISearchQueryStore store,
    CancellationToken cancellationToken) => SearchAsync(SearchQueryMode.Hybrid, payload, store, cancellationToken));

// Read-only views over the worker's SQL Server state. Like the search endpoints, these are
// unauthenticated and unfiltered, so put authentication in front of them before exposing them.
app.MapGet("/api/state/summary", (
    IIndexStateReader reader,
    CancellationToken cancellationToken) => reader.GetSummaryAsync(cancellationToken));

app.MapGet("/api/state/indexed-files", async (
    IIndexStateReader reader,
    CancellationToken cancellationToken,
    string? search = null,
    string? driveId = null,
    string? sort = null,
    bool desc = true,
    int skip = 0,
    int top = 25) =>
{
    if (top is < 1 or > 200)
    {
        return Results.BadRequest(new { error = "'top' must be between 1 and 200." });
    }

    if (skip < 0)
    {
        return Results.BadRequest(new { error = "'skip' must not be negative." });
    }

    var page = await reader.ListFilesAsync(new IndexedFileQuery(search, driveId, sort, desc, skip, top), cancellationToken);
    return Results.Ok(page);
});

app.MapGet("/api/state/indexed-files/{driveId}/{itemId}", async (
    string driveId,
    string itemId,
    IIndexStateReader reader,
    CancellationToken cancellationToken) =>
{
    var file = await reader.GetFileAsync(driveId, itemId, cancellationToken);
    return file is null ? Results.NotFound() : Results.Ok(file);
});

app.MapGet("/api/state/delta", (
    IIndexStateReader reader,
    CancellationToken cancellationToken) => reader.ListDeltaStateAsync(cancellationToken));

// Microsoft Graph webhook subscriptions. Unlike the endpoints above these change tenant state: removing
// a subscription stops change notifications, leaving the drive to the scheduled synchronization alone.
app.MapGet("/api/subscriptions", (
    SubscriptionManager subscriptions,
    CancellationToken cancellationToken) =>
    CallGraphAsync(() => subscriptions.GetOverviewAsync(cancellationToken)));

app.MapPost("/api/subscriptions", async (
    CreateSubscriptionRequest? body,
    SubscriptionManager subscriptions,
    CancellationToken cancellationToken) =>
{
    var notificationUrl = string.IsNullOrWhiteSpace(body?.NotificationUrl) ? null : body!.NotificationUrl!.Trim();
    if (notificationUrl is not null && !SubscriptionManager.IsValidNotificationUrl(notificationUrl))
    {
        return Results.BadRequest(new { error = NotificationUrlError });
    }

    return await CallGraphAsync(() => subscriptions.CreateAsync(body?.Days, notificationUrl, cancellationToken));
});

app.MapPost("/api/subscriptions/{id}/renew", (
    string id,
    SubscriptionLifetime? body,
    SubscriptionManager subscriptions,
    CancellationToken cancellationToken) =>
    CallGraphAsync(() => subscriptions.RenewAsync(id, body?.Days, cancellationToken)));

app.MapPut("/api/subscriptions/{id}", async (
    string id,
    CreateSubscriptionRequest? body,
    SubscriptionManager subscriptions,
    CancellationToken cancellationToken) =>
{
    var notificationUrl = string.IsNullOrWhiteSpace(body?.NotificationUrl) ? null : body!.NotificationUrl!.Trim();
    if (notificationUrl is not null && !SubscriptionManager.IsValidNotificationUrl(notificationUrl))
    {
        return Results.BadRequest(new { error = NotificationUrlError });
    }

    return await CallGraphAsync(() => subscriptions.UpdateAsync(id, body?.Days, notificationUrl, cancellationToken));
});

app.MapDelete("/api/subscriptions/{id}", (
    string id,
    SubscriptionManager subscriptions,
    CancellationToken cancellationToken) =>
    CallGraphAsync(async () =>
    {
        await subscriptions.DeleteAsync(id, cancellationToken);
        return new { deleted = id };
    }));

// The chat assistant. Conversations live in SQL Server; each turn replays the stored history to an
// agent that can search the index, and both the question and the answer are appended.
app.MapGet("/api/chat/conversations", (
    IChatStore store,
    CancellationToken cancellationToken) => store.ListConversationsAsync(cancellationToken));

app.MapPost("/api/chat/conversations", async (
    NewConversation? body,
    IChatStore store,
    CancellationToken cancellationToken) =>
{
    var title = string.IsNullOrWhiteSpace(body?.Title) ? "New chat" : body!.Title!.Trim();
    var userId = string.IsNullOrWhiteSpace(body?.UserId) ? null : body!.UserId!.Trim();
    return Results.Ok(await store.CreateConversationAsync(title, userId, cancellationToken));
});

app.MapDelete("/api/chat/conversations/{id:guid}", async (
    Guid id,
    IChatStore store,
    CancellationToken cancellationToken) =>
    await store.DeleteConversationAsync(id, cancellationToken)
        ? Results.Ok(new { deleted = id })
        : Results.NotFound());

app.MapGet("/api/chat/conversations/{id:guid}/messages", async (
    Guid id,
    IChatStore store,
    CancellationToken cancellationToken) =>
{
    var conversation = await store.GetConversationAsync(id, cancellationToken);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    var messages = await store.ListMessagesAsync(id, cancellationToken);
    return Results.Ok(new { conversation, messages });
});

app.MapPost("/api/chat/conversations/{id:guid}/messages", async (
    Guid id,
    ChatTurnRequest body,
    IChatStore store,
    ChatAgentService agent,
    ILogger<Program> logger,
    HttpResponse response,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(body?.Content))
    {
        return Results.BadRequest(new { error = "A non-empty 'content' is required." });
    }

    var conversation = await store.GetConversationAsync(id, cancellationToken);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    var content = body.Content.Trim();
    var history = await store.ListMessagesAsync(id, cancellationToken);

    // The question is stored before the model runs, so a failed or cancelled turn still leaves the
    // conversation showing what was asked.
    var question = await store.AppendMessageAsync(id, ChatMessageRole.User, content, [], cancellationToken);

    // A conversation created from the sidebar has no title until its first question supplies one.
    var renamed = conversation.Title;
    if (history.Count == 0 && conversation.Title == "New chat")
    {
        renamed = content.Length <= 60 ? content : content[..60].TrimEnd() + "…";
        await store.RenameConversationAsync(id, renamed, cancellationToken);
    }

    response.ContentType = "application/x-ndjson; charset=utf-8";
    response.Headers.CacheControl = "no-cache, no-transform";
    response.Headers.Append("X-Accel-Buffering", "no");

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    using var streamWriteLock = new SemaphoreSlim(1, 1);
    async ValueTask WriteEventAsync(ChatStreamEvent item, CancellationToken token)
    {
        // Tools may run concurrently. Keep each JSON object and its newline together on the wire.
        await streamWriteLock.WaitAsync(token);
        try
        {
            await JsonSerializer.SerializeAsync(response.Body, item, jsonOptions, token);
            await response.WriteAsync("\n", token);
            await response.Body.FlushAsync(token);
        }
        finally
        {
            streamWriteLock.Release();
        }
    }

    await WriteEventAsync(new ChatStreamEvent("started", Question: question, Title: renamed), cancellationToken);

    ChatTurn turn;
    try
    {
        turn = await agent.RunStreamingAsync(
            history,
            content,
            conversation.UserId,
            (text, token) => WriteEventAsync(new ChatStreamEvent("delta", Text: text), token),
            (status, token) => WriteEventAsync(new ChatStreamEvent("status", Message: status), token),
            cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "The chat agent failed while answering in conversation {ConversationId}.", id);
        await WriteEventAsync(
            new ChatStreamEvent("error", Message: $"The assistant could not answer: {ex.Message}"),
            cancellationToken);
        return Results.Empty;
    }

    var answer = await store.AppendMessageAsync(id, ChatMessageRole.Assistant, turn.Text, turn.Citations, cancellationToken);
    await WriteEventAsync(new ChatStreamEvent("completed", Answer: answer, Title: renamed), cancellationToken);
    return Results.Empty;
});

app.MapGet("/api/chat/feedback", async (
    IChatStore store,
    CancellationToken cancellationToken,
    ChatFeedback? feedback = null,
    string? search = null,
    int skip = 0,
    int top = 20) =>
{
    if (top is < 1 or > 100)
    {
        return Results.BadRequest(new { error = "'top' must be between 1 and 100." });
    }

    return Results.Ok(await store.ListFeedbackAsync(feedback, search, Math.Max(0, skip), top, cancellationToken));
});

app.MapPost("/api/chat/messages/{id:guid}/feedback", async (
    Guid id,
    MessageFeedback body,
    IChatStore store,
    CancellationToken cancellationToken) =>
    await store.SetFeedbackAsync(id, body?.Feedback, cancellationToken)
        ? Results.Ok(new { id, feedback = body?.Feedback })
        : Results.NotFound());

app.Run();

/// <summary>
/// Runs a subscription operation and maps its failures to a status the caller can act on: a rule this
/// API enforces becomes a 4xx, and a rejection from Microsoft Graph becomes a 502 carrying Graph's own
/// message rather than an opaque 500.
/// </summary>
static async Task<IResult> CallGraphAsync<T>(Func<Task<T>> call)
{
    try
    {
        return Results.Ok(await call());
    }
    catch (DuplicateNotificationUrlException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ProtectedSubscriptionException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(
            new { error = $"Microsoft Graph rejected the request: {ex.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
    }
}

static async Task<IResult> SearchAsync(
    SearchQueryMode mode,
    SearchPayload payload,
    ISearchQueryStore store,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(payload.Query))
    {
        return Results.BadRequest(new { error = "A non-empty 'query' is required." });
    }

    if (payload.Top is < 1 or > 100)
    {
        return Results.BadRequest(new { error = "'top' must be between 1 and 100." });
    }

    if (payload.Skip < 0)
    {
        return Results.BadRequest(new { error = "'skip' must not be negative." });
    }

    var request = new SearchQueryRequest(payload.Query, payload.UserId, payload.Top, payload.Skip);
    var results = await store.SearchAsync(mode, request, cancellationToken);
    return Results.Ok(results);
}

static bool SecureEquals(string? left, string right)
{
    if (left is null)
    {
        return false;
    }

    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

/// <summary>
/// Request body for the search endpoints. When <see cref="UserId"/> is supplied, results are restricted to
/// content that user is allowed to view; omitting it searches the whole index.
/// </summary>
public sealed record SearchPayload(string? Query, string? UserId, int Top = 10, int Skip = 0);

/// <summary>
/// How long a renewed subscription should last. Omit <see cref="Days"/> to use
/// <c>SharePoint:SubscriptionLifetimeDays</c>; the value is clamped to what Microsoft Graph allows.
/// </summary>
public sealed record SubscriptionLifetime(int? Days);

/// <summary>
/// A new subscription. <see cref="NotificationUrl"/> overrides <c>SharePoint:NotificationUrl</c> for
/// this subscription only and must be an absolute HTTPS URL that Microsoft Graph can reach; omit it to
/// use the configured value. A URL other than the configured one produces a subscription the renewal
/// service does not treat as its own.
/// </summary>
public sealed record CreateSubscriptionRequest(int? Days, string? NotificationUrl);

/// <summary>
/// A new conversation. <see cref="UserId"/> is optional and, when given, restricts every search the
/// assistant runs in that conversation to what the user is allowed to see.
/// </summary>
public sealed record NewConversation(string? Title, string? UserId);

public sealed record ChatTurnRequest(string? Content);

/// <summary>One newline-delimited event sent while a chat turn is running.</summary>
public sealed record ChatStreamEvent(
    string Type,
    string? Text = null,
    string? Message = null,
    ChatMessageRecord? Question = null,
    ChatMessageRecord? Answer = null,
    string? Title = null);

/// <summary>A reaction to one answer. A null <see cref="Feedback"/> clears an earlier one.</summary>
public sealed record MessageFeedback(ChatFeedback? Feedback);

public partial class Program;
