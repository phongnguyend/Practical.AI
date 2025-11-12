using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();
var services = new ServiceCollection();

var embeddingGenerator = GetEmbeddingGenerator(configuration);

var connectionString1 = configuration["ConnectionStrings:SqlServer"];
var connectionString2 = configuration["ConnectionStrings:AzureSql"];

using var collection = new SqlServerCollection<int, Blog>(connectionString1, "Blogs");

Console.WriteLine("Creating collection...");
await collection.EnsureCollectionDeletedAsync();
await collection.EnsureCollectionExistsAsync();

var blogs = new[] {
    new Blog
    {
        Id = 1,
        Description = "This is a blog about AI and machine learning.",
        Embedding = new float[] { 0.1f, 0.2f, 0.3f }
    },
    new Blog
    {
        Id = 2,
        Description = "This is a blog about animals and plants.",
        Embedding = new float[] { 99.1f, 50f, 3f },
    },
    new Blog
    {
        Id = 3,
        Description = "This is a blog about sports and outdoor activities.",
        Embedding = new float[] { 3f, 60f, 240f },
    }
};

foreach (var blog in blogs)
{
    //blog.Embedding = (await embeddingGenerator.GenerateAsync(blog.Description)).Vector;
}

Console.WriteLine("Inserting data...");
await collection.UpsertAsync(blogs);

Console.WriteLine("Retrieving data...");
var foundBlog = await collection.GetAsync(1) ?? throw new InvalidOperationException("Blog not found");
Console.WriteLine($"Blog ID: {foundBlog.Id}, Description: {foundBlog.Description}");

Console.WriteLine("Searching for similar blogs...");

var queryEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
//var queryEmbedding = (await embeddingGenerator.GenerateAsync("dog")).Vector;

await foreach (var result in collection.SearchAsync(queryEmbedding, top: 1))
{
    Console.WriteLine($"Similar blog ID: {result.Record.Id}, Description: {result.Record.Description}");
}

static IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(IConfigurationRoot configuration)
{
    // Use OpenAI

    string openAiKey = configuration["OpenAI:ApiKey"] ?? throw new Exception("Missing API Key");

    var openAIOptions = new OpenAIClientOptions()
    {
        Endpoint = new Uri("https://models.inference.ai.azure.com")
    };

    var client = new EmbeddingClient("text-embedding-3-small", new ApiKeyCredential(openAiKey), openAIOptions);
    var embeddingGenerator = client.AsIEmbeddingGenerator();


    // Use Azure OpenAI

    //var client = new AzureOpenAIClient(
    //    new Uri("https://xxx.openai.azure.com/"),
    //    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")));
    //var embeddingGenerator = client.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

    return embeddingGenerator;
}

public class Blog
{
    [VectorStoreKey]
    public int Id { get; set; }

    [VectorStoreData]
    public required string Description { get; set; }

    [VectorStoreVector(Dimensions: 3, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float>? Embedding { get; set; }

    //[VectorStoreVector(Dimensions: 1536, DistanceFunction = DistanceFunction.CosineDistance)]
    //public ReadOnlyMemory<float>? Embedding { get; set; }
}
