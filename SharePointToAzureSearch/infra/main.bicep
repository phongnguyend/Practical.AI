targetScope = 'resourceGroup'

@description('Short prefix used to generate globally unique resource names.')
@minLength(2)
@maxLength(12)
param namePrefix string = 'spsearch'

@description('Azure region for resources. Azure OpenAI model availability varies by region.')
param location string = resourceGroup().location

@description('Tags applied to all supported resources.')
param tags object = {}

@description('Allow Service Bus shared-access-key connection strings.')
param allowServiceBusLocalAuth bool = true

@description('Allow Storage account-key connection strings.')
param allowStorageSharedKeyAccess bool = true

@description('Allow Azure AI Search API-key authentication.')
param allowSearchApiKeyAuth bool = true

@description('Allow Azure OpenAI API-key authentication.')
param allowOpenAiApiKeyAuth bool = true

@description('Deploy Azure AI Document Intelligence.')
param deployDocumentIntelligence bool = false

@description('Allow Document Intelligence API-key authentication when it is deployed.')
param allowDocumentIntelligenceApiKeyAuth bool = true

@description('Azure OpenAI embedding deployment name used by the application.')
param embeddingDeploymentName string = 'text-embedding-3-small'

@description('Azure OpenAI embedding model name.')
param embeddingModelName string = 'text-embedding-3-small'

@description('Azure OpenAI embedding model version. Confirm availability in the selected region.')
param embeddingModelVersion string = '1'

@description('Azure OpenAI deployment SKU.')
param embeddingDeploymentSku string = 'GlobalStandard'

@description('Embedding deployment capacity in thousands of tokens per minute.')
@minValue(1)
param embeddingDeploymentCapacity int = 10

@description('Service Bus topic name used by the application.')
param serviceBusTopicName string = 'sharepoint-changes'

@description('Service Bus subscription name used by the worker.')
param serviceBusSubscriptionName string = 'search-indexer'

@description('Blob container used for Microsoft Graph delta checkpoints.')
param stateContainerName string = 'sharepoint-search-state'

@description('Azure AI Search service SKU.')
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param searchSku string = 'basic'

var uniqueSuffix = uniqueString(subscription().subscriptionId, resourceGroup().id)
var serviceBusNamespaceName = take(toLower('${namePrefix}-sb-${uniqueSuffix}'), 50)
var storageAccountName = 'st${uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)}'
var searchServiceName = take(toLower('${namePrefix}-search-${uniqueSuffix}'), 60)
var openAiAccountName = take(toLower('${namePrefix}-openai-${uniqueSuffix}'), 64)
var documentIntelligenceAccountName = take(toLower('${namePrefix}-docintel-${uniqueSuffix}'), 64)
var containerRegistryName = 'cr${uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)}'
var logAnalyticsWorkspaceName = take(toLower('${namePrefix}-logs-${uniqueSuffix}'), 63)
var containerAppsEnvironmentName = take(toLower('${namePrefix}-cae-${uniqueSuffix}'), 60)

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: containerRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: containerAppsEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
  }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusNamespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: !allowServiceBusLocalAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource serviceBusTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBusNamespace
  name: serviceBusTopicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    enablePartitioning: false
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    status: 'Active'
    supportOrdering: true
  }
}

resource serviceBusSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: serviceBusTopic
  name: serviceBusSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    requiresSession: false
    status: 'Active'
  }
}

resource apiServiceBusAuthorizationRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = if (allowServiceBusLocalAuth) {
  parent: serviceBusNamespace
  name: 'api-send'
  properties: {
    rights: [
      'Send'
    ]
  }
}

resource workerServiceBusAuthorizationRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = if (allowServiceBusLocalAuth) {
  parent: serviceBusNamespace
  name: 'worker-listen'
  properties: {
    rights: [
      'Listen'
    ]
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: allowStorageSharedKeyAccess
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource stateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: stateContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource searchService 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchServiceName
  location: location
  tags: tags
  sku: {
    name: searchSku
  }
  properties: {
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
    disableLocalAuth: !allowSearchApiKeyAuth
    hostingMode: 'Default'
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    replicaCount: 1
  }
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiAccountName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    disableLocalAuth: !allowOpenAiApiKeyAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAiAccount
  name: embeddingDeploymentName
  sku: {
    name: embeddingDeploymentSku
    capacity: embeddingDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource documentIntelligenceAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployDocumentIntelligence) {
  name: documentIntelligenceAccountName
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: documentIntelligenceAccountName
    disableLocalAuth: !allowDocumentIntelligenceApiKeyAuth
    publicNetworkAccess: 'Enabled'
  }
}

output serviceBusFullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
output serviceBusTopicName string = serviceBusTopic.name
output serviceBusSubscriptionName string = serviceBusSubscription.name
output apiServiceBusAuthorizationRuleName string = allowServiceBusLocalAuth ? apiServiceBusAuthorizationRule.name : ''
output workerServiceBusAuthorizationRuleName string = allowServiceBusLocalAuth ? workerServiceBusAuthorizationRule.name : ''
output storageServiceUri string = storageAccount.properties.primaryEndpoints.blob
output storageContainerName string = stateContainer.name
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'
output openAiEndpoint string = openAiAccount.properties.endpoint
output embeddingDeploymentName string = embeddingDeployment.name
output documentIntelligenceEndpoint string = documentIntelligenceAccount.?properties.endpoint ?? ''
output containerRegistryName string = containerRegistry.name
output containerRegistryLoginServer string = containerRegistry.properties.loginServer
output containerAppsEnvironmentName string = containerAppsEnvironment.name
