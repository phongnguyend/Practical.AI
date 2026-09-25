using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    MarkItDownClient markItDown,
    ILogger<ContentExtractor> logger) : IContentExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".sql" };

    private readonly DocumentIntelligenceOptions _options = options.Value;

    public async Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(item.Name);
        if (TextExtensions.Contains(extension))
        {
            return Encoding.UTF8.GetString(content);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractDocxAsync(item, content, cancellationToken);
        }

        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractPptxAsync(item, content, cancellationToken);
        }

        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractXlsxAsync(item, content, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return await documentIntelligence.ExtractAsync(content, cancellationToken);
        }

        logger.LogWarning("No Document Intelligence endpoint is configured; indexing metadata only for {FileName}.", item.Name);
        return $"File name: {item.Name}\nContent type: {item.MimeType}\nPath: {item.ParentPath}";
    }

    /// <summary>
    /// Converts the document to markdown, so headings, lists, and tables survive into the indexed text.
    /// </summary>
    private Task<string> ExtractDocxAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        markItDown.ConvertAsync(item, content, cancellationToken);

    /// <summary>
    /// Converts the presentation to markdown. MarkItDown emits one section per slide, in slide order.
    /// </summary>
    private Task<string> ExtractPptxAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        markItDown.ConvertAsync(item, content, cancellationToken);

    /// <summary>
    /// Converts the workbook to markdown. MarkItDown emits one markdown table per worksheet, with cells
    /// rendered as their displayed text rather than their stored value.
    /// </summary>
    private Task<string> ExtractXlsxAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        markItDown.ConvertAsync(item, content, cancellationToken);
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
            if (status == "succeeded")
            {
                return json.RootElement.GetProperty("analyzeResult").GetProperty("content").GetString() ?? "";
            }

            if (status is "failed" or "canceled")
            {
                throw new InvalidOperationException($"Document Intelligence analysis {status}: {json.RootElement}");
            }
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
        else
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey!);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException($"Document Intelligence returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }
}

/// <summary>
/// Converts a file to markdown with a MarkItDown HTTP service, such as the
/// <c>markitdown</c> container that exposes <c>POST /convert</c>.
/// </summary>
public sealed class MarkItDownClient(
    HttpClient httpClient,
    IOptions<MarkItDownOptions> options)
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    private readonly MarkItDownOptions _options = options.Value;

    public Task<string> ConvertAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken) =>
        ConvertAsync(item.Name, content, item.MimeType, cancellationToken);

    /// <summary>
    /// Posts the file as <c>multipart/form-data</c> and returns the markdown from the response body. The
    /// file name travels with the file part, because MarkItDown selects its converter from the extension.
    /// </summary>
    public async Task<string> ConvertAsync(string fileName, byte[] content, string? mimeType, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(fileName), "name");

        using var response = await httpClient.PostAsync(BuildUrl(_options.ConvertPath), form, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Probes the service's health endpoint. Returns normally when it answers with a success status and
    /// throws otherwise, so a caller can log why the service is unavailable. The probe carries its own
    /// short timeout rather than the conversion timeout, which is minutes long.
    /// </summary>
    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HealthTimeout);
        using var response = await httpClient.GetAsync(BuildUrl(_options.HealthPath), timeout.Token);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private string BuildUrl(string path) => $"{_options.Endpoint!.TrimEnd('/')}/{path.TrimStart('/')}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException($"MarkItDown returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }
}

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int size, int overlap)
    {
        if (overlap >= size)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be smaller than chunk size.");
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return [""];
        }

        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += size - overlap)
        {
            var length = Math.Min(size, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length)
            {
                break;
            }
        }
        return chunks;
    }
}
