using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using Practical.MicrosoftAgentFramework;
using System.ClientModel;
using System.ComponentModel;

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

OpenAIOptions.Default.ApiKey = options.ApiKey;

AIAgent agent = client.AsAIAgent(
    instructions: "You are good at telling jokes.",
    tools: [
        AIFunctionFactory.Create(GetCurrentDateTime),
        AIFunctionFactory.Create(GetComputerName),
        AIFunctionFactory.Create(SearchInternalDataAsync),
        .. tools.Cast<AITool>()
    ]);

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
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}

[Description("Search Internal Data")]
static async Task<List<Chunk>> SearchInternalDataAsync(string query)
{
    OpenAIClient openAIClient = new(
        new ApiKeyCredential(OpenAIOptions.Default.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai/inference") });

    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator =
    openAIClient.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

    var queryEmbedding = (await embeddingGenerator.GenerateAsync(query)).Vector;

    using var collection = new SqlServerCollection<Guid, Chunk>("Server=.;Database=Microsoft.Extensions.DataIngestion;User Id=sa;Password=sqladmin123!@#;Encrypt=False", "chunks");

    var rs = new List<Chunk>();

    await foreach (var item in collection.SearchAsync(queryEmbedding, 5))
    {
        rs.Add(item.Record);
    }

    return rs;
}