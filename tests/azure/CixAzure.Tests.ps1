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

# The agents' templates deploy a paper engine only: the owner switches a protected environment's engine to live from their own
# sign-in (ADR-0017). The live-mode hook doesn't read Bicep, so this holds the line: one way to set the mode, to Paper, never
# Production, and the exchange keys named in one place only, mounted only where main.bicep says the environment is protected.
$exchangeKeyLines = @("{ secret: 'revolutx-api-key', path: 'RevolutX__ApiKey' }", "{ secret: 'revolutx-private-key', path: 'RevolutX__PrivateKeyPem' }")
Check 'the Azure templates only ever run a paper engine' {
  $files = @(Get-ChildItem (Join-CixPath $repo 'infra') -Recurse -Filter '*.bicep')
  if ($files.Count -lt 3) { throw "found only $($files.Count) Bicep files" }
  $modes = @()
  $keyLines = @()
  foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($line in ($text -split "`n")) {
      if ($line -match '(?i)trading(__|:)mode') { $modes += $line.Trim() }
      if ($line -match '(?i)ASPNETCORE_ENVIRONMENT' -and $line -notmatch "value: 'Azure'") { throw "$($file.Name): $($line.Trim())" }
      if ($line -match '^\s*//') { continue }
      if ($file.Name -eq 'agents-apps.bicep' -and $exchangeKeyLines -contains $line.Trim()) { $keyLines += $line.Trim(); continue }
      if ($line -match '(?i)revolut|\blive\b') { throw "$($file.Name) mentions the exchange or live trading outside a comment: $($line.Trim())" }
    }
  }
  if ($modes.Count -ne 1 -or $modes[0] -ne "{ name: 'Trading__Mode', value: 'Paper' }") { throw "trading mode settings: $($modes -join ' | ')" }
  if ($keyLines.Count -ne 2) { throw "the exchange key files are named $($keyLines.Count) times in agents-apps.bicep, not once each" }
}

Check 'only a protected environment gets the exchange keys, in its engine alone' {
  $main = [IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'main.bicep'))
  $apps = [IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'modules' 'agents-apps.bicep'))
  if (([regex]::Matches($main, '(?m)^\s*exchangeKeys: protectedEnvironment\s*$')).Count -ne 1) { throw 'main.bicep must pass exchangeKeys: protectedEnvironment, once' }
  if ($apps -notmatch '(?m)^param exchangeKeys bool = false\s*$') { throw 'exchangeKeys must default to false' }
  # Every use of the keys' names is behind exchangeKeys: the secrets, the volume and its mount, and the engine's access.
  foreach ($use in @('secrets: exchangeKeys ? engineSecrets : []', "volumes: exchangeKeys ? [{ name: 'secrets', storageType: 'Secret', secrets: engineSecretFiles }] : []",
      "volumeMounts: exchangeKeys ? [{ volumeName: 'secrets', mountPath: '/run/secrets' }] : []", 'for i in range(0, exchangeKeys ? length(exchangeKeyFiles) : 0): {')) {
    if (-not $apps.Contains($use)) { throw "agents-apps.bicep lacks: $use" }
  }
  foreach ($name in 'engineSecrets', 'engineSecretFiles') {
    $uses = [regex]::Matches($apps, "\b$name\b").Count
    if ($uses -ne 2) { throw "$name is used $($uses - 1) times, not once behind exchangeKeys" }
  }
  # The jobs never see them: their secrets stay claude-token alone.
  if (([regex]::Matches($apps, 'exchangeKeyFiles')).Count -ne 5) { throw 'exchangeKeyFiles is used somewhere other than the engine' }
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

# The container's user has no app data folder, so the engine stops at startup unless the file journal's folder is set.
Check 'the engine in Azure is given a journal folder under /tmp' {
  $apps = [IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'modules' 'agents-apps.bicep'))
  if ($apps -notmatch "\{ name: 'Journal__Directory', value: '/tmp/[a-z-]+' \}") { throw 'Journal__Directory is not set to a folder under /tmp' }
}

# A stopped engine (startEngine false) still runs once per new revision: it must not queue agent runs, or the jobs calling
# it keep it awake and it trades until the cutover.
Check 'the engine queues agent runs only when startEngine is true' {
  $apps = [IO.File]::ReadAllText((Join-CixPath $repo 'infra' 'modules' 'agents-apps.bicep'))
  if ($apps -notmatch "\{ name: 'AgentRuns__Mode', value: startEngine \? 'Queue' : 'Off' \}") { throw 'AgentRuns__Mode does not follow startEngine' }
  if (([regex]::Matches($apps, 'AgentRuns__Mode')).Count -ne 1) { throw 'AgentRuns__Mode is set more than once' }
}

