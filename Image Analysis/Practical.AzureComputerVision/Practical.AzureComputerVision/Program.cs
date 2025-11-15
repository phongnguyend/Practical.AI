using Azure;
using Azure.AI.Vision.ImageAnalysis;
using Microsoft.Extensions.Configuration;


var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

string key = configuration["AzureAIVision:ApiKey"]!;
string endpoint = configuration["AzureAIVision:Endpoint"]!;

// Create a client
var client = CreateImageAnalysisClient(endpoint, key);

// Analyze an image to get features and other properties.
await AnalyzeImageAsync(client, "landmark.jpg");

static ImageAnalysisClient CreateImageAnalysisClient(string endpoint, string key)
{
    var client = new ImageAnalysisClient(new Uri(endpoint), new AzureKeyCredential(key));
    return client;
}

static async Task AnalyzeImageAsync(ImageAnalysisClient client, string imageFilePath)
{
    Console.WriteLine($"Analyzing the image {Path.GetFileName(imageFilePath)}...");
    Console.WriteLine();

    // Creating a list that defines the features to be extracted from the image. 
    VisualFeatures features = VisualFeatures.Caption | VisualFeatures.DenseCaptions | VisualFeatures.Tags;

    // Analyze the image 
    var result = await client.AnalyzeAsync(new BinaryData(File.ReadAllBytes(imageFilePath)), visualFeatures: features);

    // Image tags
    Console.WriteLine("Tags:");
    foreach (var tag in result.Value.Tags.Values)
    {
        Console.WriteLine($"Name: {tag.Name} Confidence: {tag.Confidence}");
    }

    // Image caption and their confidence score
    Console.WriteLine("Caption:");
    Console.WriteLine($"Text: {result.Value.Caption.Text} Confidence: {result.Value.Caption.Confidence}");

    Console.WriteLine("Dense Captions:");
    foreach (var caption in result.Value.DenseCaptions.Values)
    {
        Console.WriteLine($"Text: {caption.Text} Confidence: {caption.Confidence}");
    }

    Console.WriteLine();
}