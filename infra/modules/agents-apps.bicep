// The engine and the three agent jobs, on agents-platform.bicep (ADR-0013).
//   - The engine: one replica at most, internal ingress only, paper mode, Entra-authenticated tool server, scheduling the
//     agent runs onto the queues (AgentRuns:Mode Queue). Off (no replica) until startEngine: at the cutover the owner
//     stops the PC engine first; the engine lease keeps two engines from trading even if both run.
//   - The jobs, each started by a message on its own queue, one execution at a time, never retried by the platform: the
//     engine decides about retries (tools/trader/agent-job.sh).
// The trading mode is not a parameter: this template only ever deploys a paper engine. The live engine gets its own
// resource group, Key Vault and template, with no CI access (docs/research/azure-hosting.md).
// Everything from agents-platform.bicep is found by name (agents-names.bicep), so the preview shows these apps in full.

import { agentJobs, agentsNames, poisonQueue } from 'agents-names.bicep'

param namePrefix string
param environmentName string
param location string
param tags object

param cosmosAccountName string
param cosmosResourceGroupName string
param databaseName string

@description('The engine\'s application ID URI in Entra (api://<app ID>), which the jobs request tokens for.')
@minLength(8)
param engineAudience string

@description('The image tag to run, the same for the engine and the agents (CI tags images with the commit SHA).')
@minLength(1)
param imageTag string

@description('Run the engine (one replica). False keeps it stopped, as before the cutover.')
param startEngine bool

param traderModel string
param analystModel string

@description('When the Claude setup token expires (YYYY-MM-DD; not a secret). Runs warn from 14 days before. Empty: no check.')
param claudeTokenExpires string

var names = agentsNames(namePrefix, environmentName, resourceGroup().id)

resource appsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: names.appsEnvironment
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: names.registry
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: names.storage
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: names.vault
}

resource engineIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${names.identityPrefix}engine'
}

resource jobIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = [
  for job in agentJobs: {
    name: '${names.identityPrefix}${job.key}'
  }
]

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
  scope: resourceGroup(cosmosResourceGroupName)
}

var registryServer = registry.properties.loginServer
var queueEndpoint = storage.properties.primaryEndpoints.queue
var keyVaultUri = vault.properties.vaultUri
var engineImage = '${registryServer}/cix-engine:${imageTag}'
var agentImage = '${registryServer}/cix-trader:${imageTag}' // the trader image runs the analyst too, as in Compose

// Set by the owner before the apps are deployed (tools/azure/provision.ps1 asks for it); never by a template.
resource claudeToken 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: 'claude-token'
}

// Each job reads claude-token, and no other secret.
resource jobSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, length(agentJobs)): {
    name: guid(claudeToken.id, jobIdentities[i].id, '4633458b-17de-408a-b874-0445c86b69e6')
    scope: claudeToken
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
      principalId: jobIdentities[i].properties.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource engine 'Microsoft.App/containerApps@2025-01-01' = {
  name: names.engineApp
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${engineIdentity.id}': {} }
  }
  properties: {
    environmentId: appsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false // the jobs in this environment only
        targetPort: 5077
        transport: 'http'
        allowInsecure: false
      }
      registries: [{ server: registryServer, identity: engineIdentity.id }]
      secrets: [{ name: 'cosmos-key', keyVaultUrl: '${keyVaultUri}secrets/cosmos-key', identity: engineIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'engine'
          image: engineImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(
            [
              // Explicit: without it .NET assumes Production, the one environment where the trading mode could be changed.
              { name: 'ASPNETCORE_ENVIRONMENT', value: 'Azure' }
              { name: 'Trading__Mode', value: 'Paper' }
              { name: 'AllowedHosts', value: '*' } // internal ingress only, and every caller needs an Entra token
              { name: 'Cosmos__Enabled', value: 'true' }
              { name: 'Cosmos__Emulator', value: 'false' }
              { name: 'Cosmos__Endpoint', value: cosmos.properties.documentEndpoint }
              { name: 'Cosmos__Database', value: databaseName }
              { name: 'Cosmos__AccountKey', secretRef: 'cosmos-key' }
              { name: 'ToolServer__Auth', value: 'Entra' }
              { name: 'ToolServer__Entra__TenantId', value: tenant().tenantId }
              { name: 'ToolServer__Entra__Audience', value: engineAudience }
              { name: 'AgentRuns__Mode', value: 'Queue' }
              { name: 'AgentRuns__Queues__ServiceUri', value: queueEndpoint }
              { name: 'AgentRuns__Queues__ManagedIdentityClientId', value: engineIdentity.properties.clientId }
            ],
            // Each job's identity, by object ID, with its agent role: the only callers the tool server lets in. Spelled out
            // (Bicep can't index a resource collection inside a lambda); the test checks there is one per job.
            [
              { name: 'ToolServer__Entra__Agents__${jobIdentities[0].properties.principalId}', value: agentJobs[0].role }
              { name: 'ToolServer__Entra__Agents__${jobIdentities[1].properties.principalId}', value: agentJobs[1].role }
              { name: 'ToolServer__Entra__Agents__${jobIdentities[2].properties.principalId}', value: agentJobs[2].role }
            ]
          )
          probes: [
            // The engine listens only once the database answers (it waits, with backoff): allow it 10 minutes to start
            // (Container Apps allows at most 10 failures).
            { type: 'Startup', tcpSocket: { port: 5077 }, periodSeconds: 60, failureThreshold: 10 }
            { type: 'Liveness', tcpSocket: { port: 5077 }, periodSeconds: 30, failureThreshold: 3 }
            { type: 'Readiness', httpGet: { path: '/healthz', port: 5077 }, periodSeconds: 30, failureThreshold: 3 }
          ]
        }
      ]
      scale: {
        minReplicas: startEngine ? 1 : 0
        maxReplicas: 1 // never two engines from one deployment; the lease covers revision changes
      }
    }
  }
}

