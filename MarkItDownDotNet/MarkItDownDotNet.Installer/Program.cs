using CSnakes.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

string userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

var builder = Host.CreateApplicationBuilder(args);

var home = Path.Join(Environment.CurrentDirectory, "."); /* Path to your Python modules */

builder.Services
    .WithPython()
    .WithHome(home)
    .FromNuGet("3.12.4")
    .WithVirtualEnvironment(Path.Combine(userProfileDirectory, "MarkItDownDotNet", ".venv"))
    .WithPipInstaller();

var app = builder.Build();

var env = app.Services.GetRequiredService<IPythonEnvironment>();