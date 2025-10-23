using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Chat;
using Practical.OpenAI;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();


var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

var options = GetOpenAIOptions(configuration);
ChatClient client = options.CreateChatClient();

var messages = new List<ChatMessage>
{
    new SystemChatMessage("You are a helpful assistant.")
};

while (true)
{
    Console.Write("You:");
    var userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    messages.Add(new UserChatMessage(userInput));

    ChatCompletion response = await client.CompleteChatAsync(messages);
    var reponseText = response.Content[0].Text;
    messages.Add(new AssistantChatMessage(reponseText));

    Console.WriteLine($"AI: {reponseText}");
}

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}