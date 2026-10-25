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
$script:CixContainers = @('journal', 'state')
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
  if ($s.shared.resourceGroup -eq $s.resourceGroup) { throw "${path}: the shared Cosmos DB account needs its own resource group, not $($s.resourceGroup)." }
  return $s
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
    is Stop (always when redirected, and in the ISE), and az and npm write warnings there; so the call runs with Continue, and
    callers check $LASTEXITCODE.
  #>
  param([Parameter(Mandatory)][scriptblock]$Command)
  $ErrorActionPreference = 'Continue'
  $global:LASTEXITCODE = -1 # a command that never started (not on PATH, broken shim) counts as failed, not as the last success
  & $Command
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
  param([Parameter(Mandatory)][hashtable]$Settings, [switch]$Shared, [string]$SharedAccountName)
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
        accountName       = [string]$sh.accountName
        enableFreeTier    = [bool]$sh.enableFreeTier
        allowedIpRanges   = @($sh.allowedIpRanges)
        tags              = if ($sh.ContainsKey('tags')) { $sh.tags } else { @{} }
      }
    }
  }
  if (-not $SharedAccountName) { throw 'The shared Cosmos DB account must exist before CIX is deployed.' }
  return @{
    Template = Join-CixPath $script:RepoRoot 'infra' 'main.bicep'
    Location = $Settings.location
    Label    = "cix-$($Settings.environmentName)"
    Values   = [ordered]@{
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
  }
}

function Invoke-CixDeployment {
  <#
    Deploys a subscription-scope template (it creates its own resource group) and returns its outputs. -WhatIf previews
    only. -Shared deploys the shared Cosmos DB account; otherwise CIX's resources, in the shared account -SharedAccountName.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [switch]$Shared, [string]$SharedAccountName, [switch]$WhatIf)
  $d = Get-CixDeploymentValues -Settings $Settings -Shared:$Shared -SharedAccountName $SharedAccountName
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
    the settings give one). Returns $null if there is none yet; refuses to guess between several.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $sh = $Settings.shared
  if ((Invoke-CixAz @('group', 'exists', '--name', $sh.resourceGroup)) -ne 'true') { return $null }
  $accounts = @(Invoke-CixAz @('cosmosdb', 'list', '--resource-group', $sh.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
      Where-Object { $_.tags -and $_.tags.PSObject.Properties['shared-role'] -and $_.tags.'shared-role' -eq 'cosmos' })
  if ($sh.accountName) { $accounts = @($accounts | Where-Object { $_.name -eq $sh.accountName }) }
  if ($accounts.Count -gt 1) { throw "More than one Cosmos DB account in $($sh.resourceGroup) is tagged shared-role=cosmos; set accountName in $($sh.path)." }
  if ($accounts.Count -eq 0) { return $null }
  return $accounts[0]
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

function Assert-CixDatabase {
  <# Fails unless CIX's database exists in the shared account. #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$AccountName)
  Invoke-CixAz @('cosmosdb', 'sql', 'database', 'show', '--account-name', $AccountName, '--resource-group', $Settings.shared.resourceGroup,
    '--name', $Settings.cosmos.databaseName, '--only-show-errors', '-o', 'none') | Out-Null
}

function Get-CixEnvironmentTags {
  <# The tags on CIX's resource group (set by infra/main.bicep): cix-environment and cix-protected. $null if it doesn't exist. #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  if ((Invoke-CixAz @('group', 'exists', '--name', $Settings.resourceGroup)) -ne 'true') { return $null }
  $group = Invoke-CixAz @('group', 'show', '--name', $Settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  return $group.tags
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
