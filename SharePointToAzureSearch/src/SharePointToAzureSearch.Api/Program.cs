using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebhookServices(builder.Configuration);
builder.Services.AddSearchQueryServices(builder.Configuration);

var app = builder.Build();

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
        return Results.Text(validationToken.ToString(), "text/plain", Encoding.UTF8);

    var envelope = await request.ReadFromJsonAsync<ChangeNotificationEnvelope>(cancellationToken);
    if (envelope is null) return Results.BadRequest();

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

app.Run();

static async Task<IResult> SearchAsync(
    SearchQueryMode mode,
    SearchPayload payload,
    ISearchQueryStore store,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(payload.Query))
        return Results.BadRequest(new { error = "A non-empty 'query' is required." });
    if (payload.Top is < 1 or > 100)
        return Results.BadRequest(new { error = "'top' must be between 1 and 100." });
    if (payload.Skip < 0)
        return Results.BadRequest(new { error = "'skip' must not be negative." });

    var request = new SearchQueryRequest(payload.Query, payload.UserId, payload.Top, payload.Skip);
    var results = await store.SearchAsync(mode, request, cancellationToken);
    return Results.Ok(results);
}

static bool SecureEquals(string? left, string right)
{
    if (left is null) return false;
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

/// <summary>
/// Request body for the search endpoints. When <see cref="UserId"/> is supplied, results are restricted to
/// content that user is allowed to view; omitting it searches the whole index.
/// </summary>
public sealed record SearchPayload(string? Query, string? UserId, int Top = 10, int Skip = 0);

public partial class Program;
