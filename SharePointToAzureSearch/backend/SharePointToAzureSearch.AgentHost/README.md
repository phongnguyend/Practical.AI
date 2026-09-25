# Local API or Foundry execution

The API selects `IChatAgentExecutor` with `ChatAgent:Mode`. `Local` (the default) runs `ChatAgentService` in the API process. `Foundry` calls this separate host using Foundry's **Invocations protocol 2.0.0**. Both use the same agent, tools, context loader, and streaming writer.

## History and streaming

1. The API validates and saves the question, including attachment links, in SQL Server.
2. It invokes the executor with only `conversationId` and `questionId`.
3. The shared context loader reads the conversation, its agent/model/instructions, the question, and up to 40 preceding messages from SQL. It excludes the question itself and any later messages.
4. The agent emits status and text callbacks. The hosted adapter sends these immediately as NDJSON; the remote executor forwards them to the existing browser stream. Citations, token usage, and model ID travel in the final result.
5. The API alone persists the completed answer. Errors, cancellation, timeout, and a stream ending without `completed` do not save a successful answer. Invocations are not automatically retried because tools can modify files or upload them.

SQL remains the history authority in **both** modes. This does not use Responses-managed history or Foundry conversation storage. The frontend's `started`, `status`, `delta`, `completed`, and `error` events are unchanged.

The API stores `FoundryEndpoint` and `FoundrySessionId` on the conversation and sends the session ID in the next invocation's query string. That preserves sandbox files across turns and API restarts. New branches get a new binding. Changing the configured endpoint starts a new binding; deleting a Foundry session requires clearing its binding before further use. SQL history survives either operation, but unsaved sandbox edits do not transfer between local and Foundry execution or between different endpoints. Conversation deletion removes the SQL binding, not the remote session; manage remote retention separately.

## Run in the API

Keep the existing API configuration and set:

```json
{ "ChatAgent": { "Mode": "Local" } }
```

No Foundry configuration or host process is needed. The API needs its existing model, SQL, search, Graph, attachment, and optional officecli settings.

## Run in Foundry

Apply `AddFoundrySessionBinding` through the existing API migration flow or your database deployment pipeline before enabling this mode. The host defaults to `SqlServer:AutoMigrate=false`; provisioning a sandbox should not run schema migrations.

Configure the API:

```json
{
  "ChatAgent": {
    "Mode": "Foundry",
    "Foundry": {
      "Endpoint": "https://ACCOUNT.services.ai.azure.com/api/projects/PROJECT/agents/AGENT/endpoint/protocols/invocations?api-version=v1",
      "TimeoutSeconds": 600,
      "AllowUnauthenticatedLocalhost": false,
      "ManagedIdentityClientId": null
    }
  }
}
```

The API uses `DefaultAzureCredential` and the `https://ai.azure.com/.default` scope. Grant its identity permission to invoke the endpoint. `ManagedIdentityClientId` is optional for a user-assigned identity. Use the default **Entra** endpoint authorization scheme; header-based isolation is not configured by this client. Session IDs are managed by the application and must not be included in the configured URL.

Configure the hosted container with environment variables or a secret-backed configuration provider:

| Settings | Purpose |
| --- | --- |
| `SqlServer__ConnectionString` | The **same database** used by the API, reachable from the sandbox. A developer's LocalDB is not reachable from Azure. |
| `SharePoint__TenantId`, `SharePoint__ClientId`, `SharePoint__ClientSecret`, `SharePoint__SiteHostname`, `SharePoint__SitePath`, `SharePoint__DocumentLibraryName` | Existing Graph document access settings. Webhook and Service Bus settings are not needed. |
| `AzureOpenAI__Endpoint`, `AzureOpenAI__EmbeddingDeployment`, `AzureOpenAI__ChatDeployment` | Existing model settings. Persisted agent definitions select the chat model. Use the resource root endpoint, not `/openai/v1` or the project URL. |
| `AzureSearch__Endpoint`, `AzureSearch__SharePointIndexName`, `AzureSearch__UploadIndexName`, `AzureSearch__VectorDimensions` | The same indexes and embedding dimensions as the API. |
| `Uploads__ServiceUri`, `Uploads__ContainerName` | Existing attachment blob storage. |
| `MarkItDown__Endpoint` | Existing attachment service dependency, reachable from the sandbox. |
| `OfficeCli__Enabled`, `OfficeCli__Command` | Enable after installing officecli in the agent image. The base Dockerfile does not include officecli, matching the existing API image's dependency requirements. |

The host defaults Azure OpenAI, Search, and Blob Storage to managed identity. Grant the hosted identity access to those resources and SQL, or use the existing `UsedManagedIdentity=false` and API-key/connection-string options. Graph continues to use application credentials. `Downloads:Directory` defaults to `$HOME/sharepoint-downloads` in this host so Foundry can persist edited files; leave it unset unless using another persistent session path.

Build from the `SharePointToAzureSearch` directory:

```powershell
docker build -f backend/SharePointToAzureSearch.AgentHost/Dockerfile -t YOUR_REGISTRY.azurecr.io/sharepoint-agent:YOUR_TAG .
docker push YOUR_REGISTRY.azurecr.io/sharepoint-agent:YOUR_TAG
```

Deploy the image as a Foundry Hosted Agent configured for `invocations`, protocol version `2.0.0`, and the environment settings above. The SDK supplies port **8088**, **GET /readiness**, **POST /invocations**, platform context, and the session response header. Keep this container behind Foundry's authenticated endpoint; the internal handler trusts its authorized caller's database identifiers. See Microsoft's [deployment guide](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent) and [Invocations session contract](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-sessions).

## Exercise the remote path locally

Configure the host with its own user secrets or environment variables for the same resources as the API. Environment variables use `__` instead of `:`. For locally unavailable managed identity endpoints, use the supported key/connection-string settings.

```powershell
dotnet run --project backend/SharePointToAzureSearch.AgentHost
```

In the API's environment, set:

```powershell
$env:ChatAgent__Mode = 'Foundry'
$env:ChatAgent__Foundry__Endpoint = 'http://localhost:8088/invocations'
$env:ChatAgent__Foundry__AllowUnauthenticatedLocalhost = 'true'
dotnet run --project backend/SharePointToAzureSearch.Api
```

The unauthenticated option is restricted to loopback. The same browser UI now exercises the network path. When testing multiple conversations in one local host process, files share the local directory; only Foundry deployment supplies per-session sandbox isolation.

## Verification

```powershell
dotnet build backend/SharePointToAzureSearch.slnx
dotnet test backend/SharePointToAzureSearch.Tests
```

Tests cover history boundaries and attachment references, migration/model consistency, mode selection, endpoint validation, authentication, sandbox reuse, concurrent writes, early status delivery through the real Invocations adapter, metadata, cancellation, errors, and truncated streams. They use fake model/tool execution and do not require Azure credentials or modify SQL data. A deployed smoke test should additionally verify cloud permissions, networking, model calls, and edit/upload continuity across two turns.
