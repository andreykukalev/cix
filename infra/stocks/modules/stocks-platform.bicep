// What the stocks advisory's nightly job runs on, in its own resource group (ADR-0023). Costs nearly nothing at one run a
// night: the Cosmos DB account is serverless (per request), the environment is Consumption only, logs are capped.
//   - Log Analytics, capped at 0.05 GB a day and kept 30 days (inside the free 5 GB a month);
//   - a Container Apps environment: Consumption, no VNet (none is needed, and one would cost a load balancer and an IP), public
//     network access disabled. The job only calls out (EODHD, Cosmos DB);
//   - Key Vault (RBAC) for the EODHD key, which the owner sets (provision-stocks.ps1); the job reads that secret alone;
//   - the job's user-assigned identity;
//   - the Cosmos DB account (serverless), the cix-stocks database and its containers. Nothing queries them, so nothing is
//     indexed: every read is by id. The job signs in with its identity and has a data role on each container with only what it
//     does there, never delete; trades are append-only. Account keys can't change databases or containers (metadata writes
//     go through this template), and the dashboard reads with the read-only key;
//   - a monthly budget on the group, when a contact address is given.

import { stocksNames, stocksDatabase } from 'stocks-names.bicep'

param namePrefix string
param location string
param tags object

@description('The owner\'s Entra object ID, given Key Vault Secrets Officer on the vault to set the EODHD key. Empty: no grant.')
param ownerObjectId string

param monthlyBudget int
param budgetStartDate string

@description('Where budget alerts go. Empty: no budget.')
param budgetContactEmail string

var names = stocksNames(namePrefix, resourceGroup().id)
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.05') }
  }
}

resource appsEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: names.appsEnvironment
  location: location
  tags: tags
  properties: {
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    publicNetworkAccess: 'Disabled'
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.vault
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  }
}

resource jobIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: tags
}

// The owner, named explicitly (not whoever deploys), sets and rotates the EODHD key.
resource ownerSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ownerObjectId)) {
  name: guid(vault.id, ownerObjectId, keyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
    principalId: ownerObjectId
    principalType: 'User'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: names.cosmos
  location: location
  kind: 'GlobalDocumentDB'
  tags: tags
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [{ name: 'EnableServerless' }]
    locations: [{ locationName: location, failoverPriority: 0, isZoneRedundant: false }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    minimalTlsVersion: 'Tls12'
    // The dashboard's functions and the job have no fixed addresses; keys and Entra tokens guard the account.
    publicNetworkAccess: 'Enabled'
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuth: false // the dashboard reads with the read-only key
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: stocksDatabase
  properties: {
    resource: { id: stocksDatabase }
  }
}

var item = 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items'
var metadata = 'Microsoft.DocumentDB/databaseAccounts/readMetadata'

// What the job does in each container (Cix.Stocks.Host.CosmosStocksStore), and the dashboard reads advice-latest.
var containers = [
  { name: 'advice', role: 'writer', actions: [metadata, '${item}/read', '${item}/create', '${item}/upsert'] } // dated advice once, advice-latest upserted
  { name: 'state', role: 'writer', actions: [metadata, '${item}/read', '${item}/create', '${item}/replace'] } // the portfolio, with ETags
  { name: 'trades', role: 'appender', actions: [metadata, '${item}/read', '${item}/create'] } // append-only
  { name: 'bars', role: 'cache', actions: [metadata, '${item}/read', '${item}/upsert'] }
]

resource stores 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for c in containers: {
    parent: database
    name: c.name
    properties: {
      resource: {
        id: c.name
        partitionKey: { paths: ['/id'], kind: 'Hash' }
        // Every read is by id: indexing nothing keeps each write cheap.
        indexingPolicy: { indexingMode: 'consistent', automatic: true, includedPaths: [], excludedPaths: [{ path: '/*' }] }
      }
    }
  }
]

// One at a time: Cosmos DB refuses a role change while another one on the account is in progress.
@batchSize(1)
resource roles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = [
  for c in containers: {
    parent: cosmos
    name: guid(cosmos.id, stocksDatabase, 'cix-stocks-job', c.name)
    dependsOn: [stores]
    properties: {
      roleName: 'CIX stocks job ${c.name} ${c.role}'
      type: 'CustomRole'
      assignableScopes: ['${cosmos.id}/dbs/${stocksDatabase}/colls/${c.name}']
      permissions: [{ dataActions: c.actions }]
    }
  }
]

@batchSize(1)
resource assignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for (c, i) in containers: {
    parent: cosmos
    dependsOn: [roles]
    name: guid(cosmos.id, stocksDatabase, jobIdentity.id, c.name)
    properties: {
      roleDefinitionId: roles[i].id
      principalId: jobIdentity.properties.principalId
      scope: '${cosmos.id}/dbs/${stocksDatabase}/colls/${c.name}'
    }
  }
]

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = if (!empty(budgetContactEmail)) {
  name: names.budget
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
      }
    }
  }
}

output cosmosAccountName string = cosmos.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output databaseName string = stocksDatabase
output keyVaultName string = vault.name
output jobPrincipalId string = jobIdentity.properties.principalId
