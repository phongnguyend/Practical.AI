# Deploy to Microsoft Foundry Hosted Agents

The `Practical.MicrosoftAgentFramework.FoundryHostedAgent` project exposes the Foundry Responses protocol at `POST /responses`. The original `Practical.MicrosoftAgentFramework` console project remains unchanged. Foundry supplies the project endpoint, model deployment, managed identity, session state, and public agent endpoint at runtime.

The hosted project includes `write_sandbox_state`, `read_sandbox_state`, and `clear_sandbox_state` tools. They persist `sandbox_state.txt` beneath `$HOME/.practical-microsoft-agent-framework`, which makes them useful for verifying isolation and persistence across explicit `agent_session_id` values.

## Prerequisites

- .NET 10 SDK
- Azure CLI, authenticated with `az login`
- Azure Developer CLI 1.27.1 or later
- Foundry CLI extensions: `azure.ai.agents` 1.0.0-beta.8+ and `azure.ai.projects` 1.0.0-beta.4+
- `Foundry Project Manager` on the target project, or `Owner` on the resource group when provisioning a new project

## Deploy to the configured project

`azure.yaml` is currently bound to `foundry-sd1597/project-sd1597`. From this directory:

```powershell
azd extension install azure.ai.agents
azd extension install azure.ai.projects
azd auth login

azd env set AZURE_SUBSCRIPTION_ID "<subscription-id>"
azd env set AZURE_AI_PROJECT_ID "<foundry-project-arm-id>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "<model-deployment-name>"
azd deploy
```

To target a different existing project, also change `services.ai-project.endpoint` in `azure.yaml`. To create a new project instead, remove that endpoint and run `azd provision` before `azd deploy`.

## Run locally

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:AZURE_AI_MODEL_DEPLOYMENT_NAME = "<model-deployment-name>"
az login
dotnet run --project Practical.MicrosoftAgentFramework.FoundryHostedAgent
```

Then send a request:

```powershell
$body = @{ input = "Hello"; stream = $false } | ConvertTo-Json
Invoke-RestMethod -Uri http://localhost:8088/responses -Method Post -ContentType application/json -Body $body
```

## Optional local dependencies

The hosted agent starts without workstation-only integrations. Configure these only when their dependencies are reachable from the hosted network:

- `ConnectionStrings__InternalSearch`, `InternalSearch__EmbeddingEndpoint`, and `InternalSearch__EmbeddingApiKey` enable SQL vector search.
- `Mcp__CheckNugetPackages__Command` and indexed `Mcp__CheckNugetPackages__Arguments` enable the stdio MCP server.
- The included DNS skill requires PowerShell (`pwsh`) in the runtime. Use a custom container image if the managed source runtime doesn't include it.

Do not place production secrets in `appsettings.json` or `azure.yaml`. Use managed identities, Foundry connections, or a secret store such as Azure Key Vault.
