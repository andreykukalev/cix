// CIX in Azure: its own resource group with the read-only dashboard (Static Web Apps, Free), and its own database in the
// shared Cosmos DB account (infra/shared/main.bicep, deployed first). Until agents is 'apps', the engine and the agents run
// in Docker on the owner's PC and connect to the database over HTTPS with the account key. In Azure the engine uses its
// managed identity instead, with roles on CIX's containers only (modules/cosmos-engine-access.bicep).
// agents (ADR-0013): 'platform' adds what the engine and the agent jobs run on (modules/agents-platform.bicep); 'apps'
// also deploys the paper engine and the three jobs (modules/agents-apps.bicep). 'none', the default, adds neither.
// Deploy with tools/azure/provision.ps1, which reads infra/environments/<name>.json. See docs/azure.md and ADR-0009.
targetScope = 'subscription'

@description('Environment name, for example dev. Part of resource names and the cix-environment tag the scripts check.')
@minLength(2)
@maxLength(12)
param environmentName string

@description('CIX\'s resource group, for example rg-cix-dev.')
param resourceGroupName string

@description('Region for CIX\'s resource group.')
param location string

@description('Region for the Static Web App. Free tier is offered in a few regions only: westeurope, centralus, eastus2, westus2, eastasia.')
@allowed(['westeurope', 'centralus', 'eastus2', 'westus2', 'eastasia'])
param dashboardLocation string = 'eastus2'

@description('Short prefix for resource names.')
@minLength(2)
@maxLength(8)
param namePrefix string = 'cix'

@description('The shared Cosmos DB account\'s resource group (from infra/shared/main.bicep).')
param sharedResourceGroupName string

@description('The shared Cosmos DB account\'s name.')
param sharedAccountName string

@description('CIX\'s database in the shared account; the engine uses Cosmos:Database.')
@minLength(1)
param databaseName string = 'cix'

@description('Throughput shared by CIX\'s two containers. The free tier covers 1000 RU/s for the whole account, shared with other projects\' databases.')
@minValue(400)
@maxValue(1000)
param databaseThroughput int = 400

@description('True for an environment with real money. Tagged on CIX\'s resource group as cix-protected; the reset script refuses it.')
param protectedEnvironment bool = false

@description('Create the dashboard (Static Web App, Free).')
param deployDashboard bool = true

@description('Extra tags for CIX\'s resources.')
param tags object = {}

@description('The engine and agents in Azure: none, platform (what they run on) or apps (the paper engine and the three jobs too).')
@allowed(['none', 'platform', 'apps'])
param agents string = 'none'

@description('Region for the engine, the jobs and what they run on: the Cosmos DB account\'s region (Poland Central).')
param agentsLocation string = location

@description('agents apps: the engine\'s application ID URI in Entra (api://<app ID>), created by tools/azure/provision.ps1.')
param engineAudience string = ''

@description('agents apps: the engine image\'s digest (sha256:...). provision.ps1 keeps the one running unless given a tag.')
param engineImageDigest string = ''

@description('agents apps: the agents\' image digest (sha256:...), resolved like the engine\'s.')
param agentImageDigest string = ''

@description('agents apps: run the engine. False keeps it stopped (no replica), as until the cutover.')
param startEngine bool = false

@description('agents apps: the trader\'s model (an alias like sonnet, or a full model name).')
param traderModel string = 'sonnet'

@description('agents apps: the analyst\'s model.')
param analystModel string = 'opus'

@description('agents apps: when the Claude setup token expires (YYYY-MM-DD, not a secret). Empty: no check.')
param claudeTokenExpires string = ''

@description('agents: monthly budget for CIX\'s resource group, in the billing currency.')
@minValue(1)
param monthlyBudget int = 40

@description('agents: the budget\'s first month (YYYY-MM-01); it can\'t be moved once the budget exists.')
param budgetStartDate string = '2026-11-01'

@description('agents: where budget alerts go (provision.ps1 passes your sign-in address; it isn\'t kept in the repo). Empty: no budget.')
param budgetContactEmail string = ''

