// What the engine in Azure may do in CIX's database, with its managed identity instead of the account key (deployed into
// the shared resource group, where the account is). Cosmos DB's own data-plane roles, one per container, each assignable
// on its own container only (so the state role can't be assigned over the journal) and assigned there:
//   journal   query and add entries: never replace or delete one (the journal is append-only)
//   state     read, create, replace and upsert documents: no delete
//   requests  read, query and replace (the engine records what it did with a request): no create, no delete
// None of them can create or delete a database or container, change throughput, or reach another project's database in
// the shared account; the account key stays out of the engine's reach. A database of another engine in this account gets
// roles of its own, named after its database.

param accountName string

@minLength(1)
param databaseName string

@description('The engine identity\'s principal (object) ID.')
param enginePrincipalId string

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: accountName
}

var databaseScope = '${account.id}/dbs/${databaseName}'

// Each container gets only what the engine does there (src/Cix.Engine/Persistence): readMetadata for the SDK's reads of
// container settings and partitions, and queries need the change feed action as well.
var item = 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items'
var metadata = 'Microsoft.DocumentDB/databaseAccounts/readMetadata'
var query = [
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery'
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed'
]

var access = [
  {
    container: 'journal' // appends and queries; never reads one entry by id
    role: 'appender'
    actions: concat([metadata, '${item}/create'], query)
  }
  {
    container: 'state' // reads, creates and replaces documents by id (with ETags), and upserts; never queries
    role: 'writer'
    actions: [metadata, '${item}/read', '${item}/create', '${item}/replace', '${item}/upsert']
  }
  {
    container: 'requests' // reads, queries and replaces requests (with ETags); the dashboard's writer creates them
    role: 'updater'
    actions: concat([metadata, '${item}/read', '${item}/replace'], query)
  }
]

// One at a time: Cosmos DB refuses a role change while another one on the account is in progress.
@batchSize(1)
resource roles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = [
  for a in access: {
    parent: account
    name: guid(account.id, databaseName, 'cix-engine', a.container)
    properties: {
      roleName: 'CIX engine ${databaseName} ${a.container} ${a.role}'
      type: 'CustomRole'
      assignableScopes: ['${databaseScope}/colls/${a.container}']
      permissions: [{ dataActions: a.actions }]
    }
  }
]

@batchSize(1)
resource assignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for (a, i) in access: {
    parent: account
    dependsOn: [roles] // after every role, not only its own
    name: guid(account.id, databaseName, enginePrincipalId, a.container)
    properties: {
      roleDefinitionId: roles[i].id
      principalId: enginePrincipalId
      scope: '${databaseScope}/colls/${a.container}'
    }
  }
]
