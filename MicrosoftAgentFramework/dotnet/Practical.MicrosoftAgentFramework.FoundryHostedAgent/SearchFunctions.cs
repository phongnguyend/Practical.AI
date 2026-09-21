using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;
using System.ComponentModel;

namespace Practical.MicrosoftAgentFramework.FoundryHostedAgent;

internal class SearchFunctions
{
    private readonly string _connectionString;
    private readonly EmbeddingClient _embeddingClient;

    public SearchFunctions(string connectionString, Uri embeddingEndpoint, string embeddingApiKey, string embeddingModel)
    {
        _connectionString = connectionString;
        _embeddingClient = new OpenAIClient(
            new ApiKeyCredential(embeddingApiKey),
            new OpenAIClientOptions { Endpoint = embeddingEndpoint })
            .GetEmbeddingClient(embeddingModel);
    }

    [Description("Search internal data")]
    public async Task<List<Chunk>> SearchInternalDataAsync(string query)
    {
        var embeddingGenerator = _embeddingClient.AsIEmbeddingGenerator();
        var queryEmbedding = (await embeddingGenerator.GenerateAsync(query)).Vector;

        using var collection = new SqlServerCollection<Guid, Chunk>(_connectionString, "chunks");
        var results = new List<Chunk>();

        await foreach (var item in collection.SearchAsync(queryEmbedding, 5))
        {
            results.Add(item.Record);
        }

        return results;
    }
}
