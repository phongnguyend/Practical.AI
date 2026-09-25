using Azure.AI.AgentServer.Invocations;
using SharePointToAzureSearch.Core;
using SharePointToAzureSearch.AgentHost;

InvocationsServer.Run<ChatAgentInvocation>(args: args, configure: builder =>
{
    // Persist downloaded/edited files in the session filesystem, rather than the ephemeral /tmp.
    if (string.IsNullOrWhiteSpace(builder.Configuration["Downloads:Directory"]))
    {
        var sessionHome = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        builder.Configuration["Downloads:Directory"] = Path.Combine(sessionHome, "sharepoint-downloads");
    }
    builder.Services.AddHostedChatAgentServices(builder.Configuration);
});

public partial class Program;
