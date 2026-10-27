#Requires -Version 5.1
<#
  Shared helpers for tools/azure/provision.ps1 and tools/azure/reset-database.ps1.
  - Settings come from infra/environments/<name>.json; nothing environment-specific is hard-coded here.
  - Azure is reached through the Azure CLI (az), signed in as you. Keys are read into memory when needed and never printed.
  - Runs on Windows PowerShell 5.1 and PowerShell 7: no 7-only syntax or parameters (CI runs tests/azure on both).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$script:CixContainers = @('journal', 'state', 'requests')
$script:OnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false
$script:HttpClient = $null
Add-Type -AssemblyName System.Net.Http # built in on PowerShell 7; Windows PowerShell 5.1 loads it on demand

function Join-CixPath {
  <# Join-Path for several parts (Windows PowerShell 5.1's Join-Path takes only two). #>
  param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Child)
  foreach ($c in $Child) { $Path = Join-Path $Path $c }
  return $Path
}

function ConvertTo-CixHashtable {
  <# ConvertFrom-Json output as nested hashtables and arrays (what -AsHashtable does on PowerShell 7). #>
  param([AllowNull()][object]$InputObject)
  if ($null -eq $InputObject) { return $null }
  if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
    $table = @{}
    foreach ($p in $InputObject.PSObject.Properties) { $table[$p.Name] = ConvertTo-CixHashtable $p.Value }
    return $table
  }
  if ($InputObject -is [System.Collections.IList]) {
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($item in $InputObject) { $list.Add((ConvertTo-CixHashtable $item)) }
    return , $list.ToArray()
  }
  return $InputObject
}

function ConvertFrom-CixJson {
  <#
    ConvertFrom-Json that sends an array's items down the pipeline one by one on every version (Windows PowerShell 5.1 sends
    the whole array as one object, which breaks Where-Object filters).
  #>
  param([Parameter(Mandatory, ValueFromPipeline)][AllowEmptyString()][string]$Json)
  process {
    $value = $Json | ConvertFrom-Json
    if ($null -eq $value) { return }
    if ($value -is [System.Array]) { foreach ($item in $value) { $item } } else { $value }
  }
}

function Get-CixRepoRoot { $script:RepoRoot }

function Write-CixStep([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-CixInfo([string]$Text) { Write-Host "    $Text" }
function Write-CixWarn([string]$Text) { Write-Host "    ! $Text" -ForegroundColor Yellow }

function Read-CixEnvironment {
  <# Loads and checks infra/environments/<Name>.json, and the shared Cosmos DB settings it names (as .shared). #>
  param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Name)
  $path = Join-CixPath $script:RepoRoot 'infra' 'environments' "$Name.json"
  if (-not (Test-Path $path)) {
    throw "No settings file for environment '$Name' ($path). Copy one of the files in infra/environments and edit it."
  }
  $s = ConvertTo-CixHashtable (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json)
  foreach ($key in 'environmentName', 'protected', 'resourceGroup', 'location', 'dashboardLocation', 'namePrefix', 'sharedCosmos', 'cosmos', 'deployDashboard') {
    if (-not $s.ContainsKey($key)) { throw "$path is missing '$key'." }
  }
  if ($s.environmentName -ne $Name) { throw "$path says environmentName '$($s.environmentName)', not '$Name'." }
  if ($s.protected -isnot [bool]) { throw "${path}: 'protected' must be true or false." }
  if ($s.resourceGroup -notmatch '^[\w\-\.\(\)]{1,90}$') { throw "${path}: resourceGroup isn't a valid name." }
  if ($s.cosmos.databaseName -notmatch '^[a-zA-Z0-9-]{1,64}$') { throw "${path}: cosmos.databaseName isn't a valid name." }
  if (-not ($s.cosmos.databaseThroughput -is [int] -or $s.cosmos.databaseThroughput -is [long]) -or $s.cosmos.databaseThroughput -lt 400 -or $s.cosmos.databaseThroughput -gt 1000) {
    throw "${path}: cosmos.databaseThroughput must be a whole number from 400 to 1000."
  }
  if ($s.subscriptionId -and $s.subscriptionId -notmatch '^[0-9a-fA-F-]{36}$') { throw "${path}: subscriptionId must be a GUID or empty." }
  $s['path'] = $path
  $s['shared'] = Read-CixSharedSettings -Name $s.sharedCosmos
  # Two environments in one shared account must not share a database (a reset of one would empty the other).
  foreach ($other in Get-ChildItem (Split-Path $path) -Filter *.json) {
    if ($other.FullName -eq (Resolve-Path $path).Path -or $other.Name -like '*.example.json') { continue }
    $o = Get-Content $other.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($o.PSObject.Properties['sharedCosmos'] -and $o.sharedCosmos -eq $s.sharedCosmos -and $o.PSObject.Properties['cosmos'] -and
        $o.cosmos.PSObject.Properties['databaseName'] -and $o.cosmos.databaseName -eq $s.cosmos.databaseName) {
      throw "${path}: database '$($s.cosmos.databaseName)' is also used by $($other.Name) in the same shared account. Give each environment its own."
    }
  }
  if ($s.shared.resourceGroup -eq $s.resourceGroup) { throw "${path}: the shared Cosmos DB account needs its own resource group, not $($s.resourceGroup)." }
  if ($s.ContainsKey('agents')) { Assert-CixAgentSettings -Settings $s -Path $path }
  return $s
}

function Assert-CixAgentSettings {
  <#
    Checks the optional 'agents' block: the engine and the agent jobs in Azure (ADR-0013). deploy is none, platform (what they
    run on) or apps (the paper engine and the three jobs too). Dates are kept as YYYY-MM-DD text whatever the JSON reader made
    of them (PowerShell 7 may read them as dates).
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$Path)
  $a = $Settings.agents
  if ($a -isnot [hashtable]) { throw "${Path}: 'agents' must be an object." }
  foreach ($key in 'deploy', 'location', 'startEngine', 'traderModel', 'analystModel', 'monthlyBudget', 'budgetStartDate') {
    if (-not $a.ContainsKey($key)) { throw "${Path}: agents is missing '$key'." }
  }
  foreach ($key in 'budgetStartDate', 'claudeTokenExpires') {
    if ($a.ContainsKey($key) -and $a[$key] -is [datetime]) { $a[$key] = $a[$key].ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
  }
  if (@('none', 'platform', 'apps') -notcontains $a.deploy) { throw "${Path}: agents.deploy must be none, platform or apps." }
  if ($a.location -notmatch '^[a-z0-9]{3,40}$') { throw "${Path}: agents.location must be an Azure region name, like polandcentral." }
  if ($a.startEngine -isnot [bool]) { throw "${Path}: agents.startEngine must be true or false." }
  foreach ($key in 'traderModel', 'analystModel') {
    if ("$($a[$key])" -notmatch '^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$') { throw "${Path}: agents.$key must be a model alias or name, like sonnet." }
  }
  if (-not ($a.monthlyBudget -is [int] -or $a.monthlyBudget -is [long]) -or $a.monthlyBudget -lt 1) { throw "${Path}: agents.monthlyBudget must be a positive whole number." }
  if ("$($a.budgetStartDate)" -notmatch '^\d{4}-\d{2}-01$') { throw "${Path}: agents.budgetStartDate must be the first of a month (YYYY-MM-01)." }
  if ($a.ContainsKey('claudeTokenExpires') -and $a.claudeTokenExpires -and "$($a.claudeTokenExpires)" -notmatch '^\d{4}-\d{2}-\d{2}$') {
    throw "${Path}: agents.claudeTokenExpires must be a date (YYYY-MM-DD) or empty."
  }
  # A protected environment's engine is pinned to paper too, and stands by holding the exchange keys until the owner switches it
  # (ADR-0017); CI never deploys there (setup-ci-deploy.ps1 refuses it).
}

function Read-CixSharedSettings {
  <# Loads and checks infra/shared/<Name>.json: the Cosmos DB account shared by the owner's projects. #>
  param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,24}$')][string]$Name)
  $path = Join-CixPath $script:RepoRoot 'infra' 'shared' "$Name.json"
  if (-not (Test-Path $path)) { throw "No shared settings file '$Name' ($path)." }
  $s = ConvertTo-CixHashtable (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json)
  foreach ($key in 'resourceGroup', 'location', 'accountName', 'enableFreeTier', 'allowedIpRanges') {
    if (-not $s.ContainsKey($key)) { throw "$path is missing '$key'." }
  }
  if ($s.resourceGroup -notmatch '^[\w\-\.\(\)]{1,90}$') { throw "${path}: resourceGroup isn't a valid name." }
  if ($s.accountName -and $s.accountName -notmatch '^[a-z0-9][a-z0-9-]{2,43}$') { throw "${path}: accountName must be 3 to 44 lowercase letters, digits or hyphens, or empty." }
  if ($s.enableFreeTier -isnot [bool]) { throw "${path}: 'enableFreeTier' must be true or false." }
  $s['path'] = $path
  return $s
}

