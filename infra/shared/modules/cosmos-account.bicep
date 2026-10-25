// The shared Cosmos DB account (no databases: each project adds its own).

param accountName string
param location string
param enableFreeTier bool
param allowedIpRanges array
@description('Also accept connections from Azure datacenters. Only matters when allowedIpRanges is set.')
param allowAzureServices bool
param tags object

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  kind: 'GlobalDocumentDB'
  tags: tags
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: enableFreeTier
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
    // 0.0.0.0 is Cosmos DB's marker for "accept connections from Azure datacenters".
    ipRules: [for ip in concat(allowedIpRanges, allowAzureServices ? ['0.0.0.0'] : []): {
      ipAddressOrRange: ip
    }]
    // Keys can read and write documents, but not create or delete databases and containers: a leaked key can't drop a
    // project's data. Schema changes go through Azure Resource Manager (each project's template).
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuth: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
  }
}

output accountName string = account.name
output endpoint string = account.properties.documentEndpoint
