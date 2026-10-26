#Requires -Version 5.1
<#
  Checks the Azure scripts without Azure: they parse, the module works, and provision.ps1 runs its preview path against a
  fake Azure CLI. CI runs this on Windows PowerShell 5.1 (powershell.exe) and on PowerShell 7 (pwsh), so 7-only syntax or
  parameters fail here. No network, no sign-in, no keys (the signing check uses Microsoft's documented sample).

    powershell -NoProfile -ExecutionPolicy Bypass -File tests/azure/CixAzure.Tests.ps1
    pwsh -NoProfile -File tests/azure/CixAzure.Tests.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$azureTools = Join-Path (Join-Path $repo 'tools') 'azure'
$failures = New-Object System.Collections.Generic.List[string]
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Check([string]$Name, [scriptblock]$Test) {
  try { & $Test; Write-Host "ok   $Name" }
  catch {
    $failures.Add($Name)
    Write-Host "FAIL $Name -- $($_.Exception.Message)" -ForegroundColor Red
    # In GitHub Actions, also as an annotation: job logs aren't readable from where agents work, annotations are.
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=CixAzure.Tests ($($PSVersionTable.PSVersion))::$Name -- $(($_.Exception.Message -replace '[\r\n]+', ' ').Substring(0, [Math]::Min(900, ($_.Exception.Message -replace '[\r\n]+', ' ').Length)))" }
  }
}
function Expect($Actual, $Expected, [string]$What) {
  if ($Actual -ne $Expected) { throw "$What`: expected '$Expected', got '$Actual'" }
}

Write-Host "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"

Check 'every script parses on this version' {
  foreach ($f in @(Get-ChildItem $azureTools -Filter *.ps*1) + @(Get-Item (Join-Path (Join-Path $repo 'tools') 'setup-secrets.ps1'))) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors) | Out-Null
    if ($errors) { throw "$($f.Name): $(($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; ')" }
  }
}

Check 'no "$name?" in strings (PowerShell reads the ? as part of the variable name)' {
  foreach ($f in @(Get-ChildItem $azureTools -Filter *.ps*1) + @(Get-Item (Join-Path (Join-Path $repo 'tools') 'setup-secrets.ps1'))) {
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
      $n++
      if ($line -match '\$(\w+:)?\w+\?') { throw "$($f.Name):$n uses `$name? (write `${name}? instead)" }
    }
  }
}

Check 'scripts are plain ASCII (5.1 reads files without a byte-order mark as ANSI)' {
  foreach ($f in Get-ChildItem $azureTools -Filter *.ps*1) {
    foreach ($b in [IO.File]::ReadAllBytes($f.FullName)) { if ($b -gt 127) { throw "$($f.Name) has a non-ASCII byte" } }
  }
}

Import-Module (Join-Path $azureTools 'CixAzure.psm1') -Force

Check 'Cosmos DB signing matches the documented example' {
  $sample = @('dsZQi3KtZmCv1ljt3VNWNm7sQUF1y5rJfC6kv5Jiwv', 'W0EndXdDku/dkKBp8/ufDToSxLzR4y+O/0H/t4bQtVNw==') -join '' # Microsoft's sample, not a key
  $token = New-CixCosmosAuthToken -Verb GET -ResourceType dbs -ResourceLink 'dbs/ToDoList' -Date 'Thu, 27 Apr 2017 00:51:12 GMT' -Key $sample
  Expect ([Uri]::UnescapeDataString($token)) 'type=master&ver=1.0&sig=c09PEVJrgp2uQRkr934kFbTqhByc7TVr3OHyqlu+c+c=' 'token'
}

Check 'settings load as nested hashtables, with the shared account settings, on every version' {
  $s = Read-CixEnvironment -Name dev
  if ($s -isnot [hashtable] -or $s.cosmos -isnot [hashtable] -or $s.tags -isnot [hashtable] -or $s.shared -isnot [hashtable]) { throw 'not hashtables' }
  Expect $s.cosmos.databaseName 'cix' 'databaseName'
  Expect $s.cosmos.databaseThroughput 400 'databaseThroughput'
  Expect ($s.protected -is [bool]) $true 'protected is a bool'
  Expect $s.shared.resourceGroup 'rg-shared' 'shared resource group'
  Expect $s.shared.enableFreeTier $true 'shared free tier'
  Expect (@($s.shared.allowedIpRanges).Count) 0 'allowedIpRanges'
  foreach ($f in @(Get-ChildItem (Join-CixPath $repo 'infra' 'environments') -Filter *.json) + @(Get-ChildItem (Join-CixPath $repo 'infra' 'shared') -Filter *.json)) {
    Get-Content $f.FullName -Raw | ConvertFrom-Json | Out-Null
  }
}

Check 'CI publishes the dashboard with the same pinned Static Web Apps CLI as provision.ps1' {
  $script = [IO.File]::ReadAllText((Join-Path $azureTools 'provision.ps1'))
  $ci = [IO.File]::ReadAllText((Join-CixPath $repo '.github' 'workflows' 'ci.yml'))
  if ($script -notmatch "\`$SwaCliVersion = '([\d.]+)'") { throw 'no $SwaCliVersion in provision.ps1' }
  $version = $Matches[1]
  if ($ci -notmatch 'SWA_CLI_VERSION: "([\d.]+)"') { throw 'no SWA_CLI_VERSION in ci.yml' }
  Expect $Matches[1] $version 'SWA CLI version in CI'
}

Check 'Join-CixPath joins several parts' {
  Expect (Join-CixPath 'a' 'b' 'c') (Join-Path (Join-Path 'a' 'b') 'c') 'path'
}

Check 'deployment parameters: valid JSON, no byte-order mark, arrays stay arrays, every template parameter given' {
  $s = Read-CixEnvironment -Name dev
  $s.shared.allowedIpRanges = @('203.0.113.7')
  foreach ($d in @((Get-CixDeploymentValues -Settings $s -Shared), (Get-CixDeploymentValues -Settings $s -SharedAccountName 'cosmos-shared-test'))) {
    $path = New-CixParametersFile -Values $d.Values -Label $d.Label
    try {
      $bytes = [IO.File]::ReadAllBytes($path)
      if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'has a BOM' }
      $raw = [IO.File]::ReadAllText($path)
      $p = ($raw | ConvertFrom-Json).parameters
      # Every parameter the template declares without a default must be given, and nothing it doesn't declare.
      $template = [IO.File]::ReadAllText($d.Template)
      $declared = @([regex]::Matches($template, '(?m)^param (\w+) ') | ForEach-Object { $_.Groups[1].Value })
      $required = @([regex]::Matches($template, '(?m)^param (\w+) [\w\[\]]+\s*$') | ForEach-Object { $_.Groups[1].Value })
      foreach ($name in $p.PSObject.Properties.Name) { if ($declared -notcontains $name) { throw "$($d.Label): $name isn't a template parameter" } }
      foreach ($name in $required) { if (-not $p.PSObject.Properties[$name]) { throw "$($d.Label): $name is required but not given" } }
      if ($d.Label -eq 'shared') {
        if ($raw -notmatch '"allowedIpRanges":\s*\{\s*"value":\s*\[\s*"203\.0\.113\.7"\s*\]') { throw 'allowedIpRanges is not a one-item array' }
        Expect $p.resourceGroupName.value 'rg-shared' 'shared resource group'
        Expect $p.enableFreeTier.value $true 'enableFreeTier'
      }
      else {
        Expect $p.resourceGroupName.value 'rg-cix-dev' 'resource group'
        Expect $p.sharedResourceGroupName.value 'rg-shared' 'shared resource group'
        Expect $p.sharedAccountName.value 'cosmos-shared-test' 'shared account'
        Expect $p.databaseThroughput.value 400 'databaseThroughput'
        Expect $p.tags.value.purpose 'cix-paper-trading' 'tags'
      }
    }
    finally { Remove-Item $path -ErrorAction SilentlyContinue }
  }
}

$ownerId = '11111111-2222-3333-4444-555555555555'

# The agents' templates deploy a paper engine only (the live engine gets its own template, outside CI). The live-mode hook
# doesn't read Bicep, so this holds the line: one way to set the mode, to Paper, and never Production or exchange keys.
Check 'the Azure templates only ever run a paper engine' {
  $files = @(Get-ChildItem (Join-CixPath $repo 'infra') -Recurse -Filter '*.bicep')
  if ($files.Count -lt 3) { throw "found only $($files.Count) Bicep files" }
  $modes = @()
  foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($line in ($text -split "`n")) {
      if ($line -match '(?i)trading(__|:)mode') { $modes += $line.Trim() }
      if ($line -match '(?i)ASPNETCORE_ENVIRONMENT' -and $line -notmatch "value: 'Azure'") { throw "$($file.Name): $($line.Trim())" }
      if ($line -match '(?i)revolut|\blive\b' -and $line -notmatch '^\s*//') { throw "$($file.Name) mentions the exchange or live trading outside a comment: $($line.Trim())" }
    }
  }
  if ($modes.Count -ne 1 -or $modes[0] -ne "{ name: 'Trading__Mode', value: 'Paper' }") { throw "trading mode settings: $($modes -join ' | ')" }
}

