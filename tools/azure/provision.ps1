#Requires -Version 5.1
<#
.SYNOPSIS
  Provisions CIX's Azure resources for one environment: Cosmos DB (free tier) and the read-only dashboard (Static Web Apps,
  Free). The engine and agents keep running in Docker on this PC.

.DESCRIPTION
  1. Checks the tools it needs (Azure CLI with Bicep; Node.js only for -DeployDashboard).
  2. Reads infra/environments/<Environment>.json, signs in with the Azure CLI and selects the subscription.
  3. Creates the resource group if it doesn't exist (after asking).
  4. Shows a what-if preview of infra/main.bicep, asks, then deploys it.
  5. Prints the endpoints, and (unless -SkipEngineConfig) writes the engine's settings: COSMOS_ENDPOINT and COSMOS_DATABASE
     to .env, and the account key to secrets/cosmos_key (readable only by you; never printed).
  6. With -DeployDashboard, publishes dashboard/ to the Static Web App.

  Nothing is deleted, and no data is copied: the engine starts with an empty paper account in Azure. Switching the engine to
  Azure is a separate, manual step (docs/azure.md).

.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -WhatIfOnly      # preview only
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev                  # deploy, then write the engine's settings
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -DeployDashboard -SkipInfrastructure   # publish a new dashboard version only
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [switch]$WhatIfOnly,
  [switch]$SkipInfrastructure,
  [switch]$SkipEngineConfig,
  [switch]$DeployDashboard,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

# Pinned: this code runs with the dashboard's deployment token in its environment. Update deliberately.
$SwaCliVersion = '2.0.1'

function Confirm-Step([string]$Question) {
  if ($Yes) { return }
  $answer = Read-Host "    $Question [y/N]"
  if ($answer -notmatch '^(y|yes)$') { throw 'Stopped: not confirmed.' }
}

Write-CixStep 'Checking prerequisites'
Assert-CixAzureCli
if ($DeployDashboard) {
  Assert-CixCommand -Name 'node' -Hint 'Install Node.js 22 LTS from https://nodejs.org (needed to publish the dashboard).'
  Assert-CixCommand -Name 'npm' -Hint 'It comes with Node.js.'
}

Write-CixStep "Environment '$Environment'"
$settings = Read-CixEnvironment -Name $Environment
Write-CixInfo "Settings:        $($settings.path)"
Write-CixInfo "Resource group:  $($settings.resourceGroup) ($($settings.location))"
Write-CixInfo "Cosmos DB:       free tier $($settings.cosmos.enableFreeTier), $($settings.cosmos.databaseThroughput) RU/s shared, database '$($settings.cosmos.databaseName)'"
Write-CixInfo "Dashboard:       $(if ($settings.deployDashboard) { "Static Web App (Free) in $($settings.dashboardLocation)" } else { 'not deployed' })"
if ($settings.protected) { Write-CixWarn 'This environment is marked protected (live money). Deploying changes its infrastructure.' }

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId

if (-not $SkipInfrastructure) {
  Write-CixStep 'Resource group'
  $exists = (Invoke-CixAz @('group', 'exists', '--name', $settings.resourceGroup)) -eq 'true'
  if ($exists) { Write-CixInfo "$($settings.resourceGroup) exists." }
  elseif ($WhatIfOnly) { Write-CixWarn "$($settings.resourceGroup) doesn't exist yet; the preview needs it. Run without -WhatIfOnly to create it."; return }
  else {
    Confirm-Step "Create resource group $($settings.resourceGroup) in $($settings.location) (subscription $($account.name))?"
    $tagArgs = @("cix-environment=$($settings.environmentName)")
    Invoke-CixAz (@('group', 'create', '--name', $settings.resourceGroup, '--location', $settings.location, '--tags') + $tagArgs + @('-o', 'none')) | Out-Null
    Write-CixInfo 'Created.'
  }

  Write-CixStep 'Preview (what-if)'
  Invoke-CixDeployment -Settings $settings -WhatIf | Out-Null
  if ($WhatIfOnly) { Write-CixInfo 'Preview only: nothing was changed.'; return }

  Write-CixStep 'Deploying'
  Write-CixWarn 'Cosmos DB free tier: one account per subscription. If another account already has it, set cosmos.enableFreeTier to false and expect charges (docs/azure.md).'
  Confirm-Step "Deploy these changes to $($settings.resourceGroup)?"
  $outputs = Invoke-CixDeployment -Settings $settings
  $cosmosAccount = $outputs.cosmosAccountName.value
  $cosmosEndpoint = $outputs.cosmosEndpoint.value
  $staticWebApp = $outputs.staticWebAppName.value
  $dashboardUrl = $outputs.dashboardUrl.value
}
else {
  $found = Get-CixCosmosAccount -Settings $settings
  $cosmosAccount = $found.name
  $cosmosEndpoint = $found.documentEndpoint
  $staticWebApp = "stapp-$($settings.namePrefix)-$($settings.environmentName)"
  $dashboardUrl = $null
}

