// CIX in Azure: the database (Cosmos DB, free tier) and the read-only dashboard (Static Web Apps, Free). The engine and the
// agents keep running in Docker on the owner's PC and connect to the database over HTTPS with the account key.
// Deploy with tools/azure/provision.ps1, which reads infra/environments/<name>.json. See docs/azure.md.
targetScope = 'resourceGroup'

@description('Environment name, for example dev. Part of every resource name and the cix-environment tag the scripts check.')
@minLength(2)
@maxLength(12)
param environmentName string

@description('Region for Cosmos DB.')
param location string = resourceGroup().location

@description('Region for the Static Web App. Free tier is offered in a few regions only: westeurope, centralus, eastus2, westus2, eastasia.')
@allowed(['westeurope', 'centralus', 'eastus2', 'westus2', 'eastasia'])
param dashboardLocation string = 'westeurope'

@description('Short prefix for resource names.')
@minLength(2)
@maxLength(8)
param namePrefix string = 'cix'

@description('Use the Cosmos DB free tier (1000 RU/s and 25 GB free). Only one account per subscription can have it; set false if another already does, and expect charges.')
param enableFreeTier bool = true

@description('Throughput shared by the database\'s containers. 1000 RU/s or less is free with the free tier.')
@minValue(400)
@maxValue(1000)
param databaseThroughput int = 1000

@description('Cosmos DB database name; the engine uses Cosmos:Database (default cix).')
param databaseName string = 'cix'

@description('Public IP ranges allowed to reach Cosmos DB, for example ["203.0.113.7"]. Empty allows any address; the account key is then the protection.')
param allowedIpRanges array = []

@description('Create the dashboard (Static Web App, Free).')
param deployDashboard bool = true

@description('Extra tags for every resource.')
param tags object = {}

var allTags = union(tags, {
  'cix-environment': environmentName
  'cix-component': 'cix'
})

var accountName = toLower('${namePrefix}-${environmentName}-${uniqueString(resourceGroup().id)}')

module cosmos 'modules/cosmos.bicep' = {
  name: 'cix-cosmos'
  params: {
    accountName: accountName
    location: location
    enableFreeTier: enableFreeTier
    databaseName: databaseName
    databaseThroughput: databaseThroughput
    allowedIpRanges: allowedIpRanges
    allowAzureServices: deployDashboard && !empty(allowedIpRanges)
    tags: allTags
  }
}

module dashboard 'modules/dashboard.bicep' = if (deployDashboard) {
  name: 'cix-dashboard'
  params: {
    name: 'stapp-${namePrefix}-${environmentName}'
    location: dashboardLocation
    cosmosAccountName: cosmos.outputs.accountName
    databaseName: databaseName
    tags: allTags
  }
}

output cosmosAccountName string = cosmos.outputs.accountName
output cosmosEndpoint string = cosmos.outputs.endpoint
output databaseName string = databaseName
output staticWebAppName string = deployDashboard ? dashboard!.outputs.name : ''
output dashboardUrl string = deployDashboard ? dashboard!.outputs.url : ''
