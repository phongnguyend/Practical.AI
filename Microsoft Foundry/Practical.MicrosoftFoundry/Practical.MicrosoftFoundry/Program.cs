using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using OpenAI.Responses;

#pragma warning disable OPENAI001

const string endpoint = "https://nguye-mi1n6c7o-eastus2.services.ai.azure.com/api/projects/Practical-AI";
const string agentName = "practical-agent";
const string agentVersion = "1";

AIProjectClient projectClient = new(endpoint: new Uri(endpoint), tokenProvider: new DefaultAzureCredential());

AgentReference agentReference = new(name: agentName, version: agentVersion);
ProjectResponsesClient responseClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);

string? previousResponseId = null;

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    ResponseResult response = responseClient.CreateResponse(input, previousResponseId);

    previousResponseId = response.Id;

    Console.WriteLine($"Agent: {response.GetOutputText()}");
    Console.WriteLine();
}