using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Practical.OpenAI;
using Practical.OpenAI.Embedding;
using System.Text;
using System.Text.RegularExpressions;

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddUserSecrets<Program>();

var configuration = builder.Build();

var services = new ServiceCollection();
var serviceProvider = services.BuildServiceProvider();

var options = GetOpenAIOptions(configuration);
var embeddingClient = options.CreateEmbeddingClient();

var chunkEmbeddings = new List<ChunkEmbedding>();

var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.md";

var sentences = ChunkSentences(await File.ReadAllTextAsync(filePath)).ToArray();

foreach (var chunk in sentences)
{
    var result = await embeddingClient.GenerateAsync([chunk]);

    chunkEmbeddings.Add(new ChunkEmbedding
    {
        ChunkText = chunk,
        EmbeddingVector = result.First().Vector,
        UsageDetails = result.Usage
    });
}

Console.ReadLine();

static OpenAIOptions GetOpenAIOptions(IConfiguration configuration)
{
    var options = new OpenAIOptions();
    configuration.GetSection("OpenAI").Bind(options);
    return options;
}

static IEnumerable<string> ChunkSentences(string text, int maxTokens = 5000)
{
    var sentences = Regex.Split(text, @"(?<=[\.!\?])\s+");
    var current = new StringBuilder();
    int tokenCount = 0;

    foreach (var sentence in sentences)
    {
        int sentenceTokens = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (tokenCount + sentenceTokens > maxTokens && current.Length > 0)
        {
            yield return current.ToString().Trim();
            current.Clear();
            tokenCount = 0;
        }

        current.Append(sentence + " ");
        tokenCount += sentenceTokens;
    }

    if (current.Length > 0)
        yield return current.ToString().Trim();
}

static IEnumerable<string> ChunkWithOverlap(string text, int maxTokens = 800, double overlapRatio = 0.1)
{
    if (string.IsNullOrWhiteSpace(text))
        yield break;

    // 1. Split into sentences using regex
    var sentences = Regex.Split(text, @"(?<=[\.!\?])\s+")
                         .Select(s => s.Trim())
                         .Where(s => s.Length > 0)
                         .ToList();

    var current = new StringBuilder();
    int tokenCount = 0;
    int estimatedOverlapTokens = (int)(maxTokens * overlapRatio);
    var lastChunkSentences = new List<string>();

    foreach (var sentence in sentences)
    {
        int sentenceTokens = EstimateTokens(sentence);

        // If adding this sentence exceeds limit, yield current chunk
        if (tokenCount + sentenceTokens > maxTokens && current.Length > 0)
        {
            yield return current.ToString().Trim();

            // prepare overlap from previous sentences
            var overlap = string.Join(" ", lastChunkSentences.TakeLast(estimatedOverlapTokens / 20)); // ~20 tokens per sentence
            current.Clear();
            current.Append(overlap + " ");
            tokenCount = EstimateTokens(overlap);
            lastChunkSentences.Clear();
        }

        current.Append(sentence + " ");
        tokenCount += sentenceTokens;
        lastChunkSentences.Add(sentence);
    }

    if (current.Length > 0)
        yield return current.ToString().Trim();
}

static int EstimateTokens(string text)
{
    // Rough heuristic: 1 token ≈ 4 chars
    return Math.Max(1, text.Length / 4);
}