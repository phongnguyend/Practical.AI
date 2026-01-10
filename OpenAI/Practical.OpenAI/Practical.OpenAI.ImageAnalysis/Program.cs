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
var client = options.CreateOpenAIChatClient();

var textPart = ChatMessageContentPart.CreateTextPart("Describe this picture:");
var imgPart = ChatMessageContentPart.CreateImagePart(new BinaryData(File.ReadAllBytes("landmark.jpg")), "image/jpg");

var chatMessages = new List<ChatMessage>
{
    new SystemChatMessage("You are a helpful assistant."),
    new UserChatMessage(textPart, imgPart)
};

ChatCompletion chatCompletion = await client.CompleteChatAsync(chatMessages);

Console.WriteLine($"[ASSISTANT]:");
Console.WriteLine($"{chatCompletion.Content[0].Text}");

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}