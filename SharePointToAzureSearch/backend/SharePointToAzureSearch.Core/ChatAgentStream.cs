using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SharePointToAzureSearch.Core;

/// <summary>The private host-to-API stream. The API still owns browser events and SQL answer writes.</summary>
public sealed record ChatAgentEvent(string Type, string? Text = null, string? Message = null, ChatTurn? Turn = null);

/// <summary>Serializes and flushes complete NDJSON records, including concurrent tool callbacks.</summary>
public sealed class ChatStreamWriter<T>(HttpResponse response) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Start()
    {
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public async ValueTask WriteAsync(T item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await JsonSerializer.SerializeAsync(response.Body, item, Json, cancellationToken);
            await response.WriteAsync("\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
