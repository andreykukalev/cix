// The container registry alone (agents: registry), for an environment that runs no engine but still hosts an image: dev
// since ADR-0028, whose registry the stocks advisory builds its image in (infra/stocks). The same registry, by the same name,
// as agents-platform.bicep's, so an environment moves between registry and platform without losing its images; plus the
// monthly budget, when a contact address is given. Nothing else of the platform: no environment, logs, queues, Key Vault or
// identities (docs/azure.md, 'Removing the paper platform').

import { agentsNames } from 'agents-names.bicep'

param namePrefix string
param environmentName string

@description('Region for the registry.')
param location string

param tags object

@description('Monthly budget for CIX\'s resource group, in the billing currency.')
param monthlyBudget int

@description('The budget\'s first month (YYYY-MM-01). Fixed: a budget\'s start date can\'t be moved once created.')
param budgetStartDate string

@description('Where budget alerts go. Empty: no budget.')
param budgetContactEmail string

var names = agentsNames(namePrefix, environmentName, resourceGroup().id)

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = if (!empty(budgetContactEmail)) {
  name: names.budget
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [budgetContactEmail]
      }
    }
  }
}

output registryName string = registry.name
output registryServer string = registry.properties.loginServer
