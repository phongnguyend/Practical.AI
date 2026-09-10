using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

const string FrontendCorsPolicy = "frontend";
const string NotificationUrlError =
    "'notificationUrl' must be an absolute HTTPS URL; Microsoft Graph calls it to validate the subscription before creating it.";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebhookServices(builder.Configuration);
builder.Services.AddSearchQueryServices(builder.Configuration);
builder.Services.AddIndexStateServices(builder.Configuration);

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

public partial class Program;
