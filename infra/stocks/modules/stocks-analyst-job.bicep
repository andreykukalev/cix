// The stocks Claude analyst's job (ADR-0027), after the nightly run: on a schedule, one execution at a time, never retried
// (a run that couldn't use Claude records why, and the schedule's next slot tries again; a close already analysed is
// skipped). The nightly run writes the brief with the news (brief-latest); this job reads it, runs headless Claude Code on
// it with the owner's Claude token, and saves the checked answer. Its own identity reads that one secret and the advice,
// and writes its own analysis container alone: it never reaches the EODHD key, the portfolios or the trades. It never trades: the owner reads
// Claude's view on the Stocks page and places any order himself.

import { stocksNames, stocksDatabase, claudeSecret } from 'stocks-names.bicep'

param namePrefix string
param location string
param tags object

@description('The CIX registry\'s login server, which the job pulls from.')
param registryServer string

// By digest, not tag: what runs is fixed, so pushing the tag again can't change it.
@minLength(71)
@maxLength(71)
param imageDigest string

@description('Cron expression, UTC: after the nightly run has written the advice and the brief.')
param schedule string

@description('The Claude model the analyst uses, as an alias or a model name.')
param model string

@description('When the Claude setup token expires (YYYY-MM-DD), so the job warns 14 days ahead and stops once it has. Empty: not checked.')
param claudeTokenExpires string

var names = stocksNames(namePrefix, resourceGroup().id)
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource appsEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: names.appsEnvironment
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: names.vault
}

resource analystIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: names.analystIdentity
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: names.cosmos
}

// Set by the owner (provision-stocks.ps1 uploads it); never by a template.
resource claudeToken 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: claudeSecret
}

// The analyst reads the Claude token, and no other secret.
resource tokenAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(claudeToken.id, analystIdentity.id, keyVaultSecretsUser)
  scope: claudeToken
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: analystIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: names.analystJob
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${analystIdentity.id}': {} }
  }
  dependsOn: [tokenAccess]
  properties: {
    environmentId: appsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1200 // Claude at most 15 minutes (run.sh stops it), plus reading the brief and saving the answer
      replicaRetryLimit: 0
      scheduleTriggerConfig: {
        cronExpression: schedule
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [{ server: registryServer, identity: analystIdentity.id }]
      secrets: [{ name: claudeSecret, keyVaultUrl: '${vault.properties.vaultUri}secrets/${claudeSecret}', identity: analystIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'analyst'
          image: '${registryServer}/cix-stocks@${imageDigest}'
          command: ['bash', '/app/analyst/run.sh']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'STOCKS_COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
            { name: 'STOCKS_COSMOS_DATABASE', value: stocksDatabase }
            { name: 'AZURE_CLIENT_ID', value: analystIdentity.properties.clientId }
            { name: 'ANALYST_MODEL', value: model }
            { name: 'CLAUDE_TOKEN_EXPIRES', value: claudeTokenExpires }
          ]
          // The token as a file, where run.sh looks for it (/run/secrets/claude_token).
          volumeMounts: [{ volumeName: 'claude', mountPath: '/run/secrets' }]
        }
      ]
      volumes: [{ name: 'claude', storageType: 'Secret', secrets: [{ secretRef: claudeSecret, path: 'claude_token' }] }]
    }
  }
}

output jobName string = job.name
