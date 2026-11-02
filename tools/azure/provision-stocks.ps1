#Requires -Version 5.1
<#
.SYNOPSIS
  Sets up the stocks advisory in Azure (ADR-0023): its own resource group, Cosmos DB account, Key Vault, nightly job and Claude
  analyst job (ADR-0027), and the dashboard's Stocks tab. Nothing of the crypto engines changes.

.DESCRIPTION
  Reads infra/stocks/stocks.json and, after a preview and your confirmation of each step:
    1. deploys infra/stocks/main.bicep: the resource group, a serverless Cosmos DB account with the cix-stocks database, a
       Key Vault, the job's identity (allowed to pull from the CIX registry of registryEnvironment, and nothing else there),
       logs, a Container Apps environment and a monthly budget; and lets the dashboard's writer identity (the one
       setup-dashboard-requests.ps1 made, found from the dashboard's own settings) create the owner's portfolio
       submissions in the stocks database's requests container, and nothing else there (ADR-0026);
    2. stores the EODHD key in that vault, from -EodhdKeyFile or a hidden prompt (never echoed, never on a command line);
       a key already there is kept unless you pass -ReplaceKey;
    3. stores your Claude token in that vault as claude-token, for the Claude analyst: from -ClaudeTokenFile, else
       secrets/claude_token (tools/setup-secrets.ps1 writes it), else a hidden prompt; never echoed, never on a command line.
       A token already there is kept unless you pass -ReplaceClaudeToken (after renewing it). Without one, the analyst job
       isn't deployed and the Stocks page shows the rules' advice alone;
    4. builds the jobs' image from the commit you have checked out (git HEAD, not uncommitted changes) in that registry, as
       cix-stocks:<commit>-<UTC time>, locked, and takes its digest from the build itself; skipped when the job runs that
       commit's image already. -KeepImage redeploys the image the job runs now instead;
    5. deploys the nightly job and the analyst job with that image, by digest (with -KeepImage, a new analyst job isn't
       deployed: the running image may be from before it). The analyst job has its own identity, which reads the Claude
       token and the advice, and writes its own analysis container only; the nightly run writes its brief;
    6. gives the dashboard of dashboardEnvironment the stocks database's read-only key (STOCKS_COSMOS_* settings, never
       printed), so its Stocks tab shows the advice;
    7. with -RunNow, starts one run of the nightly job straight away; with -RunAnalystNow, one run of the analyst.

  The jobs only read EODHD and the web, ask Claude, and write their own database; they never trade. Running this again changes only what differs.
  Needs: the Azure CLI, git, and rights to create resource groups and role assignments in the subscription.

.EXAMPLE
  .\tools\azure\provision-stocks.ps1
.EXAMPLE
  .\tools\azure\provision-stocks.ps1 -EodhdKeyFile C:\keys\eodhd.txt -RunNow
.EXAMPLE
  .\tools\azure\provision-stocks.ps1 -WhatIfOnly
