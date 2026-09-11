# SharePoint to Azure AI Search

This .NET 10 solution keeps a permission-aware Azure AI Search vector index synchronized with a SharePoint document library.

## Components

- `SharePointToAzureSearch.Api` exposes `POST /api/sharepoint/webhook`, completes Microsoft Graph's validation handshake, validates `clientState`, and publishes change signals to an Azure Service Bus topic. It also serves the search endpoints and the read-only state endpoints the front end uses.
- `frontend` is a React and Vite app for viewing the worker's SQL Server state and running the three retrieval strategies against the index. See [frontend/README.md](frontend/README.md).
- `SharePointToAzureSearch.Background` consumes a topic subscription. It follows the Microsoft Graph drive delta feed and, for each file the feed returns, compares it against the metadata recorded for the last indexing run in SQL Server: an unchanged file is left alone, a renamed or re-shared file has its metadata refreshed in place, and only a file whose content actually changed is downloaded, extracted, chunked, embedded, and replaced. Deleted files have all chunks removed. Two more hosted services run alongside it: one creates the Graph subscription and renews it before expiration (`SharePoint:SubscriptionRenewalEnabled` to turn it off), and one runs the same delta synchronization every `Processor:ScheduledSyncMinutes` (5 by default, `ScheduledSyncEnabled` to turn it off) so missed notifications still get picked up. Either trigger can run without the other: disable the subscription to poll only, or disable the schedule to react only to notifications. All three triggers — notification, schedule, and startup sync — are serialized, so only one delta pass runs at a time.
- `SharePointToAzureSearch.Core` uses the Microsoft Graph .NET SDK for subscriptions, delta tracking, downloads, and permissions, and contains the Service Bus, SQL Server state, extraction, embedding, and search-index implementations. Embeddings go through `Microsoft.Extensions.AI`'s `IEmbeddingGenerator<string, Embedding<float>>`, backed by `AzureOpenAIClient` from the Azure OpenAI SDK, so the embedding model can be swapped without touching the indexing or query code.

