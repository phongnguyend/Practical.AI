using AgentGovernance.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OpenAI.Chat;
using OpenAI.Responses;
using Practical.MicrosoftAgentFramework;
using Practical.MicrosoftAgentFramework.Shared;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

IList<McpClientTool> tools = await GetMcpToolsAsync();

var options = GetOpenAIOptions(configuration);
ChatClient client = options.CreateChatClient();
var searchFunctions = new SearchFunctions(options);
var memoryFunctions = new MemoryFunctions(Path.Combine(AppContext.BaseDirectory, "user_memory.json"));

var skillsProvider = new AgentSkillsProvider(
    Path.Combine(AppContext.BaseDirectory, "skills"),
    RunScriptAsync
    );

var agent = client.AsAIAgent(new ChatClientAgentOptions
{
    Name = "SkillsAgent",
    ChatOptions = new()
    {
        Instructions = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "instructions.txt")),
        Tools = [
            AIFunctionFactory.Create(GetCurrentDateTime),
            AIFunctionFactory.Create(GetComputerName),
            AIFunctionFactory.Create(searchFunctions.SearchInternalDataAsync),
            AIFunctionFactory.Create(memoryFunctions.RememberAsync),
            AIFunctionFactory.Create(memoryFunctions.RecallAsync),
            AIFunctionFactory.Create(memoryFunctions.ListMemoriesAsync),
            AIFunctionFactory.Create(memoryFunctions.ForgetAsync),
            .. tools.Cast<AITool>()
        ],
    },
    AIContextProviders = [skillsProvider],
    ChatHistoryProvider = new InMemoryChatHistoryProvider()
})
    .AsBuilder()
    .UseToolApproval(new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule],
    })
    .Build();

var session = await agent.CreateSessionAsync();

while (true)
{
    Console.Write("You: ");

    string? userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    var userMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, userInput);

    List<ToolApprovalRequestContent> approvalRequests = await StreamAgentResponseAsync(agent, userMessage, session);

    while (approvalRequests.Count > 0)
    {
        List<Microsoft.Extensions.AI.ChatMessage> userInputResponses = approvalRequests
            .ConvertAll(functionApprovalRequest =>
            {
                var toolCall = (FunctionCallContent)functionApprovalRequest.ToolCall;
                Console.WriteLine($"Approval required for: {toolCall.Name}. Reply Y to approve:");
                bool approved = Console.ReadLine()?.Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
                return new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, [functionApprovalRequest.CreateResponse(approved)]);
            });

        approvalRequests = await StreamAgentResponseAsync(agent, userInputResponses, session);
    }
}

static async Task<List<ToolApprovalRequestContent>> StreamAgentResponseAsync(
    AIAgent agent,
    object input,
    AgentSession session)
{
    Console.Write("Agent: ");

    var approvalRequests = new List<ToolApprovalRequestContent>();

    var stream = input switch
    {
        Microsoft.Extensions.AI.ChatMessage message => agent.RunStreamingAsync(message, session),
        List<Microsoft.Extensions.AI.ChatMessage> messages => agent.RunStreamingAsync(messages, session),
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    await foreach (var update in stream)
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            Console.Write(update.Text);
        }

        approvalRequests.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
    }

    Console.WriteLine();

    return approvalRequests;
}

[Description("Get the current datetime")]
static DateTimeOffset GetCurrentDateTime() => DateTimeOffset.Now;

[Description("Get the current computer name")]
static string GetComputerName() => Environment.MachineName;

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("AzureOpenAI").Bind(options);
    return options;
}

static async Task<object?> RunScriptAsync(AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments, IServiceProvider? serviceProvider, CancellationToken cancellationToken)
{
    var psi = new ProcessStartInfo("powershell")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(script.FullPath);

    if (arguments != null && arguments.Value.ValueKind == JsonValueKind.Array)
    {
        foreach (var element in arguments.Value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Null)
            {
                psi.ArgumentList.Add(element.ValueKind == JsonValueKind.String
                    ? element.GetString()!
                    : element.ToString());
            }
        }
    }
    using var process = Process.Start(psi)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return output.Trim();
}

static async Task<IList<McpClientTool>> GetMcpToolsAsync()
{
    // Create the MCP client
    McpClient mcpClient = await McpClient.CreateAsync(
        new StdioClientTransport(new()
        {
            Command = "CheckNugetPackagesMcp",
            Arguments = [],
            Name = "Check Nuget Packages Mcp",
        }));

    var tools = await mcpClient.ListToolsAsync();

    var scanner = new McpSecurityScanner();

    foreach (var tool in tools)
    {
        var toolDefinition = new AgentGovernance.Mcp.McpToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.JsonSchema.ToString()
        };

        var result = scanner.Scan(toolDefinition);

        Console.WriteLine($"Risk score: {result.RiskScore}/100");
        foreach (var threat in result.Threats)
        {
            Console.WriteLine($"  [{threat.Severity}] {threat.Type}: {threat.Description}");
        }
    }

    return tools;
}