@description('agents: your Entra object ID (provision.ps1 passes your sign-in\'s), given Key Vault Secrets Officer to set claude-token. Empty: no grant.')
param ownerObjectId string = ''

// A protected environment (real money) never gets this paper engine or its agents, whatever the parameters say: the live
// engine has a template of its own. provision.ps1 refuses it too.
var agentsDeploy = protectedEnvironment ? 'none' : agents

// cix-database and cix-cosmos-account claim the database for this environment: the scripts refuse to deploy over, or
// reset, a database that no group or another group claims.
var allTags = union(tags, {
  'cix-environment': environmentName
  'cix-component': 'cix'
  'cix-protected': protectedEnvironment ? 'true' : 'false'
  'cix-database': databaseName
  'cix-cosmos-account': sharedAccountName
})

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: allTags
}

module database 'modules/cosmos-database.bicep' = {
  name: 'cix-${environmentName}-database'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    accountName: sharedAccountName
    databaseName: databaseName
    databaseThroughput: databaseThroughput
  }
}

module dashboard 'modules/dashboard.bicep' = if (deployDashboard) {
  name: 'cix-${environmentName}-dashboard'
  scope: group
  params: {
    name: 'stapp-${namePrefix}-${environmentName}'
    location: dashboardLocation
    cosmosAccountName: sharedAccountName
    cosmosResourceGroupName: sharedResourceGroupName
    databaseName: databaseName
    tags: allTags
  }
}

module agentsPlatform 'modules/agents-platform.bicep' = if (agentsDeploy != 'none') {
  name: 'cix-${environmentName}-agents-platform'
  scope: group
  params: {
    namePrefix: namePrefix
    environmentName: environmentName
    location: agentsLocation
    tags: allTags
    monthlyBudget: monthlyBudget
    budgetStartDate: budgetStartDate
    budgetContactEmail: budgetContactEmail
    ownerObjectId: ownerObjectId
  }
}

// The engine's access to CIX's database, granted where the account is, once the database and the identity exist.
module engineCosmosAccess 'modules/cosmos-engine-access.bicep' = if (agentsDeploy != 'none') {
  name: 'cix-${environmentName}-engine-cosmos-access'
  scope: resourceGroup(sharedResourceGroupName)
  dependsOn: [database]
  params: {
    accountName: sharedAccountName
    databaseName: databaseName
    enginePrincipalId: agentsPlatform!.outputs.enginePrincipalId
  }
}

module agentsApps 'modules/agents-apps.bicep' = if (agentsDeploy == 'apps') {
  name: 'cix-${environmentName}-agents-apps'
  scope: group
  // No outputs passed between them (the apps find the platform's resources by name), so the order is stated here.
  dependsOn: [agentsPlatform, database, engineCosmosAccess]
  params: {
    namePrefix: namePrefix
    environmentName: environmentName
    location: agentsLocation
    tags: allTags
    cosmosAccountName: sharedAccountName
    cosmosResourceGroupName: sharedResourceGroupName
    databaseName: databaseName
    engineAudience: engineAudience
    engineImageDigest: engineImageDigest
    agentImageDigest: agentImageDigest
    startEngine: startEngine
    traderModel: traderModel
    analystModel: analystModel
    claudeTokenExpires: claudeTokenExpires
  }
}

output cosmosAccountName string = sharedAccountName
output cosmosEndpoint string = database.outputs.endpoint
output staticWebAppName string = deployDashboard ? dashboard!.outputs.name : ''
output dashboardUrl string = deployDashboard ? dashboard!.outputs.url : ''
output registryName string = agentsDeploy != 'none' ? agentsPlatform!.outputs.registryName : ''
output registryServer string = agentsDeploy != 'none' ? agentsPlatform!.outputs.registryServer : ''
output keyVaultName string = agentsDeploy != 'none' ? agentsPlatform!.outputs.keyVaultName : ''
output engineAppName string = agentsDeploy == 'apps' ? agentsApps!.outputs.engineAppName : ''
output agentJobNames array = agentsDeploy == 'apps' ? agentsApps!.outputs.jobNames : []
output agentJobPrincipalIds array = agentsDeploy == 'apps' ? agentsApps!.outputs.jobPrincipalIds : []
