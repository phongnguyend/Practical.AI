using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Practical.OpenAI;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();


var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

var options = GetOpenAIOptions(configuration);
var client = options.CreateChatClient();

var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful assistant.")
};

while (true)
{
    Console.Write("You:");
    var userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput))
        break;

    messages.Add(new ChatMessage(ChatRole.User, userInput));

    var response = await client.GetResponseAsync(messages);
    var reponseText = response.Text;
    messages.Add(new ChatMessage(ChatRole.Assistant, reponseText));

    Console.WriteLine($"AI: {reponseText}");
}

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}