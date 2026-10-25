// CIX's database in the shared Cosmos DB account (deployed into the shared resource group), with its own throughput
// shared by its two containers:
//   journal  the append-only history, partitioned by UTC day (/day)
//   state    what is true now, one document per id (/id)
// The engine can't create them itself in Azure (key-based metadata writes are disabled on the account), so a missing
// container means running this template again (tools/azure/provision.ps1).

param accountName string
param databaseName string
param databaseThroughput int

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: accountName
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

output endpoint string = account.properties.documentEndpoint
