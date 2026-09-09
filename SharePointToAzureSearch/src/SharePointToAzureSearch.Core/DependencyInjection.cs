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
        AddSharePointOptions(services, configuration);
        AddServiceBusOptions(services, configuration, required: true);
        services.AddMemoryCache();
        services.AddSingleton<GraphApiClient>();
        AddServiceBusClient(services);
        services.AddSingleton<IChangeSignalPublisher, ServiceBusChangeSignalPublisher>();
        return services;
    }

    /// <summary>
    /// Adds the full-text, vector, and hybrid query pipeline. Requires <see cref="AddWebhookServices"/> (or
    /// another registration that supplies <see cref="GraphApiClient"/>) for the permission filter.
    /// </summary>
    public static IServiceCollection AddSearchQueryServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddSearchOptions(services, configuration);
        AddOpenAiOptions(services, configuration);
        services.AddHttpClient<AzureOpenAiEmbeddingClient>();
        services.AddSingleton<IEmbeddingClient, AzureOpenAiEmbeddingClient>();
        AddSearchClient(services);
        services.AddSingleton<ISearchQueryStore, AzureSearchQueryStore>();
        return services;
    }

    /// <summary>
    /// Reads <c>ServiceBus:Enabled</c> straight from configuration, before the options system is available,
    /// so Service Bus clients and the features that consume them are registered together.
    /// </summary>
    public static bool IsServiceBusEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{ServiceBusOptions.SectionName}:{nameof(ServiceBusOptions.Enabled)}", true);

    /// <summary>
    /// Reads <c>Processor:ChangeSignalListenerEnabled</c> the same way. The listener also needs Service Bus,
    /// so it only runs when <see cref="IsServiceBusEnabled"/> is true as well.
    /// </summary>
    public static bool IsChangeSignalListenerEnabled(this IConfiguration configuration) =>
        configuration.GetValue($"{ProcessorOptions.SectionName}:{nameof(ProcessorOptions.ChangeSignalListenerEnabled)}", true)
        && configuration.IsServiceBusEnabled();

    public static IServiceCollection AddChangeProcessorServices(this IServiceCollection services, IConfiguration configuration)
    {
        var serviceBusEnabled = configuration.IsServiceBusEnabled();

        AddGraphClient(services);
        AddSharePointOptions(services, configuration);
        AddServiceBusOptions(services, configuration, required: false);
        AddSearchOptions(services, configuration);
        AddOpenAiOptions(services, configuration);
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity ? !string.IsNullOrWhiteSpace(o.ServiceUri) : !string.IsNullOrWhiteSpace(o.ConnectionString), "Storage:ServiceUri is required with managed identity; otherwise Storage:ConnectionString is required.").ValidateOnStart();
        services.AddOptions<DocumentIntelligenceOptions>().Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "DocumentIntelligence:ApiKey is required when an endpoint is configured and UsedManagedIdentity is false.").ValidateOnStart();
        services.AddOptions<ProcessorOptions>().Bind(configuration.GetSection(ProcessorOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.ChunkOverlapCharacters < o.ChunkSizeCharacters, "Chunk overlap must be smaller than chunk size.")
            .Validate(o => o.AllowedFileExtensions.Any(x => !string.IsNullOrWhiteSpace(x)), "Processor:AllowedFileExtensions must list at least one file extension.").ValidateOnStart();
        services.AddMemoryCache();
        services.AddSingleton<GraphApiClient>();
        services.AddHttpClient<DocumentIntelligenceClient>();
        services.AddHttpClient<AzureOpenAiEmbeddingClient>();
        services.AddSingleton<IContentExtractor, ContentExtractor>();
        services.AddSingleton<IEmbeddingClient, AzureOpenAiEmbeddingClient>();

        if (serviceBusEnabled)
        {
            AddServiceBusClient(services);
        }
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
        AddSearchClient(services);
        services.AddSingleton<ISearchIndexStore, AzureSearchIndexStore>();
        services.AddSingleton<ISharePointChangeProcessor, SharePointChangeProcessor>();
        return services;
    }

    internal static TokenCredential CreateManagedIdentityCredential() =>
        new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

    private static void AddSharePointOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SharePointOptions>().Bind(configuration.GetSection(SharePointOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => !o.SubscriptionRenewalEnabled || !string.IsNullOrWhiteSpace(o.NotificationUrl), "SharePoint:NotificationUrl is required when SubscriptionRenewalEnabled is true.").ValidateOnStart();
    }

    private static void AddSearchOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureSearch:ApiKey is required when UsedManagedIdentity is false.").ValidateOnStart();
    }

    private static void AddOpenAiOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAiOptions>().Bind(configuration.GetSection(OpenAiOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureOpenAI:ApiKey is required when UsedManagedIdentity is false.").ValidateOnStart();
    }

    private static void AddSearchClient(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
            return options.UsedManagedIdentity
                ? new SearchClient(new Uri(options.Endpoint), options.IndexName, CreateManagedIdentityCredential())
                : new SearchClient(new Uri(options.Endpoint), options.IndexName, new AzureKeyCredential(options.ApiKey!));
        });
    }

    /// <summary>
    /// Binds the Service Bus settings. Connection settings are only required when Service Bus is enabled;
    /// set <paramref name="required"/> for applications that cannot run without it.
    /// </summary>
    private static void AddServiceBusOptions(IServiceCollection services, IConfiguration configuration, bool required)
    {
        var options = services.AddOptions<ServiceBusOptions>().Bind(configuration.GetSection(ServiceBusOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => !o.Enabled || o.IsConfigured, "ServiceBus:FullyQualifiedNamespace is required with managed identity; otherwise ServiceBus:ConnectionString is required.");
        if (required)
        {
            options.Validate(o => o.Enabled, "ServiceBus:Enabled must be true; this application publishes SharePoint change signals to Service Bus.");
        }
        options.ValidateOnStart();
    }

    private static void AddServiceBusClient(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return options.UsedManagedIdentity
                ? new ServiceBusClient(options.FullyQualifiedNamespace!, CreateManagedIdentityCredential())
                : new ServiceBusClient(options.ConnectionString!);
        });
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
