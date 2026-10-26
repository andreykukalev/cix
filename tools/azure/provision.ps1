#Requires -Version 5.1
<#
.SYNOPSIS
  Provisions CIX's Azure resources for one environment, in two resource groups:
    - the shared Cosmos DB account (free tier) in its own group, for example rg-shared, created once and reused by other
      projects (infra/shared/main.bicep, settings in infra/shared/<name>.json);
    - CIX's own group, for example rg-cix-dev, with the dashboard (Static Web Apps, Free), plus CIX's database in
      the shared account (infra/main.bicep, settings in infra/environments/<Environment>.json).
  The engine and agents keep running in Docker on this PC, unless agents.deploy in the settings says otherwise (ADR-0013):
    - platform: what the engine and the agent jobs run on (registry, queues, Key Vault, environment, logs, budget);
    - apps: also the paper engine and the three jobs. Needs platform deployed first, claude-token in its Key Vault
      (uploaded from secrets/claude_token, which tools/setup-secrets.ps1 writes, without showing it), and images CI pushed.
      Creates the engine's Entra app registration (cix-engine-<environment>) when missing.

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
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -SkipEngineConfig -ImageTag <commit SHA>   # agents.deploy apps, first time
.PARAMETER ImageTag
  agents.deploy apps: the image tag to run. Without it, the tag the engine runs now is kept (CI deploys new ones).
.PARAMETER UploadClaudeToken
  agents.deploy apps: upload secrets/claude_token to Key Vault even if claude-token is there already (after renewing it).