Check 'the agents'' templates set what the engine and the job scripts read' {
  $apps = [IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'modules' 'agents-apps.bicep'))
  $jobCount = ([regex]::Matches([IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'modules' 'agents-names.bicep')), "\{ key: '")).Count
  $allowed = ([regex]::Matches($apps, "name: 'ToolServer__Entra__Agents__")).Count
  if ($jobCount -lt 1 -or $allowed -ne $jobCount) { throw "$jobCount jobs but $allowed allowlisted identities at the engine" }
  $scripts = (Get-ChildItem (Join-CixPath $repo 'tools' 'trader') -Filter '*.sh' | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
  foreach ($m in [regex]::Matches($apps, "name: '((CIX|CLAUDE|TRADER|ANALYST)_[A-Z_]+)'")) {
    $name = $m.Groups[1].Value
    if (-not $scripts.Contains($name)) { throw "agents-apps.bicep sets $name, which no script in tools/trader reads" }
  }
}

Check 'agents settings: dev keeps them off, dates stay text, and they reach the template as its parameters' {
  $s = Read-CixEnvironment -Name dev
  Expect $s.agents.deploy 'none' 'agents.deploy in dev.json (on only when the owner says so)'
  Expect ($s.agents.budgetStartDate -is [string]) $true 'budgetStartDate is text'
  Expect $s.agents.budgetStartDate '2026-11-01' 'budgetStartDate'
  $s.agents.deploy = 'platform'
  $d = Get-CixDeploymentValues -Settings $s -SharedAccountName 'cosmos-shared-test' -Agents @{ budgetContactEmail = 'owner@example.com'; ownerObjectId = $ownerId }
  Expect $d.Values.agents 'platform' 'agents'
  Expect $d.Values.agentsLocation 'polandcentral' 'agentsLocation'
  Expect $d.Values.startEngine $false 'startEngine'
  Expect $d.Values.monthlyBudget 40 'monthlyBudget'
  Expect $d.Values.budgetContactEmail 'owner@example.com' 'budgetContactEmail'
  Expect $d.Values.ownerObjectId $ownerId 'ownerObjectId'
  if ($d.Values.Contains('imageTag')) { throw 'imageTag given without a value' }
  $template = [IO.File]::ReadAllText($d.Template)
  foreach ($name in $d.Values.Keys) { if ($template -notmatch "(?m)^param $name ") { throw "$name isn't a template parameter" } }
}

foreach ($case in @(
    @{ Change = { param($a) $a.deploy = 'all' }; Says = 'agents.deploy must be none, platform or apps' },
    @{ Change = { param($a) $a.startEngine = 'yes' }; Says = 'agents.startEngine must be true or false' },
    @{ Change = { param($a) $a.traderModel = 'Sonnet; rm' }; Says = 'agents.traderModel must be a model alias' },
    @{ Change = { param($a) $a.budgetStartDate = '2026-11-15' }; Says = 'first of a month' },
    @{ Change = { param($a) $a.Remove('location') }; Says = "agents is missing 'location'" })) {
  Check "agents settings refused: $($case.Says)" {
    $s = Read-CixEnvironment -Name dev
    & $case.Change $s.agents
    $message = $null
    try { Assert-CixAgentSettings -Settings $s -Path 'dev.json' } catch { $message = $_.Exception.Message }
    if (-not $message -or -not $message.Contains($case.Says)) { throw "got: $message" }
  }
}

Check 'agents stay off in a protected (live-money) environment' {
  $s = Read-CixEnvironment -Name dev
  $s.protected = $true
  $s.agents.deploy = 'platform'
  $message = $null
  try { Assert-CixAgentSettings -Settings $s -Path 'prod.json' } catch { $message = $_.Exception.Message }
  if (-not $message -or -not $message.Contains('must stay none in a protected')) { throw "got: $message" }
}

Check 'ConvertFrom-CixJson sends array items one by one' {
  Expect (@('[{"a":1},{"a":2},{"a":3}]' | ConvertFrom-CixJson | Where-Object { $_.a -ge 2 }).Count) 2 'filtered items'
  Expect (@('[]' | ConvertFrom-CixJson).Count) 0 'empty array'
  Expect ('{"a":1}' | ConvertFrom-CixJson).a 1 'object'
}

Check 'secret files: exact content, no byte-order mark, readable only by you' {
  $path = Write-CixSecretFile -Name 'cix_selftest' -Value 'not-a-secret'
  try {
    Expect ([IO.File]::ReadAllText($path)) 'not-a-secret' 'content'
    Expect ([IO.File]::ReadAllBytes($path).Length) 12 'length'
    if ($onWindows) {
      $rules = @((Get-Acl $path).Access)
      $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
      $others = @($rules | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]) -ne $me })
      Expect $others.Count 0 'access rules for anyone else'
    }
    else {
      $mode = (& stat -c '%a' $path 2>$null)
      if ($LASTEXITCODE -ne 0) { $mode = (& stat -f '%Lp' $path) } # macOS
      Expect $mode '600' 'file mode'
    }
  }
  finally { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}

# --- Fakes: a loopback Cosmos DB and an Azure CLI that writes a warning to stderr on every call, as the real one often does ---
$sample = @('dsZQi3KtZmCv1ljt3VNWNm7sQUF1y5rJfC6kv5Jiwv', 'W0EndXdDku/dkKBp8/ufDToSxLzR4y+O/0H/t4bQtVNw==') -join '' # Microsoft's sample, not a key

