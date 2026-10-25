// A Cosmos DB account shared by the owner's projects, in its own resource group (for example rg-shared). It holds no
// databases itself: each project adds its own database, with its own throughput, from its own template (CIX:
// infra/main.bicep). The free tier covers 1000 RU/s and 25 GB across the whole account, and only one account per
// subscription can have it, which is why it is shared. Deploy with tools/azure/provision.ps1 (settings in
// infra/shared/<name>.json). See docs/azure.md and ADR-0009.
targetScope = 'subscription'

@description('Resource group for shared resources, for example rg-shared.')
param resourceGroupName string

@description('Region for the resource group and the account.')
param location string

@description('Account name; empty gives a generic one, cosmos-shared-<unique suffix>. Lowercase letters, digits and hyphens, 3 to 44 characters, unique in Azure.')
param accountName string = ''

@description('Use the free tier (1000 RU/s and 25 GB free for the whole account). Only one account per subscription can have it.')
param enableFreeTier bool = true

@description('Public IP ranges allowed to reach the account, for example ["203.0.113.7"]. Empty allows any address; the account key is then the protection.')
param allowedIpRanges array = []

@description('Extra tags.')
param tags object = {}

// The scripts find the shared account by this tag, never by name alone.
var allTags = union(tags, {
  'shared-role': 'cosmos'
})

var name = empty(accountName) ? 'cosmos-shared-${uniqueString(subscription().id, resourceGroupName)}' : accountName

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: union(tags, { 'shared-role': 'shared' }) // replaces the group's tags on every deployment: shared.json owns them
}

module account 'modules/cosmos-account.bicep' = {
  name: 'shared-cosmos-account'
  scope: group
  params: {
    accountName: name
    location: location
    enableFreeTier: enableFreeTier
    allowedIpRanges: allowedIpRanges
    allowAzureServices: !empty(allowedIpRanges) // dashboards' managed APIs have no fixed address
    tags: allTags
  }
}

output accountName string = account.outputs.accountName
