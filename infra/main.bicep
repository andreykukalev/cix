// CIX in Azure: its own resource group with the read-only dashboard (Static Web Apps, Free), and its own database in the
// shared Cosmos DB account (infra/shared/main.bicep, deployed first). The engine and the agents keep running in Docker on
// the owner's PC and connect to the database over HTTPS with the account key.
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
param dashboardLocation string = 'westeurope'

@description('Short prefix for resource names.')
@minLength(2)
@maxLength(8)
param namePrefix string = 'cix'

@description('The shared Cosmos DB account\'s resource group (from infra/shared/main.bicep).')
param sharedResourceGroupName string

@description('The shared Cosmos DB account\'s name.')
param sharedAccountName string

@description('CIX\'s database in the shared account; the engine uses Cosmos:Database.')
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

output cosmosAccountName string = sharedAccountName
output cosmosEndpoint string = database.outputs.endpoint
output staticWebAppName string = deployDashboard ? dashboard!.outputs.name : ''
output dashboardUrl string = deployDashboard ? dashboard!.outputs.url : ''
