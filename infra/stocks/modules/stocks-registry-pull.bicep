// Deployed into the CIX registry's resource group (the dev environment's): the stocks job's identity may pull images from
// that registry, nothing else. Pushing the stocks image is the owner's (provision-stocks.ps1).

@description('The CIX registry\'s name, in this resource group.')
param registryName string

@description('The stocks job identity\'s principal (object) ID.')
param principalId string

var acrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, principalId, acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

output registryName string = registry.name
output registryServer string = registry.properties.loginServer
