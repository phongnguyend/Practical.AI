using Microsoft.Extensions.AI;

namespace Practical.OpenAI.Embedding;

public class ChunkEmbedding
{
    public string ChunkText { get; set; }

    public ReadOnlyMemory<float> EmbeddingVector { get; set; }

    public UsageDetails UsageDetails { get; set; }
    
    public int StartIndex { get; set; }
    
    public int EndIndex { get; set; }
}