function Start-FakeCosmos([string[]]$Replies) {
  <# Answers one connection per reply, in order, and records each request (head and body). #>
  $listener = New-Object System.Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
  $listener.Start()
  $server = [PowerShell]::Create()
  [void]$server.AddScript({
      param($listener, $replies)
      $seen = New-Object System.Collections.Generic.List[string]
      foreach ($reply in $replies) {
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader $stream, ([Text.Encoding]::ASCII)
        $head = New-Object System.Collections.Generic.List[string]
        while ($true) { $line = $reader.ReadLine(); if ([string]::IsNullOrEmpty($line)) { break }; $head.Add($line) }
        $length = 0
        foreach ($l in $head) { if ($l -match '^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] } }
        $buffer = New-Object char[] $length
        $read = 0
        while ($read -lt $length) { $read += $reader.Read($buffer, $read, $length - $read) }
        $seen.Add(($head -join "`n") + "`nBODY:" + (-join $buffer))
        $bytes = [Text.Encoding]::ASCII.GetBytes($reply)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
        $client.Close()
      }
      , $seen.ToArray()
    }).AddArgument($listener).AddArgument($Replies)
  return [pscustomobject]@{ Listener = $listener; Server = $server; Async = $server.BeginInvoke(); Port = $listener.LocalEndpoint.Port }
}

function Stop-FakeCosmos($Fake) {
  <# The requests it received, in order. #>
  try {
    if (-not $Fake.Async.AsyncWaitHandle.WaitOne(20000)) { throw 'the fake Cosmos DB did not get every request it expected' }
    return @($Fake.Server.EndInvoke($Fake.Async) | ForEach-Object { $_ })
  }
  finally { $Fake.Listener.Stop(); $Fake.Server.Dispose() }
}

function Reply([int]$Status, [string]$Body = '', [string]$Extra = '') {
  $reason = @{ 200 = 'OK'; 404 = 'Not Found'; 429 = 'Too Many Requests' }[$Status]
  return "HTTP/1.1 $Status $reason`r`nContent-Type: application/json`r`n$($Extra)Content-Length: $($Body.Length)`r`nConnection: close`r`n`r`n$Body"
}

function New-FakeAz([string]$CosmosAccounts = '[]', [string]$GroupExists = 'false', [string]$GroupTags = '{}',
  [string]$GroupList = '[]', [bool]$DatabaseExists = $true, [hashtable]$Extra = @{}) {
  <# A directory holding a fake az (az.cmd on Windows); put it first on PATH. #>
  $dir = Join-Path ([IO.Path]::GetTempPath()) "cix-fake-az-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir | Out-Null
  $answers = [ordered]@{
    'version'        = '{"azure-cli": "2.65.0"}'
    'bicep'          = 'Bicep CLI version 0.30.3'
    'account list'   = '[{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one","state":"Enabled"},{"id":"00000000-0000-0000-0000-000000000002","name":"Disabled one","state":"Disabled"}]'
    'account show'   = '{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one","tenantId":"00000000-0000-0000-0000-0000000000aa"}'
    'group exists'   = $GroupExists
    'group show'     = "{`"name`":`"rg-cix-dev`",`"tags`":$GroupTags}"
    'group list'     = $GroupList
    'cosmosdb list'  = $CosmosAccounts
    # Read-only key only: if a run ever forgot -SkipEngineConfig, reading the read-write key fails before any file is written.
    'cosmosdb keys'  = "{`"primaryReadonlyMasterKey`": `"$sample`"}"
    'deployment sub create' = '{"properties":{"outputs":{"accountName":{"value":"cosmos-shared-x"},"cosmosAccountName":{"value":"cosmos-shared-x"},"cosmosEndpoint":{"value":"https://cosmos-shared-x.documents.azure.com:443/"},"staticWebAppName":{"value":"stapp-cix-dev"},"dashboardUrl":{"value":"https://dash.example"}}}}'
  }
  foreach ($k in $Extra.Keys) { $answers[$k] = $Extra[$k] }
  if ($onWindows) {
    $lines = @('@echo off', 'echo %* >> "%~dp0calls.log"', 'echo WARNING: fake az writes to stderr 1>&2')
    foreach ($k in $answers.Keys) {
      $match = switch (($k -split ' ').Count) { 1 { '"%1"' } 2 { '"%1 %2"' } 3 { '"%1 %2 %3"' } 4 { '"%~1 %~2 %~3 %~4"' } default { '"%~1 %~2 %~3 %~4 %~5"' } } # %~: an argument in quotes (--filter "appId eq ...") can't break the line
      $lines += "if $match==`"$k`" echo $($answers[$k])& exit /b 0"
    }
    if (-not $DatabaseExists) { $lines += 'if "%1 %2"=="cosmosdb sql" echo ERROR: (NotFound) Resource not found 1>&2& exit /b 3' }
    $lines += @('if "%1"=="fail" echo boom 1>&2& exit /b 3', 'exit /b 0')
    [IO.File]::WriteAllText((Join-Path $dir 'az.cmd'), ($lines -join "`r`n"), [Text.Encoding]::ASCII)
  }
  else {
    $lines = @('#!/bin/sh', 'echo "$*" >> "$(dirname "$0")/calls.log"', 'echo "WARNING: fake az writes to stderr" >&2', 'case "$1 $2 $3 $4 $5 " in')
    foreach ($k in $answers.Keys) {
      $pattern = "'$k '*"
      $lines += "  $pattern) echo '$($answers[$k])' ;;"
    }
    if (-not $DatabaseExists) { $lines += "  'cosmosdb sql '*) echo 'ERROR: (NotFound) Resource not found' >&2; exit 3 ;;" }
    $lines += @("  'fail '*) echo boom >&2; exit 3 ;;", 'esac')
    $script = Join-Path $dir 'az'
    [IO.File]::WriteAllText($script, (($lines -join "`n") + "`n"))
    chmod +x $script
  }
  return $dir
}

function Assert-FakeAzFirst([string]$Dir) {
  <# Never let a test reach the real Azure CLI (some runs deploy with -Yes, others read keys). #>
  $resolved = (Get-Command az -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
  if (-not $resolved -or (Split-Path -Parent $resolved) -ne $Dir) { throw "the fake az isn't first on PATH (got '$resolved')" }
}

function Invoke-WithFakeAz([hashtable]$Fake, [scriptblock]$Script) {
  <# Runs $Script with a fake az first on PATH; returns what it wrote (all streams) and the error, if any. #>
  $dir = New-FakeAz @Fake
  $env:PATH = "$dir$([IO.Path]::PathSeparator)$savedPath"
  $result = @{ Output = ''; Error = $null; Calls = '' }
  try {
    Assert-FakeAzFirst $dir
    $result.Output = & $Script *>&1 | Out-String
  }
  catch { $result.Error = $_.Exception.Message }
  finally {
    $env:PATH = $savedPath
    $log = Join-Path $dir 'calls.log'
    if (Test-Path $log) { $result.Calls = [IO.File]::ReadAllText($log) }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  }
  return $result
}

$savedPath = $env:PATH
$fake = New-FakeAz
$env:PATH = "$fake$([IO.Path]::PathSeparator)$savedPath"
try {
  Assert-FakeAzFirst $fake
  Check 'az warnings on stderr are not errors (5.1 would stop on them)' {
    Expect ((Invoke-CixAz @('version', '-o', 'json') | ConvertFrom-CixJson).'azure-cli') '2.65.0' 'version'
    Assert-CixAzureCli 6>$null
  }

  Check 'Invoke-CixNative relaxes stderr handling for script blocks from scripts too, and restores it' {
  # A script block from this script runs in this script's scope (not the module's): what 5.1 does with stderr depends on it.
  $ErrorActionPreference = 'Stop'
  $out = Invoke-CixNative { & az fail now 2>&1 }
  if ($LASTEXITCODE -ne 3) { throw "exit code $LASTEXITCODE" }
  if (-not (($out | ForEach-Object { "$_" }) -join ' ').Contains('boom')) { throw 'stderr not captured' }
  Expect $ErrorActionPreference 'Stop' 'restored preference'
}

Check 'az missing from PATH is a failure, not a stale success' {
    $env:PATH = Join-Path ([IO.Path]::GetTempPath()) 'cix-no-such-dir'
    $global:LASTEXITCODE = 0
    $failed = $false
    try { Invoke-CixAz @('version') | Out-Null } catch { $failed = $true }
    finally { $env:PATH = "$fake$([IO.Path]::PathSeparator)$savedPath" }
    Expect $failed $true 'threw'
  }

  Check 'a failing az call throws with its message' {
    $message = $null
    try { Invoke-CixAz @('fail', 'now') | Out-Null } catch { $message = $_.Exception.Message }
    if ($message -notmatch 'az fail now failed: .*boom') { throw "got: $message" }
  }

  Check 'provision.ps1 -WhatIfOnly with no shared account yet previews it and stops, picking the enabled subscription' {
    $output = & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly 6>&1 | Out-String
    if ($output -notmatch 'Subscription: Enabled one') { throw "no subscription line: $output" }
    if ($output -notmatch "CIX's own preview needs the shared account") { throw "didn't stop after the shared preview: $output" }
  }

  Check 'reset-database.ps1 refuses -ResetSchema on state without -Mode all, before touching Azure' {
    $message = $null
    try { & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target State -ResetSchema 6>$null } catch { $message = $_.Exception.Message }
    if ($message -notmatch 'pass -Mode all') { throw "got: $message" }
  }
}
finally {
  $env:PATH = $savedPath
  Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
}

Check 'a non-https Cosmos DB endpoint is refused before any request' {
  $message = $null
  try { Invoke-CixCosmosRequest -Endpoint 'http://example.invalid/' -Key 'AAAA' -Method GET -ResourceType docs -ResourceLink 'x' -Path 'x' | Out-Null }
  catch { $message = $_.Exception.Message }
  Expect $message 'The Cosmos DB endpoint must use https.' 'message'
}

Check 'ConvertFrom-CixJson emits nothing for empty output' {
  Expect (@('' | ConvertFrom-CixJson).Count) 0 'items'
}

Check 'Cosmos DB requests: headers, retry on 429, continuation, 404' {
  $body = '{"Documents":[{"id":"e1","day":"2026-10-25"}],"_count":1}'
  $cosmos = Start-FakeCosmos @((Reply 429 '' "x-ms-retry-after-ms: 5`r`n"), (Reply 200 $body "x-ms-continuation: next-page`r`n"), (Reply 404))
  $seen = @()
  try {
    $r = Invoke-CixCosmosRequest -Endpoint "http://127.0.0.1:$($cosmos.Port)/" -Key $sample -Method POST -ResourceType docs `
      -ResourceLink 'dbs/cix/colls/journal' -Path 'dbs/cix/colls/journal/docs' -Body '{"query":"SELECT c.id FROM c","parameters":[]}' `
      -Headers @{ 'x-ms-documentdb-isquery' = 'True' } -CrossPartition
    Expect $r.Status 200 'status after one 429'
    Expect $r.Continuation 'next-page' 'continuation'
    Expect (($r.Body | ConvertFrom-Json).Documents[0].id) 'e1' 'body'
    $missing = Get-CixCosmosItem -Endpoint "http://127.0.0.1:$($cosmos.Port)/" -Key $sample -Database cix -Container state -Id 'engine-status-paper' -PartitionKey 'engine-status-paper'
    if ($null -ne $missing) { throw '404 should be $null' }
  }
  finally { $seen = Stop-FakeCosmos $cosmos }
  Expect $seen.Count 3 'requests'
  $query = $seen[1]
  if ($query -notmatch '(?im)^POST /dbs/cix/colls/journal/docs HTTP/1\.1$') { throw "request line: $query" }
  if ($query -notmatch '(?im)^authorization: type%3Dmaster%26ver%3D1\.0%26sig%3D') { throw 'authorization header' }
  if ($query -notmatch '(?im)^content-type: application/query\+json$') { throw 'content type' }
  if ($query -notmatch '(?im)^x-ms-documentdb-query-enablecrosspartition: True$') { throw 'cross-partition header' }
  if ($query -match '(?im)^expect:') { throw 'sent Expect: 100-continue' }
  if ($query -notmatch 'BODY:\{"query":"SELECT c\.id FROM c","parameters":\[\]\}') { throw 'body' }
  if ($seen[2] -notmatch '(?im)^GET /dbs/cix/colls/state/docs/engine-status-paper HTTP/1\.1$') { throw "read line: $($seen[2])" }
  if ($seen[2] -notmatch '(?im)^x-ms-documentdb-partitionkey: \["engine-status-paper"\]$') { throw 'partition key header' }
}

$devTags = '{"cix-environment":"dev","cix-protected":"false","cix-database":"cix","cix-cosmos-account":"cosmos-shared-x"}'
$ownClaim = "[{`"name`":`"rg-cix-dev`",`"tags`":$devTags}]"
$sharedAccount = '[{"name":"cosmos-shared-x","documentEndpoint":"https://cosmos-shared-x.documents.azure.com:443/","tags":{"shared-role":"cosmos"}}]'

Check 'reset-database.ps1 dry run end to end: finds the tagged account, reads the heartbeat, plans, deletes nothing' {
  $heartbeat = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('o')
  $state = '{"Documents":[{"id":"engine-state-paper","type":"engine-state"},{"id":"spec-good@v1-abc","type":"strategy-spec"}],"_count":2}'
  $journal = '{"Documents":[{"id":"j1","day":"2026-10-24"},{"id":"j2","day":"2026-10-24"},{"id":"j3","day":"2026-10-24"}],"_count":3}'
  $cosmos = Start-FakeCosmos @(
    (Reply 200 "{`"id`":`"engine-status-paper`",`"updatedAt`":`"$heartbeat`"}"),
    (Reply 404),
    (Reply 200 $state),
    (Reply 200 $journal))
  # An untagged account in the shared group must not trip StrictMode; only the one tagged shared-role=cosmos is used.
  $accounts = "[{`"name`":`"other`",`"tags`":{}},{`"name`":`"cosmos-shared-x`",`"documentEndpoint`":`"http://127.0.0.1:$($cosmos.Port)/`",`"tags`":{`"shared-role`":`"cosmos`"}}]"
  $dir = New-FakeAz -CosmosAccounts $accounts -GroupExists 'true' -GroupTags $devTags -GroupList $ownClaim
  $env:PATH = "$dir$([IO.Path]::PathSeparator)$savedPath"
  $seen = @()
  try { Assert-FakeAzFirst $dir; $output = & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target All 6>&1 | Out-String }
  finally {
    $env:PATH = $savedPath
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    $seen = Stop-FakeCosmos $cosmos
  }
  foreach ($expected in @('Account:  cosmos-shared-x (shared) in rg-shared', 'paper: last heartbeat 10 minutes ago', 'live: no status record',
      'would remove state/engine-state-paper', 'keeps state/spec-good@v1-abc', 'from 1 day(s): 2026-10-24 to 2026-10-24', 'Dry run: nothing was changed')) {
    if (-not $output.Contains($expected)) { throw "missing '$expected' in: $output" }
  }
  if (@($seen | Where-Object { $_ -match '^DELETE ' }).Count -ne 0) { throw 'a dry run sent a DELETE' }
}

$prodClaim = '{"name":"rg-cix-prod","tags":{"cix-environment":"prod","cix-protected":"true","cix-database":"cix","cix-cosmos-account":"cosmos-shared-x"}}'
foreach ($case in @(
    @{ Fake = @{ GroupExists = 'true'; GroupTags = '{"cix-environment":"dev","cix-protected":"true"}' }; Says = "isn't tagged cix-protected=false (it says 'true')" },
    @{ Fake = @{ GroupExists = 'true'; GroupTags = '{"cix-protected":"false"}' }; Says = "isn't tagged cix-environment=dev (it says 'nothing')" },
    @{ Fake = @{ GroupExists = 'true'; GroupTags = '{"cix-environment":"dev","cix-protected":"false"}'; CosmosAccounts = $sharedAccount; GroupList = '[]' }; Says = "isn't tagged cix-database=cix and cix-cosmos-account=cosmos-shared-x" },
    @{ Fake = @{ GroupExists = 'true'; GroupTags = $devTags; CosmosAccounts = $sharedAccount; GroupList = "[$($ownClaim.Trim('[', ']')),$prodClaim]" }; Says = "is claimed by resource group(s) rg-cix-prod" })) {
  Check "reset-database.ps1 refuses: $($case.Says)" {
    $r = Invoke-WithFakeAz $case.Fake { & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target All }
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
  }
}

Check 'provision.ps1 rerun: uses the existing shared account untouched and previews CIX (new database)' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $false } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly
  }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Using cosmos-shared-x; left unchanged', "==> CIX (rg-cix-dev, database 'cix')", 'Preview only: nothing was changed.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

