using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();

var services = new ServiceCollection();

string openAiKey = configuration["OpenAI:GitHubToken"] ?? throw new Exception("Missing API Key");

var openAIOptions = new OpenAIClientOptions()
{
    Endpoint = new Uri("https://models.inference.ai.azure.com")
};

ChatClient client = new ChatClient("gpt-4o", new ApiKeyCredential(openAiKey), openAIOptions);

var serviceProvider = services.BuildServiceProvider();

var messages = new List<ChatMessage>
{
    new SystemChatMessage("You are a helpful assistant.")
};

while (true)
{
    Console.Write("You >> ");
    var userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    messages.Add(new UserChatMessage(userInput));
    ChatCompletion response = await client.CompleteChatAsync(messages);
    Console.WriteLine($"AI >> {response.Content[0].Text}");
    messages.Add(new AssistantChatMessage(response.Content[0].Text));
}