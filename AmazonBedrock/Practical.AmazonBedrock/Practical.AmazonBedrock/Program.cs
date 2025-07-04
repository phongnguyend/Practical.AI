using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.CredentialManagement;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();

// Create a Bedrock Runtime client in the AWS Region you want to use.
AmazonBedrockRuntimeClient client = GetBedrockClient(configuration);

// Define the user message.
var userMessage = File.ReadAllText("../../prompt.txt");

//Format the request payload using the model's native structure.
var nativeRequest = JsonSerializer.Serialize(new
{
    anthropic_version = "bedrock-2023-05-31",
    max_tokens = 512,
    temperature = 0.5,
    messages = new[]
    {
        new { role = "user", content = userMessage }
    }
});

// Create a request with the model ID, the user message, and an inference configuration.
var request = new InvokeModelRequest()
{
    //ModelId = "anthropic.claude-3-7-sonnet-20250219-v1:0",
    ModelId = "arn:aws:bedrock:eu-central-1:891377219642:inference-profile/eu.anthropic.claude-3-7-sonnet-20250219-v1:0",
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
    var responseText = modelResponse["content"]?[0]?["text"] ?? "";
    Console.WriteLine(responseText);
}
catch (AmazonBedrockRuntimeException e)
{
    Console.WriteLine($"ERROR: Can't invoke '{request.ModelId}'. Reason: {e.Message}");
    throw;
}

static AmazonBedrockRuntimeClient GetBedrockClient(IConfiguration configuration)
{
    string profileName = configuration["Amazon:ProfileName"] ?? string.Empty;
    string awsAccessKeyId = configuration["Amazon:AccessKeyId"] ?? string.Empty;
    string awsSecretAccessKey = configuration["Amazon:SecretAccessKey"] ?? string.Empty;
    string awsSessionToken = configuration["Amazon:SessionToken"] ?? string.Empty;
    string regionEndpoint = configuration["Amazon:RegionEndpoint"] ?? throw new Exception("Missing RegionEndpoint");

    if (!string.IsNullOrEmpty(profileName))
    {
        var chain = new CredentialProfileStoreChain();
        if (chain.TryGetAWSCredentials(profileName, out var credentials))
        {
            return new AmazonBedrockRuntimeClient(credentials, RegionEndpoint.GetBySystemName(regionEndpoint));
        }

        throw new Exception("Unable to load credentials from SSO profile.");

    }
    else if (!string.IsNullOrEmpty(awsAccessKeyId))
    {
        if (!string.IsNullOrEmpty(awsSessionToken))
        {
            return new AmazonBedrockRuntimeClient(awsAccessKeyId, awsSecretAccessKey, awsSessionToken, RegionEndpoint.GetBySystemName(regionEndpoint));
        }

        return new AmazonBedrockRuntimeClient(awsAccessKeyId, awsSecretAccessKey, RegionEndpoint.GetBySystemName(regionEndpoint));
    }

    return new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(regionEndpoint));
}