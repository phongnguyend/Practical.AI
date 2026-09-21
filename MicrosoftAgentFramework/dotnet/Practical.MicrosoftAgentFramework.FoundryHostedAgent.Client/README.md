# Foundry Hosted Agent Console Client

This console application chats with the deployed `practical-microsoft-agent-framework` hosted agent through its Responses endpoint.

## Run

Authenticate once:

```powershell
az login
azd auth login
```

Then run the client from the `dotnet` directory:

```powershell
dotnet run --project Practical.MicrosoftAgentFramework.FoundryHostedAgent.Client
```

Commands inside the client:

- `/session` displays the current sandbox session ID.
- `/session <id>` selects an existing sandbox session and resets conversation history.
- `/session auto` clears the current session so the next message creates a new sandbox.
- `/new` starts a new conversation while retaining the current sandbox session.
- `/exit` closes the client.

You can also select the session at startup:

```powershell
dotnet run --project Practical.MicrosoftAgentFramework.FoundryHostedAgent.Client -- --session "<agent-session-id>"
```

The client prints the `agent_session_id` and response ID after every successful message. Session ID controls the persisted sandbox (`$HOME` and files); response ID controls conversation history. To test sandbox reuse, capture the session printed by one client process, close it, and pass the same ID to another process.

The checked-in defaults target the deployed development agent. Override them when needed:

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<account>.services.ai.azure.com/api/projects/<project>"
$env:FOUNDRY_HOSTED_AGENT_NAME = "<agent-name>"
dotnet run --project Practical.MicrosoftAgentFramework.FoundryHostedAgent.Client
```
