using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

[JsonConverter(typeof(JsonStringEnumConverter<UploadIndexStatus>))]
public enum UploadIndexStatus
{
    NotStarted,
    Indexing,
    Indexed,
    Failed
}

public sealed record UploadRecord(
    Guid Id,
    string FileName,
    string? ContentType,
    long SizeBytes,
    UploadIndexStatus Status,
    int ChunkCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? IndexedAtUtc);

public sealed record UploadPage(long TotalCount, IReadOnlyList<UploadRecord> Items);

public sealed record UploadDownload(Stream Content, string FileName, string ContentType);

public sealed class UploadTooLargeException(long maximumBytes)
    : InvalidOperationException($"The file exceeds the {maximumBytes:N0}-byte upload limit.");

public sealed class UploadService(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    BlobServiceClient blobService,
    SearchIndexClient indexClient,
    MarkItDownClient markItDown,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptions<UploadOptions> uploadOptions,
    IOptions<SearchOptions> searchOptions,
    ILogger<UploadService> logger)
{
    private readonly UploadOptions _uploads = uploadOptions.Value;
    private readonly SearchOptions _search = searchOptions.Value;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    private BlobContainerClient Container => blobService.GetBlobContainerClient(_uploads.ContainerName);
    private SearchClient Search => indexClient.GetSearchClient(_search.UploadIndexName);

    public async Task<UploadRecord> CreateAsync(
        string fileName,
        string? contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentException("The uploaded file is empty.", nameof(sizeBytes));
        }
        if (sizeBytes > _uploads.MaxFileBytes)
        {
            throw new UploadTooLargeException(_uploads.MaxFileBytes);
        }

        var id = Guid.NewGuid();
        var safeName = Path.GetFileName(fileName);
        var blobName = $"{id:N}/{safeName}";
        var now = DateTimeOffset.UtcNow;
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await Container.GetBlobClient(blobName).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            context.Uploads.Add(new UploadEntity
            {
                Id = id,
                FileName = safeName,
                BlobName = blobName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                Status = UploadIndexStatus.NotStarted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        return await IndexAsync(id, cancellationToken);
    }

    public async Task<UploadRecord?> ReindexAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Uploads.AnyAsync(x => x.Id == id, cancellationToken))
        {
            return null;
        }
        return await IndexAsync(id, cancellationToken);
    }

    public async Task<UploadPage> ListAsync(string? search, int skip, int top, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Uploads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.FileName.Contains(term));
        }
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip(skip).Take(top).ToListAsync(cancellationToken);
        return new UploadPage(total, [.. rows.Select(ToRecord)]);
    }

    public async Task<UploadDownload?> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.Uploads.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var response = await Container.GetBlobClient(row.BlobName).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return new UploadDownload(response.Value.Content, row.FileName, row.ContentType ?? "application/octet-stream");
    }

    public async Task<string> GetAttachmentContextAsync(
        IReadOnlyCollection<Guid> uploadIds,
        string query,
        CancellationToken cancellationToken)
    {
        if (uploadIds.Count == 0)
        {
            return "";
        }

        var distinctIds = uploadIds.Distinct().ToArray();
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var ready = await context.Uploads.AsNoTracking()
                .Where(x => distinctIds.Contains(x.Id) && x.Status == UploadIndexStatus.Indexed)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            if (ready.Count != distinctIds.Length)
            {
                throw new InvalidOperationException("Every attachment must be indexed successfully before it can be sent.");
            }
        }

        await EnsureInfrastructureAsync(cancellationToken);
        var values = string.Join(',', distinctIds.Select(x => x.ToString("D")));
        var vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        var options = new Azure.Search.Documents.SearchOptions
        {
            Filter = $"search.in(uploadId, '{values}', ',')",
            Size = 16,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(vector) { KNearestNeighborsCount = 16, Fields = { "contentVector" } } }
            }
        };
        options.Select.Add("uploadId");
        options.Select.Add("name");
        options.Select.Add("chunkNumber");
        options.Select.Add("content");

        var response = await Search.SearchAsync<UploadChunkDocument>(query, options, cancellationToken);
        var excerpts = new List<string>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            excerpts.Add($"### {result.Document.Name} (chunk {result.Document.ChunkNumber})\n{result.Document.Content}");
        }
        return string.Join("\n\n", excerpts);
    }

    private async Task<UploadRecord> IndexAsync(Guid id, CancellationToken cancellationToken)
    {
        UploadEntity row;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            row = await context.Uploads.SingleAsync(x => x.Id == id, cancellationToken);
            row.Status = UploadIndexStatus.Indexing;
            row.ErrorMessage = null;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await EnsureInfrastructureAsync(cancellationToken);
            var blob = await Container.GetBlobClient(row.BlobName).DownloadContentAsync(cancellationToken);
            var bytes = blob.Value.Content.ToArray();
            var markdown = await markItDown.ConvertAsync(row.FileName, bytes, row.ContentType, cancellationToken);
            var texts = TextChunker.Split(markdown, _uploads.ChunkSizeCharacters, _uploads.ChunkOverlapCharacters);
            var documents = new List<UploadChunkDocument>(texts.Count);
            for (var index = 0; index < texts.Count; index++)
            {
                var vector = await embeddings.GenerateVectorAsync(texts[index], cancellationToken: cancellationToken);
                documents.Add(new UploadChunkDocument
                {
                    Id = SearchChunkKey.For("upload", id.ToString("D"), index),
                    UploadId = id.ToString("D"),
                    Name = row.FileName,
                    MimeType = row.ContentType,
                    ChunkNumber = index,
                    Content = texts[index],
                    ContentVector = vector.ToArray(),
                });
            }

            await DeleteIndexDocumentsAsync(id, cancellationToken);
            if (documents.Count > 0)
            {
                foreach (var batch in documents.Chunk(1000))
                {
                    await Search.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
                }
            }
            await SetOutcomeAsync(id, UploadIndexStatus.Indexed, documents.Count, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing upload {UploadId} ({FileName}) failed.", id, row.FileName);
            await SetOutcomeAsync(id, UploadIndexStatus.Failed, 0, ex.Message, cancellationToken);
        }

        await using var resultContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return ToRecord(await resultContext.Uploads.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken));
    }

    private async Task SetOutcomeAsync(Guid id, UploadIndexStatus status, int chunks, string? error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Uploads.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ChunkCount, chunks)
            .SetProperty(x => x.ErrorMessage, error == null ? null : error.Length <= 4000 ? error : error[..4000])
            .SetProperty(x => x.UpdatedAtUtc, now)
            .SetProperty(x => x.IndexedAtUtc, status == UploadIndexStatus.Indexed ? now : null), cancellationToken);
    }

    private async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var fields = new List<SearchField>
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("uploadId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("name") { IsFilterable = true },
                new SimpleField("mimeType", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("chunkNumber", SearchFieldDataType.Int32) { IsSortable = true },
                new SearchableField("content"),
                new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = _search.VectorDimensions,
                    VectorSearchProfileName = "content-vector-profile"
                }
            };
            var definition = new SearchIndex(_search.UploadIndexName, fields)
            {
                VectorSearch = new VectorSearch
                {
                    Algorithms = { new HnswAlgorithmConfiguration("content-hnsw") },
                    Profiles = { new VectorSearchProfile("content-vector-profile", "content-hnsw") }
                }
            };
            await indexClient.CreateOrUpdateIndexAsync(
                definition,
                allowIndexDowntime: false,
                cancellationToken: cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task DeleteIndexDocumentsAsync(Guid id, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        for (var skip = 0; ; skip += 1000)
        {
            var response = await Search.SearchAsync<UploadChunkDocument>("*", new Azure.Search.Documents.SearchOptions
            {
                Filter = $"uploadId eq '{id:D}'",
                Size = 1000,
                Skip = skip,
                Select = { "id" }
            }, cancellationToken);
            var pageCount = 0;
            await foreach (var result in response.Value.GetResultsAsync())
            {
                ids.Add(result.Document.Id);
                pageCount++;
            }
            if (pageCount < 1000) break;
        }
        foreach (var batch in ids.Chunk(1000))
        {
            await Search.DeleteDocumentsAsync("id", batch, cancellationToken: cancellationToken);
        }
    }

    private static UploadRecord ToRecord(UploadEntity row) => new(
        row.Id, row.FileName, row.ContentType, row.SizeBytes, row.Status, row.ChunkCount,
        row.ErrorMessage, row.CreatedAtUtc, row.UpdatedAtUtc, row.IndexedAtUtc);
}

public sealed class UploadChunkDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("uploadId")] public string UploadId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("chunkNumber")] public int ChunkNumber { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("contentVector")] public IReadOnlyList<float> ContentVector { get; init; } = [];
}