function Assert-CixCommand {
  <# Fails with a clear message if a command-line tool is missing. #>
  param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Hint)
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "'$Name' isn't installed or isn't on PATH. $Hint" }
}

function Invoke-CixNative {
  <#
    Runs a native command. Windows PowerShell 5.1 can turn each stderr line into a terminating error when $ErrorActionPreference
    is Stop (always when redirected, and in the ISE), and az, gh and npm write warnings there; so the call runs with Continue,
    and callers check $LASTEXITCODE. The preference is relaxed in the caller's scope, because a script block from a script
    runs in that script's scope, not this module's (setting it here alone would not reach it).
  #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][scriptblock]$Command)
  $callerVariables = $PSCmdlet.SessionState.PSVariable
  $saved = $callerVariables.GetValue('ErrorActionPreference')
  $ErrorActionPreference = 'Continue'
  $callerVariables.Set('ErrorActionPreference', 'Continue')
  try {
    $global:LASTEXITCODE = -1 # a command that never started (not on PATH, broken shim) counts as failed, not as the last success
    & $Command
  }
  finally {
    $callerVariables.Set('ErrorActionPreference', $saved)
  }
}

function Invoke-CixAz {
  <#
    Runs the Azure CLI and returns its output. On failure throws with az's message. -Quiet hides the command line from the
    error (use it when the output is a secret, so nothing about the call is echoed).
  #>
  param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Quiet)
  $output = Invoke-CixNative { & az @Arguments 2>&1 }
  if ($LASTEXITCODE -ne 0) {
    $message = ($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() }) -join ' '
    if ($Quiet) { throw "The Azure CLI call failed." }
    throw "az $($Arguments[0..1] -join ' ') failed: $message"
  }
  return ($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"
}

function Assert-CixAzureCli {
  <# The Azure CLI, a recent enough version, and Bicep. #>
  Assert-CixCommand -Name 'az' -Hint 'Install it from https://learn.microsoft.com/cli/azure/install-azure-cli, then reopen the terminal.'
  $version = [version]((Invoke-CixAz @('version', '-o', 'json') | ConvertFrom-CixJson).'azure-cli')
  if ($version -lt [version]'2.60.0') { throw "Azure CLI $version is too old; run 'az upgrade' (2.60 or later is needed)." }
  Write-CixInfo "Azure CLI $version"
  $bicep = Invoke-CixNative { & az bicep version 2>&1 }
  if ($LASTEXITCODE -ne 0) {
    Write-CixInfo 'Installing Bicep for the Azure CLI...'
    Invoke-CixAz @('bicep', 'install') | Out-Null
  }
  else { Write-CixInfo ((@($bicep | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) | Select-Object -First 1) -replace '\s+', ' ') }
}

function Connect-CixAzure {
  <#
    Signs in if needed and selects the subscription: -SubscriptionId, else the settings file, else a choice from your
    subscriptions. Returns the selected account.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [string]$SubscriptionId)
  $null = Invoke-CixNative { & az account show -o none 2>&1 }
  if ($LASTEXITCODE -ne 0) {
    Write-CixInfo 'Signing in to Azure (a browser window opens)...'
    Invoke-CixNative { & az login -o none }
    if ($LASTEXITCODE -ne 0) { throw 'Azure sign-in failed.' }
  }

  $target = if ($SubscriptionId) { $SubscriptionId } elseif ($Settings.subscriptionId) { $Settings.subscriptionId } else { $null }
  if (-not $target) {
    $subs = @(Invoke-CixAz @('account', 'list', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson | Where-Object { $_.state -eq 'Enabled' })
    if ($subs.Count -eq 0) { throw 'This account has no enabled subscriptions.' }
    if ($subs.Count -eq 1) { $target = $subs[0].id }
    else {
      for ($i = 0; $i -lt $subs.Count; $i++) { Write-Host ("    [{0}] {1}  ({2})" -f ($i + 1), $subs[$i].name, $subs[$i].id) }
      $choice = Read-Host '    Subscription number'
      if ($choice -notmatch '^\d+$' -or [int]$choice -lt 1 -or [int]$choice -gt $subs.Count) { throw 'No subscription chosen.' }
      $target = $subs[[int]$choice - 1].id
    }
  }

  Invoke-CixAz @('account', 'set', '--subscription', $target) | Out-Null
  $account = Invoke-CixAz @('account', 'show', '-o', 'json') | ConvertFrom-CixJson
  if ($Settings.subscriptionId -and $account.id -ne $Settings.subscriptionId) {
    throw "The settings file pins subscription $($Settings.subscriptionId), but $($account.id) is selected."
  }
  Write-CixInfo "Subscription: $($account.name) ($($account.id))"
  return $account
}

function New-CixParametersFile {
  <# Writes deployment parameters (name -> value) to a temporary file and returns its path. #>
  param([Parameter(Mandatory)][System.Collections.IDictionary]$Values, [Parameter(Mandatory)][string]$Label)
  $parameters = [ordered]@{}
  foreach ($k in $Values.Keys) { $parameters[$k] = @{ value = $Values[$k] } }
  $document = [ordered]@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = $parameters
  }
  $path = Join-Path ([IO.Path]::GetTempPath()) "cix-$Label-$([guid]::NewGuid().ToString('N')).parameters.json"
  # No byte-order mark (5.1's Set-Content -Encoding utf8 writes one).
  [IO.File]::WriteAllText($path, ($document | ConvertTo-Json -Depth 10), $script:Utf8NoBom)
  return $path
}

