// What the engine in Azure may do in CIX's database, with its managed identity instead of the account key (deployed into
// the shared resource group, where the account is). Cosmos DB's own data-plane roles, one per container, each assignable
// within CIX's database only and assigned on its container:
//   journal   read, query and add entries: never replace or delete one (the journal is append-only)
//   state     read, query, create, replace and upsert documents: no delete
//   requests  read, query and replace (the engine records what it did with a request): no create, no delete
// None of them can create or delete a database or container, change throughput, or reach another project's database in
// the shared account; the account key stays out of the engine's reach. A database of another engine in this account gets
// roles of its own, named after its database.

param accountName string
param databaseName string

@description('The engine identity\'s principal (object) ID.')
param enginePrincipalId string

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: accountName
}

var databaseScope = '${account.id}/dbs/${databaseName}'

// The SDK's metadata reads (container settings, partitions), and queries, which the SDKs run through the change feed
// action as well.
var readActions = [
  'Microsoft.DocumentDB/databaseAccounts/readMetadata'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed'
]

var access = [
  {
    container: 'journal'
    role: 'appender'
    actions: concat(readActions, ['Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create'])
  }
  {
    container: 'state'
    role: 'writer'
    actions: concat(readActions, [
      'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create'
      'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace'
      'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/upsert'
    ])
  }
  {
    container: 'requests'
    role: 'updater'
    actions: concat(readActions, ['Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace'])
  }
]

resource roles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = [
  for a in access: {
    parent: account
    name: guid(account.id, databaseName, 'cix-engine', a.container)
    properties: {
      roleName: 'CIX engine ${databaseName} ${a.container} ${a.role}'
      type: 'CustomRole'
      assignableScopes: [databaseScope]
      permissions: [{ dataActions: a.actions }]
    }
  }
]

resource assignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for (a, i) in access: {
    parent: account
    name: guid(account.id, databaseName, enginePrincipalId, a.container)
    properties: {
      roleDefinitionId: roles[i].id
      principalId: enginePrincipalId
      scope: '${databaseScope}/colls/${a.container}'
    }
  }
]
