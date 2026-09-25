using SharePointToAzureSearch.Core;
using Azure.AI.AgentServer.Invocations;

namespace SharePointToAzureSearch.AgentHost;

public sealed class ChatAgentInvocation(IChatAgentExecutor executor, ILogger<ChatAgentInvocation> logger) : InvocationHandler
{
    public override async Task HandleAsync(HttpRequest httpRequest, HttpResponse response,
        InvocationContext context, CancellationToken token)
    {
        var request = await httpRequest.ReadFromJsonAsync<ChatAgentRequest>(token);
        if (request is null || request.ConversationId == Guid.Empty || request.QuestionId == Guid.Empty)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(new { error = "conversationId and questionId are required." }, token);
            return;
        }

        using var writer = new ChatStreamWriter<ChatAgentEvent>(response);
        writer.Start();
        try
        {
            var turn = await executor.RunStreamingAsync(request,
                (text, ct) => writer.WriteAsync(new("delta", Text: text), ct),
                (status, ct) => writer.WriteAsync(new("status", Message: status), ct), token);
            await writer.WriteAsync(new("completed", Turn: turn), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing the caller's stream cancels the agent and its tools; don't emit a success event.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hosted chat turn failed for {ConversationId}.", request.ConversationId);
            await writer.WriteAsync(new("error", Message: "The hosted agent could not complete the turn."), token);
        }
    }
}