function Get-CixDeploymentValues {
  <#
    The template and parameter values for one deployment: -Shared for the shared Cosmos DB account (infra/shared/main.bicep),
    otherwise CIX's resources (infra/main.bicep), which need the shared account's name.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [switch]$Shared, [string]$SharedAccountName, [hashtable]$Agents = @{})
  # -Shared with -SharedAccountName: update that existing account rather than the name the template would derive.
  # -Agents: values found at run time for the agents' deployment (engineAudience, engineImageDigest, agentImageDigest,
  # budgetContactEmail, ownerObjectId).
  $tags = if ($Settings.ContainsKey('tags')) { $Settings.tags } else { @{} }
  if ($Shared) {
    $sh = $Settings.shared
    return @{
      Template = Join-CixPath $script:RepoRoot 'infra' 'shared' 'main.bicep'
      Location = $sh.location
      Label    = 'shared'
      Values   = [ordered]@{
        resourceGroupName = $sh.resourceGroup
        location          = $sh.location
        accountName       = if ($SharedAccountName) { $SharedAccountName } else { [string]$sh.accountName }
        enableFreeTier    = [bool]$sh.enableFreeTier
        allowedIpRanges   = @($sh.allowedIpRanges)
        tags              = if ($sh.ContainsKey('tags')) { $sh.tags } else { @{} }
      }
    }
  }
  if (-not $SharedAccountName) { throw 'The shared Cosmos DB account must exist before CIX is deployed.' }
  $values = [ordered]@{
    environmentName         = $Settings.environmentName
    resourceGroupName       = $Settings.resourceGroup
    location                = $Settings.location
    dashboardLocation       = $Settings.dashboardLocation
    namePrefix              = $Settings.namePrefix
    sharedResourceGroupName = $Settings.shared.resourceGroup
    sharedAccountName       = $SharedAccountName
    databaseName            = $Settings.cosmos.databaseName
    databaseThroughput      = [int]$Settings.cosmos.databaseThroughput
    protectedEnvironment    = [bool]$Settings.protected
    deployDashboard         = [bool]$Settings.deployDashboard
    tags                    = $tags
  }
  if ($Settings.ContainsKey('agents')) {
    $a = $Settings.agents
    $values['agents'] = [string]$a.deploy
    $values['agentsLocation'] = [string]$a.location
    $values['startEngine'] = [bool]$a.startEngine
    $values['traderModel'] = [string]$a.traderModel
    $values['analystModel'] = [string]$a.analystModel
    $values['monthlyBudget'] = [int]$a.monthlyBudget
    $values['budgetStartDate'] = [string]$a.budgetStartDate
    if ($a.ContainsKey('claudeTokenExpires')) { $values['claudeTokenExpires'] = [string]$a.claudeTokenExpires }
    foreach ($key in 'engineAudience', 'engineImageDigest', 'agentImageDigest', 'budgetContactEmail', 'ownerObjectId') {
      if ($Agents.ContainsKey($key)) { $values[$key] = [string]$Agents[$key] }
    }
  }
  return @{
    Template = Join-CixPath $script:RepoRoot 'infra' 'main.bicep'
    Location = $Settings.location
    Label    = "cix-$($Settings.environmentName)"
    Values   = $values
  }
}

function Invoke-CixDeployment {
  <#
    Deploys a subscription-scope template (it creates its own resource group) and returns its outputs. -WhatIf previews
    only. -Shared deploys the shared Cosmos DB account; otherwise CIX's resources, in the shared account -SharedAccountName,
    with -Agents (see Get-CixDeploymentValues).
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [switch]$Shared, [string]$SharedAccountName, [hashtable]$Agents = @{}, [switch]$WhatIf)
  $d = Get-CixDeploymentValues -Settings $Settings -Shared:$Shared -SharedAccountName $SharedAccountName -Agents $Agents
  $parameters = New-CixParametersFile -Values $d.Values -Label $d.Label
  try {
    if ($WhatIf) {
      # Straight to the console: the caller must not swallow the preview it is about to ask you to approve.
      Invoke-CixNative { & az deployment sub what-if --location $d.Location --template-file $d.Template --parameters "@$parameters" --only-show-errors | Out-Host }
      if ($LASTEXITCODE -ne 0) { throw 'The what-if preview failed.' }
      return $null
    }
    $name = "$($d.Label)-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))"
    $result = Invoke-CixAz @('deployment', 'sub', 'create', '--location', $d.Location, '--name', $name,
      '--template-file', $d.Template, '--parameters', "@$parameters", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
    return $result.properties.outputs
  }
  finally {
    Remove-Item $parameters -ErrorAction SilentlyContinue
  }
}

