using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharePointToAzureSearch.Core;
using Xunit;

namespace SharePointToAzureSearch.Tests;

public sealed class ChatAgentRegistrationTests
{
    [Theory]
    [InlineData("Local", typeof(ChatAgentService))]
    [InlineData("Foundry", typeof(FoundryChatAgentExecutor))]
    public async Task SelectsExecutorWithoutRequiringLocalOfficeToolsInRemoteMode(string mode, Type expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ChatAgent:Mode"] = mode,
            ["ChatAgent:Foundry:Endpoint"] = "https://example.com/invocations?api-version=v1",
            ["SqlServer:ConnectionString"] = "Server=unused;Database=unused;Integrated Security=true",
            ["SqlServer:AutoMigrate"] = "false",
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com",
            ["AzureOpenAI:ApiKey"] = "test",
            ["AzureSearch:Endpoint"] = "https://example.search.windows.net",
            ["AzureSearch:ApiKey"] = "test",
            ["SharePoint:TenantId"] = Guid.NewGuid().ToString(),
            ["SharePoint:ClientId"] = Guid.NewGuid().ToString(),
            ["SharePoint:ClientSecret"] = "test",
            ["SharePoint:SiteHostname"] = "example.sharepoint.com",
            ["SharePoint:SitePath"] = "/sites/test",
            ["SharePoint:DocumentLibraryName"] = "Documents",
            ["SharePoint:NotificationUrl"] = "https://example.com/webhook",
            ["SharePoint:ClientState"] = "test-client-state",
            ["Uploads:ConnectionString"] = "UseDevelopmentStorage=true",
            ["MarkItDown:Endpoint"] = "http://localhost:8000",
            ["OfficeCli:Enabled"] = "false",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (mode == "Local")
        {
            services.AddWebhookServices(configuration);
            services.AddSearchQueryServices(configuration);
            services.AddAttachmentFileServices(configuration);
        }
        services.AddChatServices(configuration);
        await using var provider = services.BuildServiceProvider();
        Assert.IsType(expected, provider.GetRequiredService<IChatAgentExecutor>());
        if (mode == "Foundry") Assert.Null(provider.GetService<OfficeCliToolProvider>());
    }

    [Theory]
    [InlineData("https://example.com/invocations", false, true)]
    [InlineData("http://localhost:8088/invocations", true, true)]
    [InlineData("https://example.com/invocations", true, false)]
    [InlineData("http://example.com/invocations", false, false)]
    [InlineData("https://example.com/invocations?agent_session_id=shared", false, false)]
    [InlineData("", false, false)]
    public void ValidatesRemoteEndpointAndDevelopmentAuthentication(string endpoint, bool unauthenticated, bool valid)
    {
        var options = new ChatAgentHostingOptions
        {
            Mode = ChatAgentExecutionMode.Foundry,
            Foundry = new() { Endpoint = endpoint, AllowUnauthenticatedLocalhost = unauthenticated },
        };
        Assert.Equal(valid, Validator.TryValidateObject(options, new(options), [], validateAllProperties: true));
    }
}
