// The stocks advisory in Azure (ADR-0023): its own resource group, apart from CIX's crypto groups, with
//   - its own Cosmos DB account, serverless (billed per request: cents a month at one run a night), with the cix-stocks
//     database the dashboard's Stocks tab reads, and where the dashboard's writer identity creates the owner's portfolio
//     submissions (ADR-0026);
//   - its own Key Vault (the EODHD key, set by the owner, never by a template), identity, logs and Container Apps
//     environment;
//   - the nightly job (stocks/src/Cix.Stocks.Host), once an image is given.
// The image lives in the dev CIX registry, which the job's identity may only pull from (the owner chose to reuse it rather
// than pay for another). Nothing here touches the crypto engines, their databases or their apps. The job never trades: the
// owner carries the advice out by hand.
// Deploy with tools/azure/provision-stocks.ps1, which reads infra/stocks/stocks.json.
targetScope = 'subscription'

@description('The stocks advisory\'s resource group, for example rg-cix-stocks.')
param resourceGroupName string

@description('Region for everything in the group.')
param location string

@description('Short prefix for resource names.')
@minLength(2)
@maxLength(8)
param namePrefix string = 'cix'

@description('The resource group of the CIX registry the image is pulled from (the dev environment\'s).')
param registryResourceGroupName string

@description('That registry\'s name (provision-stocks.ps1 finds it by its cix-environment tag), the one the image is built in.')
param registryName string

@description('The job image\'s digest (sha256:...) in the registry\'s cix-stocks repository. Empty: no job yet.')
param imageDigest string = ''

@description('When the job runs, as a cron expression in UTC: after each US session\'s data is ready (stocks.json\'s dataReadyUtc).')
param schedule string = '0 2 * * 2-6'

@description('Your Entra object ID (provision-stocks.ps1 passes your sign-in\'s), given Key Vault Secrets Officer to set the EODHD key. Empty: no grant.')
param ownerObjectId string = ''

@description('Monthly budget for the group, in the billing currency.')
@minValue(1)
param monthlyBudget int = 5

@description('The budget\'s first month (YYYY-MM-01); it can\'t be moved once the budget exists.')
param budgetStartDate string = '2026-11-01'

@description('Where budget alerts go (provision-stocks.ps1 passes your sign-in address). Empty: no budget.')
param budgetContactEmail string = ''

@description('The dashboard writer\'s service principal object ID (provision-stocks.ps1 finds it from the dashboard\'s settings), allowed to create the owner\'s portfolio submissions only. Empty: no grant, and the Stocks page can\'t send the portfolio.')
param writerPrincipalId string = ''

@description('Extra tags.')
param tags object = {}

var allTags = union(tags, {
  'cix-component': 'stocks'
})

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: allTags
}

module platform 'modules/stocks-platform.bicep' = {
  name: 'cix-stocks-platform'
  scope: group
  params: {
    namePrefix: namePrefix
    location: location
    tags: allTags
    ownerObjectId: ownerObjectId
    monthlyBudget: monthlyBudget
    budgetStartDate: budgetStartDate
    budgetContactEmail: budgetContactEmail
    writerPrincipalId: writerPrincipalId
  }
}

// The job's identity may pull images from the CIX registry, and do nothing else there.
module registryPull 'modules/stocks-registry-pull.bicep' = {
  name: 'cix-stocks-registry-pull'
  scope: resourceGroup(registryResourceGroupName)
  params: {
    registryName: registryName
    principalId: platform.outputs.jobPrincipalId
  }
}

module job 'modules/stocks-job.bicep' = if (!empty(imageDigest)) {
  name: 'cix-stocks-job'
  scope: group
  params: {
    namePrefix: namePrefix
    location: location
    tags: allTags
    registryServer: registryPull.outputs.registryServer
    imageDigest: imageDigest
    schedule: schedule
  }
}

output cosmosAccountName string = platform.outputs.cosmosAccountName
output cosmosEndpoint string = platform.outputs.cosmosEndpoint
output databaseName string = platform.outputs.databaseName
output keyVaultName string = platform.outputs.keyVaultName
output registryName string = registryPull.outputs.registryName
output jobName string = !empty(imageDigest) ? job!.outputs.jobName : ''
