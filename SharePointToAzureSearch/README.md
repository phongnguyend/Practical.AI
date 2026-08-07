# SharePoint to Azure AI Search

This .NET 10 solution keeps a permission-aware Azure AI Search vector index synchronized with a SharePoint document library.

## Components

- `SharePointToAzureSearch.Api` exposes `POST /api/sharepoint/webhook`, completes Microsoft Graph's validation handshake, validates `clientState`, and publishes change signals to an Azure Service Bus topic. After the web host starts accepting traffic, a hosted service creates the Graph subscription and renews it before expiration.
- `SharePointToAzureSearch.Background` consumes a topic subscription. It follows the Microsoft Graph drive delta feed, downloads changed files and their effective sharing permissions, extracts/chunks text, creates Azure OpenAI embeddings, and replaces the file's search documents. Deleted files have all chunks removed.
- `SharePointToAzureSearch.Core` uses the Microsoft Graph .NET SDK for subscriptions, delta tracking, downloads, and permissions, and contains the Service Bus, Blob checkpoint, extraction, embedding, and search-index implementations.

The webhook is intentionally only a signal. Microsoft Graph drive notifications do not contain a complete, durable list of item-level changes. A delta link is checkpointed in Blob Storage only after every returned page is indexed successfully, making retries idempotent and allowing expired delta tokens to trigger a full reconciliation.

## Prerequisites

Create these resources before deploying:

1. An Azure Service Bus namespace with the configured topic and subscription.
2. An Azure Storage account. The state container is created automatically.
3. Azure AI Search and an Azure OpenAI embedding deployment. The search index is created or updated automatically.
4. Optionally, Azure AI Document Intelligence for PDF, legacy Office, and image text extraction. Plain-text formats and DOCX are handled locally; without Document Intelligence, other formats are indexed using metadata text only.
5. An Entra application or managed identity with Microsoft Graph application access to the target site/drive. Prefer `Sites.Selected` with an explicit grant to the site; `Sites.Read.All` is the broader alternative. Admin consent is required.
6. A public HTTPS URL for the API. Microsoft Graph must be able to call it during subscription creation.

Assign Azure RBAC appropriate to each process: Service Bus Data Sender to the API; Service Bus Data Receiver, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the worker. Add Cognitive Services User when Document Intelligence is enabled.

Infrastructure is split into two deployments. `main.bicep` deploys the shared Azure services, Azure Container Registry, Log Analytics, and the Container Apps environment:

```powershell
$deployment = az deployment group create `
  --resource-group <resource-group> `
  --template-file infra/main.bicep `
  --parameters namePrefix=<unique-prefix> | ConvertFrom-Json

$registry = $deployment.properties.outputs.containerRegistryName.value
```

After that deployment succeeds, run `container-apps.bicep` once to create the Container App shells, managed identities, ACR pull access, and service role assignments against the existing resources:

```powershell
$appsDeployment = az deployment group create `
  --resource-group <resource-group> `
  --template-file infra/container-apps.bicep `
  --parameters namePrefix=<unique-prefix> | ConvertFrom-Json

$apiApp = $appsDeployment.properties.outputs.apiContainerAppName.value
$workerApp = $appsDeployment.properties.outputs.workerContainerAppName.value
```

Pass the same `deployDocumentIntelligence=true` value to both deployments when Document Intelligence is required. The Container Apps are created at zero scale with Microsoft's public quickstart placeholder. Do not routinely rerun `container-apps.bicep` after releasing application revisions because it declares the placeholder as its initial desired image. `main.bicep` can be rerun independently without changing the Container Apps.

Bicep does not deploy this project's images, secrets, or runtime environment variables. Build the application images separately:

```powershell
az acr build --registry $registry --image sharepoint-api:v1 `
  --file src/SharePointToAzureSearch.Api/Dockerfile .

az acr build --registry $registry --image sharepoint-worker:v1 `
  --file src/SharePointToAzureSearch.Background/Dockerfile .
```

Configure the application secrets and environment variables first, either in the same release pipeline or with `az containerapp secret set` and `az containerapp update --set-env-vars`. Then deploy the newly built images and activate the application replicas:

```powershell
$registryServer = $deployment.properties.outputs.containerRegistryLoginServer.value

az containerapp update `
  --resource-group <resource-group> `
  --name $apiApp `
  --image "$registryServer/sharepoint-api:v1" `
  --set-env-vars `
    "ServiceBus__TopicName=sharepoint-changes" `
    "ServiceBus__SubscriptionName=search-indexer" `
  --cpu 0.5 `
  --memory 1Gi `
  --min-replicas 1 `
  --max-replicas 3

az containerapp ingress update `
  --resource-group <resource-group> `
  --name $apiApp `
  --target-port 8080

