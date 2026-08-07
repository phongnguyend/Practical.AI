using System.ComponentModel.DataAnnotations;

namespace SharePointToAzureSearch.Core;

public sealed class SharePointOptions
{
    public const string SectionName = "SharePoint";
    [Required] public string TenantId { get; set; } = "";
    [Required] public string ClientId { get; set; } = "";
    [Required] public string ClientSecret { get; set; } = "";
    [Required] public string DriveId { get; set; } = "";
    [Required, Url] public string NotificationUrl { get; set; } = "";
    [Required, MinLength(16)] public string ClientState { get; set; } = "";
    [Range(1, 29)] public int SubscriptionLifetimeDays { get; set; } = 28;
    [Range(1, 24)] public int RenewalCheckHours { get; set; } = 12;
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";
    public bool UsedManagedIdentity { get; set; }
    public string? FullyQualifiedNamespace { get; set; }
    public string? ConnectionString { get; set; }
    [Required] public string TopicName { get; set; } = "sharepoint-changes";
    [Required] public string SubscriptionName { get; set; } = "search-indexer";
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
    [Required] public string ApiVersion { get; set; } = "2024-10-21";
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

public sealed class ProcessorOptions
{
    public const string SectionName = "Processor";
    [Range(1024, 104_857_600)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    [Range(100, 8000)] public int ChunkSizeCharacters { get; set; } = 4000;
    [Range(0, 2000)] public int ChunkOverlapCharacters { get; set; } = 400;
    public bool SyncOnStartup { get; set; } = true;
}
