#Requires -Version 7.2
<#
  Shared helpers for tools/azure/provision.ps1 and tools/azure/reset-database.ps1.
  - Settings come from infra/environments/<name>.json; nothing environment-specific is hard-coded here.
  - Azure is reached through the Azure CLI (az), signed in as you. Keys are read into memory when needed and never printed.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$script:CixContainers = @('journal', 'state')

function Get-CixRepoRoot { $script:RepoRoot }

function Write-CixStep([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-CixInfo([string]$Text) { Write-Host "    $Text" }
function Write-CixWarn([string]$Text) { Write-Host "    ! $Text" -ForegroundColor Yellow }

function Read-CixEnvironment {
  <# Loads and checks infra/environments/<Name>.json. #>
  param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Name)
  $path = Join-Path $script:RepoRoot 'infra' 'environments' "$Name.json"
  if (-not (Test-Path $path)) {
    throw "No settings file for environment '$Name' ($path). Copy one of the files in infra/environments and edit it."
  }
  $s = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
  foreach ($key in 'environmentName', 'protected', 'resourceGroup', 'location', 'dashboardLocation', 'namePrefix', 'cosmos', 'deployDashboard') {
    if (-not $s.ContainsKey($key)) { throw "$path is missing '$key'." }
  }
  if ($s.environmentName -ne $Name) { throw "$path says environmentName '$($s.environmentName)', not '$Name'." }
  if ($s.protected -isnot [bool]) { throw "${path}: 'protected' must be true or false." }
  if ($s.resourceGroup -notmatch '^[\w\-\.\(\)]{1,90}$') { throw "${path}: resourceGroup isn't a valid name." }
  if ($s.cosmos.databaseName -notmatch '^[a-zA-Z0-9-]{1,64}$') { throw "${path}: cosmos.databaseName isn't a valid name." }
  if ($s.subscriptionId -and $s.subscriptionId -notmatch '^[0-9a-fA-F-]{36}$') { throw "${path}: subscriptionId must be a GUID or empty." }
  $s['path'] = $path
  return $s
}

function Assert-CixCommand {
  <# Fails with a clear message if a command-line tool is missing. #>
  param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Hint)
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "'$Name' isn't installed or isn't on PATH. $Hint" }
}

function Invoke-CixAz {
  <#
    Runs the Azure CLI and returns its output. On failure throws with az's message. -Quiet hides the command line from the
    error (use it when the output is a secret, so nothing about the call is echoed).
  #>
  param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Quiet)
  $output = & az @Arguments 2>&1
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
  $version = [version]((Invoke-CixAz @('version', '-o', 'json') | ConvertFrom-Json).'azure-cli')
  if ($version -lt [version]'2.60.0') { throw "Azure CLI $version is too old; run 'az upgrade' (2.60 or later is needed)." }
  Write-CixInfo "Azure CLI $version"
  $bicep = & az bicep version 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-CixInfo 'Installing Bicep for the Azure CLI...'
    Invoke-CixAz @('bicep', 'install') | Out-Null
  }
  else { Write-CixInfo (($bicep | Select-Object -First 1) -replace '\s+', ' ') }
}

function Connect-CixAzure {
  <#
    Signs in if needed and selects the subscription: -SubscriptionId, else the settings file, else a choice from your
    subscriptions. Returns the selected account.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings, [string]$SubscriptionId)
  $null = & az account show -o none 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-CixInfo 'Signing in to Azure (a browser window opens)...'
    & az login -o none
    if ($LASTEXITCODE -ne 0) { throw 'Azure sign-in failed.' }
  }

  $target = if ($SubscriptionId) { $SubscriptionId } elseif ($Settings.subscriptionId) { $Settings.subscriptionId } else { $null }
  if (-not $target) {
    $subs = @(Invoke-CixAz @('account', 'list', '--only-show-errors', '-o', 'json') | ConvertFrom-Json | Where-Object { $_.state -eq 'Enabled' })
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
  $account = Invoke-CixAz @('account', 'show', '-o', 'json') | ConvertFrom-Json
  if ($Settings.subscriptionId -and $account.id -ne $Settings.subscriptionId) {
    throw "The settings file pins subscription $($Settings.subscriptionId), but $($account.id) is selected."
  }
  Write-CixInfo "Subscription: $($account.name) ($($account.id))"
  return $account
}

function New-CixDeploymentParameters {
  <# Writes the Bicep parameters for an environment to a temporary file and returns its path. #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $parameters = [ordered]@{
    environmentName    = @{ value = $Settings.environmentName }
    location           = @{ value = $Settings.location }
    dashboardLocation  = @{ value = $Settings.dashboardLocation }
    namePrefix         = @{ value = $Settings.namePrefix }
    enableFreeTier     = @{ value = [bool]$Settings.cosmos.enableFreeTier }
    databaseThroughput = @{ value = [int]$Settings.cosmos.databaseThroughput }
    databaseName       = @{ value = $Settings.cosmos.databaseName }
    allowedIpRanges    = @{ value = @($Settings.cosmos.allowedIpRanges) }
    deployDashboard    = @{ value = [bool]$Settings.deployDashboard }
    protectedEnvironment = @{ value = [bool]$Settings.protected }
    tags               = @{ value = if ($Settings.ContainsKey('tags')) { $Settings.tags } else { @{} } }
  }
  $document = [ordered]@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = $parameters
  }
  $path = Join-Path ([IO.Path]::GetTempPath()) "cix-$($Settings.environmentName)-$([guid]::NewGuid().ToString('N')).parameters.json"
  $document | ConvertTo-Json -Depth 10 | Set-Content -Path $path -Encoding utf8
  return $path
}

