using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Configuration;
using Practical.AmazonBedrock;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();
var options = GetAmazonBedrockOptions(configuration);

// Create a Bedrock Runtime client in the AWS Region you want to use.
AmazonBedrockRuntimeClient client = options.CreateAmazonBedrockRuntimeClient();

// Define the user message.
var userMessage = File.ReadAllText("../../prompt2.txt");

//Format the request payload using the model's native structure.
var nativeRequest = JsonSerializer.Serialize(new
{
    anthropic_version = options.AnthropicVersion,
    max_tokens = options.MaxTokens,
    temperature = options.Temperature,
    messages = new[]
    {
        new { role = "user", content = userMessage }
    }
});

// Create a request with the model ID, the user message, and an inference configuration.
var request = new InvokeModelRequest()
{
    ModelId = options.ModelId,
    Body = new MemoryStream(Encoding.UTF8.GetBytes(nativeRequest)),
    ContentType = "application/json"
};

try
{
    // Send the request to the Bedrock Runtime and wait for the response.
    var response = await client.InvokeModelAsync(request);

    // Decode the response body.
    var modelResponse = await JsonNode.ParseAsync(response.Body);

    // Extract and print the response text.
    var responseText = modelResponse["content"]?[0]?["text"].ToString() ?? "";
    Console.WriteLine(responseText);

    var jsonStart = responseText.IndexOf('[');
    var jsonEnd = responseText.LastIndexOf(']');

    if (jsonStart >= 0 && jsonEnd > jsonStart)
    {
        var jsonPart = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
    }
}
catch (AmazonBedrockRuntimeException e)
{
    Console.WriteLine($"ERROR: Can't invoke '{request.ModelId}'. Reason: {e.Message}");
    throw;
}

static AmazonOptions GetAmazonBedrockOptions(IConfiguration configuration)
{
    var options = new AmazonOptions();
    configuration.GetSection("Amazon").Bind(options);
    return options;
}