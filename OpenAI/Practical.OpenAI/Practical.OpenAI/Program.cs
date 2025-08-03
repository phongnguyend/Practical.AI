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

//await AskMappings(client, ["Company Number", "Email Address", "Ngày Giao Dịch"], ["Company Code", "Transaction Date"]);
await AskColumns(client, "merge columns A and D to the column C", ["A", "B", "C", "D"]);

//await NormalChat(client);

static async Task AskMappings(ChatClient client, string[] headersInFile, string[] columnsInTable)
{
    var messages = new List<ChatMessage>
    {
        new SystemChatMessage("You are a helpful assistant who is very good at mapping the column headers in excel with column names in a table in database."),
        new UserChatMessage($"Here is the list of column headers in my file: {string.Join(',',headersInFile)}"),
        new UserChatMessage($"And here is the list of columns in my table in database: {string.Join(',',columnsInTable)}"),
        new UserChatMessage("Please try to best map the columns in file with columns in table if they are similar in the same or different languages and return result in json format"),
        new UserChatMessage("The json structure should be [{file: 'Column In File', table: 'Column in Table', reason: 'the reason why you choose'}] and 'Column in Table' should be null if you cannot match")
    };

    ChatCompletion response = await client.CompleteChatAsync(messages);
    var reponseText = response.Content[0].Text;
    messages.Add(new AssistantChatMessage(reponseText));

    Console.WriteLine($"AI >> {reponseText}");

    while (true)
    {
        Console.Write("You >> ");
        var userInput = Console.ReadLine();

        if (string.IsNullOrEmpty(userInput))
            break;

        messages.Add(new UserChatMessage(userInput));

        response = await client.CompleteChatAsync(messages);
        reponseText = response.Content[0].Text;
        messages.Add(new AssistantChatMessage(reponseText));

        Console.WriteLine($"AI >> {reponseText}");
    }
}

static async Task AskColumns(ChatClient client, string userRequest, string[] columns)
{
    string prompt = @$"here is a list of columns in a table in the database: {string.Join(',', columns)}
this is a user request: {userRequest}
please find out which columns we need to use to fulfill user's requirement";

    var messages = new List<ChatMessage>
    {
        new SystemChatMessage("You are a helpful assistant who is very good at mapping the column headers in excel with column names in a table in database."),
        new UserChatMessage(prompt)
    };

    ChatCompletion response = await client.CompleteChatAsync(messages);
    var reponseText = response.Content[0].Text;
    messages.Add(new AssistantChatMessage(reponseText));

    Console.WriteLine($"AI >> {reponseText}");

    while (true)
    {
        Console.Write("You >> ");
        var userInput = Console.ReadLine();

        if (string.IsNullOrEmpty(userInput))
            break;

        messages.Add(new UserChatMessage(userInput));

        response = await client.CompleteChatAsync(messages);
        reponseText = response.Content[0].Text;
        messages.Add(new AssistantChatMessage(reponseText));

        Console.WriteLine($"AI >> {reponseText}");
    }
}

static async Task NormalChat(ChatClient client)
{
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
        var reponseText = response.Content[0].Text;
        messages.Add(new AssistantChatMessage(reponseText));

        Console.WriteLine($"AI >> {reponseText}");
    }
}

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}