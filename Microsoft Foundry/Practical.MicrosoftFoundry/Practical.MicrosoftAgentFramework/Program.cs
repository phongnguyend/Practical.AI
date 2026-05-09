using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.AI;
using System.ComponentModel;

#pragma warning disable OPENAI001

const string endpoint = "https://nguye-mi1n6c7o-eastus2.services.ai.azure.com/api/projects/Practical-AI";
const string agentName = "practical-agent";
const string agentVersion = "1";

AIProjectClient projectClient = new(endpoint: new Uri(endpoint), tokenProvider: new DefaultAzureCredential());

AgentReference agentReference = new(name: agentName, version: agentVersion);
ProjectResponsesClient responseClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);

var agent = projectClient.AsAIAgent(agentReference,
    tools: [
        AIFunctionFactory.Create(GetComputerName)
    ]);

var session = await agent.CreateSessionAsync();

while (true)
{
    Console.Write("You: ");

    string? userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    var userMessage = new ChatMessage(ChatRole.User, userInput);

    var response = await agent.RunAsync(userMessage, session);
    Console.WriteLine($"Agent: {response}");
}

[Description("Get the current computer name")]
static string GetComputerName() => Environment.MachineName;