function Find-CixSharedCosmos {
  <#
    The shared Cosmos DB account: the one tagged shared-role=cosmos in the shared resource group (and named accountName when
    the settings give one). Returns $null only when the group doesn't exist or holds no Cosmos DB account at all, so an
    untagged or misnamed account is never mistaken for "none yet" and overwritten. Refuses to guess between several.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $sh = $Settings.shared
  if ((Invoke-CixAz @('group', 'exists', '--name', $sh.resourceGroup)) -ne 'true') { return $null }
  $all = @(Invoke-CixAz @('cosmosdb', 'list', '--resource-group', $sh.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  $accounts = @($all | Where-Object { $_.tags -and $_.tags.PSObject.Properties['shared-role'] -and $_.tags.'shared-role' -eq 'cosmos' })
  if ($sh.accountName) { $accounts = @($accounts | Where-Object { $_.name -eq $sh.accountName }) }
  if ($accounts.Count -gt 1) { throw "More than one Cosmos DB account in $($sh.resourceGroup) is tagged shared-role=cosmos; set accountName in $($sh.path)." }
  if ($accounts.Count -eq 1) { return $accounts[0] }
  if ($all.Count -gt 0) {
    $wanted = if ($sh.accountName) { " named $($sh.accountName)" } else { '' }
    throw "Resource group $($sh.resourceGroup) has Cosmos DB account(s) ($(($all | ForEach-Object { $_.name }) -join ', ')) but none tagged shared-role=cosmos$wanted. Tag the shared one or fix accountName in $($sh.path); refusing to create or overwrite an account."
  }
  return $null
}

function Get-CixCosmosAccount {
  <# The shared Cosmos DB account that holds CIX's database; fails if it doesn't exist yet. #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $account = Find-CixSharedCosmos -Settings $Settings
  if (-not $account) {
    throw "No shared Cosmos DB account (tag shared-role=cosmos) in resource group $($Settings.shared.resourceGroup). Run provision.ps1 first."
  }
  return $account
}

function Test-CixDatabase {
  <# Whether CIX's database exists in the shared account. Fails on any error other than "not found". #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$AccountName)
  $output = Invoke-CixNative { & az cosmosdb sql database show --account-name $AccountName --resource-group $Settings.shared.resourceGroup `
      --name $Settings.cosmos.databaseName --only-show-errors -o none 2>&1 }
  if ($LASTEXITCODE -eq 0) { return $true }
  if ((($output | ForEach-Object { "$_" }) -join ' ') -match 'NotFound|not found|does not exist') { return $false }
  throw "Couldn't check whether database '$($Settings.cosmos.databaseName)' exists in $AccountName."
}

function Get-CixEnvironmentTags {
  <# The tags on CIX's resource group (set by infra/main.bicep). $null if the group doesn't exist. #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -ne 'true') { return $null }
  $group = Invoke-CixAz @('group', 'show', '--name', $Settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  return $group.tags
}

function Assert-CixDatabaseClaim {
  <#
    The database belongs to this environment and no other: no other resource group claims it (tags cix-database and
    cix-cosmos-account, set by infra/main.bicep). With -RequireOwn, this environment's group must claim it too (resets).
    For a deployment, a database that already exists must already be this environment's, so a settings typo can't take
    over another project's or environment's database.
    The group Azure makes for this environment's Container Apps environment (ME_..., managed by it) copies its tags, so it
    carries the claim too; it is part of this environment, not another one.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$AccountName, [switch]$RequireOwn)
  $db = $Settings.cosmos.databaseName
  $ownManaged = "(?i)^/subscriptions/[^/]+/resourceGroups/$([regex]::Escape($Settings.resourceGroup))/providers/Microsoft\.App/managedEnvironments/[^/]+$"
  $claims = @(Invoke-CixAz @('group', 'list', '--tag', "cix-database=$db", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
      Where-Object { $_.tags -and $_.tags.PSObject.Properties['cix-cosmos-account'] -and $_.tags.'cix-cosmos-account' -eq $AccountName } |
      Where-Object { -not ($_.PSObject.Properties['managedBy'] -and "$($_.managedBy)" -match $ownManaged) })
  $others = @($claims | Where-Object { $_.name -ne $Settings.resourceGroup })
  if ($others.Count -gt 0) {
    throw "Database '$db' in $AccountName is claimed by resource group(s) $(($others | ForEach-Object { $_.name }) -join ', '), not only $($Settings.resourceGroup). Refusing."
  }
  $own = @($claims | Where-Object { $_.name -eq $Settings.resourceGroup }).Count -eq 1
  if ($RequireOwn -and -not $own) {
    throw "Resource group $($Settings.resourceGroup) isn't tagged cix-database=$db and cix-cosmos-account=$AccountName. Refusing."
  }
  if (-not $own -and (Test-CixDatabase -Settings $Settings -AccountName $AccountName)) {
    throw "Database '$db' already exists in $AccountName but no CIX environment claims it (it may be another project's). Choose another cosmos.databaseName in $($Settings.path). Refusing."
  }
}

function Get-CixCosmosKey {
  <# The account's primary key (read-write) or read-only key, as a string held in memory. Never print it. #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$AccountName, [switch]$ReadOnly)
  # The account is shared, so its keys reach every project's database in it (ADR-0009).
  $type = if ($ReadOnly) { 'read-only-keys' } else { 'keys' }
  $field = if ($ReadOnly) { 'primaryReadonlyMasterKey' } else { 'primaryMasterKey' }
  $keys = Invoke-CixAz -Quiet @('cosmosdb', 'keys', 'list', '--resource-group', $Settings.shared.resourceGroup, '--name', $AccountName,
    '--type', $type, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  $key = $keys.$field
  if (-not $key) { throw 'The Cosmos DB key could not be read.' }
  return $key
}

function Write-CixSecretFile {
  <# Writes secrets/<Name> readable only by you (the same way tools/setup-secrets.ps1 does). The value is never printed. #>
  param([Parameter(Mandatory)][ValidatePattern('^[a-z_]+$')][string]$Name, [Parameter(Mandatory)][string]$Value)
  $dir = Join-Path $script:RepoRoot 'secrets'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  if (-not $script:OnWindows) {
    Invoke-CixNative { chmod 700 $dir } # no one else can open a file in it, even before its chmod
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $dir; the key was not written." }
  }
  $path = Join-Path $dir $Name
  # A fresh, restricted (empty) file first, then the key: it is never on disk with wider access, and no old access rules survive.
  if (Test-Path $path) { Remove-Item $path -Force }
  New-Item -ItemType File -Path $path | Out-Null
  [IO.File]::WriteAllText($path, '', $script:Utf8NoBom)
  if ($script:OnWindows) {
    $me = "*$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
    Invoke-CixNative { icacls $path /inheritance:r /grant:r "${me}:(R,W)" | Out-Null }
  }
  else {
    Invoke-CixNative { chmod 600 $path }
  }
  if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $path; the key was not written." }
  [IO.File]::WriteAllText($path, $Value, $script:Utf8NoBom)
  return $path
}

# --- The engine and agents in Azure (agents.deploy platform or apps; ADR-0013) ---

function Assert-CixAgentsTools {
  <# Bicep 0.32 or later: the version the agents' templates are written and linted against. #>
  $line = "$(@(Invoke-CixNative { & az bicep version 2>&1 } | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) | Select-Object -First 1)"
  if ($line -notmatch '(\d+)\.(\d+)\.(\d+)') { throw "Couldn't read the Bicep version ($line); run 'az bicep upgrade'." }
  if ([version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" -lt [version]'0.32.0') { throw "Bicep $($Matches[0]) is too old for the agents' templates; run 'az bicep upgrade' (0.32 or later)." }
}

function Assert-CixAppsRegion {
  <#
    Container Apps environments must be offered in the region (owner decision, 2026-10-26: Poland Central, where the database
    is, else Germany West Central). Stops rather than switching by itself: a region can't be changed once things exist there.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $wanted = $Settings.agents.location
  # Filtered here rather than with --query: quotes inside native arguments don't survive Windows PowerShell 5.1.
  $provider = Invoke-CixAz @('provider', 'show', '--namespace', 'Microsoft.App', '-o', 'json') | ConvertFrom-CixJson
  foreach ($type in 'managedEnvironments', 'jobs', 'containerApps') {
    $found = @($provider.resourceTypes | Where-Object { $_.resourceType -eq $type })
    $regions = @($found | ForEach-Object { $_.locations } | ForEach-Object { ("$_" -replace '\s', '').ToLowerInvariant() })
    if ($regions -notcontains $wanted) {
      $hint = if ($regions -contains 'germanywestcentral') { ' Set agents.location to germanywestcentral (the agreed fallback)' } else { ' Choose another region for agents.location' }
      throw "Container Apps $type aren't offered in $wanted.$hint in $($Settings.path), and check the prices there."
    }
  }
}

function Get-CixSignedInUser {
  <#
    Your Entra object ID (Id: Key Vault Secrets Officer on the agents' vault, and the owner check on the engine's app
    registration) and sign-in address (Email: the budget's alerts, '' when there is none). Both are deployment parameters
    only; never written to the repo.
  #>
  $me = Invoke-CixAz @('ad', 'signed-in-user', 'show', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  $id = if ($me -and $me.PSObject.Properties['id']) { "$($me.id)" } else { '' }
  if ($id -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { throw 'Sign in to Azure as a user (az login): the engine and agents are set up by their owner, not by a service principal.' }
  $candidates = foreach ($field in 'mail', 'userPrincipalName') { if ($me.PSObject.Properties[$field]) { $me.$field } }
  $email = ''
  # A guest's or personal account's user name (name_gmail.com#EXT#@tenant.onmicrosoft.com) looks like an address but gets no mail.
  foreach ($candidate in @($candidates)) { if ("$candidate" -match '^[^@\s#]+@[^@\s]+\.[^@\s]+$') { $email = "$candidate"; break } }
  return @{ Id = $id; Email = $email }
}

function Assert-CixBudgetStart {
  <#
    Azure creates a budget only from the current month on, and its start can't be moved afterwards: a first deployment with
    agents.budgetStartDate in a past month would fail, so this stops first and says what to set. An existing budget keeps its date.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [datetime]$Today = [datetime]::UtcNow)
  $start = [datetime]::ParseExact($Settings.agents.budgetStartDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
  if ($start -ge [datetime]::new($Today.Year, $Today.Month, 1)) { return }
  $name = "budget-$($Settings.namePrefix)-$($Settings.environmentName)"
  if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -eq 'true') {
    $budgets = @(Invoke-CixAz @('consumption', 'budget', 'list', '--resource-group', $Settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
    if (@($budgets | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq $name }).Count -gt 0) { return }
  }
  throw "agents.budgetStartDate ($($Settings.agents.budgetStartDate)) is in a past month and $name doesn't exist yet; Azure only starts a new budget this month or later. Set it to $($Today.ToString('yyyy-MM', [Globalization.CultureInfo]::InvariantCulture))-01 in $($Settings.path)."
}

function Find-CixAgentsResource {
  <#
    The agents' Key Vault (-Kind keyvault) or container registry (-Kind acr) in CIX's resource group, tagged with this
    environment; $null before agents.deploy platform has created it.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][ValidateSet('keyvault', 'acr')][string]$Kind)
  if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -ne 'true') { return $null }
  $all = @(Invoke-CixAz @($Kind, 'list', '--resource-group', $Settings.resourceGroup, '-o', 'json') | ConvertFrom-CixJson)
  $found = @($all | Where-Object { Test-CixEnvironmentTag -Resource $_ -Settings $Settings })
  if ($found.Count -gt 1) { throw "More than one $Kind in $($Settings.resourceGroup) is tagged cix-environment=$($Settings.environmentName); refusing to guess." }
  if ($found.Count -eq 1) { return "$($found[0].name)" }
  return $null
}

function Test-CixEnvironmentTag {
  <# Whether an az resource (with .tags) is tagged cix-environment=<this environment>. Checked here, not in a --query: quotes
     inside native arguments don't survive Windows PowerShell 5.1. #>
  param([Parameter(Mandatory)][object]$Resource, [Parameter(Mandatory)][hashtable]$Settings)
  return [bool]($Resource.PSObject.Properties['tags'] -and $Resource.tags -and $Resource.tags.PSObject.Properties['cix-environment'] -and
    $Resource.tags.'cix-environment' -eq $Settings.environmentName)
}

function Test-CixVaultSecret {
  <# Whether the vault holds the secret. Lists names only: a secret's value is never read. #>
  param([Parameter(Mandatory)][string]$Vault, [Parameter(Mandatory)][string]$Name)
  $secrets = @(Invoke-CixAz @('keyvault', 'secret', 'list', '--vault-name', $Vault, '-o', 'json') | ConvertFrom-CixJson)
  return @($secrets | Where-Object { $_.PSObject.Properties['name'] -and $_.name -eq $Name }).Count -gt 0
}

function Remove-CixVaultSecretAndAccess {
  <# Deletes a secret (soft delete: the vault keeps it 7 days, recoverable only by you) and every role assignment on it. #>
  param([Parameter(Mandatory)][string]$Vault, [Parameter(Mandatory)][string]$Name)
  $vaultId = "$(Invoke-CixAz @('keyvault', 'show', '--name', $Vault, '--query', 'id', '-o', 'tsv'))".Trim()
  if ($vaultId -notmatch '^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.KeyVault/vaults/[^/]+$') { throw "Couldn't find Key Vault $Vault's resource ID." }
  Invoke-CixAz -Quiet @('role', 'assignment', 'delete', '--scope', "$vaultId/secrets/$Name", '--yes', '--only-show-errors') | Out-Null
  Invoke-CixAz -Quiet @('keyvault', 'secret', 'delete', '--vault-name', $Vault, '--name', $Name, '--only-show-errors', '-o', 'none') | Out-Null
}

function Set-CixVaultSecretFromFile {
  <# Stores a file's content as a secret. The value goes from the file to az directly: never through a variable or argument. #>
  param([Parameter(Mandatory)][string]$Vault, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Path,
    [string]$ContentType = 'Claude setup token or API key')
  Invoke-CixAz -Quiet @('keyvault', 'secret', 'set', '--vault-name', $Vault, '--name', $Name, '--file', $Path, '--encoding', 'utf-8',
    '--content-type', $ContentType, '--query', 'name', '-o', 'tsv') | Out-Null
}

function Get-CixServicePrincipalAccess {
  <#
    Role assignments of service principals and managed identities on CIX's resource group, including those inherited from
    the subscription: in a protected environment there should be none but its own identities' (ADR-0017), and above all no CI
    identity's. Returns 'role: principal' lines; empty when the group doesn't exist yet. Names and IDs only.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -ne 'true') { return @() }
  $all = @(Invoke-CixAz @('role', 'assignment', 'list', '--resource-group', $Settings.resourceGroup, '--include-inherited', '-o', 'json') | ConvertFrom-CixJson)
  # On the group itself or above it: the environment's own identities hold roles on single resources inside it, which is fine.
  $inside = "/resourceGroups/$($Settings.resourceGroup)/providers/"
  return @($all | Where-Object {
      $_ -and $_.PSObject.Properties['principalType'] -and "$($_.principalType)" -eq 'ServicePrincipal' -and
      $_.PSObject.Properties['scope'] -and "$($_.scope)".IndexOf($inside, [StringComparison]::OrdinalIgnoreCase) -lt 0 } |
    ForEach-Object { "$($_.roleDefinitionName): $($_.principalName)" } | Sort-Object -Unique)
}

# The exchange keys of a protected environment's engine: the Key Vault secret, and what provision.ps1 calls the file it reads.
$script:ExchangeKeySecrets = @(
  @{ Name = 'revolutx-api-key'; Parameter = 'ExchangeApiKeyFile' },
  @{ Name = 'revolutx-private-key'; Parameter = 'ExchangePrivateKeyFile' })
function Get-CixExchangeKeySecrets { $script:ExchangeKeySecrets }

function Get-CixEngineAudience {
  <#
    The engine's application ID URI (api://<app ID>), from its Entra app registration cix-engine-<environment>. -Create makes
    the registration (single tenant, no secrets, no permissions) and its service principal when missing; without -Create a
    missing one returns ''. The jobs ask for tokens for this audience; the engine accepts only their identities.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$OwnerId, [switch]$Create)
  $name = "cix-engine-$($Settings.environmentName)"
  # --display-name matches by prefix: keep exact names only.
  $apps = @(Invoke-CixAz @('ad', 'app', 'list', '--display-name', $name, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
      Where-Object { $_.PSObject.Properties['displayName'] -and $_.displayName -ceq $name })
  if ($apps.Count -gt 1) { throw "More than one Entra app registration is named $name; delete the extra ones or rename them." }
  if ($apps.Count -eq 1) {
    $appId = "$($apps[0].appId)"
    # Adopted only when it is yours alone: anyone else who owns it could change what the engine trusts.
    $owners = @(Invoke-CixAz @('ad', 'app', 'owner', 'list', '--id', $appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
    $others = @($owners | Where-Object { "$($_.id)" -ne $OwnerId })
    if ($others.Count -gt 0 -or $owners.Count -eq 0) {
      $who = if ($others.Count -gt 0) { "other owners ($(($others | ForEach-Object { $_.id }) -join ', '))" } else { 'no owner' }
      $fix = if ($others.Count -gt 0) { 'make yourself its only owner' } else { "make yourself its owner (az ad app owner add --id $appId --owner-object-id $OwnerId)" }
      throw "The Entra app registration $name has $who. Refusing to use it: $fix, or delete it and run this again to create one."
    }
  }
  elseif (-not $Create) { return '' }
  else {
    $appId = "$(Invoke-CixAz @('ad', 'app', 'create', '--display-name', $name, '--sign-in-audience', 'AzureADMyOrg', '--query', 'appId', '-o', 'tsv'))".Trim()
    if ($appId -notmatch '^[0-9a-fA-F-]{36}$') { throw "Unexpected app ID for $name." }
    # az ad app create (Microsoft Graph) doesn't make the creator an owner: add yourself, so a later run can tell it's yours.
    Invoke-CixAz @('ad', 'app', 'owner', 'add', '--id', $appId, '--owner-object-id', $OwnerId, '--only-show-errors') | Out-Null
    Write-CixInfo "Created the Entra app registration $name ($appId), owned by you."
  }
  if ($appId -notmatch '^[0-9a-fA-F-]{36}$') { throw "Unexpected app ID for $name." }
  $audience = "api://$appId"
  # Finished here when it was left half made (no identifier URI, no service principal); a preview only reports it.
  if ($apps.Count -eq 0 -or -not $apps[0].PSObject.Properties['identifierUris'] -or @($apps[0].identifierUris) -notcontains $audience) {
    if (-not $Create) { return '' }
    Invoke-CixAz @('ad', 'app', 'update', '--id', $appId, '--identifier-uris', $audience) | Out-Null
  }
  $principals = @(Invoke-CixAz @('ad', 'sp', 'list', '--filter', "appId eq '$appId'", '-o', 'json') | ConvertFrom-CixJson)
  if ($principals.Count -eq 0) {
    if (-not $Create) { return '' }
    Invoke-CixAz @('ad', 'sp', 'create', '--id', $appId, '--query', 'id', '-o', 'tsv') | Out-Null
  }
  return $audience
}

function Set-CixEngineAppRole {
  <#
    Who may get a token for the engine: its app registration defines one app role, CIX.Agent (for applications only), its
    service principal requires an assignment, and the role is assigned to the three job identities and nobody else. Entra
    then refuses a token for the engine to every other identity in the tenant, and the engine checks the role as well
    (ToolServer:Entra). Run after the apps' deployment, which returns the job identities. Refuses, changing nothing, if anyone
    else holds an assignment, or if anyone but you (-OwnerId) owns the service principal (an owner could assign others).
    -Ask: called with a question before the first change; it stops the run unless the answer is yes.
  #>
  param([Parameter(Mandatory)][string]$Audience, [Parameter(Mandatory)][string[]]$JobPrincipalIds,
    [Parameter(Mandatory)][string]$OwnerId, [Parameter(Mandatory)][scriptblock]$Ask)
  if ($Audience -notmatch '^api://([0-9a-fA-F-]{36})$') { throw "Unexpected engine audience $Audience." }
  $appId = $Matches[1]
  $ids = @($JobPrincipalIds | ForEach-Object { "$_".ToLowerInvariant() })
  if ($ids.Count -lt 1 -or @($ids | Where-Object { $_ -notmatch '^[0-9a-f-]{36}$' }).Count -gt 0) { throw 'The deployment returned no job identities.' }
  $roleValue = 'CIX.Agent'
  $field = { param($Object, $Name) if ($Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } }

  $app = Invoke-CixAz @('ad', 'app', 'show', '--id', $appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  $roles = @(@(& $field $app 'appRoles') | Where-Object { $_ })
  $role = @($roles | Where-Object { (& $field $_ 'value') -ceq $roleValue })
  if ($role.Count -gt 1) { throw "The engine's app registration defines $roleValue more than once." }
  $sp = Invoke-CixAz @('ad', 'sp', 'show', '--id', $appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  if ("$(& $field $sp 'id')" -notmatch '^[0-9a-fA-F-]{36}$') { throw "The engine's app registration has no service principal." }
  $owners = @(Invoke-CixAz @('ad', 'sp', 'owner', 'list', '--id', "$($sp.id)", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
    ForEach-Object { "$(& $field $_ 'id')" } | Where-Object { $_ -ne $OwnerId })
  if ($owners.Count -gt 0) { throw "The engine's service principal has other owners ($($owners -join ', ')), who could let other identities call the engine. Remove them in the Entra admin center (Enterprise applications, cix-engine, Owners)." }
  $assignedUrl = "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo"
  $page = Invoke-CixAz @('rest', '--method', 'get', '--url', $assignedUrl, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  # Only the three jobs belong here: more than one page of assignments is already wrong.
  if (& $field $page '@odata.nextLink') { throw "The engine's service principal has more assignments than one page lists: only the job identities may call the engine. Remove the others in the Entra admin center (Enterprise applications, cix-engine, Users and groups)." }
  $assigned = @(@(& $field $page 'value') | Where-Object { $_ })

  # Checked before anything changes: the role, if there, is for applications only and enabled; nobody else is assigned.
  if ($role.Count -eq 1) {
    $types = @(& $field $role[0] 'allowedMemberTypes')
    if ($types.Count -ne 1 -or $types[0] -cne 'Application' -or -not (& $field $role[0] 'isEnabled')) {
      throw "The engine's app role $roleValue must be enabled and for applications only; fix or remove it in the Entra admin center (App registrations, cix-engine, App roles)."
    }
  }
  # Before the role exists, every assignment is someone else's (default access, say), even a job identity's.
  $strays = @($assigned | Where-Object { $ids -notcontains "$(& $field $_ 'principalId')".ToLowerInvariant() -or $role.Count -eq 0 -or "$(& $field $_ 'appRoleId')" -ne "$(& $field $role[0] 'id')" })
  if ($strays.Count -gt 0) {
    throw "The engine's service principal has assignments this script didn't make ($(($strays | ForEach-Object { "$(& $field $_ 'principalDisplayName') ($(& $field $_ 'principalId'))" }) -join ', ')). Remove them in the Entra admin center (Enterprise applications, cix-engine, Users and groups): only the job identities may call the engine."
  }

  $missing = @($ids | Where-Object { $id = $_; @($assigned | Where-Object { "$(& $field $_ 'principalId')".ToLowerInvariant() -eq $id }).Count -eq 0 })
  if ($role.Count -eq 0 -or -not (& $field $sp 'appRoleAssignmentRequired') -or $missing.Count -gt 0) {
    & $Ask "Give the engine's Entra app the role $roleValue, require it for every token, and assign it to the $($ids.Count) job identities only?"
  }
  if ($role.Count -eq 0) {
    $new = [ordered]@{
      allowedMemberTypes = @('Application'); description = 'Call the CIX engine as an agent job.'; displayName = 'CIX agent job'
      id = [guid]::NewGuid().ToString(); isEnabled = $true; value = $roleValue
    }
    $file = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N')).json"
    # -InputObject keeps a one-item array an array in Windows PowerShell 5.1. Holds no secret.
    # Other roles are sent back with only the properties an update takes (not the read-only origin).
    $keep = @($roles | Select-Object allowedMemberTypes, description, displayName, id, isEnabled, value)
    [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject @($keep + @([pscustomobject]$new)) -Depth 5), $script:Utf8NoBom)
    try { Invoke-CixAz @('ad', 'app', 'update', '--id', $appId, '--app-roles', "@$file", '--only-show-errors') | Out-Null }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
    $role = @([pscustomobject]$new)
    Write-CixInfo "Added the app role $roleValue to the engine's app registration."
  }
  # Assigned first, required last: a failure on the way never leaves Entra requiring an assignment a job lacks.
  foreach ($id in $missing) {
    $file = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N')).json"
    [IO.File]::WriteAllText($file, ([ordered]@{ principalId = $id; resourceId = "$($sp.id)"; appRoleId = "$(& $field $role[0] 'id')" } | ConvertTo-Json), $script:Utf8NoBom)
    # A role added moments ago can take a little while to reach the service principal.
    try { Invoke-CixAz @('rest', '--method', 'post', '--url', $assignedUrl, '--body', "@$file", '--headers', 'Content-Type=application/json', '--only-show-errors', '-o', 'none') | Out-Null }
    catch { throw "$($_.Exception.Message) If the app role was only just added, run this again shortly." }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
    Write-CixInfo "Assigned $roleValue to job identity $id."
  }
  if (-not (& $field $sp 'appRoleAssignmentRequired')) {
    Invoke-CixAz @('ad', 'sp', 'update', '--id', "$($sp.id)", '--set', 'appRoleAssignmentRequired=true', '--only-show-errors') | Out-Null
    Write-CixInfo "The engine's service principal now requires an assignment: no other identity can get a token for it."
  }
}

function Resolve-CixImageDigests {
  <#
    The images to deploy, as digests (sha256:...), so that what runs is fixed: a tag pushed again can't change it.
    -Tag: the images CI tagged with it (the commit SHA). Without it: the images the engine and the trader job run now (CI
    deploys new ones), or $null before they exist. Returns @{ Engine; Agent }, the digests of cix-engine and cix-trader, after
    checking both are in the registry. Generic az resource calls: the containerapp commands need an extension that a fresh
    Azure CLI would stop to ask about.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$Registry, [string]$Tag)
  $hint = 'CI pushes images on merge to main (or pass -ImageTag with one it pushed).'
  $refs = @{}
  if ($Tag) {
    $repositories = @(Invoke-CixAz @('acr', 'repository', 'list', '--name', $Registry, '-o', 'json') | ConvertFrom-CixJson)
    foreach ($repository in 'cix-engine', 'cix-trader') {
      if ($repositories -notcontains $repository) { throw "$Registry has no $repository images yet. $hint" }
      $tags = @(Invoke-CixAz @('acr', 'repository', 'show-tags', '--name', $Registry, '--repository', $repository, '-o', 'json') | ConvertFrom-CixJson)
      if ($tags -notcontains $Tag) { throw "$Registry has no $repository image tagged $Tag. $hint" }
      $refs[$repository] = "${repository}:$Tag"
    }
  }
  else {
    if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -ne 'true') { return $null }
    $prefix = "$($Settings.namePrefix)-$($Settings.environmentName)"
    $server = "$($Registry.ToLowerInvariant()).azurecr.io"
    $wanted = @(
      @{ Type = 'Microsoft.App/containerApps'; Name = "ca-$prefix-engine"; Repository = 'cix-engine' },
      @{ Type = 'Microsoft.App/jobs'; Name = "caj-$prefix-trader"; Repository = 'cix-trader' }) # all three jobs run the trader image
    $all = @(Invoke-CixAz @('resource', 'list', '--resource-group', $Settings.resourceGroup, '-o', 'json') | ConvertFrom-CixJson)
    $ids = foreach ($w in $wanted) {
      $name = $w.Name.Substring(0, [Math]::Min(32, $w.Name.Length))
      $found = @($all | Where-Object { "$($_.type)" -eq $w.Type -and "$($_.name)" -eq $name -and (Test-CixEnvironmentTag -Resource $_ -Settings $Settings) })
      if ($found.Count -ne 1) { return $null }
      "$($found[0].id)"
    }
    $running = @(Invoke-CixAz (@('resource', 'show', '--ids') + @($ids) + @('-o', 'json')) | ConvertFrom-CixJson)
    foreach ($w in $wanted) {
      $resource = @($running | Where-Object { "$($_.type)" -eq $w.Type })
      if ($resource.Count -ne 1) { return $null }
      $image = "$(@($resource[0].properties.template.containers)[0].image)"
      # A digest (deployed by CI or by this script) or, from before digests, a tag.
      if ($image -cnotmatch "^$([regex]::Escape($server))/$($w.Repository)(@sha256:[0-9a-f]{64}|:[A-Za-z0-9._-]{1,128})$") { throw "$($w.Name) runs $image, not a $($w.Repository) image from $Registry. Pass -ImageTag with a tag CI pushed." }
      $refs[$w.Repository] = "$($w.Repository)$($Matches[1])"
    }
  }
  $digests = @{}
  foreach ($repository in 'cix-engine', 'cix-trader') {
    $digest = "$(Invoke-CixAz @('acr', 'repository', 'show', '--name', $Registry, '--image', $refs[$repository], '--query', 'digest', '-o', 'tsv'))".Trim()
    if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw "$Registry has no image $($refs[$repository]). $hint" }
    $digests[$repository] = $digest
  }
  return @{ Engine = $digests['cix-engine']; Agent = $digests['cix-trader'] }
}

function Get-CixStaticSiteSettings {
  <#
    A Static Web App's application settings as a hashtable of names and values. They include secrets (the read-only key, the
    writer's secret): keep them in memory, never print them. Read through the management API, so no value is on a command line.
  #>
  param([Parameter(Mandatory)][ValidatePattern('^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Web/staticSites/[^/]+$')][string]$SiteId)
  $json = Invoke-CixAz -Quiet @('rest', '--method', 'post', '--url', "https://management.azure.com$SiteId/listAppSettings?api-version=2024-04-01", '--only-show-errors', '-o', 'json')
  $value = $json | ConvertFrom-CixJson
  $table = @{}
  if ($value -and $value.PSObject.Properties['properties'] -and $value.properties) {
    foreach ($p in $value.properties.PSObject.Properties) { $table[$p.Name] = [string]$p.Value }
  }
  return $table
}

function Set-CixStaticSiteSettings {
  <#
    Replaces a Static Web App's application settings with $Settings: all of them, so pass the current ones with the change
    merged in. The values go to the management API in a file only you can read (never on a command line, where other
    programs could see them), and the file is deleted straight after.
  #>
  param(
    [Parameter(Mandatory)][ValidatePattern('^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Web/staticSites/[^/]+$')][string]$SiteId,
    [Parameter(Mandatory)][hashtable]$Settings
  )
  # A fresh directory only you can enter (Linux and macOS share /tmp), then the file in it, restricted before any value is
  # written; on Windows the per-user temp folder plus an ACL on the file.
  $dir = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N'))"
  $path = Join-Path $dir 'settings.json'
  try {
    New-Item -ItemType Directory -Path $dir | Out-Null
    if (-not $script:OnWindows) {
      Invoke-CixNative { chmod 700 $dir }
      if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to a temporary folder; the settings were not changed." }
    }
    [IO.File]::WriteAllText($path, '', $script:Utf8NoBom)
    if ($script:OnWindows) {
      $me = "*$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
      Invoke-CixNative { icacls $path /inheritance:r /grant:r "${me}:(R,W,D)" | Out-Null }
    }
    else { Invoke-CixNative { chmod 600 $path } }
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to a temporary file; the settings were not changed." }
    [IO.File]::WriteAllText($path, (@{ properties = $Settings } | ConvertTo-Json -Depth 3), $script:Utf8NoBom)
    Invoke-CixAz -Quiet @('rest', '--method', 'put', '--url', "https://management.azure.com$SiteId/config/appsettings?api-version=2024-04-01",
      '--body', "@$path", '--only-show-errors', '-o', 'none') | Out-Null
  }
  finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

function Set-CixEnvValue {
  <# Sets KEY=value in the repository's .env (git-ignored; never holds secrets). #>
  param([Parameter(Mandatory)][ValidatePattern('^[A-Z_]+$')][string]$Key, [Parameter(Mandatory)][AllowEmptyString()][string]$Value)
  $path = Join-Path $script:RepoRoot '.env'
  $lines = @()
  if (Test-Path $path) { $lines = @(Get-Content $path | Where-Object { $_ -notmatch "^$Key=" }) }
  $lines += "$Key=$Value"
  Set-Content -Path $path -Value $lines -Encoding ascii
}

# --- Cosmos DB data plane (REST), for the reset script --------------------------------------------------------------------

function New-CixCosmosAuthToken {
  <# Master-key authorization for one request (https://learn.microsoft.com/rest/api/cosmos-db/access-control-on-cosmosdb-resources). #>
  param(
    [Parameter(Mandatory)][string]$Verb,
    [Parameter(Mandatory)][string]$ResourceType,
    [Parameter(Mandatory)][AllowEmptyString()][string]$ResourceLink,
    [Parameter(Mandatory)][string]$Date,
    [Parameter(Mandatory)][string]$Key
  )
  $payload = "$($Verb.ToLowerInvariant())`n$($ResourceType.ToLowerInvariant())`n$ResourceLink`n$($Date.ToLowerInvariant())`n`n"
  $hmac = [System.Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($Key))
  try { $signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))) }
  finally { $hmac.Dispose() }
  return [Uri]::EscapeDataString("type=master&ver=1.0&sig=$signature")
}

