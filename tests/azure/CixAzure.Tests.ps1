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

Check 'settings load as nested hashtables, on every version' {
  $s = Read-CixEnvironment -Name dev
  if ($s -isnot [hashtable] -or $s.cosmos -isnot [hashtable] -or $s.tags -isnot [hashtable]) { throw 'not hashtables' }
  Expect $s.cosmos.databaseName 'cix' 'databaseName'
  Expect ($s.protected -is [bool]) $true 'protected is a bool'
  Expect (@($s.cosmos.allowedIpRanges).Count) 0 'allowedIpRanges'
  foreach ($f in Get-ChildItem (Join-CixPath $repo 'infra' 'environments') -Filter *.json) { Get-Content $f.FullName -Raw | ConvertFrom-Json | Out-Null }
}

Check 'Join-CixPath joins several parts' {
  Expect (Join-CixPath 'a' 'b' 'c') (Join-Path (Join-Path 'a' 'b') 'c') 'path'
}

Check 'deployment parameters: valid JSON, no byte-order mark, arrays stay arrays' {
  $s = Read-CixEnvironment -Name dev
  $s.cosmos.allowedIpRanges = @('203.0.113.7')
  $path = New-CixDeploymentParameters -Settings $s
  try {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'has a BOM' }
    $raw = [IO.File]::ReadAllText($path)
    if ($raw -notmatch '"allowedIpRanges":\s*\{\s*"value":\s*\[\s*"203\.0\.113\.7"\s*\]') { throw 'allowedIpRanges is not a one-item array' }
    $p = ($raw | ConvertFrom-Json).parameters
    Expect $p.environmentName.value 'dev' 'environmentName'
    Expect $p.enableFreeTier.value $true 'enableFreeTier'
    Expect $p.tags.value.purpose 'cix-paper-trading' 'tags'
  }
  finally { Remove-Item $path -ErrorAction SilentlyContinue }
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
  }
  finally { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}

# --- A fake Azure CLI that writes a warning to stderr on every call, as the real one often does ---------------------------
$fake = Join-Path ([IO.Path]::GetTempPath()) "cix-fake-az-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fake | Out-Null
$accounts = '[{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one","state":"Enabled"},{"id":"00000000-0000-0000-0000-000000000002","name":"Disabled one","state":"Disabled"}]'
$account = '{"id":"00000000-0000-0000-0000-000000000001","name":"Enabled one"}'
if ($onWindows) {
  $lines = @(
    '@echo off',
    'echo WARNING: fake az writes to stderr 1>&2',
    'if "%1"=="version" echo {"azure-cli": "2.65.0"}& exit /b 0',
    'if "%1"=="bicep" echo Bicep CLI version 0.30.3& exit /b 0',
    "if `"%1 %2`"==`"account list`" echo $accounts& exit /b 0",
    "if `"%1 %2`"==`"account show`" echo $account& exit /b 0",
    'if "%1 %2"=="group exists" echo false& exit /b 0',
    'if "%1"=="fail" echo boom 1>&2& exit /b 3',
    'exit /b 0')
  [IO.File]::WriteAllText((Join-Path $fake 'az.cmd'), ($lines -join "`r`n"), [Text.Encoding]::ASCII)
}
else {
  $lines = @(
    '#!/bin/sh',
    'echo "WARNING: fake az writes to stderr" >&2',
    'case "$1 $2" in',
    "  'version '*) echo '{`"azure-cli`": `"2.65.0`"}' ;;",
    "  'bicep '*) echo 'Bicep CLI version 0.30.3' ;;",
    "  'account list') echo '$accounts' ;;",
    "  'account show') echo '$account' ;;",
    "  'group exists') echo false ;;",
    "  'fail '*) echo boom >&2; exit 3 ;;",
    'esac')
  $script = Join-Path $fake 'az'
  [IO.File]::WriteAllText($script, (($lines -join "`n") + "`n"))
  chmod +x $script
}
$savedPath = $env:PATH
$env:PATH = "$fake$([IO.Path]::PathSeparator)$env:PATH"
try {
  Check 'az warnings on stderr are not errors (5.1 would stop on them)' {
    Expect ((Invoke-CixAz @('version', '-o', 'json') | ConvertFrom-CixJson).'azure-cli') '2.65.0' 'version'
    Assert-CixAzureCli 6>$null
  }

  Check 'a failing az call throws with its message' {
    $message = $null
    try { Invoke-CixAz @('fail', 'now') | Out-Null } catch { $message = $_.Exception.Message }
    if ($message -notmatch 'az fail now failed: .*boom') { throw "got: $message" }
  }

  Check 'provision.ps1 -WhatIfOnly runs to its first decision, picking the enabled subscription' {
    $output = & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly 6>&1 | Out-String
    if ($output -notmatch 'Subscription: Enabled one') { throw "no subscription line: $output" }
    if ($output -notmatch "rg-cix-dev doesn't exist yet") { throw "didn't stop at the missing resource group: $output" }
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

# --- A fake Cosmos DB endpoint on loopback: throttles once, then answers, then has nothing ---------------------------------
Check 'Cosmos DB requests: headers, retry on 429, continuation, 404' {
  $body = '{"Documents":[{"id":"e1","day":"2026-10-25"}],"_count":1}'
  $replies = @(
    "HTTP/1.1 429 Too Many Requests`r`nx-ms-retry-after-ms: 5`r`nContent-Length: 0`r`nConnection: close`r`n`r`n",
    "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nx-ms-continuation: next-page`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body",
    "HTTP/1.1 404 Not Found`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
  $listener = New-Object System.Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
  $listener.Start()
  $port = $listener.LocalEndpoint.Port
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
    }).AddArgument($listener).AddArgument($replies)
  $async = $server.BeginInvoke()
  try {
    $sample = @('dsZQi3KtZmCv1ljt3VNWNm7sQUF1y5rJfC6kv5Jiwv', 'W0EndXdDku/dkKBp8/ufDToSxLzR4y+O/0H/t4bQtVNw==') -join ''
    $r = Invoke-CixCosmosRequest -Endpoint "http://127.0.0.1:$port/" -Key $sample -Method POST -ResourceType docs `
      -ResourceLink 'dbs/cix/colls/journal' -Path 'dbs/cix/colls/journal/docs' -Body '{"query":"SELECT c.id FROM c","parameters":[]}' `
      -Headers @{ 'x-ms-documentdb-isquery' = 'True' } -CrossPartition
    Expect $r.Status 200 'status after one 429'
    Expect $r.Continuation 'next-page' 'continuation'
    Expect (($r.Body | ConvertFrom-Json).Documents[0].id) 'e1' 'body'
    $missing = Get-CixCosmosItem -Endpoint "http://127.0.0.1:$port/" -Key $sample -Database cix -Container state -Id 'engine-status-paper' -PartitionKey 'engine-status-paper'
    if ($null -ne $missing) { throw '404 should be $null' }

    if (-not $async.AsyncWaitHandle.WaitOne(20000)) { throw 'the fake server did not finish' }
    $seen = @($server.EndInvoke($async) | ForEach-Object { $_ })
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
  finally {
    $listener.Stop()
    $server.Dispose()
  }
}

Write-Host ''
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.'
