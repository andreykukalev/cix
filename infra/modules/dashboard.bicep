// The CIX dashboard: a Static Web App on the Free plan. Its managed API (Azure Functions, served under /api) reads CIX's
// database with the shared account's read-only key, kept in the app's settings on the server side; the browser never sees
// it. The key is account-wide (it could read other projects' databases too); the API only ever reads CIX's containers.

param name string
param location string
param cosmosAccountName string
param cosmosResourceGroupName string
param databaseName string
param tags object

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
  scope: resourceGroup(cosmosResourceGroupName)
}

resource site 'Microsoft.Web/staticSites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    stagingEnvironmentPolicy: 'Disabled' // no preview environments: one dashboard, one set of settings
    allowConfigFileUpdates: true
  }
}

resource settings 'Microsoft.Web/staticSites/config@2024-04-01' = {
  parent: site
  name: 'appsettings'
  properties: {
    COSMOS_ENDPOINT: cosmos.properties.documentEndpoint
    COSMOS_DATABASE: databaseName
    // Read-only: the dashboard can't change trading state even if its code were wrong.
    COSMOS_READONLY_KEY: cosmos.listKeys().primaryReadonlyMasterKey
  }
}

output name string = site.name
output url string = 'https://${site.properties.defaultHostname}'
