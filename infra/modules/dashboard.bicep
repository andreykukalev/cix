// The CIX dashboard: a Static Web App on the Free plan. Its managed API (Azure Functions, served under /api) reads the
// database with the account's read-only key, kept in the app's settings on the server side; the browser never sees it.

param name string
param location string
param cosmosAccountName string
param databaseName string
param tags object

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
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
