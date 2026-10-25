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
  catch { $failures.Add($Name); Write-Host "FAIL $Name -- $($_.Exception.Message)" -ForegroundColor Red }
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

function New-FakeAz([string]$CosmosAccounts = '[]', [string]$GroupExists = 'false', [string]$GroupTags = '{}') {
  <# A directory holding a fake az (az.cmd on Windows); put it first on PATH. #>
  $dir = Join-Path ([IO.Path]::GetTempPath()) "cix-fake-az-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir | Out-Null
  $answers = [ordered]@{
    'version'        = '{"azure-cli": "2.65.0"}'
    'bicep'          = 'Bicep CLI version 0.30.3'
    'account list'   = '[{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one","state":"Enabled"},{"id":"00000000-0000-0000-0000-000000000002","name":"Disabled one","state":"Disabled"}]'
    'account show'   = '{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one"}'
    'group exists'   = $GroupExists
    'group show'     = "{`"name`":`"rg-cix-dev`",`"tags`":$GroupTags}"
    'cosmosdb list'  = $CosmosAccounts
    'cosmosdb keys'  = "{`"primaryReadonlyMasterKey`": `"$sample`"}"
  }
  if ($onWindows) {
    $lines = @('@echo off', 'echo WARNING: fake az writes to stderr 1>&2')
    foreach ($k in $answers.Keys) {
      $match = if ($k.Contains(' ')) { '"%1 %2"' } else { '"%1"' }
      $lines += "if $match==`"$k`" echo $($answers[$k])& exit /b 0"
    }
    $lines += @('if "%1"=="fail" echo boom 1>&2& exit /b 3', 'exit /b 0')
    [IO.File]::WriteAllText((Join-Path $dir 'az.cmd'), ($lines -join "`r`n"), [Text.Encoding]::ASCII)
  }
  else {
    $lines = @('#!/bin/sh', 'echo "WARNING: fake az writes to stderr" >&2', 'case "$1 $2" in')
    foreach ($k in $answers.Keys) {
      $pattern = if ($k.Contains(' ')) { "'$k'" } else { "'$k '*" }
      $lines += "  $pattern) echo '$($answers[$k])' ;;"
    }
    $lines += @("  'fail '*) echo boom >&2; exit 3 ;;", 'esac')
    $script = Join-Path $dir 'az'
    [IO.File]::WriteAllText($script, (($lines -join "`n") + "`n"))
    chmod +x $script
  }
  return $dir
}

$savedPath = $env:PATH
$fake = New-FakeAz
$env:PATH = "$fake$([IO.Path]::PathSeparator)$savedPath"
try {
  Check 'az warnings on stderr are not errors (5.1 would stop on them)' {
    Expect ((Invoke-CixAz @('version', '-o', 'json') | ConvertFrom-CixJson).'azure-cli') '2.65.0' 'version'
    Assert-CixAzureCli 6>$null
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
  $dir = New-FakeAz -CosmosAccounts $accounts -GroupExists 'true' -GroupTags '{"cix-environment":"dev","cix-protected":"false"}'
  $env:PATH = "$dir$([IO.Path]::PathSeparator)$savedPath"
  $seen = @()
  try { $output = & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target All 6>&1 | Out-String }
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

foreach ($case in @(
    @{ Tags = '{"cix-environment":"dev","cix-protected":"true"}'; Says = "isn't tagged cix-protected=false (it says 'true')" },
    @{ Tags = '{"cix-protected":"false"}'; Says = "isn't tagged cix-environment=dev (it says 'nothing')" })) {
  Check "reset-database.ps1 refuses when CIX's resource group says: $($case.Says)" {
    $dir = New-FakeAz -GroupExists 'true' -GroupTags $case.Tags
    $env:PATH = "$dir$([IO.Path]::PathSeparator)$savedPath"
    $message = $null
    try { & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target All 6>$null } catch { $message = $_.Exception.Message }
    finally { $env:PATH = $savedPath; Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $message -or -not $message.Contains($case.Says)) { throw "got: $message" }
  }
}

Write-Host ''
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.'
