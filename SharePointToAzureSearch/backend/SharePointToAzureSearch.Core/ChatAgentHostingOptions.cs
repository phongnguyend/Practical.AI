using System.ComponentModel.DataAnnotations;

namespace SharePointToAzureSearch.Core;

public enum ChatAgentExecutionMode { Local, Foundry }

public sealed class ChatAgentHostingOptions : IValidatableObject
{
    public const string SectionName = "ChatAgent";
    public ChatAgentExecutionMode Mode { get; set; } = ChatAgentExecutionMode.Local;
    public FoundryChatAgentOptions Foundry { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Mode))
            yield return new("ChatAgent:Mode must be Local or Foundry.");
        if (Mode != ChatAgentExecutionMode.Foundry) yield break;

        if (!Uri.TryCreate(Foundry.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            yield return new("ChatAgent:Foundry:Endpoint must be an absolute HTTPS invocation URL (HTTP is allowed only on loopback).");
        if (Foundry.AllowUnauthenticatedLocalhost && (uri is null || !uri.IsLoopback))
            yield return new("Unauthenticated Foundry invocation is allowed only on loopback for development.");
        if (Foundry.Endpoint.Length > 2048)
            yield return new("ChatAgent:Foundry:Endpoint must not exceed 2048 characters.");
        if (uri is not null && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).ContainsKey("agent_session_id"))
            yield return new("Do not configure agent_session_id in the endpoint; the application manages it per conversation.");
        if (Foundry.TimeoutSeconds is < 1 or > 3600)
            yield return new("ChatAgent:Foundry:TimeoutSeconds must be between 1 and 3600.");
    }
}

public sealed class FoundryChatAgentOptions
{
    /// <summary>Full Invocations URL, including api-version, or http://localhost:8088/invocations.</summary>
    public string Endpoint { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 600;
    public bool AllowUnauthenticatedLocalhost { get; set; }
    public string? ManagedIdentityClientId { get; set; }
}
