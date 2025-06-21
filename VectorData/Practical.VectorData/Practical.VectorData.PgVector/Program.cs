using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;

//var client = new AzureOpenAIClient(
//    new Uri("https://xxx.openai.azure.com/"),
//    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")));
//var embeddingGenerator = client.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

//var x = await embeddingGenerator.GenerateAsync("hello");

using var collection = new PostgresCollection<int, Blog>("Host=127.0.0.1;Database=vectordata;Username=postgres;Password=postgres", "Blogs");

Console.WriteLine("Creating collection...");
await collection.EnsureCollectionDeletedAsync();
await collection.EnsureCollectionExistsAsync();

Console.WriteLine("Inserting data...");
await collection.UpsertAsync([
    new Blog
    {
        Id = 1,
        Description = "This is a blog about AI and machine learning.",
        Embedding = new float[] { 0.1f, 0.2f, 0.3f },
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
    }]);

Console.WriteLine("Retrieving data...");
var blog = await collection.GetAsync(1) ?? throw new InvalidOperationException("Blog not found");
Console.WriteLine($"Blog ID: {blog.Id}, Description: {blog.Description}");

Console.WriteLine("Searching for similar blogs...");
await foreach (var result in collection.SearchAsync(new float[] { 0.1f, 0.2f, 0.3f }, top: 1))
{
    Console.WriteLine($"Similar blog ID: {result.Record.Id}, Description: {result.Record.Description}");
}

public class Blog
{
    [VectorStoreKey]
    public int Id { get; set; }

    [VectorStoreData]
    public required string Description { get; set; }

    [VectorStoreVector(Dimensions: 3, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}