.PARAMETER BudgetEmail
  agents: where the budget's alerts go. Without it, your sign-in's email address (a guest or personal account may have
  none that receives mail; then pass this). Not kept in the repo.
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
  [ValidatePattern('^[A-Za-z0-9._-]{1,128}$')][string]$ImageTag,
  [switch]$UploadClaudeToken,
  [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')][string]$BudgetEmail,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($WhatIfOnly) { $SkipEngineConfig = $true; $DeployDashboard = $false } # a preview changes nothing, here either
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

# Pinned: this code runs with the dashboard's deployment token in its environment. Update deliberately, together with
# SWA_CLI_VERSION in .github/workflows/ci.yml (deploy-dashboard); tests/azure checks they match.
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
$agents = if ($settings.ContainsKey('agents')) { $settings.agents.deploy } else { 'none' }
Write-CixInfo "Engine, agents:  $(switch ($agents) { 'none' { 'not in Azure (agents.deploy none)' } 'platform' { "what they run on, in $($settings.agents.location) (no apps yet)" } default { "the paper engine and three jobs, in $($settings.agents.location)" } })"
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

  # The engine and agents (agents.deploy): checks and values found now, before the preview.
  $agentValues = @{}
  if ($agents -ne 'none') {
    Write-CixStep "Engine and agents ($agents, in $($settings.agents.location))"
    Assert-CixAgentsTools
    Assert-CixAppsRegion -Settings $settings
    $me = Get-CixSignedInUser
    $agentValues['ownerObjectId'] = $me.Id
    $agentValues['budgetContactEmail'] = if ($BudgetEmail) { $BudgetEmail } else { $me.Email }
    if (-not $agentValues.budgetContactEmail) { Write-CixWarn 'Your sign-in has no email address that receives mail, so no budget is created. Pass -BudgetEmail to get one.' }
    else {
      Assert-CixBudgetStart -Settings $settings
      Write-CixInfo "Budget: $($settings.agents.monthlyBudget) a month on $($settings.resourceGroup), alerts to $($agentValues.budgetContactEmail)."
    }
    if ($agents -eq 'apps') {
      $vault = Find-CixAgentsResource -Settings $settings -Kind keyvault
      $registry = Find-CixAgentsResource -Settings $settings -Kind acr
      if (-not $vault -or -not $registry) { throw "Deploy with agents.deploy 'platform' first: the apps need its Key Vault (with claude-token) and its registry (with images from CI)." }
      # Checked before anything is changed: the images...
      $tag = if ($ImageTag) { $ImageTag } else { Get-CixRunningImageTag -Settings $settings }
      if (-not $tag) { throw 'Nothing runs yet, so there is no image to keep: pass -ImageTag with a tag CI pushed (the commit SHA).' }
      Assert-CixImages -Registry $registry -Tag $tag
      $agentValues['imageTag'] = $tag
      Write-CixInfo "Images: $registry, tag $tag. Engine: $(if ($settings.agents.startEngine) { 'running (one replica)' } else { 'stopped (agents.startEngine false)' })."
      # ...then the Claude credential: uploaded from secrets/claude_token (tools/setup-secrets.ps1 writes it) straight to Key Vault.
      if ($UploadClaudeToken -or -not (Test-CixVaultSecret -Vault $vault -Name 'claude-token')) {
        $local = Join-CixPath (Get-CixRepoRoot) 'secrets' 'claude_token'
        if (-not (Test-Path $local)) { throw "Key Vault $vault has no claude-token. Run tools/setup-secrets.ps1 (it writes secrets/claude_token), then run this again: it uploads the file without showing it." }
        if ($WhatIfOnly) { Write-CixInfo "Would upload secrets/claude_token to $vault as claude-token." }
        else {
          Confirm-Step "Upload secrets/claude_token to Key Vault $vault as claude-token?"
          Set-CixVaultSecretFromFile -Vault $vault -Name 'claude-token' -Path $local
          Write-CixInfo "Uploaded claude-token to $vault (not shown)."
        }
      }
      # ...then the engine's Entra app registration, made only once you have said yes.
      $audience = Get-CixEngineAudience -Settings $settings -OwnerId $me.Id
      if (-not $audience -and -not $WhatIfOnly) {
        Confirm-Step "Create the engine's Entra app registration cix-engine-$($settings.environmentName) (single tenant, no secrets, no permissions)?"
        $audience = Get-CixEngineAudience -Settings $settings -OwnerId $me.Id -Create
      }
      if (-not $audience) {
        $audience = 'api://00000000-0000-0000-0000-000000000000'
        Write-CixInfo "The engine's Entra app (cix-engine-$($settings.environmentName)) is created when you deploy; the preview uses a placeholder."
      }
      $agentValues['engineAudience'] = $audience
    }
  }

  Write-CixStep "CIX ($($settings.resourceGroup), database '$($settings.cosmos.databaseName)')"
  Assert-CixDatabaseClaim -Settings $settings -AccountName $sharedName
  Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName -Agents $agentValues -WhatIf | Out-Null
  if ($WhatIfOnly) { Write-CixInfo 'Preview only: nothing was changed.'; return }
  Confirm-Step "Deploy CIX to $($settings.resourceGroup) and its database to ${sharedName}?"
  # The deployment sets the dashboard's settings to exactly what infra/ says, which would drop the writer identity's and the
  # confirmed owner (setup-dashboard-requests.ps1): keep them in memory and put them back. They are never printed.
  $writerSettings = @{}
  $siteId = $null
  if ($settings.deployDashboard) {
    $found = Invoke-CixNative { & az staticwebapp show --name "stapp-$($settings.namePrefix)-$($settings.environmentName)" --resource-group $settings.resourceGroup --query id -o tsv --only-show-errors 2>$null }
    if ($LASTEXITCODE -eq 0 -and "$found" -match '^/subscriptions/') {
      $siteId = "$found".Trim()
      try { $current = Get-CixStaticSiteSettings -SiteId $siteId }
      catch { throw "Couldn't read the dashboard's settings (to keep the writer identity's through the deployment); nothing was deployed." }
      foreach ($k in @($current.Keys | Where-Object { $_ -like 'COSMOS_WRITER_*' -or $_ -eq 'DASHBOARD_OWNER_USER_ID' })) { $writerSettings[$k] = $current[$k] }
    }
  }
  try { $outputs = Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName -Agents $agentValues }
  catch {
    # The jobs' access to claude-token is granted in the same deployment that creates them, and takes minutes to apply.
    if ($agents -eq 'apps' -and "$($_.Exception.Message)" -match 'claude-token') {
      throw "$($_.Exception.Message) The jobs' access to claude-token was just granted and can take a few minutes to apply: run this again shortly."
    }
    # Cosmos DB checks a role assignment's principal in Entra, which may not know an identity created moments earlier.
    if ($agents -ne 'none' -and "$($_.Exception.Message)" -match '(?i)sqlRoleAssignments|principal') {
      throw "$($_.Exception.Message) If this is about the engine's identity, it was just created and may not be known yet: run this again shortly."
    }
    throw
  }
  if ($writerSettings.Count -gt 0) {
    try {
      $now = Get-CixStaticSiteSettings -SiteId $siteId
      foreach ($k in $writerSettings.Keys) { $now[$k] = $writerSettings[$k] }
      Set-CixStaticSiteSettings -SiteId $siteId -Settings $now
      Write-CixInfo "Kept the dashboard's writer settings ($(($writerSettings.Keys | Sort-Object) -join ', '))."
    }
    catch { throw "Deployed, but couldn't put the dashboard's writer settings back; owner requests fail until you run tools/azure/setup-dashboard-requests.ps1 -Environment $Environment -OwnerAccount <you> -RotateSecret." }
    finally { $writerSettings = $null; $now = $null; $current = $null }
  }
  $cosmosAccount = $outputs.cosmosAccountName.value
  $cosmosEndpoint = $outputs.cosmosEndpoint.value
  $staticWebApp = $outputs.staticWebAppName.value
  $dashboardUrl = $outputs.dashboardUrl.value
  if ($agents -ne 'none') {
    Write-CixInfo "Registry:  $($outputs.registryServer.value)"
    Write-CixInfo "Key Vault: $($outputs.keyVaultName.value)"
    if ($agents -eq 'apps') { Write-CixInfo "Engine app: $($outputs.engineAppName.value); jobs: $(@($outputs.agentJobNames.value) -join ', ')" }
    # The template before the engine signed in with its identity copied the shared account's read-write key into Key Vault
    # as cosmos-key, readable by the engine. A deployment doesn't remove what a template no longer has: do it here.
    $vaultName = "$($outputs.keyVaultName.value)"
    if ($vaultName -and (Test-CixVaultSecret -Vault $vaultName -Name 'cosmos-key')) {
      Write-CixWarn "Key Vault $vaultName still holds cosmos-key, the shared Cosmos DB account's read-write key, from an earlier template. Nothing uses it now."
      Confirm-Step "Delete cosmos-key from $vaultName, and the engine's access to it?"
      Remove-CixVaultSecretAndAccess -Vault $vaultName -Name 'cosmos-key'
      Write-CixWarn "Deleted. If the engine app ever ran while it was there, an image CI deployed could have read the key: regenerate the shared account's primary key (docs/azure.md, 'If you suspect misuse')."
    }
  }
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
Write-CixInfo "Database:           $($settings.cosmos.databaseName) (containers: journal, state, requests)"
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
