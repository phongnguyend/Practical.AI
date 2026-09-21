using AgentGovernance.Mcp;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Practical.MicrosoftAgentFramework.FoundryHostedAgent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var projectEndpoint = new Uri(
    FirstNonBlank(
        Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT"),
        builder.Configuration["Foundry:ProjectEndpoint"])
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set."));

var model = FirstNonBlank(
    Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
    Environment.GetEnvironmentVariable("FOUNDRY_MODEL"),
    builder.Configuration["Foundry:ModelDeployment"])
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");

var tools = new List<AITool>
{
    AIFunctionFactory.Create(GetCurrentDateTime),
    AIFunctionFactory.Create(GetComputerName),
};

var memoryDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".practical-microsoft-agent-framework");
Directory.CreateDirectory(memoryDirectory);

var memoryFunctions = new MemoryFunctions(Path.Combine(memoryDirectory, "user_memory.json"));
tools.Add(AIFunctionFactory.Create(memoryFunctions.RememberAsync));
tools.Add(AIFunctionFactory.Create(memoryFunctions.RecallAsync));
tools.Add(AIFunctionFactory.Create(memoryFunctions.ListMemoriesAsync));
tools.Add(AIFunctionFactory.Create(memoryFunctions.ForgetAsync));

var sandboxStateFunctions = new SandboxStateFunctions(memoryDirectory);
tools.Add(AIFunctionFactory.Create(
    sandboxStateFunctions.WriteSandboxStateAsync,
    name: "write_sandbox_state"));
tools.Add(AIFunctionFactory.Create(
    sandboxStateFunctions.ReadSandboxStateAsync,
    name: "read_sandbox_state"));
tools.Add(AIFunctionFactory.Create(
    sandboxStateFunctions.ClearSandboxStateAsync,
    name: "clear_sandbox_state"));

AddInternalSearchTool(builder.Configuration, tools);
await AddMcpToolsAsync(builder.Configuration, tools);

var skillsProvider = new AgentSkillsProvider(
    Path.Combine(AppContext.BaseDirectory, "skills"),
    RunScriptAsync);

var instructions = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "instructions.txt"));

AIAgent agent = new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
    .AsAIAgent(new ChatClientAgentOptions
    {
        Name = builder.Configuration["Agent:Name"] ?? "practical-microsoft-agent-framework",
        Description = "A practical Microsoft Agent Framework assistant with memory, skills, and optional enterprise tools.",
        ChatOptions = new()
        {
            ModelId = model,
            Instructions = instructions,
            Tools = tools,
        },
        AIContextProviders = [skillsProvider],
    })
    .AsBuilder()
    .UseToolApproval(new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule],
    })
    .Build();

builder.Services.AddFoundryResponses(agent);

var app = builder.Build();
app.MapFoundryResponses();
app.Run();

static void AddInternalSearchTool(IConfiguration configuration, List<AITool> tools)
{
    var connectionString = configuration.GetConnectionString("InternalSearch");
    var endpoint = configuration["InternalSearch:EmbeddingEndpoint"];
    var apiKey = configuration["InternalSearch:EmbeddingApiKey"];
    var model = configuration["InternalSearch:EmbeddingModel"] ?? "text-embedding-3-small";

    if (string.IsNullOrWhiteSpace(connectionString)
        || string.IsNullOrWhiteSpace(endpoint)
        || string.IsNullOrWhiteSpace(apiKey))
    {
        return;
    }

    var searchFunctions = new SearchFunctions(connectionString, new Uri(endpoint), apiKey, model);
    tools.Add(AIFunctionFactory.Create(searchFunctions.SearchInternalDataAsync));
}

static async Task AddMcpToolsAsync(IConfiguration configuration, List<AITool> tools)
{
    var command = configuration["Mcp:CheckNugetPackages:Command"];
    if (string.IsNullOrWhiteSpace(command))
    {
        return;
    }

    var arguments = configuration.GetSection("Mcp:CheckNugetPackages:Arguments").Get<string[]>() ?? [];
    McpClient mcpClient = await McpClient.CreateAsync(
        new StdioClientTransport(new()
        {
            Command = command,
            Arguments = arguments,
            Name = "Check NuGet Packages MCP",
        }));

    var scanner = new McpSecurityScanner();
    foreach (var tool in await mcpClient.ListToolsAsync())
    {
        var result = scanner.Scan(new McpToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.JsonSchema.ToString(),
        });

        if (result.Threats.Any(threat => threat.Severity.ToString().Equals("Critical", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Skipped MCP tool '{tool.Name}' because the security scan found a critical threat.");
            continue;
        }

        tools.Add(tool);
    }
}

static async Task<object?> RunScriptAsync(
    AgentFileSkill skill,
    AgentFileSkillScript script,
    JsonElement? arguments,
    IServiceProvider? serviceProvider,
    CancellationToken cancellationToken)
{
    var executable = Path.GetExtension(script.FullPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
        ? (OperatingSystem.IsWindows() ? "powershell" : "pwsh")
        : script.FullPath;

    var psi = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    if (!string.Equals(executable, script.FullPath, StringComparison.Ordinal))
    {
        psi.ArgumentList.Add(script.FullPath);
    }

    if (arguments is { ValueKind: JsonValueKind.Array })
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

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException($"Could not start skill script '{script.FullPath}'.");
    var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Skill script '{script.FullPath}' failed with exit code {process.ExitCode}: {standardError.Trim()}");
    }

    return standardOutput.Trim();
}

[Description("Get the current datetime")]
static DateTimeOffset GetCurrentDateTime() => DateTimeOffset.Now;

[Description("Get the current computer name")]
static string GetComputerName() => Environment.MachineName;

static string? FirstNonBlank(params string?[] candidates) =>
    Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));
