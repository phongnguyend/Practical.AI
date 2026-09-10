using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// The officecli MCP server, as tools the chat agent can call. officecli works on files that are already
/// on this host, so it pairs with <see cref="SharePointFileCache"/>: the download tool produces a
/// local path, and these tools read and edit the .docx, .xlsx, or .pptx file at it.
/// <para>
/// The server is a child process speaking MCP over stdio. It is started by the first turn that asks for
/// its tools and then shared by every turn after, because starting a process per turn would add its
/// startup to every answer. A server that cannot be started is logged once and the agent runs with its
/// own two tools alone, so a broken or missing officecli installation costs one turn rather than every
/// turn; the next attempt is after a restart.
/// </para>
/// </summary>
public sealed class OfficeCliToolProvider(
    IOptions<OfficeCliOptions> options,
    IOptions<DownloadOptions> downloadOptions,
    ILoggerFactory loggerFactory,
    ILogger<OfficeCliToolProvider> logger) : IAsyncDisposable
{
    private readonly OfficeCliOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AITool> _tools = [];
    private volatile bool _started;

    /// <summary>
    /// officecli's tools, or an empty list when it is disabled, unconfigured, or could not be started.
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.IsConfigured || _started)
        {
            return _tools;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return _tools;
            }

            _tools = await StartAsync();
            _started = true;
            return _tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AITool>> StartAsync()
    {
        // Deliberately not the caller's cancellation token: the server outlives the turn that starts it,
        // so a request that is cancelled mid-handshake must not leave a half-started server behind for
        // every turn after it. The startup timeout bounds the wait instead.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));

        try
        {
            // The child process runs in the download directory, so a relative path in an officecli
            // command lands among the downloaded files rather than in the application's own directory.
            var workingDirectory = downloadOptions.Value.ResolvedDirectory;
            Directory.CreateDirectory(workingDirectory);

            var arguments = _options.ResolvedArguments;
            logger.LogInformation("Starting the officecli MCP server: {Command} {Arguments}.", _options.Command, string.Join(' ', arguments));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "officecli",
                Command = _options.Command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
            }, loggerFactory);

            _client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: timeout.Token);
            var tools = await _client.ListToolsAsync(cancellationToken: timeout.Token);

            logger.LogInformation(
                "officecli gave the chat agent {Count} tool(s): {Tools}.",
                tools.Count,
                string.Join(", ", tools.Select(x => x.Name)));

            return [.. tools];
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The officecli MCP server could not be started with '{Command}'. The chat assistant will answer and download without editing until the application is restarted; set OfficeCli:Command to the executable, or OfficeCli:Enabled to false to stop trying.",
                _options.Command);

            await DisposeClientAsync();
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _gate.Dispose();
    }

    /// <summary>Shuts the child process down, whether it was started or failed halfway.</summary>
    private async ValueTask DisposeClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The officecli MCP server did not shut down cleanly.");
        }
    }
}
