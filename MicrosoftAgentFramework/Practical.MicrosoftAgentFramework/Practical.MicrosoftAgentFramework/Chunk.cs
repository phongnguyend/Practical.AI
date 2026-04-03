using Microsoft.Extensions.VectorData;

internal class Chunk
{
    [VectorStoreKey]
    public Guid key { get; set; }

    [VectorStoreData]
    public required string content { get; set; }

    [VectorStoreData]
    public required string context { get; set; }

    [VectorStoreData]
    public required string documentid { get; set; }

    [VectorStoreVector(Dimensions: 1536, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float>? embedding { get; set; }
}