function Invoke-CixCosmosRequest {
  <#
    One Cosmos DB REST call with retries for throttling (429) and brief outages (503). Returns @{ Status; Body; Continuation }.
    Error messages name the status only, never the key or the response body.
  #>
  param(
    [Parameter(Mandatory)][string]$Endpoint,
    [Parameter(Mandatory)][string]$Key,
    [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'DELETE')][string]$Method,
    [Parameter(Mandatory)][string]$ResourceType,
    [Parameter(Mandatory)][string]$ResourceLink,
    [Parameter(Mandatory)][string]$Path,
    [object]$PartitionKey,
    [string]$Body,
    [hashtable]$Headers = @{},
    [switch]$CrossPartition
  )
  $uri = [Uri]$Endpoint
  if ($uri.Scheme -ne 'https' -and -not $uri.IsLoopback) { throw 'The Cosmos DB endpoint must use https.' } # loopback: the tests' fake server
  $base = $uri.GetLeftPart([UriPartial]::Authority)
  $client = Get-CixHttpClient
  for ($attempt = 1; ; $attempt++) {
    $date = [DateTime]::UtcNow.ToString('R')
    $h = @{
      'authorization' = New-CixCosmosAuthToken -Verb $Method -ResourceType $ResourceType -ResourceLink $ResourceLink -Date $date -Key $Key
      'x-ms-date'     = $date
      'x-ms-version'  = '2018-12-31'
    }
    foreach ($k in $Headers.Keys) { $h[$k] = $Headers[$k] }
    if ($null -ne $PartitionKey) { $h['x-ms-documentdb-partitionkey'] = (ConvertTo-Json @($PartitionKey) -Compress) }
    if ($CrossPartition) { $h['x-ms-documentdb-query-enablecrosspartition'] = 'True' }

    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($Method)), ([Uri]"$base/$Path")
    $response = $null
    try {
      foreach ($k in $h.Keys) { [void]$request.Headers.TryAddWithoutValidation($k, [string]$h[$k]) }
      if ($Body) {
        $request.Content = New-Object System.Net.Http.StringContent $Body, ([Text.Encoding]::UTF8)
        $request.Content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue 'application/query+json'
      }
      # Status codes come back as values, not exceptions, the same on 5.1 and 7.
      $response = $client.SendAsync($request).GetAwaiter().GetResult()
      $status = [int]$response.StatusCode
      if (($status -eq 429 -or $status -eq 503) -and $attempt -lt 8) {
        $wait = 1000
        $retryAfter = Get-CixHeader $response 'x-ms-retry-after-ms'
        if ($retryAfter -match '^\d+$') { $wait = [int]$retryAfter }
        Start-Sleep -Milliseconds ([Math]::Min([Math]::Max($wait, 100) * $attempt, 10000))
        continue
      }
      $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      return @{ Status = $status; Body = $content; Continuation = (Get-CixHeader $response 'x-ms-continuation') }
    }
    finally {
      if ($response) { $response.Dispose() }
      $request.Dispose()
    }
  }
}