Check 'provision.ps1 full run (-Yes) with an existing shared account: deploys CIX and reports the dashboard' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $false } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -Yes -SkipEngineConfig
  }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Using cosmos-shared-x; left unchanged', 'Cosmos DB account:  cosmos-shared-x (shared, in rg-shared)', 'Dashboard:          https://dash.example')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

Check 'provision.ps1 full run (-Yes) with no shared account: creates it, then CIX' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'false'; GroupList = '[]'; DatabaseExists = $false } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -Yes -SkipEngineConfig
  }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Shared account: cosmos-shared-x', "==> CIX (rg-cix-dev, database 'cix')", 'Dashboard:          https://dash.example')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

$site = '{"id":"/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft.Web/staticSites/stapp-cix-dev","name":"stapp-cix-dev"}'
$cleanApp = @{
  'staticwebapp show'           = $site
  'ad app list'                 = '[{"appId":"11111111-1111-1111-1111-111111111111","displayName":"cix-dashboard-deploy-dev"},{"appId":"33333333-3333-3333-3333-333333333333","displayName":"cix-dashboard-deploy-devtest"}]'
  'ad app show'                 = '{"appId":"11111111-1111-1111-1111-111111111111","passwordCredentials":[],"keyCredentials":[]}'
  'ad signed-in-user'           = '{"id":"me"}'
  'ad app owner'                = '[{"id":"me"}]'
  'ad sp list'                  = '[{"id":"22222222-2222-2222-2222-222222222222"}]'
  'ad sp show'                  = '{"id":"22222222-2222-2222-2222-222222222222","passwordCredentials":[],"keyCredentials":[]}'
  'ad sp owner'                 = '[]'
  'rest'                        = '{"value":[]}'
  'ad app federated-credential' = '[]'
  'role definition'             = '[]'
  'role assignment'             = '[]'
}
function Invoke-SetupCi([hashtable]$Overrides) {
  $extra = @{}; foreach ($k in $cleanApp.Keys) { $extra[$k] = $cleanApp[$k] }
  foreach ($k in $Overrides.Keys) { $extra[$k] = $Overrides[$k] }
  return Invoke-WithFakeAz @{ Extra = $extra } { & (Join-Path $azureTools 'setup-ci-deploy.ps1') -Environment dev -Repository 'owner/repo' -Yes }
}

