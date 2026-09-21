using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

const string defaultProjectEndpoint =
    "https://foundry-sd1597.services.ai.azure.com/api/projects/project-sd1597";
const string defaultAgentName = "practical-microsoft-agent-framework";

var projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? defaultProjectEndpoint;
var agentName = Environment.GetEnvironmentVariable("FOUNDRY_HOSTED_AGENT_NAME")
    ?? defaultAgentName;
var sessionId = GetOption(args, "--session")
    ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_SESSION_ID");

var responsesEndpoint =
    $"{projectEndpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/openai/responses?api-version=v1";

var credential = new DefaultAzureCredential();
using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
string? previousResponseId = null;

Console.WriteLine($"Connected to Foundry agent: {agentName}");
PrintSession(sessionId);
Console.WriteLine("Commands: /session, /session <id>, /session auto, /new, /exit");

while (true)
{
    Console.WriteLine();
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (input is null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (input.Equals("/new", StringComparison.OrdinalIgnoreCase))
    {
        previousResponseId = null;
        Console.WriteLine("Started a new conversation in the current sandbox session.");
        continue;
    }

    if (input.Equals("/session", StringComparison.OrdinalIgnoreCase))
    {
        PrintSession(sessionId);
        continue;
    }

    if (input.StartsWith("/session ", StringComparison.OrdinalIgnoreCase))
    {
        var requestedSession = input["/session ".Length..].Trim();
        sessionId = requestedSession.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : requestedSession;
        previousResponseId = null;
        PrintSession(sessionId);
        Console.WriteLine("Conversation history was reset because the sandbox selection changed.");
        continue;
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    try
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]),
            CancellationToken.None);

        using var request = new HttpRequestMessage(HttpMethod.Post, responsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        var body = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["stream"] = false,
        };

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            body["agent_session_id"] = sessionId;
        }

        if (!string.IsNullOrWhiteSpace(previousResponseId))
        {
            body["previous_response_id"] = previousResponseId;
        }

        request.Content = JsonContent.Create(body);

        Console.Write("Agent: ");
        using var response = await httpClient.SendAsync(request);
        var payloadText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}):");
            Console.Error.WriteLine(payloadText);
            continue;
        }

        using var payload = JsonDocument.Parse(payloadText);
        var root = payload.RootElement;

        previousResponseId = GetString(root, "id") ?? previousResponseId;
        sessionId = GetString(root, "agent_session_id")
            ?? GetHeader(response, "x-agent-session-id")
            ?? sessionId;

        Console.WriteLine(GetOutputText(root));
        Console.WriteLine($"[session: {sessionId ?? "not returned"}; response: {previousResponseId ?? "not returned"}]");
    }
    catch (AuthenticationFailedException exception)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Authentication failed: {exception.Message}");
        Console.Error.WriteLine("Run 'az login' and 'azd auth login', then try again.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Request failed: {exception.Message}");
    }
}

static string? GetOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static void PrintSession(string? sessionId) =>
    Console.WriteLine(sessionId is null
        ? "Sandbox session: automatic (the next request creates one)"
        : $"Sandbox session: {sessionId}");

static string? GetString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var property)
    && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;

static string? GetHeader(HttpResponseMessage response, string name) =>
    response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

static string GetOutputText(JsonElement root)
{
    var textParts = new List<string>();

    if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (GetString(part, "type") == "output_text" && GetString(part, "text") is { } text)
                {
                    textParts.Add(text);
                }
            }
        }
    }

    return textParts.Count > 0 ? string.Join(Environment.NewLine, textParts) : "(No text output)";
}