function Invoke-CixDeployment {
  <# Deploys infra/main.bicep to the environment's resource group and returns the deployment outputs. -WhatIf previews only. #>
  param([Parameter(Mandatory)][hashtable]$Settings, [switch]$WhatIf)
  $template = Join-Path $script:RepoRoot 'infra' 'main.bicep'
  $parameters = New-CixDeploymentParameters -Settings $Settings
  try {
    if ($WhatIf) {
      & az deployment group what-if --resource-group $Settings.resourceGroup --template-file $template --parameters "@$parameters" --only-show-errors
      if ($LASTEXITCODE -ne 0) { throw 'The what-if preview failed.' }
      return $null
    }
    $name = "cix-$($Settings.environmentName)-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))"
    $result = Invoke-CixAz @('deployment', 'group', 'create', '--resource-group', $Settings.resourceGroup, '--name', $name,
      '--template-file', $template, '--parameters', "@$parameters", '--only-show-errors', '-o', 'json') | ConvertFrom-Json
    return $result.properties.outputs
  }
  finally {
    Remove-Item $parameters -ErrorAction SilentlyContinue
  }
}

function Get-CixCosmosAccount {
  <#
    The environment's Cosmos DB account: the one in its resource group tagged cix-environment=<name>. Refuses anything else,
    so a script can't be pointed at an unrelated account by a wrong name.
  #>
  param([Parameter(Mandatory)][hashtable]$Settings)
  $accounts = @(Invoke-CixAz @('cosmosdb', 'list', '--resource-group', $Settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-Json |
      Where-Object { $_.tags -and $_.tags.'cix-environment' -eq $Settings.environmentName })
  if ($accounts.Count -eq 0) {
    throw "No Cosmos DB account tagged cix-environment=$($Settings.environmentName) in resource group $($Settings.resourceGroup)."
  }
  if ($accounts.Count -gt 1) { throw "More than one Cosmos DB account is tagged cix-environment=$($Settings.environmentName); refusing to guess." }
  return $accounts[0]
}

function Get-CixCosmosKey {
  <# The account's primary key (read-write) or read-only key, as a string held in memory. Never print it. #>
  param([Parameter(Mandatory)][hashtable]$Settings, [Parameter(Mandatory)][string]$AccountName, [switch]$ReadOnly)
  $type = if ($ReadOnly) { 'read-only-keys' } else { 'keys' }
  $field = if ($ReadOnly) { 'primaryReadonlyMasterKey' } else { 'primaryMasterKey' }
  $keys = Invoke-CixAz -Quiet @('cosmosdb', 'keys', 'list', '--resource-group', $Settings.resourceGroup, '--name', $AccountName,
    '--type', $type, '--only-show-errors', '-o', 'json') | ConvertFrom-Json
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
  # Restrict the (empty) file first, then write: the key is never on disk with wider access.
  if (-not (Test-Path $path)) { New-Item -ItemType File -Path $path | Out-Null }
  [IO.File]::WriteAllText($path, '', [Text.UTF8Encoding]::new($false))
  if ($IsWindows) {
    $me = "*$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
    icacls $path /inheritance:r /grant:r "${me}:(R,W)" | Out-Null
  }
  else {
    chmod 600 $path
  }
  if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $path; the key was not written." }
  [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
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
  $base = ([Uri]$Endpoint).GetLeftPart([UriPartial]::Authority)
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
    $request = @{ Uri = "$base/$Path"; Method = $Method; Headers = $h; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($Body) { $request.Body = $Body; $request.ContentType = 'application/query+json' }
    $response = Invoke-WebRequest @request
    $status = [int]$response.StatusCode
    if (($status -eq 429 -or $status -eq 503) -and $attempt -lt 8) {
      $wait = 1000
      if ($response.Headers['x-ms-retry-after-ms']) { $wait = [int]([string]$response.Headers['x-ms-retry-after-ms']) }
      Start-Sleep -Milliseconds ([Math]::Min([Math]::Max($wait, 100) * $attempt, 10000))
      continue
    }
    $continuation = if ($response.Headers['x-ms-continuation']) { [string]$response.Headers['x-ms-continuation'] } else { $null }
    $content = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    return @{ Status = $status; Body = $content; Continuation = $continuation }
  }
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
    foreach ($doc in ($r.Body | ConvertFrom-Json).Documents) { $items.Add($doc) }
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
