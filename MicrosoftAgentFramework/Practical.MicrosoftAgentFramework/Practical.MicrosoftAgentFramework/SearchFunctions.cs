using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.SqlServer;
using System.ComponentModel;

namespace Practical.MicrosoftAgentFramework;

internal class SearchFunctions
{
    private readonly OpenAIOptions _options;

    public SearchFunctions(OpenAIOptions options)
    {
        _options = options;
    }

    [Description("Search Internal Data")]
    public async Task<List<Chunk>> SearchInternalDataAsync(string query)
    {
        var embeddingGenerator = _options.CreateEmbeddingClient();

        var queryEmbedding = (await embeddingGenerator.GenerateAsync(query)).Vector;

        using var collection = new SqlServerCollection<Guid, Chunk>("Server=.;Database=Microsoft.Extensions.DataIngestion;User Id=sa;Password=sqladmin123!@#;Encrypt=False", "chunks");

        var rs = new List<Chunk>();

        await foreach (var item in collection.SearchAsync(queryEmbedding, 5))
        {
            rs.Add(item.Record);
        }

        return rs;
    }
}
