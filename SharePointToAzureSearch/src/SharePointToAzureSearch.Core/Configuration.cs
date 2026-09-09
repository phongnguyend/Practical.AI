using System.ComponentModel.DataAnnotations;

namespace SharePointToAzureSearch.Core;

public sealed class SharePointOptions
{
    public const string SectionName = "SharePoint";
    [Required] public string TenantId { get; set; } = "";
    [Required] public string ClientId { get; set; } = "";
    [Required] public string ClientSecret { get; set; } = "";

    [Required] public string SiteHostname { get; set; } = "";

    [Required] public string SitePath { get; set; } = "";

    [Required] public string DocumentLibraryName { get; set; } = "";

    public bool SubscriptionRenewalEnabled { get; set; } = true;
    [Url] public string NotificationUrl { get; set; } = "";
    [Required, MinLength(16)] public string ClientState { get; set; } = "";
    [Range(1, 29)] public int SubscriptionLifetimeDays { get; set; } = 28;
    [Range(1, 24)] public int RenewalCheckHours { get; set; } = 12;
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Whether Service Bus is available to this application. When false no client is registered and no
    /// connection settings are required, so features that depend on Service Bus must stay disabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public bool UsedManagedIdentity { get; set; }
    public string? FullyQualifiedNamespace { get; set; }
    public string? ConnectionString { get; set; }
    [Required] public string TopicName { get; set; } = "sharepoint-changes";
    [Required] public string SubscriptionName { get; set; } = "search-indexer";

    /// <summary>
    /// True when the settings required to create a Service Bus client are present.
    /// </summary>
    public bool IsConfigured => UsedManagedIdentity
        ? !string.IsNullOrWhiteSpace(FullyQualifiedNamespace)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class SearchOptions
{
    public const string SectionName = "AzureSearch";
    public bool UsedManagedIdentity { get; set; }
    [Required, Url] public string Endpoint { get; set; } = "";
    public string? ApiKey { get; set; }
    [Required] public string IndexName { get; set; } = "sharepoint-files";
    [Range(1, 4096)] public int VectorDimensions { get; set; } = 1536;
}

public sealed class OpenAiOptions
{
    public const string SectionName = "AzureOpenAI";
    public bool UsedManagedIdentity { get; set; }
    [Required, Url] public string Endpoint { get; set; } = "";
    [Required] public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
    public string? ApiKey { get; set; }
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public bool UsedManagedIdentity { get; set; }
    [Url] public string? ServiceUri { get; set; }
    public string? ConnectionString { get; set; }
    [Required] public string ContainerName { get; set; } = "sharepoint-search-state";
}

public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";
    public bool UsedManagedIdentity { get; set; }
    public string? Endpoint { get; set; }
    public string ModelId { get; set; } = "prebuilt-read";
    public string ApiVersion { get; set; } = "2024-11-30";
    public string? ApiKey { get; set; }
}

public sealed class MarkItDownOptions
{
    public const string SectionName = "MarkItDown";

    /// <summary>
    /// Base address of the MarkItDown service, for example <c>http://localhost:8000</c>. Required, because
    /// DOCX, PPTX, and XLSX files are converted there.
    /// </summary>
    [Url] public string? Endpoint { get; set; }

    public string ConvertPath { get; set; } = "/convert";
    [Required] public string HealthPath { get; set; } = "/health";
    [Range(1, 3600)] public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the worker probes <see cref="HealthPath"/> as it starts and on an interval, so an
    /// unreachable service is reported before the next file needs converting.
    /// </summary>
    public bool HealthCheckEnabled { get; set; } = true;

    [Range(1, 1440)] public int HealthCheckMinutes { get; set; } = 5;

    /// <summary>
    /// True when an endpoint is configured, so the client can be called.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

public sealed class ProcessorOptions
{
    public const string SectionName = "Processor";
    [Range(1024, 104_857_600)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    [Range(100, 8000)] public int ChunkSizeCharacters { get; set; } = 4000;
    [Range(0, 2000)] public int ChunkOverlapCharacters { get; set; } = 400;
    public bool SyncOnStartup { get; set; } = true;
    public bool ChangeSignalListenerEnabled { get; set; } = true;
    public bool ScheduledSyncEnabled { get; set; } = true;
    [Range(1, 1440)] public int ScheduledSyncMinutes { get; set; } = 5;

    /// <summary>
    /// File extensions eligible for indexing; files with any other extension are skipped. Entries are
    /// matched case-insensitively, with or without a leading dot.
    /// </summary>
    public IList<string> AllowedFileExtensions { get; set; } = [];
}
