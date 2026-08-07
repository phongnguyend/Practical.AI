targetScope = 'resourceGroup'

@description('The same prefix used to deploy main.bicep.')
@minLength(2)
@maxLength(12)
param namePrefix string = 'spsearch'

@description('Azure region used by the shared infrastructure.')
param location string = resourceGroup().location

@description('Tags applied to the Container Apps and registry pull identity.')
param tags object = {}

@description('Must match deployDocumentIntelligence from main.bicep.')
param deployDocumentIntelligence bool = false

var uniqueSuffix = uniqueString(subscription().subscriptionId, resourceGroup().id)
var serviceBusNamespaceName = take(toLower('${namePrefix}-sb-${uniqueSuffix}'), 50)
var storageAccountName = 'st${uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)}'
var searchServiceName = take(toLower('${namePrefix}-search-${uniqueSuffix}'), 60)
var openAiAccountName = take(toLower('${namePrefix}-openai-${uniqueSuffix}'), 64)
var documentIntelligenceAccountName = take(toLower('${namePrefix}-docintel-${uniqueSuffix}'), 64)
var containerRegistryName = 'cr${uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)}'
var containerAppsEnvironmentName = take(toLower('${namePrefix}-cae-${uniqueSuffix}'), 60)
var registryPullIdentityName = take(toLower('${namePrefix}-acr-pull-${uniqueSuffix}'), 128)
var apiContainerAppName = take(toLower('${namePrefix}-api-${uniqueSuffix}'), 32)
var workerContainerAppName = take(toLower('${namePrefix}-worker-${uniqueSuffix}'), 32)
var placeholderContainerImage = 'mcr.microsoft.com/k8se/quickstart:latest'

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var cognitiveServicesOpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppsEnvironmentName
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource searchService 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchServiceName
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAiAccountName
}

resource documentIntelligenceAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (deployDocumentIntelligence) {
  name: documentIntelligenceAccountName
}

resource registryPullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: registryPullIdentityName
  location: location
  tags: tags
}

resource registryPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, registryPullIdentity.id, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: registryPullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

resource apiContainerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: apiContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned,UserAssigned'
    userAssignedIdentities: {
      '${registryPullIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 80
        transport: 'auto'
      }
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: registryPullIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api-placeholder'
          image: placeholderContainerImage
          env: [
            {
              name: 'ServiceBus__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: '${serviceBusNamespace.name}.servicebus.windows.net'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    registryPullRole
  ]
}

resource workerContainerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: workerContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned,UserAssigned'
    userAssignedIdentities: {
      '${registryPullIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: registryPullIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker-placeholder'
          image: placeholderContainerImage
          env: [
            {
              name: 'ServiceBus__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: '${serviceBusNamespace.name}.servicebus.windows.net'
            }
            {
              name: 'Storage__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'Storage__ServiceUri'
              value: storageAccount.properties.primaryEndpoints.blob
            }
            {
              name: 'AzureSearch__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'AzureSearch__Endpoint'
              value: 'https://${searchService.name}.search.windows.net'
            }
            {
              name: 'AzureOpenAI__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'AzureOpenAI__Endpoint'
              value: openAiAccount.properties.endpoint
            }
            {
              name: 'DocumentIntelligence__UsedManagedIdentity'
              value: 'true'
            }
            {
              name: 'DocumentIntelligence__Endpoint'
              value: documentIntelligenceAccount.?properties.endpoint ?? ''
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    registryPullRole
  ]
}

resource apiServiceBusSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, apiContainerApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: apiContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
  }
}

resource workerServiceBusReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, workerContainerApp.id, serviceBusDataReceiverRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
  }
}

resource workerStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, workerContainerApp.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
  }
}

resource workerSearchIndexRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, workerContainerApp.id, searchIndexDataContributorRoleId)
  scope: searchService
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
  }
}

resource workerSearchServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, workerContainerApp.id, searchServiceContributorRoleId)
  scope: searchService
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
  }
}

resource workerOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, workerContainerApp.id, cognitiveServicesOpenAiUserRoleId)
  scope: openAiAccount
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
  }
}

resource workerDocumentIntelligenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployDocumentIntelligence) {
  name: guid(documentIntelligenceAccount.id, workerContainerApp.id, cognitiveServicesUserRoleId)
  scope: documentIntelligenceAccount
  properties: {
    principalId: workerContainerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
  }
}

output apiContainerAppName string = apiContainerApp.name
output workerContainerAppName string = workerContainerApp.name
output apiUrl string = 'https://${apiContainerAppName}.${containerAppsEnvironment.properties.defaultDomain}'
output apiPrincipalId string = apiContainerApp.identity.principalId
output workerPrincipalId string = workerContainerApp.identity.principalId
output registryPullIdentityResourceId string = registryPullIdentity.id
