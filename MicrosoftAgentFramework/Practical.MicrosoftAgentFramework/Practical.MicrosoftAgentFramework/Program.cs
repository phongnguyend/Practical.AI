using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using Practical.MicrosoftAgentFramework;
using System.ComponentModel;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

var options = GetOpenAIOptions(configuration);
ChatClient client = options.CreateChatClient();

AIAgent agent = client.CreateAIAgent(
    instructions: "You are good at telling jokes.",
    tools: [
        AIFunctionFactory.Create(GetCurrentDateTime),
        AIFunctionFactory.Create(GetComputerName)
    ]);

AgentThread thread = agent.GetNewThread();

while (true)
{
    Console.Write("You: ");

    string? userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    var response = await agent.RunAsync(userInput, thread);
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