using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();
var services = new ServiceCollection();

var embeddingGenerator = GetEmbeddingGenerator(configuration);

using var collection = new PostgresCollection<int, Blog>("Host=127.0.0.1;Database=vectordata;Username=postgres;Password=postgres", "Blogs");

Console.WriteLine("Creating collection...");
await collection.EnsureCollectionDeletedAsync();
await collection.EnsureCollectionExistsAsync();

var blogTemplates = new[]
{
    new { Description = "This is a blog about AI and machine learning.",      Embedding = new float[] { 0.1f, 0.2f, 0.3f } },
    new { Description = "This is a blog about animals and plants.",           Embedding = new float[] { 99.1f, 50f, 3f } },
    new { Description = "This is a blog about sports and outdoor activities.", Embedding = new float[] { 3f, 60f, 240f } },
};

var tenants = new[] { "tenant-1", "tenant-2", "tenant-3" };
var blogs = tenants
    .SelectMany((tenantId, ti) => blogTemplates.Select((t, bi) => new Blog
    {
        Id = ti * blogTemplates.Length + bi + 1,
        TenantId = tenantId,
        Description = t.Description,
        Embedding = t.Embedding
    }))
    .ToArray();

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

await foreach (var result in collection.SearchAsync(queryEmbedding, top: 1, new VectorSearchOptions<Blog> { Filter = r => r.TenantId == "tenant-1" }))
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
    public required string TenantId { get; set; }

    [VectorStoreData]
    public required string Description { get; set; }

    [VectorStoreVector(Dimensions: 3, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float>? Embedding { get; set; }

    //[VectorStoreVector(Dimensions: 1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    //public ReadOnlyMemory<float>? Embedding { get; set; }
}