Check 'setup-ci-deploy.ps1 (-Yes) creates the identity, a main-only credential and the role, then prints the variables' {
  $r = Invoke-SetupCi @{ 'ad app list' = '[]'; 'ad app create' = '{"appId":"11111111-1111-1111-1111-111111111111"}'; 'ad sp list' = '[]'; 'ad sp create' = '{"id":"22222222-2222-2222-2222-222222222222"}' }
  if ($r.Error) { throw $r.Error }
  $creates = @($r.Calls -split "`n" | Where-Object { $_ -match '\bcreate\b' })
  if ($creates.Count -eq 0 -or $creates[-1] -notmatch 'federated-credential create') { throw "the trust for main must be created last; creates were: $($creates -join '; ')" }
  foreach ($expected in @('trusted only for repo:owner/repo:ref:refs/heads/main', 'Trusts repo:owner/repo:ref:refs/heads/main.', 'Assigned CIX dashboard deployer (dev) on stapp-cix-dev.',
      'AZURE_CLIENT_ID = 11111111-1111-1111-1111-111111111111', 'AZURE_TENANT_ID = 00000000-0000-0000-0000-0000000000aa', 'DASHBOARD_ENVIRONMENT = dev')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

$mainCredential = '[{"subject":"repo:owner/repo:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]'

Check 'setup-ci-deploy.ps1 accepts a reused identity that is exactly as it should be, changing nothing' {
  $r = Invoke-SetupCi @{
    'ad app federated-credential' = $mainCredential
    'role definition'             = '[{"permissions":[{"actions":["Microsoft.Web/staticSites/read","Microsoft.Web/staticSites/listSecrets/action"],"notActions":[],"dataActions":[],"notDataActions":[]}],"assignableScopes":["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-dev"]}]'
    'role assignment'             = '[{"roleDefinitionName":"CIX dashboard deployer (dev)","scope":"/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft.Web/staticSites/stapp-cix-dev"}]'
  }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Already trusts repo:owner/repo:ref:refs/heads/main.', 'Exists, with exactly the expected actions.', 'Already assigned CIX dashboard deployer (dev) on stapp-cix-dev.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}
foreach ($case in @(
    @{ Name = 'a client secret'; Overrides = @{ 'ad app show' = '{"appId":"1","passwordCredentials":[{"keyId":"k"}],"keyCredentials":[]}' }; Says = 'passwordCredentials' },
    @{ Name = 'another owner'; Overrides = @{ 'ad app owner' = '[{"id":"me"},{"id":"someone-else"}]' }; Says = 'other owners (someone-else)' },
    @{ Name = 'a pull-request credential'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:owner/repo:pull_request","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]' }; Says = 'also trusts repo:owner/repo:pull_request' },
    @{ Name = 'a main credential from another issuer'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:owner/repo:ref:refs/heads/main","issuer":"https://evil.example","audiences":["api://AzureADTokenExchange"]}]' }; Says = 'issuer https://evil.example' },
    @{ Name = 'a broader role of the same name'; Overrides = @{ 'ad app federated-credential' = $mainCredential; 'role definition' = '[{"permissions":[{"actions":["Microsoft.Web/*"]}],"assignableScopes":["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-dev"]}]' }; Says = 'grants other rights than it should' },
    @{ Name = 'a same-named role with data actions'; Overrides = @{ 'ad app federated-credential' = $mainCredential; 'role definition' = '[{"permissions":[{"actions":["Microsoft.Web/staticSites/read","Microsoft.Web/staticSites/listSecrets/action"],"notActions":[],"dataActions":["Microsoft.DocumentDB/*"]}],"assignableScopes":["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-dev"]}]' }; Says = 'grants other rights than it should' },
    @{ Name = 'another role assignment, before trusting main'; Overrides = @{ 'role assignment' = '[{"roleDefinitionName":"Contributor","scope":"/subscriptions/x"}]' }; Says = 'also holds Contributor on /subscriptions/x' },
    @{ Name = 'a service principal secret'; Overrides = @{ 'ad sp show' = '{"id":"2","passwordCredentials":[{"keyId":"k"}],"keyCredentials":[]}' }; Says = 'service principal of cix-dashboard-deploy-dev has passwordCredentials' },
    @{ Name = 'a service principal owner'; Overrides = @{ 'ad sp owner' = '[{"id":"someone-else"}]' }; Says = 'service principal of cix-dashboard-deploy-dev has other owners' },
    @{ Name = 'API permissions or directory roles'; Overrides = @{ 'rest' = '{"value":[{"id":"x"}]}' }; Says = 'has appRoleAssignments' })) {
  Check "setup-ci-deploy.ps1 refuses a reused identity with $($case.Name), changing nothing" {
    $r = Invoke-SetupCi $case.Overrides
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    if ($r.Calls -match '(?m)\bcreate\b') { throw "it changed something before refusing: $(($r.Calls -split "`n" | Where-Object { $_ -match 'create' }) -join '; ')" }
  }
}

# setup-ci-deploy.ps1 -For agents: the identity CI deploys the engine and the jobs with, on an environment with the platform.
$agentsRegistryId = '/subscriptions/x/resourceGroups/rg-cix-citest/providers/Microsoft.ContainerRegistry/registries/crcixcitest1'
$agentsGroupId = '/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-citest'
$agentsTargets = @('Microsoft.App/containerApps/ca-cix-citest-engine', 'Microsoft.App/jobs/caj-cix-citest-trader', 'Microsoft.App/jobs/caj-cix-citest-requests',
  'Microsoft.App/jobs/caj-cix-citest-analyst') | ForEach-Object { "$agentsGroupId/providers/$_" }
$agentsOthers = @('Microsoft.App/managedEnvironments/cae-cix-citest', 'Microsoft.ManagedIdentity/userAssignedIdentities/id-cix-citest-engine',
  'Microsoft.KeyVault/vaults/kvcixcitest1') | ForEach-Object { "$agentsGroupId/providers/$_" }
$agentsResources = '[' + ((@($agentsTargets) + @($agentsOthers) | ForEach-Object { "{`"id`":`"$_`"}" }) -join ',') + ']'
$agentsActions = '["Microsoft.App/containerApps/read","Microsoft.App/containerApps/revisions/read","Microsoft.App/containerApps/write","Microsoft.App/jobs/read","Microsoft.App/jobs/write","Microsoft.ContainerRegistry/registries/read"]'
function Invoke-SetupAgentsCi([hashtable]$Overrides, [string]$Deploy = 'platform', [bool]$Protected = $false) {
  $file = Join-CixPath $repo 'infra' 'environments' 'citest.json'
  $json = (Get-Content (Join-CixPath $repo 'infra' 'environments' 'dev.json') -Raw).Replace('"dev"', '"citest"').Replace('rg-cix-dev', 'rg-cix-citest').Replace('"databaseName": "cix"', '"databaseName": "cix-citest"').Replace('"deploy": "none"', "`"deploy`": `"$Deploy`"")
  if ($Protected) { $json = $json.Replace('"protected": false', '"protected": true') }
  [IO.File]::WriteAllText($file, $json)
  $extra = @{}; foreach ($k in $cleanApp.Keys) { $extra[$k] = $cleanApp[$k] }
  $extra['ad app list'] = '[]'; $extra['ad app create'] = '{"appId":"66666666-6666-6666-6666-666666666666"}'
  $extra['ad sp list'] = '[]'; $extra['ad sp create'] = '{"id":"77777777-7777-7777-7777-777777777777"}'
  $extra['acr list'] = '[{"name":"crcixcitest1","tags":{"cix-environment":"citest"}}]'
  $extra['acr show'] = $agentsRegistryId
  $extra['resource list'] = $agentsResources
  foreach ($k in $Overrides.Keys) { $extra[$k] = $Overrides[$k] }
  try {
    return Invoke-WithFakeAz @{ GroupExists = 'true'; Extra = $extra } { & (Join-Path $azureTools 'setup-ci-deploy.ps1') -Environment citest -Repository 'owner/repo' -For agents -Yes }
  }
  finally { Remove-Item $file -Force }
}

Check 'setup-ci-deploy.ps1 -For agents (-Yes), platform: its own identity, AcrPush and the deployer role on the registry only, trust last' {
  $r = Invoke-SetupAgentsCi @{}
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  $calls = $r.Calls.Replace('"', '') # Windows logs arguments with spaces in quotes
  $creates = @($calls -split "`n" | Where-Object { $_ -match '\bcreate\b' })
  if ($creates[-1] -notmatch 'federated-credential create') { throw "the trust for main must be created last; creates were: $($creates -join '; ')" }
  foreach ($call in @('ad app create --display-name cix-agents-deploy-citest', 'role definition create',
      "--role AcrPush --scope $agentsRegistryId", "--role CIX agents deployer (citest) --scope $agentsRegistryId")) {
    if (-not $calls.Contains($call)) { throw "no '$call' in: $calls" }
  }
  $assigned = @($calls -split "`n" | Where-Object { $_ -match 'role assignment create' })
  Expect $assigned.Count 2 'role assignments'
  $onGroup = @($assigned | Where-Object { $_ -match "--scope $([regex]::Escape($agentsGroupId))(\s|$)" })
  if ($onGroup.Count -gt 0) { throw "assigned something on the whole group: $($onGroup -join '; ')" }
  foreach ($expected in @('AGENTS_AZURE_CLIENT_ID = 66666666-6666-6666-6666-666666666666', 'AGENTS_REGISTRY = crcixcitest1', 'AGENTS_ENVIRONMENT = citest',
      'Assigned AcrPush on crcixcitest1.', 'run this again to let it update the engine and the jobs')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
  if ($r.Output.Contains('DASHBOARD_ENVIRONMENT') -or $calls.Contains('staticwebapp')) { throw 'set up the dashboard instead' }
}

Check 'setup-ci-deploy.ps1 -For agents (-Yes), apps: the deployer role on the engine and the jobs, never the group' {
  $r = Invoke-SetupAgentsCi @{} 'apps'
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  $calls = $r.Calls.Replace('"', '')
  foreach ($scope in @($agentsTargets) + @($agentsRegistryId)) {
    if (-not $calls.Contains("--role CIX agents deployer (citest) --scope $scope")) { throw "the deployer role isn't on $scope`: $($r.Calls)" }
  }
  $assigned = @($calls -split "`n" | Where-Object { $_ -match 'role assignment create' })
  Expect $assigned.Count 6 'role assignments (AcrPush, and the deployer role on the registry, the engine and the jobs)'
  $beyond = @($assigned | Where-Object { $_ -match "--scope $([regex]::Escape($agentsGroupId))(\s|$)" -or $_ -match 'managedEnvironments|userAssignedIdentities|kvcixcitest1' })
  if ($beyond.Count -gt 0) { throw "assigned something beyond those: $($beyond -join '; ')" }
  if ($r.Output.Contains('run this again')) { throw 'asked to run again although apps is deployed' }
}

Check 'setup-ci-deploy.ps1 -For agents accepts its own earlier setup, changing nothing' {
  $r = Invoke-SetupAgentsCi @{
    'ad app list'                 = '[{"appId":"66666666-6666-6666-6666-666666666666","displayName":"cix-agents-deploy-citest"}]'
    'ad sp list'                  = '[{"id":"77777777-7777-7777-7777-777777777777"}]'
    'ad app federated-credential' = $mainCredential
    'role definition'             = "[{`"permissions`":[{`"actions`":$agentsActions,`"notActions`":[],`"dataActions`":[],`"notDataActions`":[]}],`"assignableScopes`":[`"$agentsGroupId`"]}]"
    'role assignment'             = "[{`"roleDefinitionName`":`"AcrPush`",`"scope`":`"$agentsRegistryId`"},{`"roleDefinitionName`":`"CIX agents deployer (citest)`",`"scope`":`"$agentsRegistryId`"}]"
  }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($expected in @('Already trusts repo:owner/repo:ref:refs/heads/main.', 'Exists, with exactly the expected actions.', 'Already assigned AcrPush on crcixcitest1.', 'Already assigned CIX agents deployer (citest) on crcixcitest1.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
  if ($r.Calls -match '(?m)\bcreate\b') { throw "it changed something: $($r.Calls)" }
}

foreach ($case in @(
    @{ Name = 'agents.deploy none'; Deploy = 'none'; Overrides = @{}; Says = 'run provision.ps1 with agents.deploy platform first' },
    @{ Name = 'a protected environment'; Deploy = 'none'; Protected = $true; Overrides = @{}; Says = 'is protected (live money)' },
    @{ Name = 'apps not deployed yet'; Deploy = 'apps'; Overrides = @{ 'resource list' = '[]' }; Says = "ca-cix-citest-engine isn't in rg-cix-citest" },
    @{ Name = 'an older, broader role of the same name'; Deploy = 'platform'; Overrides = @{ 'role definition' = "[{`"permissions`":[{`"actions`":[`"Microsoft.App/containerApps/listSecrets/action`",`"Microsoft.App/containerApps/write`"],`"notActions`":[],`"dataActions`":[],`"notDataActions`":[]}],`"assignableScopes`":[`"$agentsGroupId`"]}]" }; Says = 'grants other rights than it should' },
    @{ Name = 'no registry yet'; Deploy = 'platform'; Overrides = @{ 'acr list' = '[]' }; Says = 'No container registry for citest' },
    @{ Name = 'a reused identity holding another role'; Deploy = 'platform'; Overrides = @{ 'ad app list' = '[{"appId":"66666666-6666-6666-6666-666666666666","displayName":"cix-agents-deploy-citest"}]'; 'ad sp list' = '[{"id":"77777777-7777-7777-7777-777777777777"}]'; 'role assignment' = '[{"roleDefinitionName":"Owner","scope":"/subscriptions/x"}]' }; Says = 'also holds Owner on /subscriptions/x' })) {
  Check "setup-ci-deploy.ps1 -For agents refuses: $($case.Name)" {
    $r = Invoke-SetupAgentsCi $case.Overrides $case.Deploy ([bool]$case['Protected'])
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    if ($r.Calls -match '(?m)\bcreate\b') { throw 'it changed something before refusing' }
  }
}

# --- setup-dashboard-requests.ps1: the dashboard's create-only writer identity (ADR-0012) -------------------------------------
$writerSite = '{"id":"/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft.Web/staticSites/stapp-cix-dev","name":"stapp-cix-dev","defaultHostname":"dash.example"}'
$writerAccount = '[{"id":"/subscriptions/x/resourceGroups/rg-shared/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-shared-x","name":"cosmos-shared-x","documentEndpoint":"https://cosmos-shared-x.documents.azure.com:443/","tags":{"shared-role":"cosmos"}}]'
$writerScope = '/subscriptions/x/resourceGroups/rg-shared/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-shared-x/dbs/cix/colls/requests'
$writerRole = "[{`"id`":`"/role/r1`",`"name`":`"r1`",`"roleName`":`"CIX dashboard request writer (dev)`",`"assignableScopes`":[`"$writerScope`"],`"permissions`":[{`"dataActions`":[`"Microsoft.DocumentDB/databaseAccounts/readMetadata`",`"Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create`"],`"notDataActions`":[]}]}]"
$fakePassword = @('fake', 'writer', 'pass', '7f3a') -join '-' # built at run time; must never appear in output or on a command line
$newWriter = @{
  'staticwebapp show'                     = $writerSite
  'ad signed-in-user'                     = '{"id":"me"}'
  'ad app list'                           = '[]'
  'ad app create'                         = '{"appId":"44444444-4444-4444-4444-444444444444"}'
  'ad sp create'                          = '{"id":"55555555-5555-5555-5555-555555555555"}'
  'cosmosdb sql role definition list'     = '[]'
  'cosmosdb sql role definition create'   = '{"id":"/role/r1","name":"r1"}'
  'ad app credential'                     = "{`"appId`":`"44444444-4444-4444-4444-444444444444`",`"password`":`"$fakePassword`"}"
  'rest'                                  = '{"value":[],"properties":{"COSMOS_ENDPOINT":"https://cosmos-shared-x.documents.azure.com:443/"}}'
  'staticwebapp users list'               = '[]'
  'staticwebapp users invite'             = 'https://dash.example/.auth/invitations/abc'
}
$existingWriter = @{
  'staticwebapp show'                     = $writerSite
  'ad signed-in-user'                     = '{"id":"me"}'
  'ad app list'                           = '[{"appId":"44444444-4444-4444-4444-444444444444","displayName":"cix-dashboard-writer-dev"}]'
  'ad app show'                           = '{"appId":"44444444-4444-4444-4444-444444444444","passwordCredentials":[{"displayName":"cix-dashboard-requests"}],"keyCredentials":[]}'
  'ad app federated-credential'           = '[]'
  'ad app owner'                          = '[{"id":"me"}]'
  'ad sp list'                            = '[{"id":"55555555-5555-5555-5555-555555555555"}]'
  'ad sp show'                            = '{"id":"55555555-5555-5555-5555-555555555555","passwordCredentials":[],"keyCredentials":[]}'
  'ad sp owner'                           = '[]'
  'role assignment'                       = '[]'
  'cosmosdb sql role definition list'     = $writerRole
  'cosmosdb sql role assignment list'     = "[{`"principalId`":`"55555555-5555-5555-5555-555555555555`",`"roleDefinitionId`":`"/role/r1`",`"scope`":`"$writerScope`"},{`"principalId`":`"someone-else`",`"roleDefinitionId`":`"/role/other`",`"scope`":`"/`"}]"
  'rest'                                  = '{"value":[],"properties":{"COSMOS_WRITER_TENANT_ID":"00000000-0000-0000-0000-0000000000aa","COSMOS_WRITER_CLIENT_ID":"44444444-4444-4444-4444-444444444444","COSMOS_WRITER_SECRET":"set","DASHBOARD_OWNER_USER_ID":"owner-uid"}}'
  'staticwebapp users list'               = '[{"displayName":"owner@example.com","provider":"aad","userId":"owner-uid","roles":"anonymous,authenticated,owner"}]'
}
function Invoke-SetupWriter([hashtable]$Base, [hashtable]$Overrides = @{}, [hashtable]$More = @{}) {
  $extra = @{}; foreach ($k in $Base.Keys) { $extra[$k] = $Base[$k] }
  foreach ($k in $Overrides.Keys) { $extra[$k] = $Overrides[$k] }
  return Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = $writerAccount; Extra = $extra } {
    & (Join-Path $azureTools 'setup-dashboard-requests.ps1') -Environment dev -OwnerAccount 'owner@example.com' -Yes @More
  }
}

Check 'setup-dashboard-requests.ps1 (-Yes) creates a create-only identity and stores its secret without ever showing it' {
  $r = Invoke-SetupWriter $newWriter
  if ($r.Error) { throw "failed: $($r.Error)" }
  foreach ($call in 'ad app create', 'ad sp create', 'cosmosdb sql role definition create', 'cosmosdb sql role assignment create', 'ad app credential reset', 'staticwebapp users invite') {
    if ($r.Calls -notmatch [regex]::Escape($call)) { throw "no '$call' call: $($r.Calls)" }
  }
  if ($r.Calls -notmatch 'cosmosdb sql role assignment create .*--scope /dbs/cix/colls/requests') { throw 'the assignment is not scoped to the requests container' }
  if ($r.Calls -notmatch 'rest --method put --url https://management\.azure\.com/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft\.Web/staticSites/stapp-cix-dev/config/appsettings\S* --body @') {
    throw "the settings weren't written from a file: $($r.Calls)"
  }
  if ($r.Calls -match '--append') { throw 'an earlier secret would stay valid' }
  if ($r.Output.Contains($fakePassword) -or $r.Calls.Contains($fakePassword)) { throw 'the secret was printed or put on a command line' }
  if ($r.Output -notmatch 'auth/invitations/abc') { throw "the invitation link wasn't shown: $($r.Output)" }
  if ($r.Calls -notmatch 'users invite .*--invitation-expiration-in-hours 2 ') { throw 'the invitation should expire in 2 hours' }
  if ($r.Calls -notmatch 'staticwebapp users list .*--authentication-provider all ') { throw 'users are listed for all providers only (the API refuses one provider)' }
  if ($r.Output -notmatch 'run this script again') { throw "it didn't say to run it again to confirm the owner: $($r.Output)" }
}

Check 'setup-dashboard-requests.ps1 confirms the owner by user ID once the invitation is accepted' {
  $r = Invoke-SetupWriter $existingWriter @{ 'rest' = '{"value":[],"properties":{"COSMOS_WRITER_TENANT_ID":"00000000-0000-0000-0000-0000000000aa","COSMOS_WRITER_CLIENT_ID":"44444444-4444-4444-4444-444444444444","COSMOS_WRITER_SECRET":"set"}}' }
  if ($r.Error) { throw "failed: $($r.Error)" }
  if ($r.Calls -notmatch '--method put') { throw "the owner wasn't stored: $($r.Calls)" }
  if ($r.Calls -match '(?m)\bcreate\b|credential reset|users invite') { throw "it changed something else: $($r.Calls)" }
  if ($r.Output -notmatch 'Confirmed') { throw "no confirmation: $($r.Output)" }
}

Check 'setup-dashboard-requests.ps1 refuses when someone else holds the owner role' {
  foreach ($case in @(
      @{ Users = '[{"displayName":"owner@example.com","provider":"aad","userId":"owner-uid","roles":"owner"},{"displayName":"stranger@example.com","provider":"aad","userId":"x","roles":"anonymous,owner"}]'; Says = 'stranger@example.com also hold(s) the owner role' },
      @{ Users = '[{"displayName":"owner@example.com","provider":"github","userId":"gh","roles":"owner"}]'; Says = 'owner@example.com also hold(s) the owner role' },
      @{ Users = '[{"displayName":"owner@example.com","userId":"np","roles":"owner"}]'; Says = 'owner@example.com also hold(s) the owner role' },
      @{ Users = '[{"displayName":"owner@example.com","provider":"aad","userId":"a","roles":"owner"},{"displayName":"owner@example.com","provider":"aad","userId":"b","roles":"owner"}]'; Says = 'More than one account named owner@example.com' })) {
    $r = Invoke-SetupWriter $existingWriter @{ 'staticwebapp users list' = $case.Users }
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "for $($case.Users) got: $($r.Error)" }
    if ($r.Calls -match '--method put|users invite') { throw "it changed the owner: $($r.Calls)" }
  }
}

Check 'setup-dashboard-requests.ps1 run again changes nothing that is already in place' {
  $r = Invoke-SetupWriter $existingWriter
  if ($r.Error) { throw "failed: $($r.Error)" }
  if ($r.Calls -match '(?m)\bcreate\b|credential reset|--method put|users invite') { throw "it changed something: $($r.Calls)" }
  if ($r.Output -notmatch 'is the confirmed owner') { throw "no owner line: $($r.Output)" }
}

Check 'setup-dashboard-requests.ps1 -RotateSecret replaces the secret only' {
  $r = Invoke-SetupWriter $existingWriter @{ 'ad app credential' = $newWriter['ad app credential'] } @{ RotateSecret = $true }
  if ($r.Error) { throw "failed: $($r.Error)" }
  if ($r.Calls -notmatch 'ad app credential reset' -or $r.Calls -notmatch '--method put') { throw "the secret wasn't replaced: $($r.Calls)" }
  if ($r.Calls -match '(?m)\bcreate\b') { throw 'it created something' }
}

foreach ($case in @(
    @{ Name = 'a client secret it did not create'; Overrides = @{ 'ad app show' = '{"appId":"4","passwordCredentials":[{"displayName":"someone"}],"keyCredentials":[]}' }; Says = "client secrets this script didn't create" },
    @{ Name = 'a second secret of the same name'; Overrides = @{ 'ad app show' = '{"appId":"4","passwordCredentials":[{"displayName":"cix-dashboard-requests"},{"displayName":"cix-dashboard-requests"}],"keyCredentials":[]}' }; Says = 'has 2 client secrets' },
    @{ Name = 'the writer role assigned to someone else'; Overrides = @{ 'cosmosdb sql role assignment list' = "[{`"principalId`":`"55555555-5555-5555-5555-555555555555`",`"roleDefinitionId`":`"/role/r1`",`"scope`":`"$writerScope`"},{`"principalId`":`"intruder`",`"roleDefinitionId`":`"/role/r1`",`"scope`":`"$writerScope`"}]" }; Says = 'is also assigned to intruder' },
    @{ Name = 'a federated credential'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:x/y:ref:refs/heads/main"}]' }; Says = 'trusts external tokens' },
    @{ Name = 'an Azure role'; Overrides = @{ 'role assignment' = '[{"roleDefinitionName":"Reader","scope":"/subscriptions/x"}]' }; Says = 'holds Azure roles (Reader on /subscriptions/x)' },
    @{ Name = 'a broader Cosmos DB role of the same name'; Overrides = @{ 'cosmosdb sql role definition list' = $writerRole.Replace('items/create', 'items/*') }; Says = 'allows more than creating items' },
    @{ Name = 'a same-named role on the whole account'; Overrides = @{ 'cosmosdb sql role definition list' = $writerRole.Replace($writerScope, '/') }; Says = 'allows more than creating items' },
    @{ Name = 'another Cosmos DB role assignment'; Overrides = @{ 'cosmosdb sql role assignment list' = '[{"principalId":"55555555-5555-5555-5555-555555555555","roleDefinitionId":"/role/data-contributor","scope":"/"}]' }; Says = 'other Cosmos DB role assignments' })) {
  Check "setup-dashboard-requests.ps1 refuses a reused identity with $($case.Name), changing nothing" {
    $r = Invoke-SetupWriter $existingWriter $case.Overrides @{ RotateSecret = $true }
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    if ($r.Calls -match '(?m)\bcreate\b|credential reset|--method put|users invite') { throw "it changed something before refusing: $($r.Calls)" }
  }
}

Check 'only the deploy-dashboard and deploy-agents jobs may request Azure sign-in tokens, and workflows run only on push, pull_request or workflow_dispatch' {
  # A guard against accidents, not a security control: anyone who can change main can change this test too (ADR-0010).
  $files = @(Get-ChildItem (Join-CixPath $repo '.github' 'workflows') -File | Where-Object { $_.Extension -in '.yml', '.yaml' })
  if ($files.Count -eq 0) { throw 'no workflows found' }
  foreach ($f in $files) {
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $text = $lines -join "`n"
    if ($text -match 'write-all') { throw "$($f.Name) grants write-all" }
    # Triggers: the keys under the top-level on:, or its inline value.
    $triggers = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
      if ($lines[$i] -match '^["'']?on["'']?\s*:\s*(.*)$') {
        $inline = $Matches[1].Trim()
        if ($inline -and -not $inline.StartsWith('#')) { $triggers += @($inline.Trim('[', ']', '{', '}') -split ',' | ForEach-Object { ($_ -split ':')[0].Trim(' ', '"', "'") } | Where-Object { $_ }) }
        for ($j = $i + 1; $j -lt $lines.Count -and ($lines[$j] -match '^\s' -or $lines[$j] -eq ''); $j++) {
          if ($lines[$j] -match '^  ["'']?([A-Za-z_]+)["'']?\s*:') { $triggers += $Matches[1] }
        }
      }
    }
    if ($triggers.Count -eq 0) { throw "$($f.Name): couldn't read its triggers" }
    $unexpected = @($triggers | Where-Object { $_ -notin 'push', 'pull_request', 'workflow_dispatch' })
    if ($unexpected.Count -gt 0) { throw "$($f.Name) runs on $($unexpected -join ', '); only push, pull_request and workflow_dispatch are allowed" }
    # id-token, in any spelling, only in ci.yml: once inside the deploy-dashboard job and once inside deploy-agents.
    $grants = @([regex]::Matches($text, 'id-token'))
    if ($f.Name -ne 'ci.yml') { if ($grants.Count -gt 0) { throw "$($f.Name) mentions id-token" }; continue }
    Expect $grants.Count 2 'id-token mentions in ci.yml'
    foreach ($job in 'deploy-dashboard', 'deploy-agents') {
      $jobStart = $text.IndexOf("`n  ${job}:")
      if ($jobStart -lt 0) { throw "ci.yml has no $job job" }
      $next = [regex]::Match($text.Substring($jobStart + 1), '\n  [A-Za-z_-]+:')
      $jobEnd = if ($next.Success) { $jobStart + 1 + $next.Index } else { $text.Length }
      $inside = @($grants | Where-Object { $_.Index -gt $jobStart -and $_.Index -lt $jobEnd })
      Expect $inside.Count 1 "id-token mentions in the $job job"
    }
  }
}

Check 'provision.ps1 refuses a database that exists but no CIX environment claims' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $true } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly
  }
  if (-not $r.Error -or -not $r.Error.Contains("already exists in cosmos-shared-x but no CIX environment claims it")) { throw "got: $($r.Error)" }
}

