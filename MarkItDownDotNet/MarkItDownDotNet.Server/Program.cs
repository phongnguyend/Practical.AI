using CSnakes.Runtime;
using MarkItDownDotNet.Server;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services
    .WithPython()
    .WithHome(configuration["PythonModulesFolderPath"]!)
    .FromNuGet(configuration["PythonVersion"]!)
    .WithVirtualEnvironment(configuration["VirtualEnvironmentFolderPath"]!);

builder.Services.AddSingleton(sp => sp.GetRequiredService<IPythonEnvironment>().Md());

var app = builder.Build();

app.MapPost("/", (IMd markitdown, HttpContext context) =>
{
    string filePath = FileService.CreateTempFilePath();

    using (var stream = File.OpenWrite(filePath))
    {
        var formFile = context.Request.Form.Files["formFile"];
        formFile!.CopyTo(stream);
    }

    var text = markitdown.ConvertToMd(filePath);

    File.Delete(filePath);

    return Results.Text(text, "text/markdown");
});

app.Run();

