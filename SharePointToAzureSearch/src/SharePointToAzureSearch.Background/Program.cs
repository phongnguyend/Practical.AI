using SharePointToAzureSearch.Background;
using SharePointToAzureSearch.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddChangeProcessorServices(builder.Configuration);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