#>
[CmdletBinding()]
param(
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [string]$EodhdKeyFile,
  [switch]$ReplaceKey,
  [string]$ClaudeTokenFile,
  [switch]$ReplaceClaudeToken,
  [switch]$RunAnalystNow,
  [switch]$KeepImage,
  [switch]$RunNow,
  [switch]$WhatIfOnly,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

$Root = Get-CixRepoRoot
$Template = Join-CixPath $Root 'infra' 'stocks' 'main.bicep'
$SettingsPath = Join-CixPath $Root 'infra' 'stocks' 'stocks.json'
$Repository = 'cix-stocks'
$SecretName = 'eodhd-api-token' # infra/stocks/modules/stocks-names.bicep
$ClaudeSecretName = 'claude-token' # the same file
# What the image is built from (stocks/Dockerfile): only these, from the commit, never the rest of the working folder.
$ImageSources = @('Directory.Build.props', 'src/Cix.Domain', 'stocks/src', 'stocks/config', 'stocks/analyst', 'stocks/Dockerfile', 'tools/trader/agent-common.sh')
$OnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Confirm-Step([string]$Question) {
  if ($Yes) { return }
  $answer = Read-Host "    $Question [y/N]"
  if ($answer -notmatch '^(y|yes)$') { Write-CixWarn 'Stopped: not confirmed (type y to go ahead). Nothing more was changed.'; exit 1 }
}

# A yes/no question where no is a fine answer too (not a stop). With -Yes it's no: nothing is typed in for you.
function Test-Answer([string]$Question) {
  if ($Yes) { return $false }
  return (Read-Host "    $Question [y/N]") -match '^(y|yes)$'
}

function Read-StocksSettings {
  if (-not (Test-Path $SettingsPath)) { throw "No settings file $SettingsPath." }
  $s = ConvertTo-CixHashtable (Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json)
  foreach ($key in 'subscriptionId', 'resourceGroup', 'location', 'namePrefix', 'registryEnvironment', 'dashboardEnvironment', 'schedule', 'monthlyBudget', 'budgetStartDate') {
    if (-not $s.ContainsKey($key)) { throw "$SettingsPath is missing '$key'." }
  }
  if ($s.budgetStartDate -is [datetime]) { $s.budgetStartDate = $s.budgetStartDate.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
  if ($s.subscriptionId -and $s.subscriptionId -notmatch '^[0-9a-fA-F-]{36}$') { throw "${SettingsPath}: subscriptionId must be a GUID or empty." }
  if ($s.resourceGroup -notmatch '^[\w\-\.\(\)]{1,90}$') { throw "${SettingsPath}: resourceGroup isn't a valid name." }
  if ($s.location -notmatch '^[a-z0-9]{3,40}$') { throw "${SettingsPath}: location must be an Azure region name, like polandcentral." }
  if ($s.namePrefix -notmatch '^[a-z0-9]{2,8}$') { throw "${SettingsPath}: namePrefix must be 2 to 8 lowercase letters or digits." }
  foreach ($key in 'registryEnvironment', 'dashboardEnvironment') {
    if ("$($s[$key])" -notmatch '^[a-z0-9-]{2,12}$') { throw "${SettingsPath}: $key must name a file in infra/environments, like dev." }
  }
  # Five fields (minute hour day month weekday) of digits, '*', ',', '-' and '/'.
  if ("$($s.schedule)" -notmatch '^[0-9*,/-]+( [0-9*,/-]+){4}$') { throw "${SettingsPath}: schedule must be a five-field cron expression in UTC." }
  if (-not ($s.monthlyBudget -is [int] -or $s.monthlyBudget -is [long]) -or $s.monthlyBudget -lt 1) { throw "${SettingsPath}: monthlyBudget must be a positive whole number." }
  if ("$($s.budgetStartDate)" -notmatch '^\d{4}-\d{2}-01$') { throw "${SettingsPath}: budgetStartDate must be the first of a month (YYYY-MM-01)." }
  # The Claude analyst's settings (ADR-0027), with defaults for a settings file from before it.
  $defaults = @{ analystSchedule = '40 2,6 * * 2-6'; analystModel = 'opus'; claudeCodeVersion = '2.1.316'; claudeTokenExpires = '' }
  foreach ($key in $defaults.Keys) { if (-not $s.ContainsKey($key) -or $null -eq $s[$key]) { $s[$key] = $defaults[$key] } }
  if ("$($s.analystSchedule)" -notmatch '^[0-9*,/-]+( [0-9*,/-]+){4}$') { throw "${SettingsPath}: analystSchedule must be a five-field cron expression in UTC." }
  if ("$($s.analystModel)" -cnotmatch '^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$') { throw "${SettingsPath}: analystModel must be a lowercase model alias or name, like opus or sonnet." }
  if ("$($s.claudeCodeVersion)" -notmatch '^(latest|\d{1,4}\.\d{1,4}\.\d{1,4})$') { throw "${SettingsPath}: claudeCodeVersion must be latest or a version like 2.1.0." }
  if ($s.claudeTokenExpires -is [datetime]) { $s.claudeTokenExpires = $s.claudeTokenExpires.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
  if ("$($s.claudeTokenExpires)" -notmatch '^(\d{4}-\d{2}-\d{2})?$') { throw "${SettingsPath}: claudeTokenExpires must be a date like 2027-10-23, or empty." }
  if (-not $s.ContainsKey('tags') -or -not $s.tags) { $s['tags'] = @{} }
  if ($s.tags -isnot [hashtable]) { throw "${SettingsPath}: tags must be an object." }
  return $s
}

function Invoke-StocksDeployment([string]$Digest, [switch]$WhatIf) {
  $values = [ordered]@{
    resourceGroupName         = $stocks.resourceGroup
    location                  = $stocks.location
    namePrefix                = $stocks.namePrefix
    registryResourceGroupName = $registryEnv.resourceGroup
    registryName              = $registry
    imageDigest               = $Digest
    schedule                  = $stocks.schedule
    analystSchedule           = $stocks.analystSchedule
    analystEnabled            = [bool]$analystEnabled
    analystModel              = $stocks.analystModel
    claudeTokenExpires        = $tokenExpires
    ownerObjectId             = $me.Id
    monthlyBudget             = $stocks.monthlyBudget
    budgetStartDate           = $stocks.budgetStartDate
    budgetContactEmail        = $me.Email
    writerPrincipalId         = $writerPrincipal
    tags                      = $stocks.tags
  }
  $parameters = New-CixParametersFile -Values $values -Label 'stocks'
  try {
    if ($WhatIf) {
      Invoke-CixNative { & az deployment sub what-if --location $stocks.location --template-file $Template --parameters "@$parameters" --only-show-errors | Out-Host }
      if ($LASTEXITCODE -ne 0) { throw 'The what-if preview failed.' }
      return $null
    }
    # Access granted in the same deployment (Key Vault, Cosmos DB, the registry) can take minutes to reach a new identity.
    for ($attempt = 1; ; $attempt++) {
      $name = "cix-stocks-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))"
      try {
        $result = Invoke-CixAz @('deployment', 'sub', 'create', '--location', $stocks.location, '--name', $name,
          '--template-file', $Template, '--parameters', "@$parameters", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
        return $result.properties.outputs
      }
      catch {
        $message = "$($_.Exception.Message)"
        # Access to the vault, the registry or the database, granted moments ago, reaching the new identity late.
        $late = $message -match '(?i)AuthorizationFailed|Forbidden|UNAUTHORIZED|Unable to get value using Managed identity|PrincipalNotFound|does not exist in the directory'
        if (-not $late) { throw }
        if ($attempt -ge 4) { throw "$message Access granted moments ago can take up to half an hour to apply: run this again later; it picks up where it stopped." }
        Write-CixWarn 'The deployment failed on access that was just granted, which can take a few minutes to apply. Trying again in 2 minutes.'
        Start-Sleep -Seconds 120
      }
    }
  }
  finally { Remove-Item $parameters -ErrorAction SilentlyContinue }
}

# The dashboard's writer identity (ADR-0012), taken from the dashboard's own settings, so only the identity the dashboard signs
# in as gets the grant. Empty when the dashboard or its writer isn't set up: the Stocks page then can't send the portfolio.
function Find-WriterPrincipal {
  $site = "$(Invoke-CixNative { & az staticwebapp show --name $appName --resource-group $dashboardEnv.resourceGroup --query id -o tsv --only-show-errors 2>$null })".Trim()
  if ($LASTEXITCODE -ne 0 -or $site -notmatch '^/subscriptions/') { return '' }
  $settings = Get-CixStaticSiteSettings -SiteId $site
  try { $clientId = if ($settings.ContainsKey('COSMOS_WRITER_CLIENT_ID')) { "$($settings['COSMOS_WRITER_CLIENT_ID'])".Trim() } else { '' } }
  finally { $settings = $null } # it holds secrets too
  if ($clientId -notmatch '^[0-9a-fA-F-]{36}$') { return '' }
  try { $sp = Invoke-CixAz @('ad', 'sp', 'show', '--id', $clientId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson }
  catch { return '' }
  $expected = "cix-dashboard-writer-$($dashboardEnv.environmentName)"
  if (-not $sp -or "$($sp.displayName)" -cne $expected -or "$($sp.appId)" -ne $clientId -or "$($sp.id)" -notmatch '^[0-9a-fA-F-]{36}$') {
    Write-CixWarn "The dashboard's writer setting doesn't name $expected; no grant is made. Run setup-dashboard-requests.ps1 -Environment $($dashboardEnv.environmentName)."
    return ''
  }
  return "$($sp.id)"
}

function New-PrivateFolder {
  # A fresh folder only you can open (Linux and macOS share /tmp); on Windows the per-user temp folder.
  $dir = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir | Out-Null
  if ($OnWindows) {
    $me = "*$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
    Invoke-CixNative { icacls $dir /inheritance:r /grant:r "${me}:(OI)(CI)F" | Out-Null }
  }
  else { Invoke-CixNative { chmod 700 $dir } }
  if ($LASTEXITCODE -ne 0) { Remove-Item $dir -Recurse -Force; throw 'Could not restrict access to a temporary folder.' }
  return $dir
}

function Test-EodhdKey([string]$Key) { return $Key -match '^[A-Za-z0-9._-]{8,128}$' }

function Set-EodhdKey([string]$Vault) {
  # Whatever the source (a 5.1 file may be UTF-16), the vault gets the trimmed, checked value as UTF-8 from a private file.
  $dir = New-PrivateFolder
  $source = Join-Path $dir 'eodhd'
  $plain = $null
  try {
    if ($EodhdKeyFile) {
      if (-not (Test-Path $EodhdKeyFile -PathType Leaf)) { throw "No file $EodhdKeyFile." }
      $plain = "$(Get-Content $EodhdKeyFile -Raw)".Trim()
      if (-not (Test-EodhdKey $plain)) { throw "$EodhdKeyFile doesn't hold one EODHD API token (letters, digits, dots and hyphens, on one line); nothing was stored." }
    }
    else {
      Write-CixInfo 'Paste your EODHD API token (from https://eodhd.com/cp/settings) and press Enter. It is not shown.'
      $secure = Read-Host '    EODHD API token' -AsSecureString
      $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
      try { $plain = "$([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))".Trim() }
      finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $secure = $null }
      if (-not (Test-EodhdKey $plain)) { throw "That doesn't look like an EODHD API token (letters, digits, dots and hyphens); nothing was stored." }
    }
    [IO.File]::WriteAllText($source, $plain, (New-Object System.Text.UTF8Encoding $false))
    $plain = $null
    # The value never reaches a message: if az fails, say only what to check.
    try { Set-CixVaultSecretFromFile -Vault $Vault -Name $SecretName -Path $source -ContentType 'EODHD API token' }
    catch { throw "Couldn't store the key in $Vault as $SecretName. If a deleted secret of that name is kept for recovery, recover or purge it in the Azure portal (the vault, Secrets, Manage deleted secrets), then run this again." }
  }
  finally {
    $plain = $null
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Test-VaultSecretWithRetry([string]$Vault) {
  # Listing names needs the Secrets Officer role granted in step 1, which can take minutes to apply on a first run.
  for ($attempt = 1; ; $attempt++) {
    try { return (Test-CixVaultSecret -Vault $Vault -Name $SecretName) }
    catch {
      if ("$($_.Exception.Message)" -notmatch '(?i)Forbidden|403|not authorized|ForbiddenByRbac') { throw }
      if ($attempt -ge 10) { throw "$Vault still refuses you: your access to it, granted in step 1, can take up to half an hour to apply. Run this again later." }
      Write-CixInfo 'The vault refused (access granted moments ago may not have applied yet); trying again in a minute.'
      Start-Sleep -Seconds 60
    }
  }
}

function Test-ClaudeToken([string]$Token) { return $Token -match '^sk-ant-[A-Za-z0-9_-]{20,400}$' }

function Set-ClaudeToken([string]$Vault) {
  # The same way as the EODHD key: the trimmed, checked value goes to the vault as UTF-8 from a private file, never shown.
  $dir = New-PrivateFolder
  $source = Join-Path $dir 'claude'
  $plain = $null
  try {
    $file = if ($ClaudeTokenFile) { $ClaudeTokenFile } else { $LocalClaudeToken }
    if ($file -and (Test-Path $file -PathType Leaf)) {
      $plain = "$(Get-Content $file -Raw)".Trim()
      if (-not (Test-ClaudeToken $plain)) { throw "$file doesn't hold one Claude setup token or API key (sk-ant-..., on one line); nothing was stored." }
    }
    else {
      if ($ClaudeTokenFile) { throw "No file $ClaudeTokenFile." }
      Write-CixInfo "Paste your Claude setup token ('claude setup-token' prints it; sk-ant-...) and press Enter. It is not shown."
      $secure = Read-Host '    Claude token' -AsSecureString
      $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
      try { $plain = "$([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))".Trim() }
      finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $secure = $null }
      if (-not (Test-ClaudeToken $plain)) { throw "That doesn't look like a Claude setup token or API key (sk-ant-...); nothing was stored." }
    }
    [IO.File]::WriteAllText($source, $plain, (New-Object System.Text.UTF8Encoding $false))
    $plain = $null
    try { Set-CixVaultSecretFromFile -Vault $Vault -Name $ClaudeSecretName -Path $source }
    catch { throw "Couldn't store the Claude token in $Vault as $ClaudeSecretName. If a deleted secret of that name is kept for recovery, recover or purge it in the Azure portal (the vault, Secrets, Manage deleted secrets), then run this again." }
  }
  finally {
    $plain = $null
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Get-JobImageDigest([string]$JobId, [string]$Server) {
  # The digest the job runs now, or '' when there is no job yet. Generic az calls: containerapp needs an extension.
  if (-not $JobId) { return '' }
  $job = Invoke-CixAz @('resource', 'show', '--ids', $JobId, '-o', 'json') | ConvertFrom-CixJson
  $image = "$(@($job.properties.template.containers)[0].image)"
  if ($image -cnotmatch "^$([regex]::Escape($Server))/$Repository@(sha256:[0-9a-f]{64})$") { throw "The job runs $image, not a $Repository image from the registry; run this without -KeepImage to build one." }
  return $Matches[1]
}

function Build-StocksImage([string]$Registry, [string]$RunningDigest) {
  <#
    Builds the checked-out commit and returns the digest the build itself reports, never one looked up by tag: anyone who can
    push to the registry (CI's deploy identity can) could point a tag at another image. Each build gets a tag of its own,
    <commit>-<UTC time>, locked once built.
  #>
  Assert-CixCommand -Name 'git' -Hint 'Install Git from https://git-scm.com, then reopen the terminal.'
  $commit = "$(Invoke-CixNative { & git -C $Root rev-parse HEAD 2>$null })".Trim()
  if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw "Couldn't read the checked-out commit of $Root (is it a git clone?)." }
  $dirty = @(Invoke-CixNative { & git -C $Root status --porcelain -- @ImageSources 2>$null })
  if ($dirty.Count -gt 0) { Write-CixWarn "You have uncommitted changes in the stocks sources; the image is built from commit $($commit.Substring(0, 12)) without them." }
  $branch = "$(Invoke-CixNative { & git -C $Root rev-parse --abbrev-ref HEAD 2>$null })".Trim()
  Write-CixInfo "Commit: $commit ($branch). Run 'git pull' on main first if you want the latest."

  # The job's image, by its digest (fixed content): built from this commit already if one of its tags says so.
  if ($RunningDigest) {
    $running = Invoke-CixAz @('acr', 'repository', 'show', '--name', $Registry, '--image', "$Repository@$RunningDigest", '-o', 'json') | ConvertFrom-CixJson
    $runningTags = @(if ($running -and $running.PSObject.Properties['tags']) { $running.tags })
    if (@($runningTags | Where-Object { "$_" -match "^$commit-\d{14}$" }).Count -gt 0) {
      Write-CixInfo "The job runs this commit's image already ($Repository@$($RunningDigest.Substring(0, 19))...); nothing to build."
      return $RunningDigest
    }
  }

  $tag = "$commit-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
  Confirm-Step "Build ${Repository}:$($commit.Substring(0, 12)) in $Registry (a minute or two, a few cents of registry build time)?"
  Write-CixInfo 'Building in the registry (a minute or two; its log is shown only if it fails)...'
  $dir = New-PrivateFolder
  try {
    $zip = Join-Path $dir 'source.zip'
    $context = Join-Path $dir 'source'
    Invoke-CixNative { & git -C $Root archive --format=zip -o $zip $commit -- @ImageSources }
    if ($LASTEXITCODE -ne 0) { throw "git archive failed for commit $commit." }
    Expand-Archive -Path $zip -DestinationPath $context
    # --no-logs waits for the build and returns the finished run, with the digest it pushed (--no-wait returns nothing at
    # all, and the streamed log returns no run).
    try {
      $run = Invoke-CixAz @('acr', 'build', '--registry', $Registry, '--image', "${Repository}:$tag", '--file', 'stocks/Dockerfile',
        '--build-arg', "CLAUDE_CODE_VERSION=$($stocks.claudeCodeVersion)", '--platform', 'linux/amd64', '--no-logs', '--only-show-errors', '-o', 'json', $context) | ConvertFrom-CixJson
    }
    catch {
      $message = "$($_.Exception.Message)"
      if ($message -match "run with ID '([A-Za-z0-9]{1,32})'") {
        # The build's log. It carries characters a Windows console code page can't print, which stops az: UTF-8.
        $failedRun = $Matches[1]
        $savedEncoding = $env:PYTHONIOENCODING
        $env:PYTHONIOENCODING = 'utf-8'
        try { Invoke-CixNative { & az acr task logs --registry $Registry --run-id $failedRun --only-show-errors | Out-Host } }
        finally { $env:PYTHONIOENCODING = $savedEncoding }
      }
      throw "The image build failed; nothing was deployed. $message"
    }
  }
  finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
  $runId = if ($run -and $run.PSObject.Properties['runId']) { "$($run.runId)" } else { '?' }
  if (-not $run -or -not $run.PSObject.Properties['status'] -or "$($run.status)" -ne 'Succeeded') { throw "The image build (run $runId) didn't succeed; nothing was deployed. Its log: az acr task logs --registry $Registry --run-id $runId" }
  $images = @(@(if ($run.PSObject.Properties['outputImages']) { $run.outputImages }) | Where-Object { $_ -and "$($_.repository)" -eq $Repository -and "$($_.tag)" -match "^$commit-\d{14}$" })
  if ($images.Count -ne 1 -or "$($images[0].digest)" -cnotmatch '^sha256:[0-9a-f]{64}$') { throw "The build (run $runId) reported no $Repository image; nothing was deployed." }
  $digest = "$($images[0].digest)"
  $tag = "$($images[0].tag)"
  # Locked: the tag can't be moved or deleted (a push identity can't unlock it).
  Invoke-CixAz @('acr', 'repository', 'update', '--name', $Registry, '--image', "${Repository}:$tag", '--write-enabled', 'false', '--delete-enabled', 'false', '--only-show-errors', '-o', 'none') | Out-Null
  Write-CixInfo "Built ${Repository}:$tag ($($digest.Substring(0, 19))...)."
  return $digest
}

Write-CixStep 'Checking prerequisites'
Assert-CixAzureCli
Assert-CixAgentsTools # the templates need Bicep 0.32 or later

Write-CixStep 'Settings'
$stocks = Read-StocksSettings
$registryEnv = Read-CixEnvironment -Name $stocks.registryEnvironment
$dashboardEnv = Read-CixEnvironment -Name $stocks.dashboardEnvironment
if ($registryEnv.protected) { throw "registryEnvironment '$($stocks.registryEnvironment)' is the live environment; the stocks image goes in a paper environment's registry." }
if (-not $registryEnv.ContainsKey('agents') -or $registryEnv.agents.deploy -eq 'none') { throw "Environment '$($stocks.registryEnvironment)' has no registry (agents.deploy is none)." }
if (-not $dashboardEnv.deployDashboard) { throw "Environment '$($stocks.dashboardEnvironment)' has no dashboard (deployDashboard is false)." }
foreach ($group in @($registryEnv.resourceGroup, $dashboardEnv.resourceGroup, $registryEnv.shared.resourceGroup)) {
  if ($group -eq $stocks.resourceGroup) { throw "${SettingsPath}: resourceGroup $group belongs to CIX; the stocks advisory needs its own group." }
}
$appName = "stapp-$($dashboardEnv.namePrefix)-$($dashboardEnv.environmentName)"
Write-CixInfo "Resource group:  $($stocks.resourceGroup) in $($stocks.location)"
Write-CixInfo "Image registry:  the '$($registryEnv.environmentName)' environment's, in $($registryEnv.resourceGroup) (pull only)"
Write-CixInfo "Dashboard:       $appName"
Write-CixInfo "Schedule (UTC):  $($stocks.schedule); the Claude analyst at $($stocks.analystSchedule)"

Write-CixStep 'Signing in'
$null = Connect-CixAzure -Settings $stocks -SubscriptionId $SubscriptionId
$me = Get-CixSignedInUser
$registry = Find-CixAgentsResource -Settings $registryEnv -Kind acr
if (-not $registry) { throw "No registry found in $($registryEnv.resourceGroup); run provision.ps1 -Environment $($registryEnv.environmentName) first." }
$server = "$($registry.ToLowerInvariant()).azurecr.io"
Write-CixInfo "Registry: $registry"
Assert-CixAppsRegion -Settings @{ agents = @{ location = $stocks.location }; path = $SettingsPath }

# What exists already: a job keeps running its image through the first step.
$jobId = ''
$analystJobId = ''
$budgetExists = $false
if ((Invoke-CixAz @('group', 'exists', '--name', $stocks.resourceGroup)) -eq 'true') {
  # Only a group this script made: the template sets the group's tags, which other CIX scripts rely on in their own groups.
  $existing = Invoke-CixAz @('group', 'show', '--name', $stocks.resourceGroup, '-o', 'json') | ConvertFrom-CixJson
  $isStocks = $existing -and $existing.PSObject.Properties['tags'] -and $existing.tags -and $existing.tags.PSObject.Properties['cix-component'] -and $existing.tags.'cix-component' -eq 'stocks'
  if (-not $isStocks) { throw "Resource group $($stocks.resourceGroup) exists but isn't the stocks advisory's (no tag cix-component=stocks). Name a new group in $SettingsPath." }
  $all = @(Invoke-CixAz @('resource', 'list', '--resource-group', $stocks.resourceGroup, '-o', 'json') | ConvertFrom-CixJson)
  $jobs = @($all | Where-Object { "$($_.type)" -eq 'Microsoft.App/jobs' -and "$($_.name)" -eq "caj-$($stocks.namePrefix)-stocks-daily" })
  if ($jobs.Count -eq 1) { $jobId = "$($jobs[0].id)" }
  $analystJobs = @($all | Where-Object { "$($_.type)" -eq 'Microsoft.App/jobs' -and "$($_.name)" -eq "caj-$($stocks.namePrefix)-stocks-analyst" })
  if ($analystJobs.Count -eq 1) { $analystJobId = "$($analystJobs[0].id)" }
  $budgets = @(Invoke-CixAz @('consumption', 'budget', 'list', '--resource-group', $stocks.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  $budgetExists = @($budgets | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq "budget-$($stocks.namePrefix)-stocks" }).Count -gt 0
}
$today = [datetime]::UtcNow
$start = [datetime]::ParseExact($stocks.budgetStartDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
if ($me.Email -and -not $budgetExists -and $start -lt [datetime]::new($today.Year, $today.Month, 1)) {
  throw "budgetStartDate ($($stocks.budgetStartDate)) is in a past month; Azure only starts a new budget this month or later. Set it to $($today.ToString('yyyy-MM', [Globalization.CultureInfo]::InvariantCulture))-01 in $SettingsPath."
}
if (-not $me.Email) { Write-CixWarn 'Your sign-in has no mail address, so no budget alert is set up.' }
$currentDigest = Get-JobImageDigest -JobId $jobId -Server $server
$writerPrincipal = Find-WriterPrincipal
if ($writerPrincipal) { Write-CixInfo "Dashboard writer: cix-dashboard-writer-$($dashboardEnv.environmentName), to be allowed to create portfolio submissions only." }
else { Write-CixWarn "No dashboard writer found on ${appName}: the Stocks page can't send your portfolio. Run setup-dashboard-requests.ps1 -Environment $($dashboardEnv.environmentName), then this again." }
if ($KeepImage -and -not $currentDigest) { throw '-KeepImage: there is no job yet to keep the image of; run this without it.' }
# The Claude token: stocks.json's expiry, else the registry environment's (the same token the crypto agents use).
# CIX_SECRETS_DIR moves it (the tests point it at an empty folder, so they never read your token).
$SecretsDir = if ($env:CIX_SECRETS_DIR) { $env:CIX_SECRETS_DIR } else { Join-CixPath $Root 'secrets' }
$LocalClaudeToken = Join-Path $SecretsDir 'claude_token'
if ("$($stocks.claudeCodeVersion)" -eq 'latest') { Write-CixWarn "claudeCodeVersion is latest in ${SettingsPath}: each image build takes whatever Claude Code is newest. Pin a version (like 2.1.316) to choose when it changes." }
if ($ReplaceClaudeToken -and $Yes -and -not $ClaudeTokenFile -and -not (Test-Path $LocalClaudeToken -PathType Leaf)) { throw '-ReplaceClaudeToken with -Yes needs the new token in a file: pass -ClaudeTokenFile, or put it in secrets/claude_token.' }
$tokenExpires = "$($stocks.claudeTokenExpires)"
if (-not $tokenExpires -and $registryEnv.ContainsKey('agents') -and $registryEnv.agents -is [hashtable] -and $registryEnv.agents.ContainsKey('claudeTokenExpires')) { $tokenExpires = "$($registryEnv.agents.claudeTokenExpires)" }
if ($tokenExpires -notmatch '^(\d{4}-\d{2}-\d{2})?$') { $tokenExpires = '' }
# Until step 3 has looked in the vault, the analyst job stays as it is: deployed when it exists already and its token is
# still in the vault (a template that names a missing token fails, and step 3 could then never store it again).
$analystEnabled = $false
if ($analystJobId) {
  $vaults = @($all | Where-Object { "$($_.type)" -eq 'Microsoft.KeyVault/vaults' })
  $analystEnabled = $vaults.Count -eq 1 -and (Test-CixVaultSecret -Vault "$($vaults[0].name)" -Name $ClaudeSecretName)
  if (-not $analystEnabled) { Write-CixWarn "The analyst job's Claude token isn't in the vault: step 3 stores it, and step 5 deploys the analyst again." }
}

Write-CixStep "Step 1 of 6: the stocks resources ($($stocks.resourceGroup))"
Invoke-StocksDeployment -Digest $currentDigest -WhatIf | Out-Null
if ($WhatIfOnly) { Write-CixInfo 'Preview only: nothing was changed. (The job appears once an image is built and the key is stored.)'; return }
Confirm-Step "Deploy the stocks resources to $($stocks.resourceGroup) (and pull access to $registry)?"
$outputs = Invoke-StocksDeployment -Digest $currentDigest
$vault = "$($outputs.keyVaultName.value)"
$cosmosName = "$($outputs.cosmosAccountName.value)"
$cosmosEndpoint = "$($outputs.cosmosEndpoint.value)"
$database = "$($outputs.databaseName.value)"
Write-CixInfo "Deployed: Cosmos DB $cosmosName (serverless, database $database), Key Vault $vault."

Write-CixStep "Step 2 of 6: the EODHD key ($vault)"
if ((Test-VaultSecretWithRetry -Vault $vault) -and -not $ReplaceKey) { Write-CixInfo 'Stored already (pass -ReplaceKey to replace it).' }
else {
  Confirm-Step "Store the EODHD key in $vault as ${SecretName}?"
  Set-EodhdKey -Vault $vault
  Write-CixInfo 'Stored. It was not shown or written anywhere else; the next run uses it.'
}

Write-CixStep "Step 3 of 6: the Claude token for the analyst ($vault)"
$hasToken = Test-CixVaultSecret -Vault $vault -Name $ClaudeSecretName
if ($hasToken -and -not $ReplaceClaudeToken) { Write-CixInfo 'Stored already (pass -ReplaceClaudeToken after renewing it).' }
elseif (-not $hasToken -and -not $ClaudeTokenFile -and -not (Test-Path $LocalClaudeToken -PathType Leaf) -and -not (Test-Answer 'No Claude token file (secrets/claude_token). Type the token in now?')) {
  Write-CixWarn "No Claude token stored (pass -ClaudeTokenFile, or run tools/setup-secrets.ps1 for secrets/claude_token): the analyst job isn't deployed, and the Stocks page shows the rules' advice alone."
}
else {
  Confirm-Step "Store your Claude token in $vault as $ClaudeSecretName (it shares your Claude usage limit with the crypto agents)?"
  Set-ClaudeToken -Vault $vault
  $hasToken = $true
  Write-CixInfo 'Stored. It was not shown or written anywhere else; the analyst''s next run uses it.'
}
$analystEnabled = [bool]$hasToken
if ($analystEnabled -and $KeepImage -and -not $analystJobId) {
  Write-CixWarn "-KeepImage: the image the job runs may be from before the analyst, so the analyst isn't deployed now. Run this without -KeepImage to build an image with it."
  $analystEnabled = $false
}

Write-CixStep "Step 4 of 6: the jobs' image ($registry)"
$digest = if ($KeepImage) { Write-CixInfo "Keeping the image the job runs: $Repository@$currentDigest"; $currentDigest } else { Build-StocksImage -Registry $registry -RunningDigest $currentDigest }

Write-CixStep 'Step 5 of 6: the nightly job and the Claude analyst'
if ($digest -eq $currentDigest -and $jobId -and ($analystEnabled -eq [bool]$analystJobId)) { Write-CixInfo "The jobs run $Repository@$digest already." }
else {
  Invoke-StocksDeployment -Digest $digest -WhatIf | Out-Null
  $what = if ($analystEnabled) { "the nightly job and the Claude analyst (at '$($stocks.schedule)' and '$($stocks.analystSchedule)' UTC)" } else { "the nightly job (at '$($stocks.schedule)' UTC)" }
  Confirm-Step "Deploy $what with $Repository@$($digest.Substring(0, 19))...?"
  $outputs = Invoke-StocksDeployment -Digest $digest
  $jobName = "$($outputs.jobName.value)"
  if (-not $jobName) { throw 'The deployment returned no job name.' }
  $jobId = "$(Invoke-CixAz @('resource', 'show', '--resource-group', $stocks.resourceGroup, '--resource-type', 'Microsoft.App/jobs', '--name', $jobName, '--query', 'id', '-o', 'tsv'))".Trim()
  Write-CixInfo "Deployed $jobName."
  $analystJobName = if ($outputs.PSObject.Properties['analystJobName']) { "$($outputs.analystJobName.value)" } else { '' }
  if ($analystEnabled) {
    if (-not $analystJobName) { throw 'The deployment returned no analyst job name.' }
    $analystJobId = "$(Invoke-CixAz @('resource', 'show', '--resource-group', $stocks.resourceGroup, '--resource-type', 'Microsoft.App/jobs', '--name', $analystJobName, '--query', 'id', '-o', 'tsv'))".Trim()
    Write-CixInfo "Deployed $analystJobName."
  }
}

Write-CixStep "Step 6 of 6: the dashboard's Stocks tab ($appName)"
$siteId = "$(Invoke-CixNative { & az staticwebapp show --name $appName --resource-group $dashboardEnv.resourceGroup --query id -o tsv --only-show-errors 2>$null })".Trim()
if ($LASTEXITCODE -ne 0 -or $siteId -notmatch '^/subscriptions/') { Write-CixWarn "$appName not found in $($dashboardEnv.resourceGroup); run provision.ps1 -Environment $($dashboardEnv.environmentName), then this again." }
else {
  # The read-only key reads the stocks database only: this account holds nothing else.
  $keys = Invoke-CixAz -Quiet @('cosmosdb', 'keys', 'list', '--resource-group', $stocks.resourceGroup, '--name', $cosmosName, '--type', 'read-only-keys', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  try {
    if (-not $keys -or -not $keys.PSObject.Properties['primaryReadonlyMasterKey'] -or -not $keys.primaryReadonlyMasterKey) { throw "Couldn't read the stocks database's read-only key." }
    $current = Get-CixStaticSiteSettings -SiteId $siteId
    $wanted = @{ STOCKS_COSMOS_ENDPOINT = $cosmosEndpoint; STOCKS_COSMOS_DATABASE = $database; STOCKS_COSMOS_READONLY_KEY = $keys.primaryReadonlyMasterKey }
    $differs = @($wanted.Keys | Where-Object { -not $current.ContainsKey($_) -or $current[$_] -cne $wanted[$_] })
    if ($differs.Count -eq 0) { Write-CixInfo 'Set already.' }
    else {
      Confirm-Step "Give $appName read access to the stocks database (STOCKS_COSMOS_* settings; the key is not shown)?"
      foreach ($k in $wanted.Keys) { $current[$k] = $wanted[$k] }
      Set-CixStaticSiteSettings -SiteId $siteId -Settings $current
      Write-CixInfo 'Set. provision.ps1 keeps them when it redeploys the dashboard.'
    }
  }
  finally { $keys = $null; $current = $null; $wanted = $null }
}

if ($RunNow) {
  Write-CixStep 'A run now'
  Confirm-Step 'Start one run of the job now (it spends up to 18 of the day''s EODHD calls)?'
  Invoke-CixAz @('rest', '--method', 'post', '--url', "https://management.azure.com$jobId/start?api-version=2025-01-01", '--only-show-errors', '-o', 'none') | Out-Null
  Write-CixInfo 'Started. In two or three minutes the Stocks tab shows the advice; the run''s log is in the Azure portal (the job, Execution history).'
}

if ($RunAnalystNow) {
  Write-CixStep 'A Claude analyst run now'
  if (-not $analystJobId) { Write-CixWarn 'There is no analyst job (no Claude token in the vault): nothing was started.' }
  elseif ($RunNow) { Write-CixWarn 'Not started: the analyst needs the brief the run just started writes. Run this again with -RunAnalystNow once that run has finished (its Execution history in the portal), or let the schedule run it.' }
  else {
    Confirm-Step 'Start one run of the Claude analyst now (it uses your Claude usage; it needs the brief the nightly run writes)?'
    Invoke-CixAz @('rest', '--method', 'post', '--url', "https://management.azure.com$analystJobId/start?api-version=2025-01-01", '--only-show-errors', '-o', 'none') | Out-Null
    Write-CixInfo 'Started. In five to fifteen minutes the Stocks page shows Claude''s view; the run''s log is in the Azure portal (the analyst job, Execution history).'
  }
}

Write-CixStep 'Done'
Write-CixInfo "The job runs at '$($stocks.schedule)' UTC, after each US session, and writes the advice the Stocks tab shows."
if ($analystEnabled) { Write-CixInfo "The Claude analyst runs at '$($stocks.analystSchedule)' UTC and adds its view of each night's advice." }
Write-CixInfo 'They only read EODHD and the web and write their own database: you carry the advice out in Revolut yourself.'
