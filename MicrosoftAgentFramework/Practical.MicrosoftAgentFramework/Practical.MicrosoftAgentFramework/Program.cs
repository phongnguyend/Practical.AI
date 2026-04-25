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

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

// Create the MCP client
McpClient mcpClient = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Command = "CheckNugetPackagesMcp",
        Arguments = [],
        Name = "Check Nuget Packages Mcp",
    }));

var tools = await mcpClient.ListToolsAsync();

var options = GetOpenAIOptions(configuration);
ChatClient client = options.CreateChatClient();
var searchFunctions = new SearchFunctions(options);
var memoryFunctions = new MemoryFunctions(Path.Combine(AppContext.BaseDirectory, "user_memory.json"));

#pragma warning disable MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var skillsProvider = new AgentSkillsProvider(
    Path.Combine(AppContext.BaseDirectory, "skills"),
    RunScriptAsync
    );
#pragma warning restore MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

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
});

var session = await agent.CreateSessionAsync();

while (true)
{
    Console.Write("You: ");

    string? userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    var userMessage = new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, userInput);

    var response = await agent.RunAsync(userMessage, session);
    Console.WriteLine($"Agent: {response}");
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

#pragma warning disable MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
static async Task<object?> RunScriptAsync(AgentFileSkill skill,
    AgentFileSkillScript script,
    AIFunctionArguments arguments,
    CancellationToken cancellationToken)
{
    var psi = new ProcessStartInfo("powershell")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(script.FullPath);
    if (arguments != null)
    {
        foreach (var (key, value) in arguments)
        {
            if (value is not null)
            {
                psi.ArgumentList.Add($"-{key}");
                psi.ArgumentList.Add(value.ToString()!);
            }
        }
    }
    using var process = Process.Start(psi)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return output.Trim();
}
#pragma warning restore MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.