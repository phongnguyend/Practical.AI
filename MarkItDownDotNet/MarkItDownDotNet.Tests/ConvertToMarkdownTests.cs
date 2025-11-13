using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace MarkItDownDotNet.Tests;

public class ConvertToMarkdownTests
{
    private readonly ITestOutputHelper _outputHelper;

    public ConvertToMarkdownTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    [Fact]
    public async Task ConvertToMarkdown()
    {
        var client = new HttpClient();

        var filePath = "C:\\Users\\Phong.NguyenDoan\\Downloads\\phongnguyend.pdf";

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(File.ReadAllBytes(filePath));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        form.Add(fileContent, "formFile", Path.GetFileName(filePath));
        form.Add(new StringContent("Test Name"), "name");

        var response = await client.PostAsync($"https://localhost:7110/", form);
        response.EnsureSuccessStatusCode();

        var createdFile = await response.Content.ReadAsStringAsync();

        _outputHelper.WriteLine(createdFile);

        
        // Assert
        Assert.NotNull(createdFile);
        //await Verify(createdFile);
    }
}