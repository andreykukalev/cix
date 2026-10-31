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
  Azure is a separate, manual step (docs/azure.md). The one exception: a Container Apps environment still on its own VNet
  (deployed before 2026-10-28) is deleted with its apps and VNet, after you confirm it, and recreated without one.

.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -WhatIfOnly      # preview only
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev                  # deploy, then write the engine's settings
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -DeployDashboard -SkipInfrastructure -SkipEngineConfig   # publish the dashboard only
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment dev -SkipEngineConfig -ImageTag <commit SHA>   # agents.deploy apps, first time
.EXAMPLE
  .\tools\azure\provision.ps1 -Environment prod -PromoteFrom dev -ImageTag <commit SHA>   # the protected environment (ADR-0017)
.PARAMETER ImageTag
  agents.deploy apps: the images to run, by the tag CI gave them (the commit SHA). They are deployed by digest, so what runs
  can't change if the tag is pushed again. Without it, the images the engine and the jobs run now are kept (CI deploys new ones).
.PARAMETER PromoteFrom
  A protected environment only, with -ImageTag: first copy the images CI tagged with it from that (paper) environment's
  registry into this one's, by digest, and deploy those. Live runs only images that ran on paper; CI deploys new ones there only
  after the owner approves each run (ADR-0019).
.PARAMETER ExchangeApiKeyFile
  A protected environment only: upload this file (the exchange API key) to its Key Vault as revolutx-api-key, without showing
  it. With -ExchangePrivateKeyFile, before the apps are deployed, and again to rotate (docs/live-trading.md).
.PARAMETER ExchangePrivateKeyFile
  A protected environment only: upload this file (the Ed25519 private key, PEM) as revolutx-private-key, without showing it.
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
  [ValidatePattern('^[A-Za-z0-9._-]{1,128}$')][string]$ImageTag,
  [ValidatePattern('^[a-z0-9-]{2,12}$')][string]$PromoteFrom,
  [string]$ExchangeApiKeyFile,
  [string]$ExchangePrivateKeyFile,
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
Write-CixInfo "Engine, agents:  $(switch ($agents) { 'none' { 'not in Azure (agents.deploy none)' } 'platform' { "what they run on, in $($settings.agents.location) (no apps yet)" } default { "$(if ($settings.protected) { 'the engine (on standby until you switch it)' } else { 'the paper engine' }) and three jobs, in $($settings.agents.location)" } })"
$exchangeKeyFiles = @{ ExchangeApiKeyFile = $ExchangeApiKeyFile; ExchangePrivateKeyFile = $ExchangePrivateKeyFile }
if (-not $settings.protected -and ($PromoteFrom -or $ExchangeApiKeyFile -or $ExchangePrivateKeyFile)) {
  throw "-PromoteFrom, -ExchangeApiKeyFile and -ExchangePrivateKeyFile are for a protected environment only; '$Environment' isn't one."
}
if (($PromoteFrom -or $ExchangeApiKeyFile -or $ExchangePrivateKeyFile) -and $agents -ne 'apps') {
  throw "-PromoteFrom, -ExchangeApiKeyFile and -ExchangePrivateKeyFile go with agents.deploy apps; $($settings.path) says $agents."
}
if ($PromoteFrom -and -not $ImageTag) { throw '-PromoteFrom needs -ImageTag: the tag CI gave the images to promote (the commit SHA).' }
foreach ($name in @($exchangeKeyFiles.Keys)) {
  $file = $exchangeKeyFiles[$name]
  if (-not $file) { continue }
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "-$name ${file}: no such file." }
  # A byte-order mark would stay in the secret and break the engine's signatures: only the first three bytes are read.
  $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $file).ProviderPath)
  try { $head = New-Object byte[] 3; $read = $stream.Read($head, 0, 3) } finally { $stream.Dispose() }
  if ($read -eq 3 -and $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF) { throw "-$name ${file} starts with a UTF-8 byte-order mark: save it without one (docs/live-trading.md)." }
}
if ($settings.protected) {
  # Real money (ADR-0017). Its database is its own, in the shared account (owner decision 5): the account's keys still reach it.
  Write-CixWarn 'This environment is protected (real money). Deploying changes its infrastructure. Its engine stays pinned to paper and stands by with the exchange keys until you switch it (docs/live-trading.md).'
  Write-CixWarn "Its database is its own, in the shared account, whose read-write key reaches every database there (docs/research/azure-hosting.md, 'Risk that remains')."
  if (-not $SkipEngineConfig) { Write-CixInfo 'The engine settings on this PC (.env, secrets/cosmos_key) stay as they are: they are for paper.'; $SkipEngineConfig = $true }
}

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
if ($settings.protected) {
  # CI and the agents have no role here (ADR-0017), but for the live deploy's expected roles (ADR-0019): anyone but you and
  # the environment's own identities who holds a role on the group, in it or above it is shown. -Yes never goes on past
  # them: you decide, having seen them.
  $outside = @(Get-CixOutsideAccess -Settings $settings -SubscriptionId $account.id -OwnerId (Get-CixSignedInUser).Id)
  if ($outside.Count -gt 0) {
    $list = "Others with roles on $($settings.resourceGroup), in it or above it: $($outside -join '; ')."
    if ($Yes) { throw "$list A CI or agent identity among them could reach the real-money engine. Remove them, or run without -Yes to go on after seeing them." }
    Write-CixWarn "$list A CI or agent identity among them could reach the real-money engine."
    Confirm-Step 'Go on anyway?'
  }
}

