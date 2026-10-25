#Requires -Version 5.1
<#
.SYNOPSIS
  Provisions CIX's Azure resources for one environment, in two resource groups:
    - the shared Cosmos DB account (free tier) in its own group, for example rg-shared, created once and reused by other
      projects (infra/shared/main.bicep, settings in infra/shared/<name>.json);
    - CIX's own group, for example rg-cix-dev, with the read-only dashboard (Static Web Apps, Free), plus CIX's database in
      the shared account (infra/main.bicep, settings in infra/environments/<Environment>.json).
  The engine and agents keep running in Docker on this PC.

.DESCRIPTION
  1. Checks the tools it needs (Azure CLI with Bicep; Node.js only for -DeployDashboard).
  2. Reads the settings, signs in with the Azure CLI and selects the subscription.
  3. Shared account: if it doesn't exist yet (or with -UpdateShared), shows a what-if preview, asks, then deploys it.
     An existing shared account is left exactly as it is.
  4. CIX: shows a what-if preview, asks, then deploys its resource group, database and dashboard.
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
  .\tools\azure\provision.ps1 -Environment dev -DeployDashboard -SkipInfrastructure -SkipEngineConfig   # publish the dashboard only
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [switch]$WhatIfOnly,
  [switch]$SkipInfrastructure,
  [switch]$SkipEngineConfig,
  [switch]$DeployDashboard,
  [switch]$UpdateShared,
  [switch]$AllowProtectedInSharedAccount,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($WhatIfOnly) { $SkipEngineConfig = $true; $DeployDashboard = $false } # a preview changes nothing, here either
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

# Pinned: this code runs with the dashboard's deployment token in its environment. Update deliberately.
$SwaCliVersion = '2.0.1'

function Confirm-Step([string]$Question) {
  if ($Yes) { return }
  $answer = Read-Host "    $Question [y/N]"
  if ($answer -notmatch '^(y|yes)$') { Write-CixWarn 'Stopped: not confirmed (type y to go ahead). Nothing more was changed.'; exit 1 }
}

Write-CixStep 'Checking prerequisites'
Assert-CixAzureCli
if ($DeployDashboard) {
  Assert-CixCommand -Name 'node' -Hint 'Install Node.js 22 LTS from https://nodejs.org (needed to publish the dashboard).'
  Assert-CixCommand -Name 'npm' -Hint 'It comes with Node.js.'
}

Write-CixStep "Environment '$Environment'"
$settings = Read-CixEnvironment -Name $Environment
$shared = $settings.shared
Write-CixInfo "Settings:        $($settings.path)"
Write-CixInfo "Shared account:  resource group $($shared.resourceGroup) ($($shared.location)), free tier $($shared.enableFreeTier) ($($shared.path))"
Write-CixInfo "CIX:             resource group $($settings.resourceGroup) ($($settings.location)), database '$($settings.cosmos.databaseName)' at $($settings.cosmos.databaseThroughput) RU/s"
Write-CixInfo "Dashboard:       $(if ($settings.deployDashboard) { "Static Web App (Free) in $($settings.dashboardLocation)" } else { 'not deployed' })"
if ($settings.protected) {
  # The shared account's keys reach every project's database (ADR-0009): fine for paper and tests, not for live money.
  if (-not $AllowProtectedInSharedAccount) {
    throw "Environment '$Environment' is protected (live money), and its database would live in the shared account, whose keys every project holds. Revisit ADR-0009 first (a separate account, or per-project access), or pass -AllowProtectedInSharedAccount."
  }
  Write-CixWarn 'This environment is marked protected (live money). Deploying changes its infrastructure.'
}

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId

if (-not $SkipInfrastructure) {
  Write-CixStep "Shared Cosmos DB account ($($shared.resourceGroup))"
  $existing = Find-CixSharedCosmos -Settings $settings
  if ($existing -and -not $UpdateShared) {
    Write-CixInfo "Using $($existing.name); left unchanged (pass -UpdateShared to apply infra/shared/main.bicep to it)."
    $sharedName = $existing.name
  }
  else {
    $target = if ($existing) { $existing.name } else { '' }
    if ($existing) { Write-CixWarn "Updating $target, which other projects may use too. Its settings and tags become those in $($shared.path)." }
    Invoke-CixDeployment -Settings $settings -Shared -SharedAccountName $target -WhatIf | Out-Null
    if ($WhatIfOnly) {
      if (-not $existing) { Write-CixInfo "Preview only: nothing was changed. CIX's own preview needs the shared account; it shows once that exists."; return }
      $sharedName = $existing.name
    }
    else {
      Write-CixWarn 'Cosmos DB free tier: one account per subscription. If another account already has it, this fails: use another subscription, or set enableFreeTier to false in the shared settings and expect charges (docs/azure.md).'
      Confirm-Step "Deploy the shared account to $($shared.resourceGroup) (subscription $($account.name))?"
      $sharedName = (Invoke-CixDeployment -Settings $settings -Shared -SharedAccountName $target).accountName.value
      Write-CixInfo "Shared account: $sharedName"
    }
  }

  Write-CixStep "CIX ($($settings.resourceGroup), database '$($settings.cosmos.databaseName)')"
  Assert-CixDatabaseClaim -Settings $settings -AccountName $sharedName
  Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName -WhatIf | Out-Null
  if ($WhatIfOnly) { Write-CixInfo 'Preview only: nothing was changed.'; return }
  Confirm-Step "Deploy CIX to $($settings.resourceGroup) and its database to ${sharedName}?"
  $outputs = Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName
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
Write-CixInfo "Cosmos DB account:  $cosmosAccount (shared, in $($shared.resourceGroup))"
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
  Write-CixInfo "Wrote the account key to $secretPath (readable only by you). It is the shared account's key: it reaches every project's database there."
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
