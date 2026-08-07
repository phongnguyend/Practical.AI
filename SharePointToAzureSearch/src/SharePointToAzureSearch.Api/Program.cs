using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebhookServices(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/sharepoint/webhook", async (
    HttpRequest request,
    IChangeSignalPublisher publisher,
    IOptions<SharePointOptions> options,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (request.Query.TryGetValue("validationToken", out var validationToken))
        return Results.Text(validationToken.ToString(), "text/plain", Encoding.UTF8);

    var envelope = await request.ReadFromJsonAsync<ChangeNotificationEnvelope>(cancellationToken);
    if (envelope is null) return Results.BadRequest();

    foreach (var notification in envelope.Value)
    {
        if (!SecureEquals(notification.ClientState, options.Value.ClientState))
        {
            logger.LogWarning("Ignored a SharePoint notification with an invalid clientState.");
            continue;
        }

        await publisher.PublishAsync(new SharePointChangeSignal(
            options.Value.DriveId,
            notification.SubscriptionId,
            notification.ChangeType,
            DateTimeOffset.UtcNow), cancellationToken);
    }
    return Results.Accepted();
});

app.Run();

static bool SecureEquals(string? left, string right)
{
    if (left is null) return false;
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

public partial class Program;