function Get-CixHeader {
  <# One response header's value, or $null. #>
  param([Parameter(Mandatory)][System.Net.Http.HttpResponseMessage]$Response, [Parameter(Mandatory)][string]$Name)
  $values = $null
  if ($Response.Headers.TryGetValues($Name, [ref]$values)) { return (@($values) -join ',') }
  return $null
}

function Get-CixHttpClient {
  <# One HttpClient for the session. TLS 1.2 is switched on for Windows PowerShell 5.1 (.NET Framework), which may default to older versions. #>
  if (-not $script:HttpClient) {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
      # Only on 5.1, and only add TLS 1.2: SystemDefault (0) already allows it and anything newer.
      $protocols = [Net.ServicePointManager]::SecurityProtocol
      if ([int]$protocols -ne 0 -and -not ($protocols -band [Net.SecurityProtocolType]::Tls12)) {
        [Net.ServicePointManager]::SecurityProtocol = $protocols -bor [Net.SecurityProtocolType]::Tls12
      }
    }
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false # Cosmos DB never redirects; don't send signed requests anywhere else
    $script:HttpClient = New-Object System.Net.Http.HttpClient $handler
    $script:HttpClient.Timeout = [TimeSpan]::FromSeconds(30)
    $script:HttpClient.DefaultRequestHeaders.ExpectContinue = $false # .NET Framework would otherwise wait for a 100 Continue
  }
  return $script:HttpClient
}

