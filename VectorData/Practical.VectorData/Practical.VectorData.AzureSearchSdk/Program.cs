using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();
var services = new ServiceCollection();

//var embeddingGenerator = GetEmbeddingGenerator(configuration);

var endpoint = configuration["AzureAISearch:Endpoint"]!;
var apiKey = configuration["AzureAISearch:ApiKey"]!;

var indexClient = new SearchIndexClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

var searchClient = new SearchClient(new Uri(endpoint), "blogs", new AzureKeyCredential(apiKey));

Console.WriteLine("Creating collection...");

await indexClient.DeleteIndexAsync("blogs");

var index = new SearchIndex("blogs")
{
    Fields =
    {
        new SimpleField("Id", SearchFieldDataType.String)
        {
            IsKey = true,
        },

        new SearchableField("Description")
        {
            AnalyzerName = LexicalAnalyzerName.EnLucene
        },

        new SearchField("Embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
        {
            IsSearchable = true,
            IsHidden = false,
            VectorSearchDimensions = 3,
            VectorSearchProfileName = "vector-profile"
        }
    },
    VectorSearch = new VectorSearch
    {
        Algorithms =
        {
            new HnswAlgorithmConfiguration("hnsw-config")
        },
        Profiles =
        {
            new VectorSearchProfile(
                name: "vector-profile",
                algorithmConfigurationName: "hnsw-config")
        }
    }
};

await indexClient.CreateIndexAsync(index);

var blogs = new[] {
    new Blog
    {
        Id = Guid.CreateVersion7(),
        Description = "This is a blog about AI and machine learning.",
        Embedding = new float[] { 0.1f, 0.2f, 0.3f }
    },
    new Blog
    {
        Id = Guid.CreateVersion7(),
        Description = "This is a blog about animals and plants.",
        Embedding = new float[] { 99.1f, 50f, 3f },
    },
    new Blog
    {
        Id = Guid.CreateVersion7(),
        Description = "This is a blog about sports and outdoor activities.",
        Embedding = new float[] { 3f, 60f, 240f },
    }
};

foreach (var blog in blogs)
{
    //blog.Embedding = (await embeddingGenerator.GenerateAsync(blog.Description)).Vector;
}

Console.WriteLine("Inserting data...");
await searchClient.MergeOrUploadDocumentsAsync(blogs);

Console.WriteLine("Retrieving data...");
var foundBlog = (await searchClient.GetDocumentAsync<Blog>(blogs[0].Id.ToString())).Value ?? throw new InvalidOperationException("Blog not found");
Console.WriteLine($"Blog ID: {foundBlog.Id}, Description: {foundBlog.Description}");

Console.WriteLine("Searching for similar blogs...");

var queryEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
//var queryEmbedding = (await embeddingGenerator.GenerateAsync("dog")).Vector;

var options = new SearchOptions
{
    VectorSearch = new()
    {
        Queries =
        {
            new VectorizedQuery(queryEmbedding)
            {
                KNearestNeighborsCount = 2,
                Fields = { "Embedding" }
            }
        }
    }
};

var results = searchClient.Search<Blog>(null, options);

await foreach (var result in results.Value.GetResultsAsync())
{
    var blog = result.Document;
    Console.WriteLine($"Similar blog ID: {blog.Id}, Description: {blog.Description}");
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
    public Guid Id { get; set; }

    public required string Description { get; set; }

    public ReadOnlyMemory<float>? Embedding { get; set; }
}
