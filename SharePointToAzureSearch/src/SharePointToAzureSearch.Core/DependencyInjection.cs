using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace SharePointToAzureSearch.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddWebhookServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddGraphClient(services);
        services.AddOptions<SharePointOptions>().Bind(configuration.GetSection(SharePointOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        AddServiceBusOptions(services, configuration);
        services.AddSingleton<GraphApiClient>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return options.UsedManagedIdentity
                ? new ServiceBusClient(options.FullyQualifiedNamespace!, CreateManagedIdentityCredential())
                : new ServiceBusClient(options.ConnectionString!);
        });
        services.AddSingleton<IChangeSignalPublisher, ServiceBusChangeSignalPublisher>();
        services.AddHostedService<SubscriptionRenewalService>();
        return services;
    }

    public static IServiceCollection AddChangeProcessorServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddGraphClient(services);
        services.AddOptions<SharePointOptions>().Bind(configuration.GetSection(SharePointOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        AddServiceBusOptions(services, configuration);
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureSearch:ApiKey is required when UsedManagedIdentity is false.").ValidateOnStart();
        services.AddOptions<OpenAiOptions>().Bind(configuration.GetSection(OpenAiOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureOpenAI:ApiKey is required when UsedManagedIdentity is false.").ValidateOnStart();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity ? !string.IsNullOrWhiteSpace(o.ServiceUri) : !string.IsNullOrWhiteSpace(o.ConnectionString), "Storage:ServiceUri is required with managed identity; otherwise Storage:ConnectionString is required.").ValidateOnStart();
        services.AddOptions<DocumentIntelligenceOptions>().Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "DocumentIntelligence:ApiKey is required when an endpoint is configured and UsedManagedIdentity is false.").ValidateOnStart();
        services.AddOptions<ProcessorOptions>().Bind(configuration.GetSection(ProcessorOptions.SectionName)).ValidateDataAnnotations().Validate(o => o.ChunkOverlapCharacters < o.ChunkSizeCharacters, "Chunk overlap must be smaller than chunk size.").ValidateOnStart();
        services.AddSingleton<GraphApiClient>();
        services.AddHttpClient<DocumentIntelligenceClient>();
        services.AddHttpClient<AzureOpenAiEmbeddingClient>();
        services.AddSingleton<IContentExtractor, ContentExtractor>();
        services.AddSingleton<IEmbeddingClient, AzureOpenAiEmbeddingClient>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return options.UsedManagedIdentity
                ? new ServiceBusClient(options.FullyQualifiedNamespace!, CreateManagedIdentityCredential())
                : new ServiceBusClient(options.ConnectionString!);
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            return options.UsedManagedIdentity
                ? new BlobContainerClient(new Uri($"{options.ServiceUri!.TrimEnd('/')}/{options.ContainerName}"), CreateManagedIdentityCredential())
                : new BlobContainerClient(options.ConnectionString!, options.ContainerName);
        });
        services.AddSingleton<IDeltaStateStore, BlobDeltaStateStore>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
            return options.UsedManagedIdentity
                ? new SearchIndexClient(new Uri(options.Endpoint), CreateManagedIdentityCredential())
                : new SearchIndexClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey!));
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
            return options.UsedManagedIdentity
                ? new SearchClient(new Uri(options.Endpoint), options.IndexName, CreateManagedIdentityCredential())
                : new SearchClient(new Uri(options.Endpoint), options.IndexName, new AzureKeyCredential(options.ApiKey!));
        });
        services.AddSingleton<ISearchIndexStore, AzureSearchIndexStore>();
        services.AddSingleton<ISharePointChangeProcessor, SharePointChangeProcessor>();
        return services;
    }

    internal static TokenCredential CreateManagedIdentityCredential() =>
        new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

    private static void AddServiceBusOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceBusOptions>().Bind(configuration.GetSection(ServiceBusOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity ? !string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace) : !string.IsNullOrWhiteSpace(o.ConnectionString), "ServiceBus:FullyQualifiedNamespace is required with managed identity; otherwise ServiceBus:ConnectionString is required.").ValidateOnStart();
    }

    private static void AddGraphClient(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SharePointOptions>>().Value;
            var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
            return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        });
    }
}
