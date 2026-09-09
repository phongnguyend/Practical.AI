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

    private static readonly XNamespace WordProcessing = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly DocumentIntelligenceOptions _options = options.Value;

    public async Task<string> ExtractAsync(DriveItemChange item, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(item.Name);
        if (TextExtensions.Contains(extension)) return Encoding.UTF8.GetString(content);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return ExtractDocx(content);
        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)) return ExtractPptx(content);
        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return ExtractXlsx(content);
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
        return string.Join(" ", document.Descendants(WordProcessing + "t").Select(x => x.Value));
    }

    /// <summary>
    /// Reads the drawing text of every slide, in slide order. Speaker notes live in separate parts and are
    /// deliberately not indexed.
    /// </summary>
    private static string ExtractPptx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var text = new List<string>();
        foreach (var slide in GetOrderedParts(archive, "ppt/slides/slide"))
        {
            using var xmlStream = slide.Open();
            var document = XDocument.Load(xmlStream);
            text.AddRange(document.Descendants(Drawing + "t").Select(x => x.Value));
        }
        return string.Join(" ", text);
    }

    /// <summary>
    /// Reads the cell values of every worksheet, in worksheet order. Numbers, dates, and cached formula
    /// results are indexed as the stored value, so a date arrives as its serial number rather than a
    /// formatted string.
    /// </summary>
    private static string ExtractXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sharedStrings = ReadSharedStrings(archive);
        var text = new List<string>();
        foreach (var sheet in GetOrderedParts(archive, "xl/worksheets/sheet"))
        {
            using var xmlStream = sheet.Open();
            var document = XDocument.Load(xmlStream);
            foreach (var cell in document.Descendants(Spreadsheet + "c"))
            {
                var value = ReadCellValue(cell, sharedStrings);
                if (!string.IsNullOrWhiteSpace(value)) text.Add(value);
            }
        }
        return string.Join(" ", text);
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var xmlStream = entry.Open();
        var document = XDocument.Load(xmlStream);
        return document.Descendants(Spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(x => x.Value)))
            .ToArray();
    }

    /// <summary>
    /// A cell typed <c>s</c> indexes the shared string table, <c>inlineStr</c> carries its own runs, and
    /// every other type stores its text in the value element.
    /// </summary>
    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "s")
        {
            return int.TryParse(cell.Element(Spreadsheet + "v")?.Value, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : "";
        }
        if (type == "inlineStr") return string.Concat(cell.Descendants(Spreadsheet + "t").Select(x => x.Value));
        return cell.Element(Spreadsheet + "v")?.Value ?? "";
    }

    /// <summary>
    /// Selects the parts whose name starts with <paramref name="prefix"/> and orders them by the number in
    /// the file name, so slide10 follows slide9 instead of slide1.
    /// </summary>
    private static IEnumerable<ZipArchiveEntry> GetOrderedParts(ZipArchive archive, string prefix) =>
        archive.Entries
            .Where(entry => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(GetPartNumber);

    private static int GetPartNumber(ZipArchiveEntry entry)
    {
        var name = Path.GetFileNameWithoutExtension(entry.FullName);
        var start = name.Length;
        while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
        return int.TryParse(name.AsSpan(start), out var number) ? number : int.MaxValue;
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
