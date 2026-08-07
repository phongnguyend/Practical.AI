using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public interface IContentExtractor
{
    Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken);
}

public sealed class ContentExtractor(
    DocumentIntelligenceClient documentIntelligence,
    IOptions<DocumentIntelligenceOptions> options,
    ILogger<ContentExtractor> logger) : IContentExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".sql" };
    private readonly DocumentIntelligenceOptions _options = options.Value;

    public async Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(item.Name);
        if (TextExtensions.Contains(extension)) return Encoding.UTF8.GetString(content);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return ExtractDocx(content);
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
            return await documentIntelligence.ExtractAsync(content, cancellationToken);

        logger.LogWarning("No Document Intelligence endpoint is configured; indexing metadata only for {FileName}.", item.Name);
        return $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
    }

    private static string ExtractDocx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("The DOCX document has no word/document.xml part.");
        using var xmlStream = entry.Open();
        var document = XDocument.Load(xmlStream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join(" ", document.Descendants(word + "t").Select(x => x.Value));
    }
}

public sealed class DocumentIntelligenceClient(
    HttpClient httpClient,
    IOptions<DocumentIntelligenceOptions> options)
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly DocumentIntelligenceOptions _options = options.Value;
    private readonly TokenCredential? _credential = options.Value.UsedManagedIdentity
        ? DependencyInjection.CreateManagedIdentityCredential()
        : null;

    public async Task<string> ExtractAsync(byte[] content, CancellationToken cancellationToken)
    {
        var endpoint = _options.Endpoint!.TrimEnd('/');
        var url = $"{endpoint}/documentintelligence/documentModels/{Uri.EscapeDataString(_options.ModelId)}:analyze?_overload=analyzeDocument&api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { base64Source = Convert.ToBase64String(content) })
        };
        await AuthorizeAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var operationUrl = response.Headers.Location?.ToString()
            ?? (response.Headers.TryGetValues("Operation-Location", out var values) ? values.Single() : throw new InvalidOperationException("Document Intelligence omitted Operation-Location."));

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operationUrl);
            await AuthorizeAsync(poll, cancellationToken);
            using var pollResponse = await httpClient.SendAsync(poll, cancellationToken);
            await EnsureSuccessAsync(pollResponse, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await pollResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var status = json.RootElement.GetProperty("status").GetString();
            if (status == "succeeded") return json.RootElement.GetProperty("analyzeResult").GetProperty("content").GetString() ?? "";
            if (status is "failed" or "canceled") throw new InvalidOperationException($"Document Intelligence analysis {status}: {json.RootElement}");
        }
        throw new TimeoutException("Document Intelligence analysis did not finish within two minutes.");
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.UsedManagedIdentity)
        {
            var token = await _credential!.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey!);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"Document Intelligence returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }
}

public interface IEmbeddingClient
{
    Task<IReadOnlyList<float>> CreateAsync(string text, CancellationToken cancellationToken);
}

public sealed class AzureOpenAiEmbeddingClient(
    HttpClient httpClient,
    IOptions<OpenAiOptions> options,
    IOptions<SearchOptions> searchOptions) : IEmbeddingClient
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly OpenAiOptions _options = options.Value;
    private readonly TokenCredential? _credential = options.Value.UsedManagedIdentity
        ? DependencyInjection.CreateManagedIdentityCredential()
        : null;
    private readonly int _dimensions = searchOptions.Value.VectorDimensions;

    public async Task<IReadOnlyList<float>> CreateAsync(string text, CancellationToken cancellationToken)
    {
        var url = $"{_options.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(_options.EmbeddingDeployment)}/embeddings?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { input = text, dimensions = _dimensions })
        };
        if (_options.UsedManagedIdentity)
        {
            var token = await _credential!.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else request.Headers.Add("api-key", _options.ApiKey!);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Azure OpenAI returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
}

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int size, int overlap)
    {
        if (overlap >= size) throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be smaller than chunk size.");
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return [""];
        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += size - overlap)
        {
            var length = Math.Min(size, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length) break;
        }
        return chunks;
    }
}
