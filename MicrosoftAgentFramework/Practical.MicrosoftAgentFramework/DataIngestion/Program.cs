using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using OpenAI;
using System.ClientModel;

var builder = new ConfigurationBuilder()
    .AddUserSecrets<Program>();

var configuration = builder.Build();

OpenAIClient openAIClient = new(
    new ApiKeyCredential(configuration["OpenAI:ApiKey"]!),
    new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai/inference") });

IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator =
openAIClient.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

using IngestionPipeline<string> pipeline = new(CreateReader(), CreateChunker(embeddingGenerator), CreateWriter(embeddingGenerator));

await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo("C:\\Users\\phongnguyend\\Downloads\\Test"), searchPattern: "*"))
{
    Console.WriteLine($"Completed processing '{result.DocumentId}'. Succeeded: '{result.Succeeded}'.");
}

IngestionChunkWriter<string> CreateWriter(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    using SqlServerVectorStore vectorStore = new(
        "Server=.;Database=Microsoft.Extensions.DataIngestion;User Id=sa;Password=sqladmin123!@#;Encrypt=False",
        new()
        {
            EmbeddingGenerator = embeddingGenerator,
        });

    // The writer requires the embedding dimension count to be specified.
    // For OpenAI's `text-embedding-3-small`, the dimension count is 1536.
    VectorStoreWriter<string> writer = new(vectorStore, dimensionCount: 1536);

    return writer;
}

IngestionChunker<string> CreateChunker(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-5");
    IngestionChunkerOptions options = new(tokenizer)
    {
        MaxTokensPerChunk = 2000,
        OverlapTokens = 0
    };

    //IngestionChunker<string> chunker = new HeaderChunker(options);

    IngestionChunker<string> chunker = new SemanticSimilarityChunker(embeddingGenerator, options);

    return chunker;
}

static IngestionDocumentReader CreateReader()
{
    // Connect to a MarkItDown MCP server (e.g., running in Docker)
    // docker run -p 3001:3001 mcp/markitdown --http --host 0.0.0.0 --port 3001
    return new MarkItDownMcpReader(new Uri("http://localhost:3001/mcp"));
}