# The engine in Azure signs in to Cosmos DB with its identity: no account key anywhere in the agents' templates, and its
# roles reach CIX's containers only, never delete, and leave the journal append-only.
Check 'the engine''s Cosmos DB access: no account key, no deletes, the journal append-only' {
  $modules = Join-CixPath $repo 'infra' 'modules'
  foreach ($name in 'agents-platform.bicep', 'agents-apps.bicep') {
    $text = [IO.File]::ReadAllText((Join-CixPath $modules $name))
    if ($text -match '(?i)listKeys\(\)\.primary(Master|Readonly)|AccountKey|cosmos-key') { throw "$name hands out the account key" }
  }
  $apps = [IO.File]::ReadAllText((Join-CixPath $modules 'agents-apps.bicep'))
  if ($apps -notmatch "\{ name: 'Cosmos__ManagedIdentityClientId', value: engineIdentity\.properties\.clientId \}") { throw 'the engine is not given its identity for Cosmos DB' }
  $access = [IO.File]::ReadAllText((Join-CixPath $modules 'cosmos-engine-access.bicep'))
  if ($access -match '(?i)items/delete|\{item\}/delete|unmask|executeStoredProcedure|manageConflicts') { throw 'a role allows more than reading and writing items' }
  if ($access -notmatch "var databaseScope = '\$\{account\.id\}/dbs/\$\{databaseName\}'") { throw 'the roles are not limited to CIX''s database' }
  $perContainer = "'\$\{databaseScope\}/colls/\$\{a\.container\}'"
  if (([regex]::Matches($access, "assignableScopes: \[$perContainer\]")).Count -ne 1 -or ([regex]::Matches($access, "(?m)^\s*scope: $perContainer")).Count -ne 1) { throw 'the roles are not assignable and assigned per container' }
  # The shared pieces only read: item writes are spelled out per container below.
  Expect ([regex]::Match($access, "(?m)^var item = '([^']+)'").Groups[1].Value) 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items' 'item actions'
  Expect ([regex]::Match($access, "(?m)^var metadata = '([^']+)'").Groups[1].Value) 'Microsoft.DocumentDB/databaseAccounts/readMetadata' 'metadata action'
  $query = @([regex]::Matches([regex]::Match($access, '(?s)var query = \[(.*?)\]').Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
  Expect ($query -join ',') 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery,Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed' 'query actions'
  if ($access -match '/\*') { throw 'a role uses a wildcard' }
  $parts = @{}
  foreach ($part in @(($access -split "container: '") | Select-Object -Skip 1)) { $parts[$part.Substring(0, $part.IndexOf("'"))] = $part }
  Expect (($parts.Keys | Sort-Object) -join ',') 'journal,requests,state' 'containers'
  if (([regex]::Matches($access, '\$\{item\}/')).Count -ne 7 -or ([regex]::Matches($access, "Microsoft\.DocumentDB/[^']*items")).Count -ne 1) { throw 'item actions written some other way than per container' }
  if ($parts['journal'] -notmatch '\$\{item\}/create' -or $parts['journal'] -match '\$\{item\}/(replace|upsert)') { throw 'the journal role is not append-only' }
  if ($parts['requests'] -match '\$\{item\}/(create|upsert)') { throw 'the engine may only update requests, not add them' }
}

Check 'agents settings: dev''s are valid, dates stay text, and they reach the template as its parameters' {
  $s = Read-CixEnvironment -Name dev
  # The owner moves dev.json from none to platform to apps, and starts the engine at the cutover; no test pins which.
  if (@('none', 'platform', 'apps') -notcontains $s.agents.deploy) { throw "agents.deploy in dev.json is '$($s.agents.deploy)'" }
  Expect ($s.agents.budgetStartDate -is [string]) $true 'budgetStartDate is text'
  Expect $s.agents.budgetStartDate '2026-11-01' 'budgetStartDate'
  $s.agents.deploy = 'platform'
  $d = Get-CixDeploymentValues -Settings $s -SharedAccountName 'cosmos-shared-test' -Agents @{ budgetContactEmail = 'owner@example.com'; ownerObjectId = $ownerId }
  Expect $d.Values.agents 'platform' 'agents'
  Expect $d.Values.agentsLocation 'polandcentral' 'agentsLocation'
  Expect ($s.agents.startEngine -is [bool]) $true 'startEngine is a boolean'
  Expect $d.Values.startEngine $s.agents.startEngine 'startEngine'
  Expect $d.Values.monthlyBudget 40 'monthlyBudget'
  Expect $d.Values.budgetContactEmail 'owner@example.com' 'budgetContactEmail'
  Expect $d.Values.ownerObjectId $ownerId 'ownerObjectId'
  foreach ($key in 'engineImageDigest', 'agentImageDigest') { if ($d.Values.Contains($key)) { throw "$key given without a value" } }
  $digest = 'sha256:' + ('0' * 64)
  $withApps = Get-CixDeploymentValues -Settings $s -SharedAccountName 'cosmos-shared-test' -Agents @{ engineAudience = 'api://x'; engineImageDigest = $digest; agentImageDigest = $digest }
  Expect $withApps.Values.engineImageDigest $digest 'engineImageDigest'
  Expect $withApps.Values.agentImageDigest $digest 'agentImageDigest'
  $template = [IO.File]::ReadAllText($d.Template)
  foreach ($name in @($d.Values.Keys) + @($withApps.Values.Keys)) { if ($template -notmatch "(?m)^param $name ") { throw "$name isn't a template parameter" } }
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

Check 'the protected (live-money) environment: its own group and database, no dashboard, its engine in Azure' {
  $s = Read-CixEnvironment -Name prod
  Expect $s.protected $true 'protected'
  Expect $s.resourceGroup 'rg-cix-live' 'resource group (owner decision 6)'
  Expect $s.cosmos.databaseName 'cix-live' 'database (owner decision 5)'
  Expect $s.deployDashboard $false 'deployDashboard'
  Expect $s.tags.purpose 'cix-live-trading' 'tags'
  if (@('platform', 'apps') -notcontains $s.agents.deploy) { throw "agents.deploy is $($s.agents.deploy)" }
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

# A delete lock on the shared account forbids deleting containers, and recreating them is provisioning's job: the reset
# deletes documents only.
Check 'reset-database.ps1 deletes documents only, never a container, the database or the account' {
  $script = Get-Content (Join-Path $azureTools 'reset-database.ps1') -Raw
  foreach ($pattern in @("'delete'", 'Invoke-CixDeployment', 'ResetSchema')) {
    if ($script.Contains($pattern)) { throw "reset-database.ps1 contains $pattern" }
  }
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

Check 'reset-database.ps1 State plans every document of the mode, and -IncludeRequests only that mode''s requests' {
  $heartbeat = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('o')
  $state = '{"Documents":[{"id":"engine-state-paper","type":"engine-state"},{"id":"engine-lease-paper","type":"engine-lease"},{"id":"agent-runs-paper","type":"agent-runs"},{"id":"paper-account","type":"paper-account"},{"id":"agent-runs-live","type":"agent-runs"},{"id":"spec-x-paper","type":"strategy-spec"}],"_count":6}'
  $requests = '{"Documents":[{"id":"r1","day":"2026-10-26"},{"id":"r2","day":"2026-10-26","tradingMode":"live"},{"id":"r3","day":"2026-10-25","tradingMode":"paper"}],"_count":3}'
  $cosmos = Start-FakeCosmos @(
    (Reply 200 "{`"id`":`"engine-status-paper`",`"updatedAt`":`"$heartbeat`"}"),
    (Reply 404),
    (Reply 200 $state),
    (Reply 200 $requests))
  $accounts = "[{`"name`":`"cosmos-shared-x`",`"documentEndpoint`":`"http://127.0.0.1:$($cosmos.Port)/`",`"tags`":{`"shared-role`":`"cosmos`"}}]"
  $dir = New-FakeAz -CosmosAccounts $accounts -GroupExists 'true' -GroupTags $devTags -GroupList $ownClaim
  $env:PATH = "$dir$([IO.Path]::PathSeparator)$savedPath"
  $seen = @()
  try { Assert-FakeAzFirst $dir; $output = & (Join-Path $azureTools 'reset-database.ps1') -Environment dev -Target State -IncludeRequests 6>&1 | Out-String }
  finally {
    $env:PATH = $savedPath
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    $seen = Stop-FakeCosmos $cosmos
  }
  foreach ($expected in @('containers: state, requests', 'would remove state/engine-state-paper', 'would remove state/engine-lease-paper',
      'would remove state/agent-runs-paper', 'would remove state/paper-account', 'keeps state/agent-runs-live', 'keeps state/spec-x-paper',
      'would remove 2 owner request(s)', 'keeps requests/r2 (live)', 'Dry run: nothing was changed')) {
    if (-not $output.Contains($expected)) { throw "missing '$expected' in: $output" }
  }
  if ($output.Contains('would remove journal')) { throw "planned journal entries for -Target State: $output" }
  if (@($seen | Where-Object { $_ -match '(?im)^POST /dbs/cix/colls/requests/docs HTTP/1\.1$' }).Count -ne 1) { throw 'never queried the requests container' }
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

# A copy of dev.json as environment 'citest' (its own group and database), with agents.deploy set here and the engine
# stopped, rather than whatever dev.json has; written to infra/environments/citest.json by the helpers below, and removed after.
function Get-CitestJson([string]$Deploy) {
  $json = (Get-Content (Join-CixPath $repo 'infra' 'environments' 'dev.json') -Raw).Replace('"dev"', '"citest"').Replace('rg-cix-dev', 'rg-cix-citest').Replace('"databaseName": "cix"', '"databaseName": "cix-citest"')
  if ($json -notmatch '"deploy": "(none|platform|apps)"') { throw 'dev.json has no agents.deploy to replace' }
  if ($json -notmatch '"startEngine": (true|false)') { throw 'dev.json has no agents.startEngine to replace' }
  $json = [regex]::Replace($json, '"startEngine": (true|false)', '"startEngine": false')
  return [regex]::Replace($json, '"deploy": "(none|platform|apps)"', "`"deploy`": `"$Deploy`"")
}
function Invoke-CitestProvision([hashtable]$Fake, [hashtable]$Arguments) {
  <# provision.ps1 for CIX alone (agents.deploy none), whatever dev.json deploys. #>
  $file = Join-CixPath $repo 'infra' 'environments' 'citest.json'
  [IO.File]::WriteAllText($file, (Get-CitestJson 'none'))
  try { return Invoke-WithFakeAz $Fake { & (Join-Path $azureTools 'provision.ps1') -Environment citest @Arguments } }
  finally { Remove-Item $file -Force }
}

Check 'provision.ps1 rerun: uses the existing shared account untouched and previews CIX (new database)' {
  $r = Invoke-CitestProvision @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $false } @{ WhatIfOnly = $true }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Using cosmos-shared-x; left unchanged', "==> CIX (rg-cix-citest, database 'cix-citest')", 'Preview only: nothing was changed.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

Check 'provision.ps1 full run (-Yes) with an existing shared account: deploys CIX and reports the dashboard' {
  $r = Invoke-CitestProvision @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $false } @{ Yes = $true; SkipEngineConfig = $true }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Using cosmos-shared-x; left unchanged', 'Cosmos DB account:  cosmos-shared-x (shared, in rg-shared)', 'Dashboard:          https://dash.example')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

Check 'provision.ps1 full run (-Yes) with no shared account: creates it, then CIX' {
  $r = Invoke-CitestProvision @{ GroupExists = 'false'; GroupList = '[]'; DatabaseExists = $false } @{ Yes = $true; SkipEngineConfig = $true }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Shared account: cosmos-shared-x', "==> CIX (rg-cix-citest, database 'cix-citest')", 'Dashboard:          https://dash.example')) {
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
  return Invoke-WithFakeAz @{ Extra = $extra } { & (Join-Path $azureTools 'setup-ci-deploy.ps1') -Environment dev -Repository 'owner/repo' -RepositoryIds '11/22' -Yes }
}

Check 'setup-ci-deploy.ps1 (-Yes) creates the identity, a main-only credential and the role, then prints the variables' {
  $r = Invoke-SetupCi @{ 'ad app list' = '[]'; 'ad app create' = '{"appId":"11111111-1111-1111-1111-111111111111"}'; 'ad sp list' = '[]'; 'ad sp create' = '{"id":"22222222-2222-2222-2222-222222222222"}' }
  if ($r.Error) { throw $r.Error }
  $creates = @($r.Calls -split "`n" | Where-Object { $_ -match '\bcreate\b' })
  if ($creates.Count -eq 0 -or $creates[-1] -notmatch 'federated-credential create') { throw "the trust for main must be created last; creates were: $($creates -join '; ')" }
  foreach ($expected in @('trusted only for repo:owner@11/repo@22:ref:refs/heads/main', 'Trusts repo:owner@11/repo@22:ref:refs/heads/main.', 'Assigned CIX dashboard deployer (dev) on stapp-cix-dev.',
      'AZURE_CLIENT_ID = 11111111-1111-1111-1111-111111111111', 'AZURE_TENANT_ID = 00000000-0000-0000-0000-0000000000aa', 'DASHBOARD_ENVIRONMENT = dev')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

$mainCredential = '[{"id":"new","subject":"repo:owner@11/repo@22:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]'

Check 'setup-ci-deploy.ps1 accepts a reused identity that is exactly as it should be, changing nothing' {
  $r = Invoke-SetupCi @{
    'ad app federated-credential' = $mainCredential
    'role definition'             = '[{"permissions":[{"actions":["Microsoft.Web/staticSites/read","Microsoft.Web/staticSites/listSecrets/action"],"notActions":[],"dataActions":[],"notDataActions":[]}],"assignableScopes":["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-dev"]}]'
    'role assignment'             = '[{"roleDefinitionName":"CIX dashboard deployer (dev)","scope":"/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft.Web/staticSites/stapp-cix-dev"}]'
  }
  if ($r.Error) { throw $r.Error }
  foreach ($expected in @('Already trusts repo:owner@11/repo@22:ref:refs/heads/main.', 'Exists, with exactly the expected actions.', 'Already assigned CIX dashboard deployer (dev) on stapp-cix-dev.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}
Check 'setup-ci-deploy.ps1 replaces the name-only trust an earlier version made with the immutable one' {
  $legacy = '[{"id":"old-trust","subject":"repo:owner/repo:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]'
  $r = Invoke-SetupCi @{ 'ad app federated-credential' = $legacy }
  if ($r.Error) { throw $r.Error }
  $calls = $r.Calls.Replace('"', '')
  foreach ($call in @('ad app federated-credential create --id 11111111-1111-1111-1111-111111111111', 'ad app federated-credential delete --id 11111111-1111-1111-1111-111111111111 --federated-credential-id old-trust')) {
    if (-not $calls.Contains($call)) { throw "no '$call' in: $calls" }
  }
  if ($calls.IndexOf('federated-credential delete') -lt $calls.IndexOf('federated-credential create')) { throw 'removed the old trust before adding the new one' }
  if (-not $r.Output.Contains('Removed the trust for repo:owner/repo:ref:refs/heads/main.')) { throw "no note in: $($r.Output)" }
}

Check 'setup-ci-deploy.ps1 keeps an immutable trust made by hand and removes the name-only one beside it' {
  $both = '[{"id":"new","subject":"repo:owner@11/repo@22:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]},{"id":"old-trust","subject":"repo:owner/repo:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]'
  $r = Invoke-SetupCi @{ 'ad app federated-credential' = $both }
  if ($r.Error) { throw $r.Error }
  $calls = $r.Calls.Replace('"', '')
  if ($calls -match 'federated-credential create') { throw "it created another trust: $calls" }
  $deletes = @($calls -split "`n" | Where-Object { $_ -match 'federated-credential delete' })
  if ($deletes.Count -ne 1 -or -not $deletes[0].Contains('--federated-credential-id old-trust')) { throw "expected one delete of old-trust, got: $($deletes -join '; ')" }
  if (-not $r.Output.Contains('Already trusts repo:owner@11/repo@22:ref:refs/heads/main.')) { throw "no note in: $($r.Output)" }
}

foreach ($case in @(
    @{ Name = 'a client secret'; Overrides = @{ 'ad app show' = '{"appId":"1","passwordCredentials":[{"keyId":"k"}],"keyCredentials":[]}' }; Says = 'passwordCredentials' },
    @{ Name = 'another owner'; Overrides = @{ 'ad app owner' = '[{"id":"me"},{"id":"someone-else"}]' }; Says = 'other owners (someone-else)' },
    @{ Name = 'a pull-request credential'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:owner/repo:pull_request","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]' }; Says = 'also trusts repo:owner/repo:pull_request' },
    @{ Name = 'a main credential for other repository IDs'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:owner@11/repo@99:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]' }; Says = 'also trusts repo:owner@11/repo@99:ref:refs/heads/main' },
    @{ Name = 'a main credential for another repository'; Overrides = @{ 'ad app federated-credential' = '[{"subject":"repo:other/repo:ref:refs/heads/main","issuer":"https://token.actions.githubusercontent.com","audiences":["api://AzureADTokenExchange"]}]' }; Says = 'also trusts repo:other/repo:ref:refs/heads/main' },
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
    if ($r.Calls -match '(?m)\b(create|delete)\b') { throw "it changed something before refusing: $(($r.Calls -split "`n" | Where-Object { $_ -match 'create|delete' }) -join '; ')" }
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
$agentsActions = '["Microsoft.App/containerApps/read","Microsoft.App/containerApps/revisions/deactivate/action","Microsoft.App/containerApps/revisions/read","Microsoft.App/containerApps/write","Microsoft.App/jobs/read","Microsoft.App/jobs/write","Microsoft.ContainerRegistry/registries/read"]'
# The role as the script made it before deployments stopped the engine's old revision (#80).
$agentsActionsBefore = '["Microsoft.App/containerApps/read","Microsoft.App/containerApps/revisions/read","Microsoft.App/containerApps/write","Microsoft.App/jobs/read","Microsoft.App/jobs/write","Microsoft.ContainerRegistry/registries/read"]'
function Invoke-SetupAgentsCi([hashtable]$Overrides, [string]$Deploy = 'platform', [bool]$Protected = $false) {
  $file = Join-CixPath $repo 'infra' 'environments' 'citest.json'
  $json = (Get-CitestJson $Deploy)
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
    return Invoke-WithFakeAz @{ GroupExists = 'true'; Extra = $extra } { & (Join-Path $azureTools 'setup-ci-deploy.ps1') -Environment citest -Repository 'owner/repo' -RepositoryIds '11/22' -For agents -Yes }
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
      'Assigned AcrPush on crcixcitest1.', 'run this again.')) {
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
  foreach ($expected in @('Already trusts repo:owner@11/repo@22:ref:refs/heads/main.', 'Exists, with exactly the expected actions.', 'Already assigned AcrPush on crcixcitest1.', 'Already assigned CIX agents deployer (citest) on crcixcitest1.')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
  if ($r.Calls -match '(?m)\bcreate\b') { throw "it changed something: $($r.Calls)" }
}

Check 'setup-ci-deploy.ps1 -For agents adds what its earlier role lacks (stopping revisions), in place' {
  $r = Invoke-SetupAgentsCi @{
    'ad app list'                 = '[{"appId":"66666666-6666-6666-6666-666666666666","displayName":"cix-agents-deploy-citest"}]'
    'ad sp list'                  = '[{"id":"77777777-7777-7777-7777-777777777777"}]'
    'ad app federated-credential' = $mainCredential
    'role definition'             = "[{`"permissions`":[{`"actions`":$agentsActionsBefore,`"notActions`":[],`"dataActions`":[],`"notDataActions`":[]}],`"assignableScopes`":[`"$agentsGroupId`"]}]"
    'role assignment'             = "[{`"roleDefinitionName`":`"AcrPush`",`"scope`":`"$agentsRegistryId`"},{`"roleDefinitionName`":`"CIX agents deployer (citest)`",`"scope`":`"$agentsRegistryId`"}]"
  }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  if (-not $r.Calls.Contains('role definition update')) { throw "the role wasn't updated: $($r.Calls)" }
  if (-not $r.Output.Contains('Added Microsoft.App/containerApps/revisions/deactivate/action.')) { throw "missing what was added in: $($r.Output)" }
  if ($r.Calls -match '(?m)\bcreate\b') { throw "it created something: $($r.Calls)" }
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
  $r = Invoke-CitestProvision @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = '[]'; DatabaseExists = $true } @{ WhatIfOnly = $true }
  if (-not $r.Error -or -not $r.Error.Contains("already exists in cosmos-shared-x but no CIX environment claims it")) { throw "got: $($r.Error)" }
}

$citestClaim = '{"name":"rg-cix-citest","managedBy":null,"tags":{"cix-database":"cix-citest","cix-cosmos-account":"cosmos-shared-x"}}'
function Get-ManagedClaim([string]$Group) {
  return "{`"name`":`"ME_cae-cix-citest_rg-cix-citest_polandcentral`",`"managedBy`":`"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/$Group/providers/Microsoft.App/managedEnvironments/cae-cix-citest`",`"tags`":{`"cix-database`":`"cix-citest`",`"cix-cosmos-account`":`"cosmos-shared-x`"}}"
}
Check "provision.ps1 accepts the claim its own Container Apps environment's managed group copies" {
  $r = Invoke-CitestProvision @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = "[$citestClaim,$(Get-ManagedClaim 'RG-CIX-CITEST')]"; DatabaseExists = $true } @{ WhatIfOnly = $true }
  if ($r.Error) { throw $r.Error }
  if (-not $r.Output.Contains('Preview only: nothing was changed.')) { throw "no preview in: $($r.Output)" }
}
Check "provision.ps1 refuses a claim from a group another environment's Container Apps environment manages" {
  $r = Invoke-CitestProvision @{ GroupExists = 'true'; CosmosAccounts = $sharedAccount; GroupList = "[$citestClaim,$(Get-ManagedClaim 'rg-cix-prod')]"; DatabaseExists = $true } @{ WhatIfOnly = $true }
  if (-not $r.Error -or -not $r.Error.Contains('is claimed by resource group(s) ME_cae-cix-citest_rg-cix-citest_polandcentral')) { throw "got: $($r.Error)" }
}

Check 'provision.ps1 refuses to create the shared account over an untagged one' {
  $r = Invoke-WithFakeAz @{ GroupExists = 'true'; CosmosAccounts = '[{"name":"someone-elses","tags":{}}]' } {
    & (Join-Path $azureTools 'provision.ps1') -Environment dev -WhatIfOnly
  }
  if (-not $r.Error -or -not $r.Error.Contains('none tagged shared-role=cosmos')) { throw "got: $($r.Error)" }
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
  'keyvault secret list'  = '[{"name":"claude-token","id":"https://kvcixcitest1.vault.azure.net/secrets/claude-token"}]'
  'ad app list'           = '[]'
  'ad app create'         = '44444444-4444-4444-4444-444444444444'
  'ad app show'           = '{"appRoles":[]}'
  'ad sp show'            = '{"id":"66666666-6666-6666-6666-666666666666","appRoleAssignmentRequired":false}'
  'rest --method get'     = '{"value":[]}'
  'ad sp owner'           = '[]'
  'ad sp list'            = '[]'
  'acr repository list'   = '["cix-engine","cix-trader"]'
  'acr repository show-tags' = '["abc123","def456"]'
  'acr repository show'   = "sha256:$('5' * 64)"
  'resource list'         = '[]'
  'consumption budget'    = '[{"name":"budget-cix-citest"}]'
  'deployment sub create' = '{"properties":{"outputs":{"cosmosAccountName":{"value":"cosmos-shared-x"},"cosmosEndpoint":{"value":"https://cosmos-shared-x.documents.azure.com:443/"},"staticWebAppName":{"value":"stapp-cix-citest"},"dashboardUrl":{"value":"https://dash.example"},"registryServer":{"value":"crcixcitest1.azurecr.io"},"keyVaultName":{"value":"kvcixcitest1"},"engineAppName":{"value":"ca-cix-citest-engine"},"agentJobNames":{"value":["caj-cix-citest-trader","caj-cix-citest-requests","caj-cix-citest-analyst"]},"agentJobPrincipalIds":{"value":["aaaaaaaa-0000-0000-0000-000000000001","aaaaaaaa-0000-0000-0000-000000000002","aaaaaaaa-0000-0000-0000-000000000003"]}}}}'
}
function Invoke-AgentsProvision([hashtable]$Overrides, [hashtable]$Arguments, [string]$Deploy = 'apps', [bool]$Protected = $false) {
  $file = Join-CixPath $repo 'infra' 'environments' 'citest.json'
  $json = (Get-CitestJson $Deploy)
  if ($Protected) { $json = $json.Replace('"protected": false', '"protected": true') }
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

$digest5 = "sha256:$('5' * 64)"
Check 'provision.ps1 agents apps (-Yes): creates the engine''s Entra app, deploys both images by digest and reports the apps' {
  $r = Invoke-AgentsProvision @{} @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($expected in @('Engine and agents (apps, in polandcentral)', 'Images: crcixcitest1, tag abc123. Engine: stopped', "cix-engine@$digest5", "cix-trader@$digest5", 'Created the Entra app registration cix-engine-citest',
      'Engine app: ca-cix-citest-engine; jobs: caj-cix-citest-trader, caj-cix-citest-requests, caj-cix-citest-analyst')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
  foreach ($call in @('ad app update --id 44444444-4444-4444-4444-444444444444 --identifier-uris api://44444444-4444-4444-4444-444444444444', 'ad sp create --id 44444444-4444-4444-4444-444444444444',
      "ad app owner add --id 44444444-4444-4444-4444-444444444444 --owner-object-id $ownerId",
      'acr repository show-tags --name crcixcitest1 --repository cix-engine', 'acr repository show-tags --name crcixcitest1 --repository cix-trader',
      'acr repository show --name crcixcitest1 --image cix-engine:abc123 --query digest', 'acr repository show --name crcixcitest1 --image cix-trader:abc123 --query digest')) {
    if (-not $r.Calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  if ($r.Calls.Contains('keyvault secret set')) { throw 'uploaded claude-token although the vault has it' }
  if ($r.Calls -match 'keyvault secret show') { throw 'read a secret value' }
  if ($r.Calls.Contains('keyvault secret delete') -or $r.Calls.Contains('role assignment delete')) { throw "deleted something: $($r.Calls)" }
  # Only the three job identities may get a token for the engine.
  foreach ($call in @('ad app update --id 44444444-4444-4444-4444-444444444444 --app-roles @', 'ad sp update --id 66666666-6666-6666-6666-666666666666 --set appRoleAssignmentRequired=true')) {
    if (-not $r.Calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  Expect ([regex]::Matches($r.Calls, 'rest --method post --url https://graph\.microsoft\.com/v1\.0/servicePrincipals/66666666-6666-6666-6666-666666666666/appRoleAssignedTo')).Count 3 'app role assignments'
  if ($r.Calls.IndexOf('ad app update --id 44444444-4444-4444-4444-444444444444 --app-roles') -lt $r.Calls.IndexOf('deployment sub create')) { throw 'set up the app role before the deployment made the jobs' }
}

Check 'provision.ps1 agents apps leaves the engine''s app role alone when it is set up, and refuses anyone else assigned to it' {
  $role = '{"appRoles":[{"allowedMemberTypes":["Application"],"id":"77777777-7777-7777-7777-777777777777","isEnabled":true,"value":"CIX.Agent"}]}'
  $sp = '{"id":"66666666-6666-6666-6666-666666666666","appRoleAssignmentRequired":true}'
  $mine = '{"value":[' + ((1..3 | ForEach-Object { "{`"principalId`":`"aaaaaaaa-0000-0000-0000-00000000000$_`",`"appRoleId`":`"77777777-7777-7777-7777-777777777777`",`"principalDisplayName`":`"job$_`"}" }) -join ',') + ']}'
  $r = Invoke-AgentsProvision @{ 'ad app show' = $role; 'ad sp show' = $sp; 'rest --method get' = $mine } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($never in @('--app-roles', 'ad sp update', 'rest --method post --url https://graph')) { if ($r.Calls.Contains($never)) { throw "ran '$never' although it was set up" } }
  $stranger = $mine.Replace('aaaaaaaa-0000-0000-0000-000000000003', '99999999-9999-9999-9999-999999999999').Replace('job3', 'someone')
  $r = Invoke-AgentsProvision @{ 'ad app show' = $role; 'ad sp show' = $sp; 'rest --method get' = $stranger } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
  if (-not $r.Error -or -not $r.Error.Contains('someone (99999999-9999-9999-9999-999999999999)')) { throw "got: $($r.Error)" }
  if ($r.Calls.Contains('rest --method post --url https://graph')) { throw 'assigned the role anyway' }
  # Refused too, before any change: another owner of the service principal, more than one page of assignments, and any
  # assignment from before the role existed (a job identity's default access included).
  foreach ($case in @(
      @{ Overrides = @{ 'ad sp owner' = "[{`"id`":`"$ownerId`"},{`"id`":`"88888888-8888-8888-8888-888888888888`"}]" }; Says = 'other owners (88888888-8888-8888-8888-888888888888)' },
      @{ Overrides = @{ 'rest --method get' = '{"value":[],"@odata.nextLink":"https://graph.microsoft.com/next"}' }; Says = 'more assignments than one page lists' },
      @{ Overrides = @{ 'rest --method get' = '{"value":[{"principalId":"aaaaaaaa-0000-0000-0000-000000000001","appRoleId":"00000000-0000-0000-0000-000000000000","principalDisplayName":"job1"}]}' }; Says = 'job1 (aaaaaaaa-0000-0000-0000-000000000001)' })) {
    $r = Invoke-AgentsProvision $case.Overrides @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    foreach ($never in @('--app-roles', 'ad sp update', 'rest --method post --url https://graph')) { if ($r.Calls.Contains($never)) { throw "ran '$never' before refusing ($($case.Says))" } }
  }
}

Check 'provision.ps1 agents deletes the cosmos-key an earlier template left in Key Vault, and the engine''s access to it' {
  $vaultId = '/subscriptions/x/resourceGroups/rg-cix-citest/providers/Microsoft.KeyVault/vaults/kvcixcitest1'
  $r = Invoke-AgentsProvision @{ 'keyvault secret list' = '[{"name":"cosmos-key"},{"name":"claude-token"}]'; 'keyvault show' = $vaultId } @{ Yes = $true; SkipEngineConfig = $true } 'platform'
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($call in @("role assignment delete --scope $vaultId/secrets/cosmos-key --yes", 'keyvault secret delete --vault-name kvcixcitest1 --name cosmos-key')) {
    if (-not $r.Calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  if ($r.Calls.IndexOf('keyvault secret delete') -lt $r.Calls.IndexOf('deployment sub create')) { throw 'deleted it before the deployment' }
  if (-not $r.Output.Contains('still holds cosmos-key')) { throw "no warning in: $($r.Output)" }
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

Check 'provision.ps1 agents apps keeps the images the engine and the jobs run, by digest, and sends budget alerts where -BudgetEmail says' {
  $apps = '/subscriptions/x/resourceGroups/rg-cix-citest/providers/Microsoft.App'
  $agentDigest = "sha256:$('7' * 64)"
  $running = @{
    # Also an app of another environment in the group, and a job whose name only ends the same: neither is read.
    'resource list' = "[{`"id`":`"$apps/containerApps/ca-cix-citest-engine`",`"name`":`"ca-cix-citest-engine`",`"type`":`"Microsoft.App/containerApps`",`"tags`":{`"cix-environment`":`"citest`"}},{`"id`":`"$apps/containerApps/ca-cix-other-engine`",`"name`":`"ca-cix-other-engine`",`"type`":`"Microsoft.App/containerApps`",`"tags`":{`"cix-environment`":`"other`"}},{`"id`":`"$apps/jobs/caj-cix-citest-trader`",`"name`":`"caj-cix-citest-trader`",`"type`":`"Microsoft.App/jobs`",`"tags`":{`"cix-environment`":`"citest`"}},{`"id`":`"$apps/jobs/old-caj-cix-citest-trader`",`"name`":`"old-caj-cix-citest-trader`",`"type`":`"Microsoft.App/jobs`",`"tags`":{`"cix-environment`":`"citest`"}}]"
    # The engine as deployed before digests (by tag), the trader job by digest.
    'resource show' = "[{`"type`":`"Microsoft.App/containerApps`",`"properties`":{`"template`":{`"containers`":[{`"image`":`"crcixcitest1.azurecr.io/cix-engine:def456`"}]}}},{`"type`":`"Microsoft.App/jobs`",`"properties`":{`"template`":{`"containers`":[{`"image`":`"crcixcitest1.azurecr.io/cix-trader@$agentDigest`"}]}}}]"
  }
  $r = Invoke-AgentsProvision $running @{ Yes = $true; SkipEngineConfig = $true; BudgetEmail = 'alerts@example.com' }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  foreach ($expected in @('Images: crcixcitest1, the ones running now', 'alerts to alerts@example.com.')) { if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" } }
  foreach ($call in @("resource show --ids $apps/containerApps/ca-cix-citest-engine $apps/jobs/caj-cix-citest-trader -o json",
      'acr repository show --name crcixcitest1 --image cix-engine:def456 --query digest', "acr repository show --name crcixcitest1 --image cix-trader@$agentDigest --query digest")) {
    if (-not $r.Calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  if ($r.Calls.Contains('show-tags')) { throw "looked for a tag although none was given: $($r.Calls)" }
  if ($r.Calls -cmatch '(^|\s)containerapp ') { throw "used the containerapp extension: $($r.Calls)" }
}

# A protected environment (ADR-0017): the citest copy marked protected, its images promoted from dev's registry.
$devApps = '/subscriptions/x/resourceGroups/rg-cix-dev/providers/Microsoft.App'
function Get-PromoteFake([string]$RunningDigest = "sha256:$('5' * 64)") {
  <# Both registries, and dev's engine and trader job running $RunningDigest (the fake gives every image the 5s digest). #>
  return @{
    'acr list'      = '[{"name":"crcixcitest1","tags":{"cix-environment":"citest"}},{"name":"crcixdev1","tags":{"cix-environment":"dev"}}]'
    'resource list' = "[{`"id`":`"$devApps/containerApps/ca-cix-dev-engine`",`"name`":`"ca-cix-dev-engine`",`"type`":`"Microsoft.App/containerApps`",`"tags`":{`"cix-environment`":`"dev`"}},{`"id`":`"$devApps/jobs/caj-cix-dev-trader`",`"name`":`"caj-cix-dev-trader`",`"type`":`"Microsoft.App/jobs`",`"tags`":{`"cix-environment`":`"dev`"}}]"
    'resource show' = "[{`"type`":`"Microsoft.App/containerApps`",`"properties`":{`"template`":{`"containers`":[{`"image`":`"crcixdev1.azurecr.io/cix-engine@$RunningDigest`"}]}}},{`"type`":`"Microsoft.App/jobs`",`"properties`":{`"template`":{`"containers`":[{`"image`":`"crcixdev1.azurecr.io/cix-trader@$RunningDigest`"}]}}}]"
  }
}
$promoteFake = Get-PromoteFake
function New-FakeKeyFiles {
  <# Two files standing in for the exchange keys, with content built at run time: not a secret. #>
  $dir = Join-Path ([IO.Path]::GetTempPath()) "cix-keys-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir | Out-Null
  $marker = @('fake', 'exchange', 'key', '9c1d') -join '-'
  [IO.File]::WriteAllText((Join-Path $dir 'api.txt'), $marker)
  [IO.File]::WriteAllText((Join-Path $dir 'private.pem'), $marker)
  [IO.File]::WriteAllText((Join-Path $dir 'bom.txt'), $marker, (New-Object Text.UTF8Encoding $true))
  return @{ Dir = $dir; Api = (Join-Path $dir 'api.txt'); Pem = (Join-Path $dir 'private.pem'); Bom = (Join-Path $dir 'bom.txt'); Marker = $marker }
}

Check 'provision.ps1 protected apps (-Yes): checks access, promotes dev''s images by digest, uploads the keys unseen, then deploys' {
  $keys = New-FakeKeyFiles
  try {
    $r = Invoke-AgentsProvision $promoteFake @{ Yes = $true; ImageTag = 'abc123'; PromoteFrom = 'dev'; ExchangeApiKeyFile = $keys.Api; ExchangePrivateKeyFile = $keys.Pem } 'apps' $true
  }
  finally { Remove-Item $keys.Dir -Recurse -Force }
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  $calls = $r.Calls.Replace('"', '') # Windows logs arguments with spaces in quotes
  foreach ($call in @('role assignment list --all', 'role assignment list --scope /subscriptions/00000000-0000-0000-0000-000000000001 --include-inherited',
      'acr repository show-tags --name crcixdev1 --repository cix-engine',
      "acr import --name crcixcitest1 --source crcixdev1.azurecr.io/cix-engine@$digest5 --image cix-engine:abc123 --force",
      "acr import --name crcixcitest1 --source crcixdev1.azurecr.io/cix-trader@$digest5 --image cix-trader:abc123 --force",
      "keyvault secret set --vault-name kvcixcitest1 --name revolutx-api-key --file $($keys.Api)",
      "keyvault secret set --vault-name kvcixcitest1 --name revolutx-private-key --file $($keys.Pem)",
      '--content-type Exchange key')) {
    if (-not $calls.Contains($call)) { throw "no '$call' in: $($r.Calls)" }
  }
  if ($calls.IndexOf('acr import') -gt $calls.IndexOf('acr repository show --name crcixcitest1')) { throw 'read the digests to deploy before copying them' }
  if ($calls.IndexOf('keyvault secret set') -gt $calls.IndexOf('deployment sub create')) { throw 'uploaded the keys after the deployment that mounts them' }
  if ($r.Output.Contains($keys.Marker) -or $r.Calls.Contains($keys.Marker)) { throw 'showed a key' }
  if ($calls.Contains('cosmosdb keys')) { throw "read the account key for this PC's paper engine" }
  foreach ($expected in @('This environment is protected (real money)', 'The engine stands by')) {
    if (-not $r.Output.Contains($expected)) { throw "missing '$expected' in: $($r.Output)" }
  }
}

$citestGroup = '/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-citest'
Check 'provision.ps1 protected: anyone but the owner and its own identities with a role on the group, in it or above it, stops -Yes' {
  $row = { param($Role, $Scope, $Id, $Name, $Type) "{`"roleDefinitionName`":`"$Role`",`"scope`":`"$Scope`",`"principalId`":`"$Id`",`"principalName`":`"$Name`",`"principalType`":`"$Type`"}" }
  $roles = '[' + ((
      (& $row 'Contributor' '/subscriptions/00000000-0000-0000-0000-000000000001' 'p1' 'cix-agents-deploy-dev' 'ServicePrincipal'),
      (& $row 'AcrPush' "$citestGroup/providers/Microsoft.ContainerRegistry/registries/crcixcitest1" 'p2' 'cix-agents-deploy-citest' 'ServicePrincipal'),
      (& $row 'Reader' '/providers/Microsoft.Management/managementGroups/mg1' 'p3' 'ops' 'Group'),
      (& $row 'Key Vault Secrets User' "$citestGroup/providers/Microsoft.KeyVault/vaults/kvcixcitest1" 'e1' 'e1' 'ServicePrincipal'),
      (& $row 'Owner' '/subscriptions/00000000-0000-0000-0000-000000000001' $ownerId 'owner@example.com' 'User'),
      (& $row 'Contributor' '/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-dev' 'p4' 'dev-only' 'ServicePrincipal'),
      (& $row 'Contributor' '/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-cix-citestother' 'p5' 'look-alike' 'ServicePrincipal')) -join ',') + ']'
  $identities = '[{"name":"id-cix-citest-engine","principalId":"e1"},{"name":"id-someone-else","principalId":"p2"}]'
  $r = Invoke-AgentsProvision @{ 'role assignment list' = $roles; 'identity list' = $identities } @{ Yes = $true; WhatIfOnly = $true } 'none' $true
  foreach ($expected in @('Contributor on /subscriptions/00000000-0000-0000-0000-000000000001: cix-agents-deploy-dev (ServicePrincipal)',
      "AcrPush on $citestGroup/providers/Microsoft.ContainerRegistry/registries/crcixcitest1: cix-agents-deploy-citest (ServicePrincipal)",
      'Reader on /providers/Microsoft.Management/managementGroups/mg1: ops (Group)', 'run without -Yes')) {
    if (-not $r.Error -or -not $r.Error.Contains($expected)) { throw "missing '$expected' in: $($r.Error)" }
  }
  foreach ($never in @('Key Vault Secrets User', 'owner@example.com', 'dev-only', 'look-alike')) {
    if ($r.Error.Contains($never)) { throw "listed '$never': $($r.Error)" }
  }
  if ($r.Calls -match '(?m)\bdeployment\b') { throw 'went on to the preview' }

  $r = Invoke-AgentsProvision @{ 'identity list' = $identities } @{ Yes = $true; WhatIfOnly = $true } 'none' $true
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  if (-not $r.Output.Contains('Preview only: nothing was changed.')) { throw "no preview in: $($r.Output)" }
}

foreach ($case in @(
    @{ Name = 'an exchange key file for a paper environment'; Protected = $false; Deploy = 'apps'; Arguments = @{ ExchangeApiKeyFile = 'KEYFILE' }; Says = 'are for a protected environment only' },
    @{ Name = '-PromoteFrom for a paper environment'; Protected = $false; Deploy = 'apps'; Arguments = @{ PromoteFrom = 'dev'; ImageTag = 'abc123' }; Says = 'are for a protected environment only' },
    @{ Name = '-PromoteFrom without -ImageTag'; Protected = $true; Deploy = 'apps'; Arguments = @{ PromoteFrom = 'dev' }; Says = '-PromoteFrom needs -ImageTag' },
    @{ Name = 'key files without the apps'; Protected = $true; Deploy = 'platform'; Arguments = @{ ExchangeApiKeyFile = 'KEYFILE' }; Says = 'go with agents.deploy apps' },
    @{ Name = 'a key file with a byte-order mark'; Protected = $true; Deploy = 'apps'; Arguments = @{ ExchangeApiKeyFile = 'BOMFILE' }; Says = 'starts with a UTF-8 byte-order mark' },
    @{ Name = 'a key file that is not there'; Protected = $true; Deploy = 'apps'; Arguments = @{ ExchangePrivateKeyFile = 'no-such-key.pem' }; Says = 'no-such-key.pem: no such file' },
    @{ Name = 'no exchange keys given or in Key Vault'; Protected = $true; Deploy = 'apps'; Arguments = @{ ImageTag = 'abc123'; PromoteFrom = 'dev' }; Says = 'has no revolutx-api-key' },
    @{ Name = 'promoting from a protected environment'; Protected = $true; Deploy = 'apps'; Arguments = @{ ImageTag = 'abc123'; PromoteFrom = 'citest'; ExchangeApiKeyFile = 'KEYFILE'; ExchangePrivateKeyFile = 'KEYFILE' }; Says = '-PromoteFrom citest is protected' },
    @{ Name = 'promoting images paper doesn''t run'; Protected = $true; Deploy = 'apps'; Fake = @{ 'acr list' = $promoteFake['acr list'] }; Arguments = @{ ImageTag = 'abc123'; PromoteFrom = 'dev'; ExchangeApiKeyFile = 'KEYFILE'; ExchangePrivateKeyFile = 'KEYFILE' }; Says = "dev doesn't run the images tagged abc123 now: promote the images paper runs" },
    @{ Name = 'a protected environment without secrets/claude_token'; Protected = $true; Deploy = 'apps'; Fake = @{ 'acr list' = $promoteFake['acr list']; 'keyvault secret list' = '[]' }; Arguments = @{ ImageTag = 'abc123'; PromoteFrom = 'dev'; ExchangeApiKeyFile = 'KEYFILE'; ExchangePrivateKeyFile = 'KEYFILE' }; Says = 'has no claude-token'; NoLocalToken = $true })) {
  Check "provision.ps1 refuses $($case.Name), before any change" {
    $keys = New-FakeKeyFiles
    $arguments = @{ Yes = $true }
    foreach ($k in $case.Arguments.Keys) { $arguments[$k] = switch ($case.Arguments[$k]) { 'KEYFILE' { $keys.Api } 'BOMFILE' { $keys.Bom } default { $_ } } }
    $fakeAnswers = if ($case['Fake']) { $case.Fake } else { $promoteFake }
    # Skipped where a developer has secrets/claude_token (it is never in the repo), as below.
    if ($case['NoLocalToken'] -and (Test-Path (Join-CixPath $repo 'secrets' 'claude_token'))) { Remove-Item $keys.Dir -Recurse -Force; return }
    try { $r = Invoke-AgentsProvision $fakeAnswers $arguments $case.Deploy $case.Protected }
    finally { Remove-Item $keys.Dir -Recurse -Force }
    if (-not $r.Error -or -not $r.Error.Contains($case.Says)) { throw "got: $($r.Error)" }
    if ($r.Calls -match '(?m)\b(create|import|secret set)\b') { throw "it changed something before refusing: $($r.Calls)" }
  }
}

Check 'provision.ps1 agents platform: no budget without an address that gets mail, and nothing for the apps' {
  $guest = "{`"id`":`"$ownerId`",`"mail`":null,`"userPrincipalName`":`"owner_gmail.com#EXT#@cixtest.onmicrosoft.com`"}"
  $r = Invoke-AgentsProvision @{ 'ad signed-in-user' = $guest } @{ Yes = $true; SkipEngineConfig = $true } 'platform'
  if ($r.Error) { throw "$($r.Error) -- $($r.Output)" }
  if (-not $r.Output.Contains('no budget is created. Pass -BudgetEmail')) { throw "no warning in: $($r.Output)" }
  foreach ($never in @('ad app', 'acr ')) { if ($r.Calls.Contains($never)) { throw "platform ran '$never'" } }
  # Only the names in Key Vault are listed (for a cosmos-key an earlier template left): no secret is set, read or deleted.
  $vaultCalls = @(($r.Calls -split "`n") | Where-Object { $_ -match 'keyvault' -and $_ -notmatch '^\s*keyvault secret list ' })
  if ($vaultCalls.Count -gt 0) { throw "platform ran $($vaultCalls -join '; ')" }
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
  # The app role is set up on it as on a new one (--app-roles); its identifier URI is left alone.
  foreach ($never in @('ad app create', 'ad app update --id 55555555-5555-5555-5555-555555555555 --identifier-uris', 'ad sp create', 'ad app owner add')) { if ($r.Calls.Contains($never)) { throw "ran '$never'" } }
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
    @{ Overrides = @{ 'ad app list' = $engineApp; 'ad app owner' = '[]' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = "has no owner. Refusing to use it: make yourself its owner (az ad app owner add --id 55555555-5555-5555-5555-555555555555 --owner-object-id $ownerId)" },
    @{ Overrides = @{ 'ad signed-in-user' = '{"userPrincipalName":"ci@example.com"}' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'Sign in to Azure as a user' },
    @{ Overrides = @{ 'acr repository list' = '[]' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'has no cix-engine images yet' },
    @{ Overrides = @{ 'acr repository show' = 'not-a-digest' }; Arguments = @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }; Says = 'has no image cix-engine:abc123' })) {
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
    $r = Invoke-AgentsProvision @{ 'keyvault secret list' = '[{"name":"other-secret"}]' } @{ Yes = $true; SkipEngineConfig = $true; ImageTag = 'abc123' }
    if (-not $r.Error -or -not $r.Error.Contains('has no claude-token. Run tools/setup-secrets.ps1')) { throw "got: $($r.Error)" }
    if ($r.Calls.Contains('ad app create') -or $r.Calls.Contains('deployment sub create')) { throw 'went on anyway' }
  }
}

Write-Host ''
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.'