function Get-CixCosmosItems {
  <# Runs a simple query and returns every row (following continuations). Cross-partition unless -PartitionKey is given. #>
  param(
    [Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][string]$Key, [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][ValidateScript({ $_ -in $script:CixContainers })][string]$Container,
    [Parameter(Mandatory)][string]$Query
  )
  $link = "dbs/$Database/colls/$Container"
  $items = [System.Collections.Generic.List[object]]::new()
  $continuation = $null
  do {
    $headers = @{ 'x-ms-documentdb-isquery' = 'True'; 'x-ms-max-item-count' = '1000' }
    if ($continuation) { $headers['x-ms-continuation'] = $continuation }
    $r = Invoke-CixCosmosRequest -Endpoint $Endpoint -Key $Key -Method POST -ResourceType 'docs' -ResourceLink $link -Path "$link/docs" `
      -Body (@{ query = $Query; parameters = @() } | ConvertTo-Json -Compress) -Headers $headers -CrossPartition
    if ($r.Status -ne 200) { throw "Listing $Container failed (HTTP $($r.Status))." }
    foreach ($doc in @(($r.Body | ConvertFrom-Json).Documents)) { $items.Add($doc) }
    $continuation = $r.Continuation
  } while ($continuation)
  return , $items.ToArray()
}

function Remove-CixCosmosItem {
  <# Deletes one document. Returns $true if it was deleted or was already gone. #>
  param(
    [Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][string]$Key, [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][ValidateScript({ $_ -in $script:CixContainers })][string]$Container,
    [Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$PartitionKey
  )
  $link = "dbs/$Database/colls/$Container/docs/$Id"
  $path = "dbs/$Database/colls/$Container/docs/$([Uri]::EscapeDataString($Id))"
  $r = Invoke-CixCosmosRequest -Endpoint $Endpoint -Key $Key -Method DELETE -ResourceType 'docs' -ResourceLink $link -Path $path -PartitionKey $PartitionKey
  return ($r.Status -eq 204 -or $r.Status -eq 404)
}

function Get-CixCosmosItem {
  <# Reads one document, or $null if it doesn't exist. #>
  param(
    [Parameter(Mandatory)][string]$Endpoint, [Parameter(Mandatory)][string]$Key, [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][ValidateScript({ $_ -in $script:CixContainers })][string]$Container,
    [Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$PartitionKey
  )
  $link = "dbs/$Database/colls/$Container/docs/$Id"
  $path = "dbs/$Database/colls/$Container/docs/$([Uri]::EscapeDataString($Id))"
  $r = Invoke-CixCosmosRequest -Endpoint $Endpoint -Key $Key -Method GET -ResourceType 'docs' -ResourceLink $link -Path $path -PartitionKey $PartitionKey
  if ($r.Status -eq 404) { return $null }
  if ($r.Status -ne 200) { throw "Reading $Container/$Id failed (HTTP $($r.Status))." }
  return $r.Body | ConvertFrom-Json
}

Export-ModuleMember -Function *-Cix*