Check 'provision.ps1 refuses to create the shared account over an untagged one' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = '[{"name":"someone-elses","tags":{}}]' } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly
  }
  if (-not $r.Error -or -not $r.Error.Contains('none tagged shared-role=cosmos')) { throw "got: $($r.Error)" }
}

Check 'provision.ps1 refuses a protected environment in the shared account' {
  $prod = Join-CixPath $repo 'infra' 'environments' 'prodtest.json'
  $json = (Get-Content (Join-CixPath $repo 'infra' 'environments' 'prod.example.json') -Raw).Replace('"prod"', '"prodtest"')
  [IO.File]::WriteAllText($prod, $json)
  try {
    $r = Invoke-WithFakeAz @{} { & (Join-Path $azureTools 'provision.ps1') -Environment prodtest -WhatIfOnly }
    if (-not $r.Error -or -not $r.Error.Contains('-AllowProtectedInSharedAccount')) { throw "got: $($r.Error)" }
  }
  finally { Remove-Item $prod -Force }
}

Check 'two environment files sharing one database are refused' {
  $copy = Join-CixPath $repo 'infra' 'environments' 'devcopy.json'
  [IO.File]::WriteAllText($copy, (Get-Content (Join-CixPath $repo 'infra' 'environments' 'dev.json') -Raw).Replace('"dev"', '"devcopy"'))
  try {
    $message = $null
    try { Read-CixEnvironment -Name dev | Out-Null } catch { $message = $_.Exception.Message }
    if (-not $message -or -not $message.Contains("is also used by devcopy.json")) { throw "got: $message" }
  }
  finally { Remove-Item $copy -Force }
}

