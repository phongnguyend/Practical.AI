# SharePoint to Azure AI Search

This .NET 10 solution keeps a permission-aware Azure AI Search vector index synchronized with a SharePoint document library.

## Components

- `SharePointToAzureSearch.Api` exposes `POST /api/sharepoint/webhook`, completes Microsoft Graph's validation handshake, validates `clientState`, and publishes change signals to an Azure Service Bus topic.
- `SharePointToAzureSearch.Background` consumes a topic subscription. It follows the Microsoft Graph drive delta feed, downloads changed files and their effective sharing permissions, extracts/chunks text, creates Azure OpenAI embeddings, and replaces the file's search documents. Deleted files have all chunks removed. Two more hosted services run alongside it: one creates the Graph subscription and renews it before expiration (`SharePoint:SubscriptionRenewalEnabled` to turn it off), and one runs the same delta synchronization every `Processor:ScheduledSyncMinutes` (5 by default, `ScheduledSyncEnabled` to turn it off) so missed notifications still get picked up. Either trigger can run without the other: disable the subscription to poll only, or disable the schedule to react only to notifications. All three triggers — notification, schedule, and startup sync — are serialized, so only one delta pass runs at a time.
- `SharePointToAzureSearch.Core` uses the Microsoft Graph .NET SDK for subscriptions, delta tracking, downloads, and permissions, and contains the Service Bus, Blob checkpoint, extraction, embedding, and search-index implementations.

The webhook is intentionally only a signal. Microsoft Graph drive notifications do not contain a complete, durable list of item-level changes. A delta link is checkpointed in Blob Storage only after every returned page is indexed successfully, making retries idempotent and allowing expired delta tokens to trigger a full reconciliation.

## Prerequisites

Create these resources before deploying:

1. An Azure Service Bus namespace with the configured topic and subscription.
2. An Azure Storage account. The state container is created automatically.
3. Azure AI Search and an Azure OpenAI embedding deployment. The search index is created or updated automatically.
4. Azure AI Document Intelligence, unless the allow list is narrowed to DOCX alone. DOCX and plain-text formats are extracted locally; every other format — including the default PPTX and XLSX — needs Document Intelligence, or it is indexed using metadata text only.
5. An Entra application or managed identity with Microsoft Graph application access to the target site/drive. Prefer `Sites.Selected` with an explicit grant to the site; `Sites.Read.All` is the broader alternative. Admin consent is required.
6. A public HTTPS URL for the API. Microsoft Graph must be able to call it during subscription creation. Not needed when `SharePoint:SubscriptionRenewalEnabled` is `false` and the worker polls on its schedule alone.

Assign Azure RBAC appropriate to each process: Service Bus Data Sender to the API; Service Bus Data Receiver, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the worker. Add Cognitive Services User when Document Intelligence is enabled.

Infrastructure is split into two deployments. `main.bicep` deploys the shared Azure services, Azure Container Registry, Log Analytics, and the Container Apps environment:

```powershell
$resourceGroup = '<resource-group>'
$location = '<azure-region>'
$namePrefix = '<unique-prefix>'
$deployDocumentIntelligence = 'false'
$imageTag = 'v1'

$apiImageRepository = 'sharepoint-api'
$workerImageRepository = 'sharepoint-worker'
$serviceBusTopicName = 'sharepoint-changes'
$serviceBusSubscriptionName = 'search-indexer'
$stateContainerName = 'sharepoint-search-state'
$searchIndexName = 'sharepoint-files'
$vectorDimensions = 1536
$embeddingDeploymentName = 'text-embedding-3-small'

$apiTargetPort = 8080
$apiCpu = 0.5
$apiMemory = '1Gi'
$apiMinReplicas = 1
$apiMaxReplicas = 3
$workerCpu = 1.0
$workerMemory = '2Gi'
$workerMinReplicas = 1
$workerMaxReplicas = 1

$deployment = az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/main.bicep `
  --parameters `
    namePrefix=$namePrefix `
    location=$location `
    deployDocumentIntelligence=$deployDocumentIntelligence `
    serviceBusTopicName=$serviceBusTopicName `
    serviceBusSubscriptionName=$serviceBusSubscriptionName `
    stateContainerName=$stateContainerName `
    embeddingDeploymentName=$embeddingDeploymentName | ConvertFrom-Json

$registry = $deployment.properties.outputs.containerRegistryName.value
```

After that deployment succeeds, run `container-apps.bicep` once to create the Container App shells, managed identities, ACR pull access, and service role assignments against the existing resources:

```powershell
$appsDeployment = az deployment group create `
  --resource-group $resourceGroup `
  --template-file infra/container-apps.bicep `
  --parameters `
    namePrefix=$namePrefix `
    location=$location `
    deployDocumentIntelligence=$deployDocumentIntelligence | ConvertFrom-Json

$apiApp = $appsDeployment.properties.outputs.apiContainerAppName.value
$workerApp = $appsDeployment.properties.outputs.workerContainerAppName.value
```

Pass the same `deployDocumentIntelligence=true` value to both deployments when Document Intelligence is required. The Container Apps are created at zero scale with Microsoft's public quickstart placeholder. Do not routinely rerun `container-apps.bicep` after releasing application revisions because it declares the placeholder as its initial desired image. `main.bicep` can be rerun independently without changing the Container Apps.

Bicep does not deploy this project's images, SharePoint settings, or application secrets. Build the application images separately:

```powershell
az acr build --registry $registry --image "${apiImageRepository}:$imageTag" `
  --file src/SharePointToAzureSearch.Api/Dockerfile .

az acr build --registry $registry --image "${workerImageRepository}:$imageTag" `
  --file src/SharePointToAzureSearch.Background/Dockerfile .
```