resource jobs 'Microsoft.App/jobs@2025-01-01' = [
  for (job, i) in agentJobs: {
    name: take('${names.jobPrefix}${job.key}', 32)
    location: location
    tags: tags
    identity: {
      type: 'UserAssigned'
      userAssignedIdentities: { '${jobIdentities[i].id}': {} }
    }
    dependsOn: [jobSecret]
    properties: {
      environmentId: appsEnvironment.id
      workloadProfileName: 'Consumption'
      configuration: {
        triggerType: 'Event'
        replicaTimeout: 1200 // 20 minutes: a run is stopped after 10 (or the job's timeout, at most 15) and then reports
        replicaRetryLimit: 0 // a failed run is never repeated behind the engine's back
        eventTriggerConfig: {
          parallelism: 1
          replicaCompletionCount: 1
          scale: {
            minExecutions: 0
            maxExecutions: 1 // one run of each kind at a time
            pollingInterval: 30
            rules: [
              {
                name: 'run-queue'
                type: 'azure-queue'
                metadata: {
                  accountName: storage.name
                  queueName: job.queue
                  queueLength: '1'
                }
                identity: jobIdentities[i].id
              }
            ]
          }
        }
        registries: [{ server: registryServer, identity: jobIdentities[i].id }]
        secrets: [{ name: 'claude-token', keyVaultUrl: '${keyVaultUri}secrets/claude-token', identity: jobIdentities[i].id }]
      }
      template: {
        containers: [
          {
            name: 'agent'
            image: agentImage
            command: job.key == 'analyst' ? ['bash', '/cix/tools/trader/run-analyst.sh'] : ['bash', '/cix/tools/trader/run-trader.sh']
            resources: { cpu: json('0.5'), memory: '1Gi' }
            env: [
              { name: 'CIX_JOB_MODE', value: '1' }
              { name: 'CIX_JOB', value: job.role }
              { name: 'CIX_ENGINE_URL', value: 'https://${engine.properties.configuration.ingress.fqdn}' }
              { name: 'CIX_ENGINE_AUDIENCE', value: engineAudience }
              { name: 'CIX_QUEUE_URL', value: '${queueEndpoint}${job.queue}' }
              { name: 'CIX_POISON_QUEUE_URL', value: '${queueEndpoint}${poisonQueue}' }
              { name: 'AZURE_CLIENT_ID', value: jobIdentities[i].properties.clientId }
              { name: 'CLAUDE_TOKEN_EXPIRES', value: claudeTokenExpires }
              { name: job.key == 'analyst' ? 'ANALYST_MODEL' : 'TRADER_MODEL', value: job.key == 'analyst' ? analystModel : traderModel }
            ]
            // The Claude credential as a file, where the scripts look for it (CLAUDE_TOKEN_FILE's default).
            volumeMounts: [{ volumeName: 'secrets', mountPath: '/run/secrets' }]
          }
        ]
        volumes: [
          {
            name: 'secrets'
            storageType: 'Secret'
            secrets: [{ secretRef: 'claude-token', path: 'claude_token' }]
          }
        ]
      }
    }
  }
]

output engineAppName string = engine.name
output engineUrl string = 'https://${engine.properties.configuration.ingress.fqdn}'
output jobNames array = [for i in range(0, length(agentJobs)): jobs[i].name]
