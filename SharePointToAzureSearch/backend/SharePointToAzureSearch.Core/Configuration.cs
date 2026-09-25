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
    [Required] public string SharePointIndexName { get; set; } = "sharepoint-files";
    [Required] public string UploadIndexName { get; set; } = "chat-uploads";
    [Range(1, 4096)] public int VectorDimensions { get; set; } = 1536;
}

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public bool UsedManagedIdentity { get; set; }
    public string? ConnectionString { get; set; }
    public string? ServiceUri { get; set; }
    [Required] public string ContainerName { get; set; } = "chat-uploads";
    [Range(1024, 209_715_200)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    [Range(100, 8000)] public int ChunkSizeCharacters { get; set; } = 4000;
    [Range(0, 2000)] public int ChunkOverlapCharacters { get; set; } = 400;

    public bool IsConfigured => UsedManagedIdentity
        ? Uri.TryCreate(ServiceUri, UriKind.Absolute, out _)
        : !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed class OpenAiOptions
{
    public const string SectionName = "AzureOpenAI";
    public bool UsedManagedIdentity { get; set; }
    [Required, Url] public string Endpoint { get; set; } = "";
    [Required] public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// The chat deployment the assistant runs on, on the same resource as
    /// <see cref="EmbeddingDeployment"/>.
    /// </summary>
    [Required] public string ChatDeployment { get; set; } = "gpt-5-mini";

    public string? ApiKey { get; set; }
}

/// <summary>
/// The SQL Server database the worker keeps its own state in: the Microsoft Graph delta checkpoint, and a
/// record of what was last indexed for each SharePoint file. The record lets a delta pass tell an
/// unchanged file from a changed one, so an unchanged file is not downloaded, extracted, and embedded
/// again. Managed identity is expressed in the connection string — <c>Authentication=Active Directory
/// Default</c> — because SQL Server access is granted inside the database rather than by Azure RBAC.
/// </summary>
public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    [Required] public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Whether an application applies pending Entity Framework Core migrations as it starts. Set it to
    /// false when the schema is deployed by the pipeline and the application's login has no DDL rights.
    /// The schema itself is defined by <c>SharePointIndexDbContext</c>, not by configuration.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    [Range(1, 600)] public int CommandTimeoutSeconds { get; set; } = 30;
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

/// <summary>
/// Where the chat assistant's download tool puts the SharePoint files it fetches, and how large a file it
/// will fetch. The directory is a cache: a file already on disk is handed back as it is rather than
/// downloaded again.
/// </summary>
public sealed class DownloadOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// Root directory for downloaded files. A relative path resolves against the process working
    /// directory. Empty — the default — puts them in <c>sharepoint-downloads</c> under the system
    /// temporary directory.
    /// </summary>
    public string Directory { get; set; } = "";

    [Range(1024, 209_715_200)] public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>The absolute root directory, whatever form <see cref="Directory"/> was configured in.</summary>
    public string ResolvedDirectory => Path.GetFullPath(string.IsNullOrWhiteSpace(Directory)
        ? Path.Combine(Path.GetTempPath(), "sharepoint-downloads")
        : Directory);
}

/// <summary>
/// The officecli MCP server the chat assistant edits Office files with. officecli is a local command line
/// over .docx, .xlsx, and .pptx files, run as a child process speaking MCP over stdio, so it only reaches
/// files that are already on this host — the ones the download tool put there.
/// </summary>
public sealed class OfficeCliOptions
{
    public const string SectionName = "OfficeCli";

    /// <summary>
    /// Whether the assistant gets officecli's tools at all. With this false, or with no
    /// <see cref="Command"/>, no child process is started and the assistant can search and download but
    /// not edit.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The officecli executable — a name to be found on <c>PATH</c>, as the npm package's shim is, or a
    /// full path to it.
    /// </summary>
    public string Command { get; set; } = "officecli";

    /// <summary>
    /// Arguments that put officecli into MCP server mode. Empty — the default — means
    /// <see cref="DefaultArguments"/>.
    /// <para>
    /// It must stay empty here rather than carrying the default value: the configuration binder adds
    /// configured entries to a collection instead of replacing it, so a property initialized to
    /// <c>["mcp"]</c> and configured as <c>[ "mcp" ]</c> would run <c>officecli mcp mcp</c>, where the
    /// second <c>mcp</c> is read as the name of an editor to register officecli with.
    /// </para>
    /// </summary>
    public IList<string> Arguments { get; set; } = [];

    /// <summary>The verb that runs officecli as an MCP server over stdio.</summary>
    public static readonly string[] DefaultArguments = ["mcp"];

    /// <summary>
    /// The arguments to start the server with: the configured ones, or <see cref="DefaultArguments"/>
    /// when none are configured.
    /// </summary>
    public string[] ResolvedArguments =>
        Arguments.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() is { Length: > 0 } configured
            ? configured
            : DefaultArguments;

    /// <summary>
    /// How long the server has to start and list its tools. It is spent once, on the first turn that
    /// needs the tools, not on every turn.
    /// </summary>
    [Range(1, 600)] public int StartupTimeoutSeconds { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Command);
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