Write-CixStep 'Resources'
Write-CixInfo "Cosmos DB account:  $cosmosAccount"
Write-CixInfo "Cosmos DB endpoint: $cosmosEndpoint"
Write-CixInfo "Database:           $($settings.cosmos.databaseName) (containers: journal, state)"
if ($dashboardUrl) { Write-CixInfo "Dashboard:          $dashboardUrl" }

if (-not $SkipEngineConfig) {
  Write-CixStep 'Engine configuration'
  $key = Get-CixCosmosKey -Settings $settings -AccountName $cosmosAccount
  $secretPath = Write-CixSecretFile -Name 'cosmos_key' -Value $key
  $key = $null
  Set-CixEnvValue -Key 'COSMOS_ENDPOINT' -Value $cosmosEndpoint
  Set-CixEnvValue -Key 'COSMOS_DATABASE' -Value $settings.cosmos.databaseName
  Write-CixInfo "Wrote the account key to $secretPath (readable only by you)."
  Write-CixInfo 'Wrote COSMOS_ENDPOINT and COSMOS_DATABASE to .env.'
}

if ($DeployDashboard) {
  Write-CixStep 'Publishing the dashboard'
  if (-not $settings.deployDashboard) { throw "deployDashboard is false in $($settings.path)." }
  $root = Get-CixRepoRoot
  $api = Join-CixPath $root 'dashboard' 'api'
  Push-Location $api
  try {
    Invoke-CixNative { & npm install --omit=dev --no-audit --no-fund --ignore-scripts }
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed for dashboard/api.' }
  }
  finally { Pop-Location }
  $env:SWA_CLI_DEPLOYMENT_TOKEN = Invoke-CixAz -Quiet @('staticwebapp', 'secrets', 'list', '--name', $staticWebApp, '--resource-group',
    $settings.resourceGroup, '--query', 'properties.apiKey', '-o', 'tsv')
  try {
    Invoke-CixNative {
      & npx --yes "@azure/static-web-apps-cli@$SwaCliVersion" deploy (Join-CixPath $root 'dashboard' 'app') --api-location $api `
        --api-language node --api-version 22 --env production
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the dashboard failed.' }
  }
  finally {
    Remove-Item Env:SWA_CLI_DEPLOYMENT_TOKEN -ErrorAction SilentlyContinue
  }
  $hostName = Invoke-CixAz @('staticwebapp', 'show', '--name', $staticWebApp, '--resource-group', $settings.resourceGroup, '--query', 'defaultHostname', '-o', 'tsv')
  Write-CixInfo "Dashboard published: https://$hostName"
}

Write-CixStep 'Next steps'
Write-CixInfo 'The engine still uses the local emulator until you switch it. To switch (it starts with a fresh paper account):'
Write-CixInfo '  docker compose stop'
Write-CixInfo '  docker compose -f docker-compose.yml -f docker-compose.azure.yml up -d --build'
Write-CixInfo '  docker compose ps        # the engine shows (healthy) once it has reached Azure'
if (-not $DeployDashboard -and $settings.deployDashboard) {
  Write-CixInfo "Publish the dashboard: .\tools\azure\provision.ps1 -Environment $Environment -DeployDashboard -SkipInfrastructure -SkipEngineConfig"
}
Write-CixInfo 'Details, costs and the reset procedure: docs/azure.md'
