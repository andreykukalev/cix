// The stocks advisory's nightly job (stocks/src/Cix.Stocks.Host): on a schedule, one execution at a time, retried once on
// failure (the run is safe to repeat: a close is never carried out twice, and bars fetched that night are reused). It reads
// the EODHD key from the vault as a file and signs in to its Cosmos DB account with its identity. It never trades.

import { stocksNames, stocksDatabase, eodhdSecret } from 'stocks-names.bicep'

param namePrefix string
param location string
param tags object

@description('The CIX registry\'s login server, which the job pulls from.')
param registryServer string

// By digest, not tag: what runs is fixed, so pushing the tag again can't change it.
@minLength(71)
@maxLength(71)
param imageDigest string

@description('Cron expression, UTC.')
param schedule string

var names = stocksNames(namePrefix, resourceGroup().id)
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource appsEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: names.appsEnvironment
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: names.vault
}

resource jobIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: names.identity
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: names.cosmos
}

// Set by the owner before the job is deployed (provision-stocks.ps1 asks for it); never by a template.
resource key 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: eodhdSecret
}

// The job reads the EODHD key, and no other secret.
resource keyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(key.id, jobIdentity.id, keyVaultSecretsUser)
  scope: key
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: names.job
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${jobIdentity.id}': {} }
  }
  dependsOn: [keyAccess]
  properties: {
    environmentId: appsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 900 // a run takes a minute or two; fifteen covers a slow provider
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: schedule
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [{ server: registryServer, identity: jobIdentity.id }]
      secrets: [{ name: eodhdSecret, keyVaultUrl: '${vault.properties.vaultUri}secrets/${eodhdSecret}', identity: jobIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'stocks'
          image: '${registryServer}/cix-stocks@${imageDigest}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'STOCKS_COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
            { name: 'STOCKS_COSMOS_DATABASE', value: stocksDatabase }
            { name: 'AZURE_CLIENT_ID', value: jobIdentity.properties.clientId }
          ]
          // The key as a file, where the host looks for it (/run/secrets/eodhd-api-token).
          volumeMounts: [{ volumeName: 'secrets', mountPath: '/run/secrets' }]
        }
      ]
      volumes: [
        {
          name: 'secrets'
          storageType: 'Secret'
          secrets: [{ secretRef: eodhdSecret, path: eodhdSecret }]
        }
      ]
    }
  }
}

output jobName string = job.name
