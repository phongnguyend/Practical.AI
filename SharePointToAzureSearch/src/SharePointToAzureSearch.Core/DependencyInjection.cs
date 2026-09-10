using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddWebhookServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddGraphClient(services);
        AddSharePointOptions(services, configuration);
        AddServiceBusOptions(services, configuration, required: true);
        services.AddMemoryCache();
        services.AddSingleton<SharePointClient>();
        services.AddSingleton<SubscriptionManager>();
        AddServiceBusClient(services);
        services.AddSingleton<IChangeSignalPublisher, ServiceBusChangeSignalPublisher>();
        return services;
    }

    /// <summary>
    /// Adds the full-text, vector, and hybrid query pipeline. Requires <see cref="AddWebhookServices"/> (or
    /// another registration that supplies <see cref="SharePointClient"/>) for the permission filter.
    /// </summary>
    public static IServiceCollection AddSearchQueryServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddSearchOptions(services, configuration);
        AddOpenAiOptions(services, configuration);
        AddEmbeddingGenerator(services);
        AddSearchClient(services);
        services.AddSingleton<ISearchQueryStore, AzureSearchQueryStore>();
        return services;
    }

    /// <summary>
    /// Adds the chat assistant: conversation storage in SQL Server, and an agent on the Azure OpenAI
    /// chat deployment that can search the index. Requires <see cref="AddSearchQueryServices"/> for the
    /// retrieval tool and <see cref="AddWebhookServices"/> for the permission filter behind it.
    /// </summary>
    public static IServiceCollection AddChatServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);
        AddOpenAiOptions(services, configuration);

        // The chat deployment sits on the same Azure OpenAI resource as the embedding model, so it is
        // reached through the same endpoint and credential.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value;
            var client = options.UsedManagedIdentity
                ? new AzureOpenAIClient(new Uri(options.Endpoint), CreateManagedIdentityCredential())
                : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey!));
            return client.GetChatClient(options.ChatDeployment);
        });

        services.AddSingleton<IChatStore, EfChatStore>();
        services.AddSingleton<ChatAgentService>();
        return services;
    }

    /// <summary>
    /// Adds read-only access to the worker's SQL Server state — the delta checkpoints and the indexed-file
    /// records — for operator-facing views. Nothing registered here writes to those tables.
    /// </summary>
    public static IServiceCollection AddIndexStateServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);
        services.AddSingleton<IIndexStateReader, EfIndexStateReader>();
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
        AddDatabase(services, configuration);
        services.AddOptions<DocumentIntelligenceOptions>().Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "DocumentIntelligence:ApiKey is required when an endpoint is configured and UsedManagedIdentity is false.").ValidateOnStart();
        services.AddOptions<MarkItDownOptions>().Bind(configuration.GetSection(MarkItDownOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.IsConfigured, "MarkItDown:Endpoint is required; DOCX, PPTX, and XLSX files are converted to markdown by the MarkItDown service.").ValidateOnStart();
        services.AddOptions<ProcessorOptions>().Bind(configuration.GetSection(ProcessorOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.ChunkOverlapCharacters < o.ChunkSizeCharacters, "Chunk overlap must be smaller than chunk size.")
            .Validate(o => o.AllowedFileExtensions.Any(x => !string.IsNullOrWhiteSpace(x)), "Processor:AllowedFileExtensions must list at least one file extension.").ValidateOnStart();
        services.AddMemoryCache();
        services.AddSingleton<SharePointClient>();
        services.AddSingleton<SubscriptionManager>();
        services.AddHttpClient<DocumentIntelligenceClient>();

        // Conversion of a large file is a single long request, so the client carries its own timeout
        // rather than the 100 second default.
        services.AddHttpClient<MarkItDownClient>((sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<MarkItDownOptions>>().Value.TimeoutSeconds));
        services.AddSingleton<IContentExtractor, ContentExtractor>();
        AddEmbeddingGenerator(services);

        if (serviceBusEnabled)
        {
            AddServiceBusClient(services);
        }
        services.AddSingleton<IDeltaStateStore, EfDeltaStateStore>();
        services.AddSingleton<IFileMetadataStore, EfFileMetadataStore>();
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

    /// <summary>
    /// Binds the SQL Server settings and registers the Entity Framework Core context, once per
    /// application however many features ask for it.
    /// <para>
    /// The context is pooled and handed out by an <see cref="IDbContextFactory{TContext}"/>, because the
    /// stores that use it are singletons shared by the worker's hosted services, which have no request
    /// scope of their own. A scoped <see cref="SharePointIndexDbContext"/> is registered alongside it so
    /// an API endpoint can inject the context directly and query with LINQ where a store method would be
    /// more than it needs; it comes from the same pool and returns to it with the request.
    /// </para>
    /// </summary>
    private static void AddDatabase(IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<SharePointIndexDbContext>)))
        {
            return;
        }

        services.AddOptions<SqlServerOptions>().Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddPooledDbContextFactory<SharePointIndexDbContext>((sp, builder) =>
        {
            var options = sp.GetRequiredService<IOptions<SqlServerOptions>>().Value;
            builder.UseSqlServer(options.ConnectionString, sql => sql.CommandTimeout(options.CommandTimeoutSeconds));
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<SharePointIndexDbContext>>().CreateDbContext());

        if (configuration.GetValue($"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.AutoMigrate)}", true))
        {
            services.AddHostedService<DatabaseMigrationHostedService>();
        }
    }

    private static void AddSearchOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureSearch:ApiKey is required when UsedManagedIdentity is false.").ValidateOnStart();
    }

    private static void AddOpenAiOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenAiOptions>().Bind(configuration.GetSection(OpenAiOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.UsedManagedIdentity || !string.IsNullOrWhiteSpace(o.ApiKey), "AzureOpenAI:ApiKey is required when UsedManagedIdentity is false.")
            .Validate(o => IsResourceRootEndpoint(o.Endpoint), "AzureOpenAI:Endpoint must be the resource endpoint without an API path, for example https://<resource>.services.ai.azure.com; the SDK appends the deployment path itself, so a base URL ending in /openai/v1 returns 404.").ValidateOnStart();
    }

    /// <summary>
    /// Registers the Azure OpenAI embedding generator. <c>AzureSearch:VectorDimensions</c> becomes the
    /// generator's default dimension count, so the index definition and the embeddings cannot drift apart.
    /// </summary>
    private static void AddEmbeddingGenerator(IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value;
            var dimensions = sp.GetRequiredService<IOptions<SearchOptions>>().Value.VectorDimensions;

            // The service version travels with the Azure OpenAI package rather than with configuration.
            var clientOptions = new AzureOpenAIClientOptions();
            var client = options.UsedManagedIdentity
                ? new AzureOpenAIClient(new Uri(options.Endpoint), CreateManagedIdentityCredential(), clientOptions)
                : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey!), clientOptions);
            return client.GetEmbeddingClient(options.EmbeddingDeployment).AsIEmbeddingGenerator(dimensions);
        });
    }

    /// <summary>
    /// True when the endpoint is the resource root the Azure OpenAI SDK expects. The SDK appends the
    /// deployment path itself, so a configured path such as the Foundry portal's <c>/openai/v1</c> base URL
    /// would be doubled into <c>/openai/v1/openai/deployments/...</c> and return 404 on every request.
    /// </summary>
    private static bool IsResourceRootEndpoint(string endpoint) =>
        !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.AbsolutePath.Trim('/').Length == 0;

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