The webhook is intentionally only a signal. Microsoft Graph drive notifications do not contain a complete, durable list of item-level changes. A delta link is checkpointed in SQL Server only after every returned page is indexed successfully, making retries idempotent and allowing expired delta tokens to trigger a full reconciliation. Those reconciliations are why file metadata is tracked in the same database: the delta feed then returns every file in the library, and without a record of what was already indexed each one would be extracted and embedded again. See [Worker state in SQL Server](#worker-state-in-sql-server).

## Prerequisites

Create these resources before deploying:

1. An Azure Service Bus namespace with the configured topic and subscription.
2. A SQL Server database reachable at `SqlServer:ConnectionString`, holding the worker's delta checkpoint and indexed-file metadata. Azure SQL Database, SQL Server, or SQL Server in a container all work; the schema is applied by an Entity Framework Core migration as the application starts.
3. Azure AI Search and an Azure OpenAI embedding deployment. The search index is created or updated automatically.
4. A MarkItDown service reachable at `MarkItDown:Endpoint`, which converts DOCX, PPTX, and XLSX to markdown. Plain-text formats are read in-process and need no service.
5. Azure AI Document Intelligence, unless the allow list stays within the formats above. Every other format — PDF and images, for example — needs Document Intelligence, or it is indexed using metadata text only.
6. An Entra application or managed identity with Microsoft Graph application access to the target site/drive. Prefer `Sites.Selected` with an explicit grant to the site; `Sites.Read.All` is the broader alternative. Admin consent is required. Read access covers everything but the chat assistant's [`upload_file`](#uploading-a-file-back) tool, which needs a `write` grant (or `Sites.ReadWrite.All`) — grant it only if the assistant should be able to replace documents.
7. A public HTTPS URL for the API. Microsoft Graph must be able to call it during subscription creation. Not needed when `SharePoint:SubscriptionRenewalEnabled` is `false` and the worker polls on its schedule alone.
8. `@officecli/officecli` on the API's host, only for the chat assistant's editing tools. See [Editing a file with officecli](#editing-a-file-with-officecli); set `OfficeCli:Enabled` to `false` where it is not installed.

Assign Azure RBAC appropriate to each process: Service Bus Data Sender to the API; Service Bus Data Receiver, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User to the worker. Add Cognitive Services User when Document Intelligence is enabled. SQL Server permissions are granted inside the database rather than through RBAC: see [Worker state in SQL Server](#worker-state-in-sql-server).

Neither the SQL Server nor the MarkItDown service is deployed by the Bicep templates. Provision the database separately and pass its connection string to the worker as a secret.

Infrastructure is split into two deployments. `main.bicep` deploys the shared Azure services, Azure Container Registry, Log Analytics, and the Container Apps environment. It does not deploy the SQL Server:

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
    "AzureSearch__IndexName=$searchIndexName" `
    "AzureSearch__VectorDimensions=$vectorDimensions" `
    "AzureOpenAI__EmbeddingDeployment=$embeddingDeploymentName" `
  --cpu $workerCpu `
  --memory $workerMemory `
  --min-replicas $workerMinReplicas `
  --max-replicas $workerMaxReplicas
```

`container-apps.bicep` configures `UsedManagedIdentity=true` and discoverable Azure service endpoints. The release pipeline supplies topic, subscription, index, vector-dimension, and model-deployment settings alongside the SharePoint settings, the `SqlServer__ConnectionString` secret, and the other application secrets.

The API and worker receive separate system-assigned identities. Bicep grants the API Service Bus Data Sender and grants the worker Service Bus Data Receiver, Search Index Data Contributor, Search Service Contributor, and Cognitive Services OpenAI User. SQL Server access is not an RBAC grant; the worker's identity is added inside the database instead. A separate user-assigned identity has only `AcrPull` and is attached to both Container Apps for private image retrieval. Set `deployDocumentIntelligence=true` to include Document Intelligence and its worker role assignment.

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
ServiceBus__Enabled
ServiceBus__UsedManagedIdentity
ServiceBus__FullyQualifiedNamespace
ServiceBus__ConnectionString
SqlServer__ConnectionString
SqlServer__AutoMigrate
AzureSearch__UsedManagedIdentity
AzureSearch__Endpoint
AzureSearch__ApiKey
AzureOpenAI__UsedManagedIdentity
AzureOpenAI__Endpoint
AzureOpenAI__EmbeddingDeployment
DocumentIntelligence__UsedManagedIdentity
MarkItDown__Endpoint
Downloads__Directory
OfficeCli__Enabled
OfficeCli__Command
```

Microsoft Graph authentication uses the SharePoint `TenantId`, `ClientId`, and `ClientSecret` settings. Store `ClientSecret` in user secrets, environment variables, or a secret store rather than committing a real value to `appsettings.json`. The Entra application needs Microsoft Graph application permissions for the target SharePoint site or drive, with admin consent.

`AzureOpenAI:Endpoint` takes the resource endpoint with no API path, such as `https://<resource>.openai.azure.com` or `https://<resource>.services.ai.azure.com`. The SDK appends `/openai/deployments/<deployment>/embeddings` itself, so the OpenAI-compatible base URL that the Foundry portal also offers — the same host with `/openai/v1` appended — would be doubled into a path that returns 404 on every embedding request. Startup validation rejects an endpoint that carries a path rather than letting it fail per request.

Each Azure service has its own `UsedManagedIdentity` setting. Set it to `true` to use the host's system-assigned managed identity. Set it to `false` to use `ConnectionString` for Service Bus, or `ApiKey` for Azure AI Search, Azure OpenAI, and Document Intelligence. SQL Server is the exception: it has no such flag, because the choice belongs in `SqlServer:ConnectionString` itself. Store connection strings and keys in user secrets, environment variables, or a secret store rather than in `appsettings.json`.

```powershell
dotnet restore
dotnet run --project src/SharePointToAzureSearch.Api
dotnet run --project src/SharePointToAzureSearch.Background
```

`Processor:SyncOnStartup` defaults to `true`, so existing documents are indexed immediately rather than waiting for the next webhook or scheduled tick. Both the change signal listener and the scheduled synchronization honour it, so the startup pass happens whichever trigger is enabled. When both are enabled the second request is a no-op: passes are serialized, and the first one has already advanced the delta checkpoint. Service Bus notifications after that advance the checkpoint further.

`Processor:ChangeSignalListenerEnabled` defaults to `true`. Set it to `false` to stop the worker from consuming change signals from the Service Bus subscription, leaving `Processor:ScheduledSyncEnabled` as the only trigger for delta synchronization.

`ServiceBus:Enabled` defaults to `true` and controls whether the application uses Service Bus at all. When it is `false` no Service Bus client is created and `ServiceBus:FullyQualifiedNamespace`/`ServiceBus:ConnectionString` are not validated, so a polling-only worker can be deployed with no Service Bus settings; every feature that depends on Service Bus is switched off with it, including the change signal listener regardless of `Processor:ChangeSignalListenerEnabled`. The API requires `ServiceBus:Enabled` to be `true` and refuses to start otherwise, because its webhook endpoint publishes the change signals.

## Search index schema

The worker owns the index definition and applies it with `CreateOrUpdateIndex`, so `AzureSearch:IndexName` is created if missing and updated in place otherwise. Both the change signal listener and the scheduled synchronization do this as they start, so the index is prepared whichever trigger is enabled — a worker with both disabled indexes nothing and expects the index to exist already.

One document is one chunk of one file: a file indexed as three chunks becomes three documents that share `driveId`, `itemId`, and the same file and permission metadata.

| Field | Type | Attributes | Content |
| --- | --- | --- | --- |
| `id` | `Edm.String` | key, filterable | Base64url of `<driveId>:<itemId>:<chunkNumber>` |
| `driveId` | `Edm.String` | filterable | Graph drive ID of the document library |
| `itemId` | `Edm.String` | filterable | Graph `driveItem` ID of the file |
| `name` | `Edm.String` | searchable, filterable | File name including extension |
| `path` | `Edm.String` | searchable, filterable | Parent folder path of the file |
| `webUrl` | `Edm.String` | retrievable | Browser URL of the file in SharePoint |
| `mimeType` | `Edm.String` | filterable | Content type reported by Graph |
| `size` | `Edm.Int64` | filterable, sortable | File size in bytes |
| `lastModifiedUtc` | `Edm.DateTimeOffset` | filterable, sortable | Last modification timestamp from Graph |
| `eTag` | `Edm.String` | filterable | Graph ETag of the file version that was indexed |
| `chunkNumber` | `Edm.Int32` | sortable | Zero-based position of the chunk within the file |
| `content` | `Edm.String` | searchable | Extracted text of this chunk |
| `contentVector` | `Collection(Edm.Single)` | vector-searchable | Embedding of `content` |
| `allowedPrincipals` | `Collection(Edm.String)` | filterable | Principals granted access to the file |
| `permissionRoles` | `Collection(Edm.String)` | filterable | Graph permission roles on the file, such as `read` or `write` |
| `hasAnonymousAccess` | `Edm.Boolean` | filterable | True when an anonymous sharing link exists |

Every field is retrievable, and the file-level fields are copied onto each chunk so a single query can filter and render results without a second lookup. The search endpoints project a narrower set: `eTag`, `contentVector`, `allowedPrincipals`, `permissionRoles`, and `hasAnonymousAccess` back the filters but are never returned to callers.

`contentVector` uses an HNSW configuration named `content-hnsw` through the `content-vector-profile` profile, with default HNSW parameters and `AzureSearch:VectorDimensions` dimensions. No analyzers, scoring profiles, suggesters, or semantic configuration are defined; fields use the default analyzer.

`allowedPrincipals` holds prefixed tokens rather than raw IDs — `user:<id>`, `group:<id>`, `siteGroup:<id>`, `siteUser:<id>`, `application:<id>`, `email:<address>` (lowercased), and `anonymous` for anonymously shared files. Query-time principals are built in the same shape, so they compare directly in a filter. See [Permission-aware queries](#permission-aware-queries).

Azure AI Search rejects breaking field changes on an existing index, including a change to a vector field's dimensions. Changing `AzureSearch:VectorDimensions` — or the embedding model behind it — therefore means pointing `AzureSearch:IndexName` at a new index and re-indexing from scratch rather than editing the live one.

## Worker state in SQL Server

All of the worker's own state lives in one SQL Server database, configured by the `SqlServer` section, and is reached through Entity Framework Core. `SharePointIndexDbContext` in `SharePointToAzureSearch.Core/Data` defines every table — the two below plus the chat assistant's — and the migrations in `SharePointToAzureSearch.Core/Migrations` are generated from it, so the model is the source of truth for the schema and table names are fixed rather than configured. See [Schema and migrations](#schema-and-migrations).

### Delta checkpoint

`SharePointDeltaState` holds one row per drive, keyed by `DriveId`:

| Column | Type | Content |
| --- | --- | --- |
| `DriveId` | `NVARCHAR(200)` | Graph drive ID of the document library, the primary key |
| `DeltaLink` | `NVARCHAR(MAX)` | Delta link the next pass resumes from |
| `ScanId` | `UNIQUEIDENTIFIER` | Reconciliation round the checkpoint belongs to |
| `SweptScanId` | `UNIQUEIDENTIFIER` | Round whose orphan sweep has already run; `NULL` until it has |
| `UpdatedAtUtc` | `DATETIMEOFFSET(7)` | When the checkpoint was last advanced |

The link is written only after every item on a delta page has been indexed, so a pass that fails is repeated from the last successful checkpoint. An expired delta token deletes the row and reconciles the whole drive.

### Reconciliation rounds

A pass that starts with no delta link walks the entire drive: the first pass ever, or the one that follows an expired delta token, which clears the checkpoint. Each of those opens a new round with a fresh `ScanId`, logged as `Walking the whole drive as reconciliation round <id>`. Every incremental pass that follows resumes from the stored link and keeps that round's id, so a round spans one full scan plus all the incremental passes built on top of it.

The round id is stamped on each file's metadata row as the pass reaches it — whether the file was rebuilt, refreshed, or skipped as unchanged. A file skipped during an incremental pass already carries the current round, so nothing is written; during a full scan it costs one narrow `UPDATE` of `ScanId` alone. After a full scan completes, `ScanId` therefore separates the files the scan reached from rows left behind by files it never returned.

### Orphan sweep

Once a round has walked the whole drive, any tracked file still carrying an older `ScanId` is one the drive no longer returns — most often a deletion that happened while the worker was down, whose notification nobody was listening for. The sweep removes those files' search documents and their metadata rows, and logs each one.

Deleting on the basis of "not seen this round" is only correct in a narrow window, so three conditions gate it:

- **The drive was walked end to end.** The sweep runs only after a checkpoint has been written, and a checkpoint is written only after the final delta page. Mid-walk, a file the pass has not reached yet is indistinguishable from a file that is gone.
- **The pass succeeded.** Any failure — a download, a conversion, an embedding, an index write — aborts the pass before the checkpoint, so a partial walk can never delete anything.
- **The round has not been swept already.** `SweptScanId` records the round whose sweep has finished. The incremental passes that follow share the round id and skip the sweep, so it runs once per full scan rather than on every tick.

The marker is written only after the sweep finishes, so a sweep interrupted halfway is resumed by the next pass in that round rather than being abandoned until the next full scan. Re-running it is harmless: the rows it would act on are gone.

Orphans are claimed in batches of 500 so a large clean-up does not read the whole backlog at once. The sweep is driven by metadata rows, so documents indexed before this table existed have no row and are not swept; re-indexing them once puts them under its care.

### Indexed file metadata

The worker also records what it last indexed for every file, and uses that record to do as little work as each change requires. Extraction and embedding are the expensive part of a pass — a MarkItDown conversion plus one Azure OpenAI request per chunk — so a file that has not changed is not fetched at all.

`SharePointIndexedFiles` is keyed by `(DriveId, ItemId)`:

| Column | Type | Content |
| --- | --- | --- |
| `DriveId` | `NVARCHAR(200)` | Graph drive ID of the document library, part of the primary key |
| `ItemId` | `NVARCHAR(200)` | Graph `driveItem` ID of the file, part of the primary key |
| `FileName` | `NVARCHAR(400)` | File name including extension |
| `ParentPath` | `NVARCHAR(1000)` | Parent folder path of the file |
| `WebUrl` | `NVARCHAR(2000)` | Browser URL of the file in SharePoint |
| `MimeType` | `NVARCHAR(200)` | Content type reported by Graph |
| `SizeBytes` | `BIGINT` | File size in bytes |
| `LastModifiedUtc` | `DATETIMEOFFSET(7)` | Last modification timestamp from Graph |
| `ETag` | `NVARCHAR(200)` | Graph ETag, which changes on a content **or** metadata change |
| `CTag` | `NVARCHAR(200)` | Graph CTag, which changes only on a content change |
| `PermissionsHash` | `CHAR(44)` | SHA-256 of the sharing snapshot stored on the chunks |
| `IndexFingerprint` | `NVARCHAR(200)` | Chunk size/overlap, embedding deployment, and vector dimensions used |
| `ChunkCount` | `INT` | Number of search documents the file was indexed as |
| `ScanId` | `UNIQUEIDENTIFIER` | Reconciliation round that last saw the file |
| `IndexedAtUtc` | `DATETIMEOFFSET(7)` | When the file was last indexed |

For each file the delta feed returns, the worker compares the item against its record and takes the cheapest sufficient action:

| Situation | Action |
| --- | --- |
| No record, or `IndexFingerprint` differs from the current settings | Download, extract, chunk, embed, replace |
| `CTag` differs (content changed) | Download, extract, chunk, embed, replace |
| Content unchanged, but name, path, URL, MIME type, size, modification time, `ETag`, or permissions differ | Merge the changed metadata onto the existing `ChunkCount` chunks; no download, extraction, or embedding |
| Everything matches | Nothing but the round stamp; logged as skipped |

Only permissions are read from Graph to make that decision, because a sharing change alters neither tag on the item. Every other comparison uses the delta response the worker already has.

The record is written only after the search index write succeeds, so a failed pass re-indexes the file on its retry. A file removed from the index — deleted, renamed to a disallowed extension, grown past `Processor:MaxFileBytes`, or gone from SharePoint — has its row deleted with it. Should the index have lost chunks the record still claims, the metadata merge fails, and the worker logs a warning and rebuilds the file in full.

`IndexFingerprint` is what makes a settings change safe: raising `Processor:ChunkSizeCharacters`, changing the overlap, or pointing at a different embedding deployment makes every existing row stale, so files are rebuilt rather than reported as unchanged.

### Schema and migrations

The schema is defined by `SharePointIndexDbContext` and applied by Entity Framework Core migrations. When `SqlServer:AutoMigrate` is true — the default — each application applies any pending migration as it starts, so a fresh deployment needs no separate schema step. The API and the worker both do this and may start together; applying a migration takes a SQL Server application lock, so whichever gets there second waits and then finds nothing to do.

Turn `AutoMigrate` off where the login has no DDL rights, and apply the schema from the pipeline instead:

```bash
dotnet ef migrations script --idempotent   --project src/SharePointToAzureSearch.Core --output schema.sql
```

Changing a table, a column, or an index means changing the model and generating a migration for it, rather than editing the database by hand:

```bash
cd src/SharePointToAzureSearch.Core
dotnet ef migrations add <Name>
dotnet ef database update      # or let AutoMigrate apply it on the next start
```

`dotnet ef` builds the context through `DesignTimeDbContextFactory`, so no application host has to start. Commands that reach the database — `database update`, `migrations script` without `--idempotent` — use `SqlServer__ConnectionString` from the environment, falling back to the LocalDB default.

#### An existing database created before migrations

Databases whose tables were created by the earlier auto-create code have no `__EFMigrationsHistory`, so the first migration would try to create tables that are already there. Both tables the worker keeps are a cache — emptying them costs one full re-extraction, nothing more — so unless there is chat history worth keeping, the simplest course is to drop the four tables and let the migration recreate them. To keep the data instead, baseline the database: add the foreign key and indexes the migration introduces, then record it as applied.

```sql
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260910133933_InitialCreate', N'10.0.12');
```

### Connecting

`appsettings.json` ships pointing at SQL Server LocalDB — `Server=(localdb)\MSSQLLocalDB;Database=SharePointSearch;Integrated Security=true;TrustServerCertificate=true` — so a local run needs no SQL setup beyond creating the empty database once with `sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "CREATE DATABASE [SharePointSearch];"`. The tables themselves are created by the migration on first start. Deployments override the setting with `SqlServer__ConnectionString`.

The worker's SQL login needs `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on its tables, plus DDL rights while `SqlServer:AutoMigrate` is on. Managed identity is expressed in the connection string rather than a `UsedManagedIdentity` flag, because SQL Server access is granted inside the database:

```text
Server=<server>.database.windows.net;Database=<database>;Authentication=Active Directory Default;Encrypt=True
```

```sql
CREATE USER [<worker-container-app-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<worker-container-app-name>];
ALTER ROLE db_datawriter ADD MEMBER [<worker-container-app-name>];
ALTER ROLE db_ddladmin ADD MEMBER [<worker-container-app-name>];
```

Past the migration at startup, connections are opened when a pass needs them rather than held open, so a database that goes away fails that pass — retried on the next tick or left unsettled on the Service Bus — instead of stopping the worker. A database unreachable at startup fails startup while `SqlServer:AutoMigrate` is on, because the schema check cannot run. Emptying the tables is safe but not free: the drive is reconciled in full, and every file is re-extracted and re-embedded once, because a file with no record is treated as new.

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

## State endpoints

Read-only views over the worker's SQL Server state, for the front end and for operators. They read the database at `SqlServer:ConnectionString` and never write to it, and an empty table reads as an empty result, so they work before the worker's first pass.

| Endpoint | Returns |
| --- | --- |
| `GET /api/state/summary` | Totals over `SharePointIndexedFiles` — files, chunks, source size, distinct drives, the files whose `ScanId` is not the round in the checkpoint, distinct index fingerprints, the indexing window, and a breakdown by content type |
| `GET /api/state/indexed-files` | A page of `SharePointIndexedFiles`. `search` matches name, folder, URL, content type, or item ID; `driveId` filters exactly; `sort` is one of `name`, `path`, `mimeType`, `size`, `lastModifiedUtc`, `chunkCount`, `indexedAtUtc` with `desc`; `skip` and `top` (1-200, default 25) page it |
| `GET /api/state/indexed-files/{driveId}/{itemId}` | One row, or 404 |
| `GET /api/state/delta` | Every `SharePointDeltaState` row, newest checkpoint first |

Like the search endpoints they are unauthenticated and unfiltered, so the same warning applies: put authentication in front of them, because between them they expose every indexed file's metadata and the Graph delta tokens.

## Subscription endpoints

Managing Microsoft Graph webhook subscriptions by hand, for when the renewal service is off or a subscription has to be replaced. Subscription names and Graph IDs are persisted in `WebhookSubscriptions`; the signed name is also carried in `clientState` so incoming notifications can still be authenticated. They share `SubscriptionManager` with `SubscriptionRenewalBackgroundService`, so both paths use the same Default definition.

| Endpoint | Effect |
| --- | --- |
| `GET /api/subscriptions` | One union of database-tracked records and subscriptions found on the application registration, merged by Graph subscription ID. A tracked record missing from Graph has a `databaseId`, null Graph `id`/`expirationUtc`, and `Missing` status; unmatched Graph entries are untracked. Saved names and the `isDefault` flag identify the Default record. |
| `POST /api/subscriptions` | Creates one over the configured resource. Body `{ "days": 28, "notificationUrl": "https://..." }` — both optional, falling back to `SharePoint:SubscriptionLifetimeDays` and `SharePoint:NotificationUrl`; `days` is clamped to 1-29 |
| `PUT /api/subscriptions/{id}` | Changes a subscription's lifetime and, for a non-default one, its notification URL |
| `POST /api/subscriptions/{id}/renew` | Extends an existing subscription, body `{ "days": 28 }` |
| `DELETE /api/subscriptions/{id}` | Removes it. Graph stops delivering notifications immediately |

Two rules are enforced across all of them:

- **The default subscription cannot be deleted or moved.** The default is whichever subscription sits on the configured `SharePoint:NotificationUrl`; the renewal service owns it and would recreate it, so both operations return `400`. Change `SharePoint:NotificationUrl` to move it.
- **Notification URLs are unique.** Creating or editing onto a URL another subscription already uses returns `409`.

Microsoft Graph cannot `PATCH` a subscription's notification URL, so `PUT` applies a URL change by creating the replacement first and deleting the original only once that succeeds — a failure leaves the original in place rather than leaving the drive uncovered. The response says whether it did (`replaced`), because the subscription ID changes when it does, and carries a `warning` if the old one could not be removed afterwards.

`PUT /api/subscriptions/{id}` accepts either a Graph ID or a tracked row's `databaseId`. Updating a tracked row that is missing from Graph creates and associates a new Graph subscription. Graph-only untracked rows cannot be updated.

`clientState` is never returned — it is the secret the webhook authenticates notifications with, so each entry carries a `clientStateMatches` boolean instead. A rejection from Graph comes back as `502` with Graph's own message rather than an opaque `500`.

**These endpoints change tenant state and are unauthenticated like the rest.** `DELETE` in particular stops change notifications, leaving the scheduled synchronization as the only trigger.

## Chat assistant

An agent built with the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) (`Microsoft.Agents.AI.OpenAI`) answers questions about the indexed library. It runs on `AzureOpenAI:ChatDeployment` — `gpt-5-mini` by default, on the same resource and endpoint as the embedding deployment — and is given four tools of its own: `search_documents`, a hybrid search over this solution's index, `download_file`, which copies one of the files that search returned onto the local file system, `refresh_file`, which takes that file again as SharePoint holds it now, and `upload_file`, which sends the local copy back over the document. When [officecli](#editing-a-file-with-officecli) is configured its tools are added to those, which is what lets the assistant read a whole document or edit one. Its instructions tell it to search before answering anything about document content and to say so plainly when the index does not cover the question, rather than answering from the model's own knowledge.

Conversations and messages are stored in the same SQL Server database, in `ChatConversations` and `ChatMessages`, which the same migration creates as the worker's tables. Deleting a conversation cascades to its messages through the foreign key. Each turn replays the stored history — the last 40 messages — so the agent needs no state of its own between requests, and the documents the tool retrieved are saved with the answer as citations.

| Endpoint | Effect |
| --- | --- |
| `GET /api/chat/conversations` | Every conversation, most recently updated first |
| `POST /api/chat/conversations` | Starts one. Body `{ "title": …, "userId": … }`, both optional |
| `DELETE /api/chat/conversations/{id}` | Removes the conversation and its messages |
| `GET /api/chat/conversations/{id}/messages` | The conversation and its full thread |
| `POST /api/chat/conversations/{id}/messages` | Runs one turn. Body `{ "content": "…" }`; returns the stored question, the answer with its citations, and the conversation title |
| `POST /api/chat/messages/{id}/feedback` | Rates an answer. Body `{ "feedback": "Like" \| "Dislike" \| null }`, where null clears an earlier rating |
| `GET /api/chat/feedback` | Every rated answer, newest first, each with the question that prompted it and the documents it cited. `feedback` narrows to one rating, `search` matches the answer or the conversation title, `skip` and `top` (1-100, default 20) page it. The `liked` and `disliked` totals ignore the rating filter, so they hold still while it is toggled |

A conversation created with a `userId` passes it to every search the assistant runs in that conversation, so answers are restricted to what that user may view — the same filter the search endpoints apply. **Without one the assistant searches the whole index**, so an unauthenticated deployment lets any caller read any indexed document through it.

The first question replaces the placeholder title, so conversations name themselves. The question is stored before the model runs, so a turn that fails still shows what was asked.

### Downloading and refreshing a file

The instructions call for `download_file` when the user asks for a local copy of a document, and also when they ask to edit, change, or update one — a local copy is where editing starts, so the agent fetches the file and reports where it went. Neither download nor refresh writes anything back; [`upload_file`](#uploading-a-file-back) is the only tool that does.

`download_file` takes the `fileId` of a search result — the drive item ID, which `search_documents` returns alongside each excerpt — streams the file out of Microsoft Graph into `Downloads:Directory/<item id>/<file name>` — never through a byte array, so file size costs disk rather than memory — and returns that path to the model. **A file already on disk is not downloaded again**: the tool returns the existing path with `alreadyOnDisk` set, so repeated requests for the same document cost nothing.

`refresh_file` is the same download without that check: it always fetches, and replaces whatever is at the path. The cache is keyed by item ID and notices neither a new version in SharePoint nor an edit made locally, so this is how either is resolved — take the library's current version, at the cost of local changes that were not uploaded. The instructions have the agent say what would be lost and ask first when it is the one that made those changes. The new copy is streamed to a staging name and moved into place, so the copy being replaced survives a download that fails halfway.

| Setting | Default | Effect |
| --- | --- | --- |
| `Downloads:Directory` | empty | Root directory for downloaded files. A relative path resolves against the process working directory; empty means `sharepoint-downloads` under the system temporary directory |
| `Downloads:MaxFileBytes` | 20971520 | Largest file `download_file` and `refresh_file` will fetch, and the largest `upload_file` will send back. A larger file is refused, and the model reports that instead of a path |

### Uploading a file back

`upload_file` is `download_file` reversed: it takes the same `fileId`, and sends whatever is on disk at that moment back over the document in SharePoint. Microsoft Graph takes it in one request up to 4 MB and through an upload session in slices above that, so a large file is streamed rather than held in memory, and `Downloads:MaxFileBytes` caps it either way. **SharePoint keeps the previous file as a version rather than losing it**, so an unwanted upload is recoverable from the document's version history. The local copy is left in place, which means a later `download_file` for that item still reuses it — the cache does not notice new versions, including the one just uploaded, so `refresh_file` is what takes the file back off the server.

The new version reaches the index the ordinary way, with no special case for it: SharePoint notifies the webhook, the next delta pass sees a `cTag` that does not match the one recorded for the file, and the worker extracts, chunks, and embeds it again.

Uploading changes what other people see, so the instructions hold the agent to an explicit request — "save it back", "upload it", "publish it" — and tell it to stop after an edit and offer, rather than upload because an edit finished, and to ask when the request is ambiguous. The tool accepts only a `fileId` from one of the same turn's searches, so the model cannot name an arbitrary drive item, and only a file that has been downloaded; there is nothing else to send.

Two requirements that indexing alone does not give you:

- **Write access for the Entra application.** Indexing needs only read — `Sites.Selected` with a read grant, or `Sites.Read.All`. Uploading needs write: a `Sites.Selected` grant of `write`, or `Sites.ReadWrite.All`. Without it Graph rejects the upload and the model reports the rejection.
- **A deliberate decision about who may trigger it.** The permission filter behind `search_documents` is a *read* filter: it says the user may see the document, not that they may change it. Any file a conversation can search, it can overwrite. With no `userId` on the conversation that is the whole index, and the chat endpoints are unauthenticated — so put authentication in front of them before granting the application write access.

### Editing a file with officecli

[officecli](https://www.npmjs.com/package/@officecli/officecli) is a command line over `.docx`, `.xlsx`, and `.pptx` files that also runs as an [MCP](https://modelcontextprotocol.io/) server. The assistant connects to it with the MCP C# SDK (`ModelContextProtocol.Core`) and adds whatever tools it publishes — one, `officecli`, which takes an officecli command line — to its own four. Since officecli only reaches files on this host, the pairing is `download_file` first, then officecli on the `localPath` it returned; the instructions say as much, and that an edit to the local copy is not a change in SharePoint until `upload_file` sends it back.

They also require added or changed content to match the style of the document around it: read the neighbouring paragraphs, rows, or shapes and their properties first, reuse the style, font, spacing, list format, table formatting, and slide layout they use rather than leaving default-formatted content behind, follow the document's own wording conventions, and check the result before reporting the edit as done.

| Setting | Default | Effect |
| --- | --- | --- |
| `OfficeCli:Enabled` | `true` | Whether the assistant gets officecli's tools. With `false` it can search, download, refresh, and upload but not edit, and no child process is started |
| `OfficeCli:Command` | `officecli` | The executable — a name on `PATH`, as the npm package's shim is, or a full path to it |
| `OfficeCli:Arguments` | empty, meaning `mcp` | Arguments that put officecli into MCP server mode. Configuring a list *adds* to what the options object holds — the configuration binder appends to collections rather than replacing them — which is why the default is empty rather than `[ "mcp" ]`: those two together would run `officecli mcp mcp`, and officecli reads the second `mcp` as the name of an editor to register itself with |
| `OfficeCli:StartupTimeoutSeconds` | 60 | How long the server has to start and list its tools |

Install it with `npm install -g @officecli/officecli`, which puts the shim `OfficeCli:Command` defaults to on `PATH`.

The server is one child process per application, started by the first turn that needs its tools and shared by every turn after — starting it per turn would add its startup to every answer — and shut down with the host. **A server that cannot be started costs one turn, not every turn**: the failure is logged, the assistant answers with its own four tools, and the next attempt is after a restart. Startup validation only requires that `OfficeCli:Command` is set when `OfficeCli:Enabled` is true; whether the command works is discovered on first use rather than at boot, so a missing officecli does not stop the API from starting.

**These tools read and write this host's file system as the API process.** officecli takes a path, and nothing constrains that path to `Downloads:Directory` — the child process runs there, so a bare file name lands among the downloaded files, but an absolute path elsewhere is the model's to pass. Combined with an unauthenticated API, that is a wide capability: put authentication in front of the chat endpoints, run the API as an account with little else to reach, or set `OfficeCli:Enabled` to `false` where editing is not wanted.

The tool only accepts a `fileId` that one of the same turn's searches returned, so the permission filter that trims those results also bounds what can be downloaded — the model cannot reach a file by inventing an ID. That still means **any file a conversation can search, it can also write to the host's file system**, and with no `userId` on the conversation that is the whole index. The file name is sanitized and the resolved path is checked to be inside `Downloads:Directory`, so a name coming back from SharePoint cannot write outside it. Failures — a file over the limit, a rejection from Graph, a disk error — come back to the model as a message rather than failing the turn.

## Front end

`frontend/` is a React and Vite app over these endpoints: the two state tables, and the three retrieval strategies run one at a time or all three side by side. See [frontend/README.md](frontend/README.md).

```bash
cd frontend
npm install
npm run dev     # http://localhost:5173, proxying /api to http://localhost:5263
```

The API must be running as well. `Cors:AllowedOrigins` lists the origins allowed to call it directly, `http://localhost:5173` by default; the dev server's proxy means the browser makes same-origin requests and does not rely on it.

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
- A content change rebuilds the file's chunks, content vectors, and permission fields.
- A rename, a move, or a permission-only change updates those fields on the existing chunks instead, leaving the content and vectors as they are. See [Indexed file metadata](#indexed-file-metadata) under [Worker state in SQL Server](#worker-state-in-sql-server).
- A file the delta feed returns with nothing changed is skipped without being downloaded, and only stamped with the current reconciliation round. This is what keeps a full reconciliation — after an expired delta token, or on the startup pass of a restarted worker — from re-extracting and re-embedding the whole library.
- A deleted file removes all documents matching its drive/item IDs, and its metadata row.
- A deletion that Microsoft Graph never reported — because the worker was down, or the notification was lost — is cleaned up by the orphan sweep at the end of the next full scan, not by the incremental passes in between. See [Orphan sweep](#orphan-sweep).
- Processing failures leave the Service Bus message unsettled, allowing normal retry/dead-letter behavior. The delta checkpoint is not advanced on failure, and neither is a file's metadata row.
- Files over `Processor:MaxFileBytes` are skipped. Increase the limit only after considering Graph, memory, extraction, and embedding costs.
- Only files whose extension is in `Processor:AllowedFileExtensions` are indexed; `appsettings.json` ships with `.docx`, `.pptx`, and `.xlsx`. Entries match case-insensitively, with or without a leading dot, and the worker refuses to start on an empty list rather than silently indexing nothing.
- A file outside the allow list has any previously indexed chunks removed, so narrowing the list or renaming a file to a disallowed extension cleans the index on the next pass rather than leaving stale documents behind. The removal is unconditional rather than driven by the metadata table, so it also cleans up documents indexed before metadata tracking was enabled.
- DOCX, PPTX, and XLSX are converted to markdown by the MarkItDown service at `MarkItDown:Endpoint`, which keeps headings, lists, and tables in the indexed text. There is no local fallback: a conversion that fails leaves the file unindexed and the Service Bus message unsettled, so the normal retry path applies, and the worker refuses to start without an endpoint.
- The worker probes `MarkItDown:HealthPath` (`/health`) as it starts and every `MarkItDown:HealthCheckMinutes` afterwards, with a 10 second timeout of its own rather than the conversion timeout. Only transitions are logged, so a healthy service is reported once and an outage logs one warning until it recovers. The probe reports and nothing more — indexing is not gated on it, and `MarkItDown:HealthCheckEnabled` turns it off.
