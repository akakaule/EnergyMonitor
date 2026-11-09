// Energy Monitor – one resource group per environment.
//
//   az deployment group create -g rg-energymonitor-dev -f infra/main.bicep -p infra/dev.bicepparam
//
// Secrets are never parameters here. After the first deployment the owner stores them in the Key Vault:
//   Eloverblik--RefreshToken, Telegram--BotToken, Auth--ClientSecret   (see README)

targetScope = 'resourceGroup'

@description('Short environment name; part of every resource name.')
@allowed(['dev', 'prod'])
param environmentName string

@description('Azure region.')
param location string = resourceGroup().location

@description('Container image, e.g. ghcr.io/<owner>/energy-monitor:<tag>.')
param containerImage string

@description('Registry server for a private image, e.g. ghcr.io. Leave empty for a public image.')
param registryServer string = ''

@description('Registry user name for a private image.')
param registryUsername string = ''

@description('Registry password or token for a private image.')
@secure()
param registryPassword string = ''

@description('OIDC authority, e.g. https://login.microsoftonline.com/<tenant-id>/v2.0')
param authAuthority string

@description('Client (application) id of the app registration used for sign-in.')
param authClientId string

@description('The household allowlist: object id (oid) or sign-in name, and role admin or viewer.')
param users array

@description('Default Telegram chat ids for notifications. Can be changed later in Settings.')
param telegramChatIds array = []

@description('Daily ingestion cap for Log Analytics in GB, to keep cost predictable.')
param logDailyCapGb string = '0.1'

var suffix = uniqueString(resourceGroup().id)
var baseName = 'energymonitor-${environmentName}'
var tableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${baseName}'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${baseName}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json(logDailyCapGb) }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${baseName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('stem${environmentName}${suffix}', 24)
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Managed identity only: no account keys or connection strings exist to leak.
    allowSharedKeyAccess: false
  }
}

resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, tableDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', tableDataContributor)
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv-em-${environmentName}-${suffix}', 24)
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
  }
}

resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, keyVaultSecretsUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${baseName}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var userSettings = flatten(map(range(0, length(users)), i => [
  { name: 'Auth__Users__${i}__Subject', value: string(users[i].subject) }
  { name: 'Auth__Users__${i}__Role', value: string(users[i].role) }
]))

var chatSettings = map(range(0, length(telegramChatIds)), i => {
  name: 'Telegram__ChatIds__${i}'
  value: string(telegramChatIds[i])
})

var privateRegistry = !empty(registryServer)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${baseName}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      // One revision at a time: two schedulers must never run side by side.
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      secrets: privateRegistry ? [{ name: 'registry-password', value: registryPassword }] : []
      registries: privateRegistry
        ? [{ server: registryServer, username: registryUsername, passwordSecretRef: 'registry-password' }]
        : []
    }
    template: {
      containers: [
        {
          name: 'energy-monitor'
          image: containerImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(
            [
              { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
              { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
              { name: 'ConnectionStrings__tables', value: storage.properties.primaryEndpoints.table }
              { name: 'KeyVault__Uri', value: vault.properties.vaultUri }
              { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
              { name: 'Auth__Mode', value: 'Oidc' }
              { name: 'Auth__Authority', value: authAuthority }
              { name: 'Auth__ClientId', value: authClientId }
            ],
            userSettings,
            chatSettings
          )
          probes: [
            { type: 'Liveness', httpGet: { path: '/alive', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30 }
          ]
        }
      ]
      // Exactly one replica, always on: the scheduler lives in the app, and must neither sleep nor double up.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [storageRole, vaultRole]
}

output appUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output redirectUri string = 'https://${app.properties.configuration.ingress.fqdn}/signin-oidc'
output keyVaultName string = vault.name
output storageAccountName string = storage.name