Configure the application secrets and environment variables first, either in the same release pipeline or with `az containerapp secret set` and `az containerapp update --set-env-vars`. Then deploy the newly built images and activate the application replicas:

```powershell
$registryServer = $deployment.properties.outputs.containerRegistryLoginServer.value

az containerapp update `
  --resource-group $resourceGroup `
  --name $apiApp `
  --image "${registryServer}/${apiImageRepository}:$imageTag" `
  --set-env-vars `
    "ServiceBus__TopicName=$serviceBusTopicName" `
    "ServiceBus__SubscriptionName=$serviceBusSubscriptionName" `
  --cpu $apiCpu `
  --memory $apiMemory `
  --min-replicas $apiMinReplicas `
  --max-replicas $apiMaxReplicas

az containerapp ingress update `
  --resource-group $resourceGroup `
  --name $apiApp `
  --target-port $apiTargetPort

az containerapp update `
  --resource-group $resourceGroup `
  --name $workerApp `
  --image "${registryServer}/${workerImageRepository}:$imageTag" `
  --set-env-vars `
    "ServiceBus__TopicName=$serviceBusTopicName" `
    "ServiceBus__SubscriptionName=$serviceBusSubscriptionName" `
    "Storage__ContainerName=$stateContainerName" `
    "AzureSearch__IndexName=$searchIndexName" `
    "AzureSearch__VectorDimensions=$vectorDimensions" `
    "AzureOpenAI__EmbeddingDeployment=$embeddingDeploymentName" `
  --cpu $workerCpu `
  --memory $workerMemory `
  --min-replicas $workerMinReplicas `
  --max-replicas $workerMaxReplicas
```

`container-apps.bicep` configures `UsedManagedIdentity=true` and discoverable Azure service endpoints. The release pipeline supplies topic, subscription, container, index, vector-dimension, and model-deployment settings alongside the SharePoint settings and application secrets.

The API and worker receive separate system-assigned identities. Bicep grants the API Service Bus Data Sender and grants the worker Service Bus Data Receiver, Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User. A separate user-assigned identity has only `AcrPull` and is attached to both Container Apps for private image retrieval. Set `deployDocumentIntelligence=true` to include Document Intelligence and its worker role assignment.

Local/key authentication is enabled by default so services can still use their `UsedManagedIdentity: false` fallback. Disable the corresponding `allow*LocalAuth` or `allow*ApiKeyAuth` parameters for managed-identity-only deployments. The templates output endpoints, app URLs, registry details, and identity object IDs, but deliberately do not output connection strings or keys.

## Configure and run

Replace the placeholders in both `appsettings.json` files or use environment variables (recommended in deployments), for example:

```text
SharePoint__SiteHostname
SharePoint__SitePath
SharePoint__DocumentLibraryName
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

## Search endpoints

The API exposes the same request body over three retrieval strategies:

| Endpoint | Strategy |
| --- | --- |
| `POST /api/search/fulltext` | Keyword search over the searchable fields |
| `POST /api/search/vector` | Pure k-nearest-neighbour search over `contentVector` |
| `POST /api/search/hybrid` | Keyword and vector search in one request, fused by reciprocal rank |

```jsonc
{
  "query": "quarterly revenue",
  "userId": "<entra-user-object-id-or-upn>", // optional
  "top": 10,                                  // 1-100, default 10
  "skip": 0
}
```

The response carries `totalCount` and the matching chunks with their relevance `score`. `contentVector` is never projected. Vector and hybrid requests embed `query` with the same Azure OpenAI deployment used at indexing time, so both apps must point at the same model and `AzureSearch:VectorDimensions`.

## Permission-aware queries

Every chunk stores `allowedPrincipals`, `permissionRoles`, and `hasAnonymousAccess`. When `userId` is supplied, the endpoints resolve that user through Microsoft Graph — object ID, mail addresses, and every transitive group membership — and filter results to what the user can view:

```text
hasAnonymousAccess eq true or allowedPrincipals/any(p: search.in(p, 'user:<object-id>,email:<address>,group:<group-id>', ','))
```

Principals are resolved server-side from the user ID and cached for 10 minutes; principal identifiers are never accepted directly from the request body. This needs `User.Read.All` and `GroupMember.Read.All` (or `Directory.Read.All`) Graph application permissions in addition to the site/drive permissions used for indexing.

**Omitting `userId` searches the whole index with no security filter.** The endpoints themselves are unauthenticated, so put authentication in front of them and derive `userId` from the validated caller identity rather than from client input — otherwise any caller can read every indexed document.

SharePoint site groups (`siteGroup:`/`siteUser:` principals) are not Entra groups and cannot be expanded from directory membership, so grants made only through a site group are not matched. Validate the permission model against your SharePoint inheritance and group-expansion requirements before production use.

## Operational behavior

- Duplicate webhook deliveries are safe: a delta call after the checkpoint returns no changes, and item replacement is idempotent.
- A file update rebuilds its chunks, content vectors, and permission fields.
- A permission-only file change rebuilds the same record with the current permission snapshot.
- A deleted file removes all documents matching its drive/item IDs.
- Processing failures leave the Service Bus message unsettled, allowing normal retry/dead-letter behavior. The delta checkpoint is not advanced on failure.
- Files over `Processor:MaxFileBytes` are skipped. Increase the limit only after considering Graph, memory, extraction, and embedding costs.
- Only files whose extension is in `Processor:AllowedFileExtensions` are indexed; `appsettings.json` ships with `.docx`, `.pptx`, and `.xlsx`. Entries match case-insensitively, with or without a leading dot, and the worker refuses to start on an empty list rather than silently indexing nothing.
- A file outside the allow list has any previously indexed chunks removed, so narrowing the list or renaming a file to a disallowed extension cleans the index on the next pass rather than leaving stale documents behind.