$rebuild = $null # the old VNet environment to replace, if any (below)
if (-not $SkipInfrastructure) {
  # An environment still on its own VNet (before 2026-10-28) leaves it by being rebuilt: found now, deleted only once you say yes.
  $rebuild = if ($agents -ne 'none') { Get-CixVnetRebuild -Settings $settings } else { $null }
  if ($rebuild) {
    if ($agents -ne 'apps' -and $rebuild.Apps.Count -gt 0) { throw "The Container Apps environment is on a VNet and has apps, but $($settings.path) says agents.deploy ${agents}: set it to apps, so the rebuild recreates them." }
    $what = @(@($rebuild.Apps) + @($rebuild.Environment, $rebuild.Vnet) | Where-Object { $_ } | ForEach-Object { $_.Split('/')[-1] }) -join ', '
    Write-CixWarn "The Container Apps environment is on its own VNet, which costs a load balancer and a static IP (about 22 USD a month) and can't be removed in place. Before deploying, this deletes $what; the deployment recreates the environment and the apps without a VNet."
    if ($settings.protected) {
      if ($Yes -and -not $WhatIfOnly) { throw 'Rebuilding the protected environment stops its engine: run without -Yes, to confirm it yourself.' }
      Write-CixWarn 'The live engine is gone while it is rebuilt (10-20 minutes): no stop-loss checks or protective exits run then. Do it with no open positions, or accept the gap. Afterwards it stands by in paper until you switch it on again.'
    }
  }

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

  # The database claim and the engine and agents (agents.deploy): checks and values found now, before any change.
  Assert-CixDatabaseClaim -Settings $settings -AccountName $sharedName
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
      # A protected environment's engine mounts both exchange keys: given now, or in Key Vault already. Checked before any change.
      if ($settings.protected) {
        foreach ($key in Get-CixExchangeKeySecrets) {
          if (-not $exchangeKeyFiles[$key.Parameter] -and -not (Test-CixVaultSecret -Vault $vault -Name $key.Name)) {
            throw "Key Vault $vault has no $($key.Name). Pass -ExchangeApiKeyFile and -ExchangePrivateKeyFile (docs/live-trading.md); they are uploaded without being shown."
          }
        }
      }
      $claudeToken = Join-CixPath (Get-CixRepoRoot) 'secrets' 'claude_token'
      $uploadClaudeToken = $UploadClaudeToken -or -not (Test-CixVaultSecret -Vault $vault -Name 'claude-token')
      if ($uploadClaudeToken -and -not (Test-Path $claudeToken)) { throw "Key Vault $vault has no claude-token. Run tools/setup-secrets.ps1 (it writes secrets/claude_token), then run this again: it uploads the file without showing it." }
      # The engine's Entra app registration, looked up now (it is created only once you have said yes, below).
      $audience = Get-CixEngineAudience -Settings $settings -OwnerId $me.Id
      # Checked before anything is changed: the images (for a protected environment, copied by digest from paper's registry)...
      if ($PromoteFrom) {
        $source = Read-CixEnvironment -Name $PromoteFrom
        if ($source.protected) { throw "-PromoteFrom $PromoteFrom is protected: promote from the paper environment." }
        $sourceRegistry = Find-CixAgentsResource -Settings $source -Kind acr
        if (-not $sourceRegistry) { throw "Environment '$PromoteFrom' has no registry to promote from." }
        $promoted = Resolve-CixImageDigests -Settings $source -Registry $sourceRegistry -Tag $ImageTag
        # Only what paper runs now: a tag CI pushed again, or an image that never ran there, isn't promoted.
        $onPaper = Resolve-CixImageDigests -Settings $source -Registry $sourceRegistry
        if (-not $onPaper -or $onPaper.Engine -ne $promoted.Engine -or $onPaper.Agent -ne $promoted.Agent) {
          throw "$PromoteFrom doesn't run the images tagged $ImageTag now$(if ($onPaper) { " (it runs cix-engine@$($onPaper.Engine), cix-trader@$($onPaper.Agent))" }): promote the images paper runs. Nothing was changed."
        }
        foreach ($image in @(@{ Repository = 'cix-engine'; Digest = $promoted.Engine }, @{ Repository = 'cix-trader'; Digest = $promoted.Agent })) {
          $from = "$($sourceRegistry.ToLowerInvariant()).azurecr.io/$($image.Repository)@$($image.Digest)"
          if ($WhatIfOnly) { Write-CixInfo "Would copy $from to $registry as $($image.Repository):$ImageTag."; continue }
          Confirm-Step "Copy $from to $registry as $($image.Repository):${ImageTag}?"
          Invoke-CixAz -Quiet @('acr', 'import', '--name', $registry, '--source', $from, '--image', "$($image.Repository):$ImageTag", '--force', '--only-show-errors') | Out-Null
        }
      }
      $images = if ($PromoteFrom -and $WhatIfOnly) { $promoted } else { Resolve-CixImageDigests -Settings $settings -Registry $registry -Tag $ImageTag }
      if ($PromoteFrom -and $images -and ($images.Engine -ne $promoted.Engine -or $images.Agent -ne $promoted.Agent)) {
        throw "The images tagged $ImageTag in $registry aren't the ones copied from $PromoteFrom; nothing was deployed."
      }
      if (-not $images) { throw 'Nothing runs yet, so there is no image to keep: pass -ImageTag with a tag CI pushed (the commit SHA).' }
      $agentValues['engineImageDigest'] = $images.Engine
      $agentValues['agentImageDigest'] = $images.Agent
      $which = if ($ImageTag) { "tag $ImageTag" } else { 'the ones running now' }
      Write-CixInfo "Images: $registry, $which. Engine: $(if ($settings.agents.startEngine) { 'running (one replica)' } else { 'stopped (agents.startEngine false)' })."
      Write-CixInfo "  cix-engine@$($images.Engine)"
      Write-CixInfo "  cix-trader@$($images.Agent)"
      # ...then the Claude credential: uploaded from secrets/claude_token (tools/setup-secrets.ps1 writes it) straight to Key Vault.
      if ($uploadClaudeToken) {
        if ($WhatIfOnly) { Write-CixInfo "Would upload secrets/claude_token to $vault as claude-token." }
        else {
          Confirm-Step "Upload secrets/claude_token to Key Vault $vault as claude-token?"
          Set-CixVaultSecretFromFile -Vault $vault -Name 'claude-token' -Path $claudeToken
          Write-CixInfo "Uploaded claude-token to $vault (not shown)."
        }
      }
      # ...then the exchange keys you passed (a protected environment only): from your files straight to Key Vault, never shown.
      foreach ($key in Get-CixExchangeKeySecrets) {
        $file = $exchangeKeyFiles[$key.Parameter]
        if (-not $file) { continue }
        if ($WhatIfOnly) { Write-CixInfo "Would upload -$($key.Parameter) to $vault as $($key.Name)."; continue }
        Confirm-Step "Upload $file to Key Vault $vault as $($key.Name)?"
        Set-CixVaultSecretFromFile -Vault $vault -Name $key.Name -Path $file -ContentType 'Exchange key'
        Write-CixInfo "Uploaded $($key.Name) to $vault (not shown)."
      }
      # ...then the engine's Entra app registration, made only once you have said yes.
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
  Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName -Agents $agentValues -WhatIf | Out-Null
  if ($WhatIfOnly) {
    if ($rebuild) { Write-CixInfo "The deployment would first delete $what (the preview above doesn't show that)." }
    Write-CixInfo 'Preview only: nothing was changed.'; return
  }
  Confirm-Step "Deploy CIX to $($settings.resourceGroup) and its database to ${sharedName}?"
  # The deployment sets the dashboard's settings to exactly what infra/ says, which would drop the writer identity's and the
  # confirmed owner (setup-dashboard-requests.ps1) and the Stocks tab's read access (provision-stocks.ps1): keep them in memory
  # and put them back. They are never printed.
  $writerSettings = @{}
  $siteId = $null
  if ($settings.deployDashboard) {
    $found = Invoke-CixNative { & az staticwebapp show --name "stapp-$($settings.namePrefix)-$($settings.environmentName)" --resource-group $settings.resourceGroup --query id -o tsv --only-show-errors 2>$null }
    if ($LASTEXITCODE -eq 0 -and "$found" -match '^/subscriptions/') {
      $siteId = "$found".Trim()
      try { $current = Get-CixStaticSiteSettings -SiteId $siteId }
      catch { throw "Couldn't read the dashboard's settings (to keep the writer identity's through the deployment); nothing was deployed." }
      foreach ($k in @($current.Keys | Where-Object { $_ -like 'COSMOS_WRITER_*' -or $_ -like 'STOCKS_COSMOS_*' -or $_ -eq 'DASHBOARD_OWNER_USER_ID' })) { $writerSettings[$k] = $current[$k] }
    }
  }
  # Last, once every check has passed: from here on the old apps are gone.
  if ($rebuild) {
    Confirm-Step "Delete $what now, and recreate the environment and the apps without a VNet?"
    if ($agentValues.ContainsKey('engineImageDigest')) {
      $again = if ($settings.protected) { "-PromoteFrom <paper environment> -ImageTag <the commit SHA paper runs>" } else { '-ImageTag <a tag CI pushed>' }
      Write-CixWarn "If the deployment fails after this, nothing runs to keep images from: run this again with $again. The images now are cix-engine@$($agentValues.engineImageDigest) and cix-trader@$($agentValues.agentImageDigest)."
    }
    Remove-CixVnetEnvironment -Rebuild $rebuild
  }
  try { $outputs = Invoke-CixDeployment -Settings $settings -SharedAccountName $sharedName -Agents $agentValues }
  catch {
    # The jobs' access to claude-token is granted in the same deployment that creates them, and takes minutes to apply.
    if ($agents -eq 'apps' -and "$($_.Exception.Message)" -match 'claude-token|revolutx-') {
      throw "$($_.Exception.Message) The access to the Key Vault secrets was just granted and can take a few minutes to apply: run this again shortly."
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
    if ($agents -eq 'apps') {
      Write-CixInfo "Engine app: $($outputs.engineAppName.value); jobs: $(@($outputs.agentJobNames.value) -join ', ')"
    }
    # The template before the engine signed in with its identity copied the shared account's read-write key into Key Vault
    # as cosmos-key, readable by the engine. A deployment doesn't remove what a template no longer has: do it here.
    $vaultName = "$($outputs.keyVaultName.value)"
    if ($vaultName -and (Test-CixVaultSecret -Vault $vaultName -Name 'cosmos-key')) {
      Write-CixWarn "Key Vault $vaultName still holds cosmos-key, the shared Cosmos DB account's read-write key, from an earlier template. Nothing uses it now."
      Confirm-Step "Delete cosmos-key from $vaultName, and the engine's access to it?"
      Remove-CixVaultSecretAndAccess -Vault $vaultName -Name 'cosmos-key'
      Write-CixWarn "Deleted. If the engine app ever ran while it was there, an image CI deployed could have read the key: regenerate the shared account's primary key (docs/azure.md, 'If you suspect misuse')."
    }
    if ($agents -eq 'apps') {
      # Who may get a token for the engine at all: the three job identities, by app role assignment.
      Set-CixEngineAppRole -Audience $agentValues.engineAudience -JobPrincipalIds @($outputs.agentJobPrincipalIds.value) -OwnerId $me.Id -Ask { param($Question) Confirm-Step $Question }
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
if ($rebuild -and $rebuild.Apps.Count -gt 0) {
  # CI's deploy identity holds its roles on the engine and each job, and those went with the deleted apps.
  Write-CixWarn "The engine and the jobs were recreated, and CI's roles on them went with the old ones: run .\tools\azure\setup-ci-deploy.ps1 -Environment $Environment -For agents again before CI's next deploy."
}
if ($settings.protected) {
  Write-CixInfo "The engine stands by: it holds the exchange keys in paper mode, so it runs nothing. Switching it is yours alone: docs/live-trading.md, 'Switching the engine on'."
  Write-CixInfo 'A deployment of this environment puts the engine back on standby until you switch it again.'
  return
}
Write-CixInfo 'The engine still uses the local emulator until you switch it. To switch (it starts with a fresh paper account):'
Write-CixInfo '  docker compose stop'
Write-CixInfo '  docker compose -f docker-compose.yml -f docker-compose.azure.yml up -d --build'
Write-CixInfo '  docker compose ps        # the engine shows (healthy) once it has reached Azure'
if (-not $DeployDashboard -and $settings.deployDashboard) {
  Write-CixInfo "Publish the dashboard: .\tools\azure\provision.ps1 -Environment $Environment -DeployDashboard -SkipInfrastructure -SkipEngineConfig"
}
Write-CixInfo 'Details, costs and the reset procedure: docs/azure.md'
