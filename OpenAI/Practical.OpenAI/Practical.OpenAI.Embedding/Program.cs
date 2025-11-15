using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Practical.OpenAI;
using Practical.OpenAI.Embedding;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

var services = new ServiceCollection();
services.AddSingleton<ITextChunkingService, TextChunkingService>();
var serviceProvider = services.BuildServiceProvider();

var options = GetOpenAIOptions(configuration);
var embeddingClient = options.CreateEmbeddingClient();
var chunkingService = serviceProvider.GetRequiredService<ITextChunkingService>();

var chunkEmbeddings = new List<ChunkEmbedding>();

var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.md";

var fileContent = await File.ReadAllTextAsync(filePath);
var chunks = chunkingService.ChunkSentences(fileContent).ToArray();

foreach (var chunk in chunks)
{
    var result = await embeddingClient.GenerateAsync([chunk.Text]);

    chunkEmbeddings.Add(new ChunkEmbedding
    {
        ChunkText = chunk.Text,
        EmbeddingVector = result.First().Vector,
        UsageDetails = result.Usage,
        StartIndex = chunk.StartIndex,
        EndIndex = chunk.EndIndex
    });
}

Console.ReadLine();

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}