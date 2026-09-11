using Azure;
using Azure.AI.ContentUnderstanding;

string endpoint = "<endpoint>";
string apiKey = "<apiKey>";
var client = new ContentUnderstandingClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

// You can replace this URL with your own publicly accessible document URL.
Uri uriSource = new Uri("https://raw.githubusercontent.com/Azure-Samples/azure-ai-content-understanding-assets/main/document/mixed_financial_docs.pdf");

Operation<AnalysisResult> operation = await client.AnalyzeAsync(
    WaitUntil.Completed,
    "prebuilt-documentSearch",
    inputs: new[]
    {
        new AnalysisInput
        {
            Uri = uriSource
        }
    });

AnalysisResult result = operation.Value;
AnalysisContent content = result.Contents!.First();
Console.WriteLine("Markdown:");
Console.WriteLine(content.Markdown);

// Cast AnalysisContent to DocumentContent to access document-specific properties
// DocumentContent derives from AnalysisContent and provides additional properties
// to access full information about document, including Pages, Tables and many others
DocumentContent documentContent = (DocumentContent)content;
Console.WriteLine($"Pages: {documentContent.StartPageNumber} - {documentContent.EndPageNumber}");

// Check for pages
if (documentContent.Pages != null && documentContent.Pages.Count > 0)
{
    Console.WriteLine($"Number of pages: {documentContent.Pages.Count}");
    foreach (var page in documentContent.Pages)
    {
        var unit = documentContent.Unit?.ToString() ?? "units";
        Console.WriteLine($"  Page {page.PageNumber}: {page.Width} x {page.Height} {unit}");
    }
}