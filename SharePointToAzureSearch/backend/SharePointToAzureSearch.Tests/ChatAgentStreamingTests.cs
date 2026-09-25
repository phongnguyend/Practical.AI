using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.AI.AgentServer.Invocations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using SharePointToAzureSearch.AgentHost;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;
using SharePointToAzureSearch.Infrastructure;
using Xunit;

namespace SharePointToAzureSearch.Tests;

public sealed class ChatAgentStreamingTests
{
    private static readonly ChatTurn Turn = new("Answer", [new("File", null, "https://example.com/file", 1, 0.9)], new(10, 5, 15), "model");

    [Fact]
    public async Task ActualInvocationsHostStreamsStatusBeforeCompletionAndPreservesTurnMetadata()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ChatAgentRequest(Guid.NewGuid(), Guid.NewGuid());
        var executor = new StubExecutor(async (input, text, status, ct) =>
        {
            Assert.Equal(request, input);
            await status("Searching documents…", ct);
            await release.Task.WaitAsync(ct);
            await text("Answer", ct);
            return Turn;
        });
        await using var server = await CreateServer(executor);
        using var http = server.GetTestClient();
        var sessions = EmptySessions();
        var proxy = new FoundryChatAgentExecutor(http, new TestCredential(), sessions, Options.Create(new ChatAgentHostingOptions
        {
            Mode = ChatAgentExecutionMode.Foundry,
            Foundry = new() { Endpoint = "http://localhost/invocations" },
        }));
        var textReceived = new StringBuilder();
        var running = proxy.RunStreamingAsync(request,
            (text, ct) => { textReceived.Append(text); return ValueTask.CompletedTask; },
            (status, ct) => { Assert.Equal("Searching documents…", status); observed.SetResult(); return ValueTask.CompletedTask; }, default);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Turn.Text, result.Text);
        Assert.Equal(Turn.Citations, result.Citations);
        Assert.Equal(Turn.Usage, result.Usage);
        Assert.Equal(Turn.ModelId, result.ModelId);
        Assert.Equal("Answer", textReceived.ToString());
        await sessions.Received(1).SaveAsync(request.ConversationId, "http://localhost/invocations", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsOnlyIdentifiersAndReusesSavedSandboxWithBearerAuthentication()
    {
        var request = new ChatAgentRequest(Guid.NewGuid(), Guid.NewGuid());
        var sessions = Substitute.For<IFoundrySessionRepository>();
        const string endpoint = "https://example.com/invocations?api-version=v1";
        sessions.GetAsync(request.ConversationId, endpoint, Arg.Any<CancellationToken>()).Returns("saved-session");
        using var http = new HttpClient(new StubHttpHandler(async (message, ct) =>
        {
            Assert.Contains("api-version=v1&agent_session_id=saved-session", message.RequestUri!.Query);
            Assert.Equal("Bearer test-token", message.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal(request.QuestionId, json.RootElement.GetProperty("questionId").GetGuid());
            return Response(new("completed", Turn: Turn), "saved-session");
        }));
        var credential = new TestCredential();
        var result = await Proxy(http, sessions, endpoint, credential).RunStreamingAsync(request, Ignore, Ignore, default);
        Assert.Equal(Turn.Text, result.Text);
        Assert.Equal("https://ai.azure.com/.default", Assert.Single(credential.Scopes!));
    }

    [Theory]
    [InlineData("{\"type\":\"status\",\"message\":\"Working\"}\n", typeof(EndOfStreamException))]
    [InlineData("{\"type\":\"error\",\"message\":\"Tool failed\"}\n", typeof(InvalidOperationException))]
    [InlineData("{\"type\":\"completed\"}\n", typeof(InvalidDataException))]
    [InlineData("not-json\n", typeof(JsonException))]
    public async Task FailedOrTruncatedStreamsNeverReturnACompletedTurn(string body, Type errorType)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
            response.Headers.Add("x-agent-session-id", "session");
            return Task.FromResult(response);
        }));
        var error = await Record.ExceptionAsync(() => Proxy(http).RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, default));
        Assert.IsAssignableFrom(errorType, error);
        Assert.Equal(1, calls); // No automatic replay of file edits/uploads.
    }

    [Fact]
    public async Task CallerCancellationReachesTheHostedExecutor()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await CreateServer(new StubExecutor(async (_, _, status, ct) =>
        {
            try
            {
                await status("Working", ct);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Turn;
            }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        }));
        using var http = server.GetTestClient();
        using var cts = new CancellationTokenSource();
        var running = Proxy(http, endpoint: "http://localhost/invocations")
            .RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HostReportsFailuresAsErrorEvents()
    {
        await using var server = await CreateServer(new StubExecutor((_, _, _, _) => throw new InvalidOperationException("private details")));
        using var http = server.GetTestClient();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Proxy(http, endpoint: "http://localhost/invocations").RunStreamingAsync(new(Guid.NewGuid(), Guid.NewGuid()), Ignore, Ignore, default));
        Assert.Equal("The hosted agent could not complete the turn.", error.Message);
    }

    [Fact]
    public async Task ConcurrentCallbacksProduceWholeJsonRecords()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var writer = new ChatStreamWriter<ChatAgentEvent>(context.Response);
        writer.Start();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => writer.WriteAsync(new("status", Message: i.ToString()), default).AsTask()));
        context.Response.Body.Position = 0;
        var lines = (await new StreamReader(context.Response.Body).ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(100, lines.Length);
        Assert.Equal(100, lines.Select(l => JsonSerializer.Deserialize<ChatAgentEvent>(l, ChatStreamWriter<ChatAgentEvent>.Json)!.Message).Distinct().Count());
    }

    private static async Task<WebApplication> CreateServer(IChatAgentExecutor executor)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddInvocationsServer();
        builder.Services.AddSingleton(executor);
        builder.Services.AddScoped<InvocationHandler, ChatAgentInvocation>();
        var app = builder.Build();
        app.MapInvocationsServer();
        await app.StartAsync();
        return app;
    }

    private static IFoundrySessionRepository EmptySessions()
    {
        var sessions = Substitute.For<IFoundrySessionRepository>();
        sessions.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        return sessions;
    }

    private static FoundryChatAgentExecutor Proxy(HttpClient http, IFoundrySessionRepository? sessions = null,
        string endpoint = "https://example.com/invocations", TestCredential? credential = null) =>
        new(http, credential ?? new TestCredential(), sessions ?? EmptySessions(),
            Options.Create(new ChatAgentHostingOptions { Mode = ChatAgentExecutionMode.Foundry, Foundry = new() { Endpoint = endpoint } }));

    private static HttpResponseMessage Response(ChatAgentEvent item, string session)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(item, ChatStreamWriter<ChatAgentEvent>.Json) + "\n", Encoding.UTF8, "application/x-ndjson"),
        };
        response.Headers.Add("x-agent-session-id", session);
        return response;
    }

    private static ValueTask Ignore(string _, CancellationToken ct) => ValueTask.CompletedTask;

    private sealed class StubExecutor(Func<ChatAgentRequest, Func<string, CancellationToken, ValueTask>, Func<string, CancellationToken, ValueTask>, CancellationToken, Task<ChatTurn>> run) : IChatAgentExecutor
    {
        public Task<ChatTurn> RunStreamingAsync(ChatAgentRequest request, Func<string, CancellationToken, ValueTask> onText,
            Func<string, CancellationToken, ValueTask> onStatus, CancellationToken cancellationToken) => run(request, onText, onStatus, cancellationToken);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestCredential : TokenCredential
    {
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }
}
