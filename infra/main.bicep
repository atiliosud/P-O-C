targetScope = 'resourceGroup'

@description('Base name used to derive resource names.')
@minLength(3)
@maxLength(16)
param baseName string = 'caseagents'

@description('Azure region.')
param location string = resourceGroup().location

@description('Public base URL of the Case API (for example the App Service or Container App URL).')
param caseApiUrl string

@description('Public base URL of the MCP server.')
param mcpServerUrl string

@description('Entra ID tenant id, stored as APIM named value tenant-id.')
param tenantId string = tenant().tenantId

@description('Expected token audience (the API application id URI), stored as named value api-audience.')
param apiAudience string

@description('Client application id allowed to call the API, stored as named value client-app-id.')
param clientAppId string

@description('Publisher e-mail shown in APIM.')
param publisherEmail string

@description('Publisher name shown in APIM.')
param publisherName string = 'Case Agents PoC'

@description('Name of an existing Azure OpenAI account in this resource group. Leave empty to skip the role assignment.')
param openAiAccountName string = ''

@description('API Management tier. The policy uses rate-limit-by-key, which the Consumption tier does not support. Developer is the cheapest tier that supports it but has no SLA.')
@allowed([
  'Developer'
  'BasicV2'
  'StandardV2'
])
param apimSku string = 'Developer'

var suffix = uniqueString(resourceGroup().id, baseName)
var databaseName = 'caseagents'
var cosmosDataReaderRoleId = '00000000-0000-0000-0000-000000000001'
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

// Identity for the Case API (and, in this PoC, the AI backend that runs in the same process): read/write Cosmos data,
// call Azure OpenAI. In production the AI backend gets its own managed identity: Azure OpenAI User plus the Case.Agent
// app role, and no Cosmos role, because it reaches case data only through the API.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-id-app'
  location: location
}

// Identity for the MCP server: read-only Cosmos data, nothing else.
resource mcpIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-id-mcp'
  location: location
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: '${baseName}-${suffix}'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    capabilities: [
      { name: 'EnableServerless' }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

resource casesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'cases'
  properties: {
    resource: {
      id: 'cases'
      partitionKey: {
        paths: [ '/caseId' ]
        kind: 'Hash'
      }
    }
  }
}

resource leasesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'leases'
  properties: {
    resource: {
      id: 'leases'
      partitionKey: {
        paths: [ '/id' ]
        kind: 'Hash'
      }
    }
  }
}

// Cosmos data-plane roles are scoped to the database, not the whole account.
resource appCosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(database.id, appIdentity.id, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: appIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${databaseName}'
  }
}

resource mcpCosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(database.id, mcpIdentity.id, cosmosDataReaderRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataReaderRoleId}'
    principalId: mcpIdentity.properties.principalId
    scope: '${cosmos.id}/dbs/${databaseName}'
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (!empty(openAiAccountName)) {
  name: openAiAccountName
}

resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(openAiAccountName)) {
  scope: openAi
  name: guid(resourceGroup().id, appIdentity.id, openAiUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: '${baseName}-apim-${suffix}'
  location: location
  sku: {
    name: apimSku
    capacity: 1
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource tenantIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'tenant-id'
  properties: {
    displayName: 'tenant-id'
    value: tenantId
  }
}

resource apiAudienceValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'api-audience'
  properties: {
    displayName: 'api-audience'
    value: apiAudience
  }
}

resource clientAppIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'client-app-id'
  properties: {
    displayName: 'client-app-id'
    value: clientAppId
  }
}

resource caseApiBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'case-api'
  properties: {
    protocol: 'http'
    url: caseApiUrl
  }
}

resource mcpBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'mcp-server'
  properties: {
    protocol: 'http'
    url: mcpServerUrl
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'case-agents'
  properties: {
    displayName: 'Case Agents'
    path: ''
    protocols: [ 'https' ]
    subscriptionRequired: false
  }
}

resource wildcardGet 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'get-any'
  properties: {
    displayName: 'GET any'
    method: 'GET'
    urlTemplate: '/*'
  }
}

resource wildcardPost 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'post-any'
  properties: {
    displayName: 'POST any'
    method: 'POST'
    urlTemplate: '/*'
  }
}

// The AI backend writes proposals with PUT /cases/{id}/proposals/{questionId}.
resource wildcardPut 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'put-any'
  properties: {
    displayName: 'PUT any'
    method: 'PUT'
    urlTemplate: '/*'
  }
}

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  dependsOn: [
    tenantIdValue
    apiAudienceValue
    clientAppIdValue
    caseApiBackend
    mcpBackend
    wildcardGet
    wildcardPost
    wildcardPut
  ]
  properties: {
    format: 'rawxml'
    value: loadTextContent('apim/policy.xml')
  }
}

output cosmosEndpoint string = cosmos.properties.documentEndpoint
output appIdentityClientId string = appIdentity.properties.clientId
output appIdentityPrincipalId string = appIdentity.properties.principalId
output appIdentityResourceId string = appIdentity.id
output mcpIdentityClientId string = mcpIdentity.properties.clientId
output mcpIdentityPrincipalId string = mcpIdentity.properties.principalId
output mcpIdentityResourceId string = mcpIdentity.id
output apimGatewayUrl string = apim.properties.gatewayUrl