az containerapp update `
  --resource-group <resource-group> `
  --name $workerApp `
  --image "$registryServer/sharepoint-worker:v1" `
  --set-env-vars `
    "ServiceBus__TopicName=sharepoint-changes" `
    "ServiceBus__SubscriptionName=search-indexer" `
    "Storage__ContainerName=sharepoint-search-state" `
    "AzureSearch__IndexName=sharepoint-files" `
    "AzureSearch__VectorDimensions=1536" `
    "AzureOpenAI__EmbeddingDeployment=text-embedding-3-small" `
  --cpu 1.0 `
  --memory 2Gi `
  --min-replicas 1 `
  --max-replicas 1
```

`container-apps.bicep` configures `UsedManagedIdentity=true` and discoverable Azure service endpoints. The release pipeline supplies topic, subscription, container, index, vector-dimension, and model-deployment settings alongside the SharePoint settings and application secrets.

The API and worker receive separate system-assigned identities. Bicep grants the API Service Bus Data Sender and grants the worker Service Bus Data Receiver, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User. A separate user-assigned identity has only `AcrPull` and is attached to both Container Apps for private image retrieval. Set `deployDocumentIntelligence=true` to include Document Intelligence and its worker role assignment.

Local/key authentication is enabled by default so services can still use their `UsedManagedIdentity: false` fallback. Disable the corresponding `allow*LocalAuth` or `allow*ApiKeyAuth` parameters for managed-identity-only deployments. The templates output endpoints, app URLs, registry details, and identity object IDs, but deliberately do not output connection strings or keys.

## Configure and run

Replace the placeholders in both `appsettings.json` files or use environment variables (recommended in deployments), for example:

```text
SharePoint__DriveId
SharePoint__TenantId
SharePoint__ClientId
SharePoint__ClientSecret
SharePoint__NotificationUrl
SharePoint__ClientState
ServiceBus__UsedManagedIdentity
ServiceBus__FullyQualifiedNamespace
ServiceBus__ConnectionString
Storage__UsedManagedIdentity
Storage__ServiceUri
Storage__ConnectionString
AzureSearch__UsedManagedIdentity
AzureSearch__Endpoint
AzureSearch__ApiKey
AzureOpenAI__UsedManagedIdentity
AzureOpenAI__Endpoint
AzureOpenAI__EmbeddingDeployment
DocumentIntelligence__UsedManagedIdentity
```

Microsoft Graph authentication uses the SharePoint `TenantId`, `ClientId`, and `ClientSecret` settings. Store `ClientSecret` in user secrets, environment variables, or a secret store rather than committing a real value to `appsettings.json`. The Entra application needs Microsoft Graph application permissions for the target SharePoint site or drive, with admin consent.

Each Azure service has its own `UsedManagedIdentity` setting. Set it to `true` to use the host's system-assigned managed identity. Set it to `false` to use `ConnectionString` for Service Bus and Storage, or `ApiKey` for Azure AI Search, Azure OpenAI, and Document Intelligence. Store connection strings and keys in user secrets, environment variables, or a secret store rather than in `appsettings.json`.

```powershell
dotnet restore
dotnet run --project src/SharePointToAzureSearch.Api
dotnet run --project src/SharePointToAzureSearch.Background
```

`Processor:SyncOnStartup` defaults to `true`, so existing documents are indexed immediately rather than waiting for the next webhook. Service Bus notifications after that advance the persisted delta checkpoint.

## Permission-aware queries

Every chunk stores `allowedPrincipals`, `permissionRoles`, and `hasAnonymousAccess`. Querying applications must always add a security filter built from the signed-in user's trusted Entra identifiers; never accept principal identifiers directly from an untrusted request. A typical filter shape is:

```text
allowedPrincipals/any(p: search.in(p, 'user:<object-id>,email:<address>')) or hasAnonymousAccess eq true
```

The index stores sharing/effective permission identities returned by Microsoft Graph. Validate the permission model against your SharePoint inheritance and group-expansion requirements before production use; applications that authorize through nested Entra or SharePoint groups normally need to add the caller's transitive group IDs to the query filter.

## Operational behavior

- Duplicate webhook deliveries are safe: a delta call after the checkpoint returns no changes, and item replacement is idempotent.
- A file update rebuilds its chunks, content vectors, and permission fields.
- A permission-only file change rebuilds the same record with the current permission snapshot.
- A deleted file removes all documents matching its drive/item IDs.
- Processing failures leave the Service Bus message unsettled, allowing normal retry/dead-letter behavior. The delta checkpoint is not advanced on failure.
- Files over `Processor:MaxFileBytes` are skipped. Increase the limit only after considering Graph, memory, extraction, and embedding costs.
