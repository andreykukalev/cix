// Names shared by stocks-platform.bicep, which creates the resources, and stocks-job.bicep, which finds them by name.

@export()
@description('The names of the stocks advisory\'s resources in its resource group (its ID makes the globally unique ones unique).')
func stocksNames(namePrefix string, resourceGroupId string) object => {
  logs: 'log-${namePrefix}-stocks'
  appsEnvironment: 'cae-${namePrefix}-stocks'
  vault: take('kv${namePrefix}stocks${take(uniqueString(resourceGroupId), 6)}', 24)
  cosmos: take('cosmos-${namePrefix}-stocks-${take(uniqueString(resourceGroupId), 6)}', 44)
  identity: 'id-${namePrefix}-stocks-job'
  job: take('caj-${namePrefix}-stocks-daily', 32)
  budget: 'budget-${namePrefix}-stocks'
}

@export()
@description('The stocks database and its containers, all partitioned by /id (Cix.Stocks.Host.CosmosStocksStore).')
var stocksDatabase = 'cix-stocks'

@export()
@description('Where the EODHD key is kept in the vault, and the file the job reads it from.')
var eodhdSecret = 'eodhd-api-token'
