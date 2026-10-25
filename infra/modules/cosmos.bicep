// Cosmos DB for CIX: one account, one database with shared throughput, and the two containers the engine uses.
//   journal  the append-only history, partitioned by UTC day (/day)
//   state    what is true now, one document per id (/id)
// The engine creates them itself when missing, but here they are created first, with the database's shared throughput,
// so nothing gets its own (billed) throughput.

param accountName string
param location string
param enableFreeTier bool
param databaseName string
param databaseThroughput int
param allowedIpRanges array
@description('Also accept connections from Azure datacenters (the dashboard\'s managed API has no fixed address). Only matters when allowedIpRanges is set.')
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
    // Keys can read and write documents, but not create or delete databases and containers: a leaked key can't drop the
    // journal. Schema changes go through Azure Resource Manager (this template, or the reset script with -ResetSchema).
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuth: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
    options: {
      throughput: databaseThroughput
    }
  }
}

resource journal 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'journal'
  properties: {
    resource: {
      id: 'journal'
      partitionKey: {
        paths: ['/day']
        kind: 'Hash'
      }
    }
  }
}

resource state 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'state'
  properties: {
    resource: {
      id: 'state'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
    }
  }
}

output accountName string = account.name
output endpoint string = account.properties.documentEndpoint
