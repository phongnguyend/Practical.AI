using SharePointToAzureSearch.Background;
using SharePointToAzureSearch.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddChangeProcessorServices(builder.Configuration);
builder.Services.AddHostedService<SubscriptionRenewalBackgroundService>();
builder.Services.AddHostedService<ChangeSignalListenerBackgroundService>();
builder.Services.AddHostedService<ScheduledSyncBackgroundService>();

await builder.Build().RunAsync();