# The engine and agents in Azure (agents.deploy apps), against a fake az: an environment file of its own, removed after.
$agentsFake = @{
  'bicep'                 = 'Bicep CLI version 0.33.0'
  'provider show'         = '{"resourceTypes":[{"resourceType":"jobs","locations":["Poland Central"]},{"resourceType":"containerApps","locations":["Poland Central"]},{"resourceType":"managedEnvironments","locations":["Poland Central","Germany West Central"]}]}'
  'ad signed-in-user'     = "{`"id`":`"$ownerId`",`"userPrincipalName`":`"owner@example.com`"}"
  'ad app owner'          = "[{`"id`":`"$ownerId`"}]"
  'keyvault list'         = '[{"name":"kvother","tags":{}},{"name":"kvcixcitest1","tags":{"cix-environment":"citest"}}]'
  'acr list'              = '[{"name":"crcixcitest1","tags":{"cix-environment":"citest"}}]'
  'keyvault secret list'  = '[{"name":"cosmos-key","id":"https://kvcixcitest1.vault.azure.net/secrets/cosmos-key"},{"name":"claude-token","id":"https://kvcixcitest1.vault.azure.net/secrets/claude-token"}]'
  'ad app list'           = '[]'
  'ad app create'         = '44444444-4444-4444-4444-444444444444'
  'ad sp list'            = '[]'
  'acr repository list'   = '["cix-engine","cix-trader"]'
  'acr repository show-tags' = '["abc123","def456"]'
  'resource list'         = '[]'
  'consumption budget'    = '[{"name":"budget-cix-citest"}]'
  'deployment sub create' = '{"properties":{"outputs":{"cosmosAccountName":{"value":"cosmos-shared-x"},"cosmosEndpoint":{"value":"https://cosmos-shared-x.documents.azure.com:443/"},"staticWebAppName":{"value":"stapp-cix-citest"},"dashboardUrl":{"value":"https://dash.example"},"registryServer":{"value":"crcixcitest1.azurecr.io"},"keyVaultName":{"value":"kvcixcitest1"},"engineAppName":{"value":"ca-cix-citest-engine"},"agentJobNames":{"value":["caj-cix-citest-trader","caj-cix-citest-requests","caj-cix-citest-analyst"]}}}}'
}
function Invoke-AgentsProvision([hashtable]$Overrides, [hashtable]$Arguments, [string]$Deploy = 'apps') {
  $file = Join-CixPath $repo 'infra' 'environments' 'citest.json'
  $json = (Get-Content (Join-CixPath $repo 'infra' 'environments' 'dev.json') -Raw).Replace('"dev"', '"citest"').Replace('rg-cix-dev', 'rg-cix-citest').Replace('"databaseName": "cix"', '"databaseName": "cix-citest"').Replace('"deploy": "none"', "`"deploy`": `"$Deploy`"")
  [IO.File]::WriteAllText($file, $json)
  $extra = @{}; foreach ($k in $agentsFake.Keys) { $extra[$k] = $agentsFake[$k] }
  foreach ($k in $Overrides.Keys) { $extra[$k] = $Overrides[$k] }
  try {
    return Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $false; Extra = $extra } {
      & (Join-Path $azureTools 'provision.ps1') -Environment citest @Arguments
    }
  }
  finally { Remove-Item $file -Force }
}

