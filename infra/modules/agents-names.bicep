// Names shared by agents-platform.bicep, which creates these resources, and agents-apps.bicep, which finds them by name.
// Finding them by name, rather than through the platform module's outputs, keeps every value the apps' module is given
// known before the deployment starts, so the preview (what-if) shows the engine and the jobs in full.

@export()
@description('Each job, the queue it takes runs from, and the role its identity has at the engine (ToolServer:Entra:Agents).')
var agentJobs = [
  { key: 'trader', queue: 'runs-trader', role: 'trader' }
  { key: 'requests', queue: 'runs-requests', role: 'trader-requests' }
  { key: 'analyst', queue: 'runs-analyst', role: 'analyst' }
]

@export()
@description('Where a job parks a run message it couldn\'t take.')
var poisonQueue = 'runs-poison'

@export()
@description('The names of the agents\' resources in a resource group (its ID makes the globally unique ones unique).')
func agentsNames(namePrefix string, environmentName string, resourceGroupId string) object => {
  logs: 'log-${namePrefix}-${environmentName}'
  vnet: 'vnet-${namePrefix}-${environmentName}'
  appsEnvironment: 'cae-${namePrefix}-${environmentName}'
  registry: take('cr${namePrefix}${replace(environmentName, '-', '')}${take(uniqueString(resourceGroupId), 6)}', 50)
  storage: take('st${namePrefix}${replace(environmentName, '-', '')}${take(uniqueString(resourceGroupId), 6)}', 24)
  vault: take('kv${namePrefix}${replace(environmentName, '-', '')}${take(uniqueString(resourceGroupId), 6)}', 24)
  budget: 'budget-${namePrefix}-${environmentName}'
  // Identities: identityPrefix + 'engine', or + a job's key.
  identityPrefix: 'id-${namePrefix}-${environmentName}-'
  engineApp: take('ca-${namePrefix}-${environmentName}-engine', 32)
  // Jobs: take(jobPrefix + a job's key, 32).
  jobPrefix: 'caj-${namePrefix}-${environmentName}-'
}
