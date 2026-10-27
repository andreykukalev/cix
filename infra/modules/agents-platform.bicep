// What the engine and the agent jobs run on in Azure (ADR-0013, docs/research/azure-hosting.md), without the apps themselves
// (agents-apps.bicep): costs nothing until something runs, except the registry (Basic, about $5 a month).
//   - Log Analytics, capped at 0.1 GB a day and kept 30 days (inside the free 5 GB a month);
//   - a VNet and a Container Apps environment on it: workload profiles, Consumption only, internal (no public endpoint). A
//     NAT gateway for a fixed outbound address can be added to this VNet later, without rebuilding the environment;
//   - the container registry (Basic, no admin user: the apps pull with their identities);
//   - the run queues (runs-trader, runs-requests, runs-analyst and runs-poison), in a storage account with shared keys off;
//   - Key Vault (RBAC), holding claude-token (set by the owner); the engine reaches the database with its identity
//     (cosmos-engine-access.bicep, deployed by main.bicep into the shared resource group), so no account key is kept here;
//   - one user-assigned identity each for the engine and the three jobs, with only the rights each one needs;
//   - a monthly budget on CIX's resource group, when a contact address is given.
// Deployed into CIX's resource group by infra/main.bicep (agents: platform or apps). Names come from agents-names.bicep.

import { agentJobs, agentsNames, poisonQueue } from 'agents-names.bicep'

param namePrefix string
param environmentName string

@description('Region for the environment, the registry, the storage account, Key Vault and the identities.')
param location string

param tags object

@description('Monthly budget for CIX\'s resource group, in the billing currency.')
param monthlyBudget int

@description('The budget\'s first month (YYYY-MM-01). Fixed: a budget\'s start date can\'t be moved once created.')
param budgetStartDate string

@description('Where budget alerts go. Empty: no budget.')
param budgetContactEmail string

@description('The owner\'s Entra object ID, given Key Vault Secrets Officer on the vault to set claude-token. Empty: no grant.')
param ownerObjectId string

var names = agentsNames(namePrefix, environmentName, resourceGroup().id)
var queueNames = ['runs-trader', 'runs-requests', 'runs-analyst', poisonQueue] // roles below find each queue by name, not position

// Built-in role definitions.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  queueMessageSender: 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a'
  queueMessageProcessor: '8a0f0c08-91a1-4084-bc3d-661d67233fed'
  queueReader: '19e7f393-937e-4f77-808e-94535e297925'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: names.vnet
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.42.0.0/16'] }
    subnets: [
      {
        name: 'snet-apps'
        properties: {
          addressPrefix: '10.42.0.0/24' // workload profiles need at least a /27
          delegations: [{ name: 'apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
    ]
  }
}

resource appsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: names.appsEnvironment
  location: location
  tags: tags
  properties: {
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    vnetConfiguration: {
      infrastructureSubnetId: vnet.properties.subnets[0].id
      internal: true // no public endpoint: the engine's ingress is reachable from inside the environment only
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowSharedKeyAccess: false // Entra only: the engine and the jobs use their identities, nothing holds a key
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource queues 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = [
  for name in queueNames: {
    parent: queueService
    name: name
  }
]

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

resource engineIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${names.identityPrefix}engine'
  location: location
  tags: tags
}

resource jobIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [
  for job in agentJobs: {
    name: '${names.identityPrefix}${job.key}'
    location: location
    tags: tags
  }
]

// Pulling images: every identity, on the registry only.
resource enginePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, engineIdentity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: engineIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource jobPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, length(agentJobs)): {
    name: guid(registry.id, jobIdentities[i].id, roles.acrPull)
    scope: registry
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
      principalId: jobIdentities[i].properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// The engine puts runs on the three job queues: send only, on each queue.
resource engineSend 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for job in agentJobs: {
    name: guid(queues[indexOf(queueNames, job.queue)].id, engineIdentity.id, roles.queueMessageSender)
    scope: queues[indexOf(queueNames, job.queue)]
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.queueMessageSender)
      principalId: engineIdentity.properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Each job reads (for its scale rule) and takes messages from its own queue only, and can park one on runs-poison.
resource jobRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, length(agentJobs)): {
    name: guid(queues[indexOf(queueNames, agentJobs[i].queue)].id, jobIdentities[i].id, roles.queueReader)
    scope: queues[indexOf(queueNames, agentJobs[i].queue)]
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.queueReader)
      principalId: jobIdentities[i].properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource jobTake 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, length(agentJobs)): {
    name: guid(queues[indexOf(queueNames, agentJobs[i].queue)].id, jobIdentities[i].id, roles.queueMessageProcessor)
    scope: queues[indexOf(queueNames, agentJobs[i].queue)]
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.queueMessageProcessor)
      principalId: jobIdentities[i].properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource jobPoison 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, length(agentJobs)): {
    name: guid(queues[indexOf(queueNames, poisonQueue)].id, jobIdentities[i].id, roles.queueMessageSender)
    scope: queues[indexOf(queueNames, poisonQueue)]
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.queueMessageSender)
      principalId: jobIdentities[i].properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Secrets: the jobs' access to claude-token, and in a protected environment the engine's to the exchange keys, are granted by
// agents-apps.bicep, once the owner has set them, each on that secret alone.

// The owner, named explicitly (not whoever deploys: a CI identity must never get this), can set and rotate secrets:
// claude-token is put there by hand, never by a template.
resource ownerSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ownerObjectId)) {
  name: guid(vault.id, ownerObjectId, roles.keyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: ownerObjectId
    principalType: 'User'
  }
}

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
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [budgetContactEmail]
      }
    }
  }
}

// What the scripts report; agents-apps.bicep finds the rest by name.
output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output keyVaultName string = vault.name
output enginePrincipalId string = engineIdentity.properties.principalId