Check 'provision.ps1 agents apps (-Yes): creates the engine''s Entra app, checks both images, deploys and reports the apps' {
  $r = Invoke-AgentsProvision @{} @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($expected in @('Engine and agents (apps, in polandcentral)', 'Images: crcixcitest1, tag abc123. Engine: stopped', 'Created the Entra app registration cix-engine-citest',
      'Engine app: ca-cix-citest-engine; jobs: caj-cix-citest-trader, caj-cix-citest-requests, caj-cix-citest-analyst')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
  foreach ($call in @('ad app update --id 44444444-4444-4444-4444-444444444444 --identifier-uris api://44444444-4444-4444-4444-444444444444', 'ad sp create --id 44444444-4444-4444-4444-444444444444',
      'acr repository show-tags --name crcixcitest1 --repository cix-engine', 'acr repository show-tags --name crcixcitest1 --repository cix-trader')) {
    if (-not $r.Calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  if ($r.Calls.Contains('keyvault secret set')) { throw 'uploaded claude-token although the vault has it' }
  if ($r.Calls -match 'keyvault secret show') { throw 'read a secret value' }
}

Check 'provision.ps1 agents apps preview creates nothing in Entra' {
  $r = Invoke-AgentsProvision @{} @{ WhatIfOnly = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($never in @('ad app create', 'ad app update', 'ad sp create', 'deployment sub create', 'keyvault secret set')) {
    if ($r.Calls.Contains($never)) { throw "a preview ran '$never'" }
  }
  if (-not $r.Output.Contains('the preview uses a placeholder')) { throw "no placeholder note in: $($r.Output)" }
}

Check 'provision.ps1 agents apps keeps the image tag the engine runs, and sends budget alerts where -BudgetEmail says' {
  $running = @{
    'resource list' = '[{"id":"/subscriptions/x/resourceGroups/rg-cix-citest/providers/Microsoft.App/containerApps/ca-cix-citest-engine","name":"ca-cix-citest-engine","tags":{"cix-environment":"citest"}}]'
    'resource show' = '{"properties":{"template":{"containers":[{"image":"crcixcitest1.azurecr.io/cix-engine:def456"}]}}}'
  }
  $r = Invoke-AgentsProvision $running @{ Yes = $true; SkipEngineConfig = $true; BudgetEmail = 'alerts@example.com' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($expected in @('Images: crcixcitest1, tag def456.', 'alerts to alerts@example.com.')) { if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" } }
  if ($r.Calls -cmatch '(^|\s)containerapp ') { throw "used the containerapp extension: $($r.Calls)" }
}

Check 'provision.ps1 agents platform: no budget without an address that gets mail, and nothing for the apps' {
  $guest = "{`"id`":`"$ownerId`",`"mail`":null,`"userPrincipalName`":`"owner_gmail.com#EXT#@cixtest.onmicrosoft.com`"}"
  $r = Invoke-AgentsProvision @{ 'ad signed-in-user' = $guest } @{ Yes = $true; SkipEngineConfig = $true } 'platform'
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  if (-not $r.Output.Contains('no budget is created. Pass -BudgetEmail')) { throw "no warning in: $($r.Output)" }
  foreach ($never in @('ad app', 'keyvault', 'acr ')) { if ($r.Calls.Contains($never)) { throw "platform ran '$never'" } }
}

Check 'a budget starting in a past month is refused only while it does not exist' {
  $s = Read-CixEnvironment -Name dev
  $later = [datetime]::new(2027, 1, 15)
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; Extra = @{ 'consumption budget' = '[]' } } { Assert-CixBudgetStart -Settings $s -Today $later }
  if (-not $r.Error -or -not $r.Error.Contains('Set it to 2027-01-01')) { throw "got: $($r.Error)" }
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; Extra = @{ 'consumption budget' = '[{"name":"budget-cix-dev"}]' } } { Assert-CixBudgetStart -Settings $s -Today $later }
  if ($r.Error) { throw "refused an existing budget: $($r.Error)" }
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; Extra = @{ 'consumption budget' = '[]' } } { Assert-CixBudgetStart -Settings $s -Today ([datetime]::new(2026, 11, 30)) }
  if ($r.Error -or $r.Calls.Contains('consumption')) { throw "checked a current start date: $($r.Error) $($r.Calls)" }
}

$engineApp = '[{"appId":"55555555-5555-5555-5555-555555555555","displayName":"cix-engine-citest","identifierUris":["api://55555555-5555-5555-5555-555555555555"]}]'
Check 'provision.ps1 agents apps adopts an engine app registration that is yours alone' {
  $r = Invoke-AgentsProvision @{ 'ad app list' = $engineApp; 'ad sp list' = '[{"id":"sp"}]' } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($never in @('ad app create', 'ad app update', 'ad sp create')) { if ($r.Calls.Contains($never)) { throw "ran '$never'" } }
  if (-not $r.Calls.Contains('ad app owner list --id 55555555-5555-5555-5555-555555555555')) { throw "owners not checked: $($r.Calls)" }
}

Check 'provision.ps1 agents apps ignores app registrations whose name only starts the same' {
  $r = Invoke-AgentsProvision @{ 'ad app list' = $engineApp.Replace('"cix-engine-citest"', '"cix-engine-citest-old"') } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  if (-not $r.Calls.Contains('ad app create --display-name cix-engine-citest ')) { throw "didn't create its own: $($r.Calls)" }
}

foreach ($case in @(
    @{ Overrides = @{ 'keyvault list' = '[]' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = "Deploy with agents.deploy 'platform' first" },
    @{ Overrides = @{}; Arguments = @{ Yes = $true; SkipEngineConfig = $true }; Says = 'pass -ImageTag' },
    @{ Overrides = @{}; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'nope' }; Says = 'has no cix-engine image tagged nope' },
    @{ Overrides = @{ 'provider show' = '{"resourceTypes":[{"resourceType":"managedEnvironments","locations":["Germany West Central"]}]}' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'Set agents.location to germanywestcentral' },
    @{ Overrides = @{ 'bicep' = 'Bicep CLI version 0.31.1' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = "run 'az bicep upgrade'" },
    @{ Overrides = @{ 'ad app list' = $engineApp; 'ad app owner' = "[{`"id`":`"$ownerId`"},{`"id`":`"someone-else`"}]" }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'has other owners (someone-else)' },
    @{ Overrides = @{ 'ad signed-in-user' = '{"userPrincipalName":"ci@example.com"}' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'Sign in to Azure as a user' },
    @{ Overrides = @{ 'acr repository list' = '[]' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'has no cix-engine images yet' })) {
  Check "provision.ps1 agents apps refuses: $($case.Says)" {
    $r = Invoke-AgentsProvision $case.Overrides $case.Arguments
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    if ($r.Calls.Contains('deployment sub create')) { throw 'deployed anyway' }
    if ($r.Calls.Contains('ad app create') -or $r.Calls.Contains('keyvault secret set')) { throw 'changed Entra or Key Vault before refusing' }
  }
}

# Without secrets/claude_token (it is never in the repo; skipped where a developer has one), a vault without claude-token stops.
if (-not (Test-Path (Join-CixPath $repo 'secrets' 'claude_token'))) {
  Check 'provision.ps1 agents apps refuses: no claude-token in the vault and none to upload' {
    $r = Invoke-AgentsProvision @{ 'keyvault secret list' = '[{"name":"cosmos-key"}]' } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
    if (-not $r.Error -or -not $r.Error.Contains('has no claude-token. Run tools/setup-secrets.ps1')) { throw "got: $($r.Error)" }
    if ($r.Calls.Contains('ad app create') -or $r.Calls.Contains('deployment sub create')) { throw 'went on anyway' }
  }
}

Write-Host ''
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.'
