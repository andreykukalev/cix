#Requires -Version 5.1
<#
.SYNOPSIS
  Resets CIX's Azure Cosmos DB data for testing (for example after releasing a new version): bot state, journal, or both,
  and the owner's requests if asked.
  A dry run unless you pass -Apply, and then only after you type the confirmation it asks for.

.DESCRIPTION
  What it can remove, in the environment's database (cosmos.databaseName) in the shared Cosmos DB account (found by its
  shared-role=cosmos tag in the shared resource group, never by name alone). Other projects' databases are never touched:
    -Target State    the engine's state documents for -Mode (paper by default): every <name>-<mode> document (engine-state,
                     engine-status, engine-lease, agent-runs, manual-requests, scanner-filters, ...), and for paper the
                     paper-account. With -IncludeSpecArchive, also the archived strategy specs (spec-*).
    -Target Journal  every journal entry (optionally only days before -JournalBefore).
    -Target All      both.
  With -IncludeRequests, also the owner's requests for -Mode in the requests container (kept otherwise).
  It deletes documents only: containers, the database and the shared account are never deleted, so it works under a
  delete lock on the account.

  Safeguards:
    - The environment must have a settings file in infra/environments, and must not be marked "protected" there (live money).
    - CIX's resource group must be tagged cix-environment=<Environment>, cix-protected=false, and cix-database and
      cix-cosmos-account naming this database and account (all set by infra/main.bicep); no other group may claim the
      database; and it must exist in the single shared account.
    - It refuses while the engine is running (its status heartbeat is under 3 minutes old) unless -IgnoreRunningEngine.

  A protected (real-money) environment is reset only with -LiveWhenFlat, and only its live state, journal and requests:
    - its resource group must be tagged cix-protected=true, and -IgnoreRunningEngine and -IncludeSpecArchive are refused;
    - the account must be flat: the engine's last status lists no holding worth -DustQuote or more (1 in the quote
      currency by default; a smaller residual can't be sold anyway) and its state has no order in flight. A reset forgets
      every position's entry price, stop and target, so it is refused while there is anything to protect;
    - you type a longer confirmation that names real money.
  After it, the live engine starts halted until you press Resume on the dashboard (docs/live-trading.md), with a fresh
  peak equity, and adopts any residual it finds on the account without a stop.
    - It lists what it would remove first; nothing is deleted without -Apply and the typed confirmation.
    - It is never run by deployment, the engine, or the dashboard.

  Local emulator: reset it with `docker compose down -v` instead (that removes the emulator's volume).

.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target All                 # dry run: lists what would go
.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target State -Apply        # paper state only
.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target All -Mode all -IncludeSpecArchive -IncludeRequests -Apply   # a fresh start
.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment prod -LiveWhenFlat -Target All -IncludeRequests -Apply   # live, account all cash
.EXAMPLE
  .\tools\azure\reset-paper-and-live.ps1 -Target All -IncludeRequests -Apply   # both: dev's paper, then prod's live
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [Parameter(Mandatory)][ValidateSet('State', 'Journal', 'All')][string]$Target,
  [ValidateSet('paper', 'live', 'all')][string]$Mode = 'paper',
  [switch]$IncludeSpecArchive,
  [switch]$IncludeRequests,
  [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$JournalBefore,
  [switch]$IgnoreRunningEngine,
  [switch]$LiveWhenFlat,
  [ValidateRange(0, 100)][decimal]$DustQuote = 1,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

# @(): a switch with one result returns it alone, and += would then join strings instead of adding a container.
$containers = @(switch ($Target) { 'State' { 'state' } 'Journal' { 'journal' } 'All' { 'state', 'journal' } })
if ($IncludeRequests) { $containers += 'requests' }

Write-CixStep "Reset in environment '$Environment' ($(if ($Apply) { 'APPLY' } else { 'dry run' }))"
$settings = Read-CixEnvironment -Name $Environment
if ($settings.protected -and -not $LiveWhenFlat) {
  throw "Environment '$Environment' is marked protected in $($settings.path) (real money). Only -LiveWhenFlat resets it, and only while the account holds nothing but cash."
}
if ($LiveWhenFlat) {
  if (-not $settings.protected) { throw "-LiveWhenFlat is for a protected environment; '$Environment' isn't one." }
  if (-not $PSBoundParameters.ContainsKey('Mode')) { $Mode = 'live' }
  if ($Mode -ne 'live') { throw "A protected environment resets only its live state: pass -Mode live (or leave -Mode out)." }
  if ($IgnoreRunningEngine) { throw "-IgnoreRunningEngine is refused for a protected environment: stop its engine first." }
  if ($IncludeSpecArchive) { throw "-IncludeSpecArchive is refused for a protected environment: its archive holds the specs you reviewed for live." }
}
$expectProtected = if ($LiveWhenFlat) { 'true' } else { 'false' }

Assert-CixAzureCli
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
# CIX's resource group tags must agree: a settings file edited locally can't unprotect a live environment.
$tags = Get-CixEnvironmentTags -Settings $settings
$say = { param($name) if ($tags -and $tags.PSObject.Properties[$name]) { $tags.$name } else { 'nothing' } }
if (-not $tags -or -not $tags.PSObject.Properties['cix-environment'] -or $tags.'cix-environment' -ne $Environment) {
  throw "Resource group $($settings.resourceGroup) isn't tagged cix-environment=$Environment (it says '$(& $say 'cix-environment')'). Refusing."
}
if (-not $tags.PSObject.Properties['cix-protected'] -or $tags.'cix-protected' -ne $expectProtected) {
  throw "Resource group $($settings.resourceGroup) isn't tagged cix-protected=$expectProtected (it says '$(& $say 'cix-protected')'). Refusing."
}
$cosmos = Get-CixCosmosAccount -Settings $settings
$database = $settings.cosmos.databaseName
# The database must be this environment's alone: claimed by its group's tags and by no other group.
Assert-CixDatabaseClaim -Settings $settings -AccountName $cosmos.name -RequireOwn
if (-not (Test-CixDatabase -Settings $settings -AccountName $cosmos.name)) { throw "Database '$database' doesn't exist in $($cosmos.name)." }
$endpoint = $cosmos.documentEndpoint
Write-CixInfo "Account:  $($cosmos.name) (shared) in $($settings.shared.resourceGroup) (subscription $($account.name))"
Write-CixInfo "Database: $database; containers: $($containers -join ', ')"

# Keys are needed for the data plane: read-only to plan, read-write only when applying. Never printed.
$key = Get-CixCosmosKey -Settings $settings -AccountName $cosmos.name -ReadOnly:(-not $Apply)

Write-CixStep 'Is the engine running?'
$modes = if ($Mode -eq 'all') { @('paper', 'live') } else { @($Mode) }
$running = @()
foreach ($m in @('paper', 'live')) {
  $status = Get-CixCosmosItem -Endpoint $endpoint -Key $key -Database $database -Container 'state' -Id "engine-status-$m" -PartitionKey "engine-status-$m"
  if ($null -ne $status -and $status.PSObject.Properties['updatedAt'] -and $status.updatedAt) {
    # ConvertFrom-Json may already have turned the timestamp into a DateTime.
    $updated = $status.updatedAt
    $at = if ($updated -is [datetime]) { [DateTimeOffset]$updated.ToUniversalTime() } else { [DateTimeOffset]::Parse([string]$updated, [Globalization.CultureInfo]::InvariantCulture) }
    $age = [DateTimeOffset]::UtcNow - $at
    Write-CixInfo ("{0}: last heartbeat {1:N0} minutes ago" -f $m, $age.TotalMinutes)
    if ($age -lt [TimeSpan]::FromMinutes(3)) { $running += $m }
  }
  else { Write-CixInfo "${m}: no status record" }
}
if ($running.Count -gt 0 -and -not $IgnoreRunningEngine) {
  throw "The engine ($($running -join ', ')) wrote its status less than 3 minutes ago: stop it first (locally: docker compose stop engine trader analyst; in Azure: deactivate the engine app's active revision, see docs/azure.md), or pass -IgnoreRunningEngine."
}

if ($LiveWhenFlat) {
  # Real money: a reset forgets every position's entry, stop and target, so the account must hold nothing to protect. The
  # engine's last status (written up to its stop) and its state say so; with neither, nothing proves the account is flat.
  Write-CixStep 'Is the live account flat?'
  $statusDoc = Get-CixCosmosItem -Endpoint $endpoint -Key $key -Database $database -Container 'state' -Id 'engine-status-live' -PartitionKey 'engine-status-live'
  $stateDoc = Get-CixCosmosItem -Endpoint $endpoint -Key $key -Database $database -Container 'state' -Id 'engine-state-live' -PartitionKey 'engine-state-live'
  $portfolio = if ($statusDoc -and $statusDoc.PSObject.Properties['value'] -and $statusDoc.value.PSObject.Properties['portfolio']) { $statusDoc.value.portfolio } else { $null }
  if ($null -eq $portfolio) {
    throw "The live engine's status has no valued portfolio, so nothing shows the account is flat. Start the engine until the dashboard values it, stop it, and run this again."
  }
  $problems = [System.Collections.Generic.List[string]]::new()
  # The valuation must be the engine's last word, not an old one kept while prices were missing (a held coin unpriced).
  $toTime = { param($v) if ($v -is [datetime]) { [DateTimeOffset]$v.ToUniversalTime() } else { [DateTimeOffset]::Parse([string]$v, [Globalization.CultureInfo]::InvariantCulture) } }
  $valuedAt = & $toTime $portfolio.at
  $statusAt = & $toTime $statusDoc.updatedAt
  if (($statusAt - $valuedAt) -gt [TimeSpan]::FromMinutes(10)) {
    $problems.Add("the last valuation ($valuedAt) is more than 10 minutes older than the last status ($statusAt), so a holding may be missing from it")
  }
  foreach ($h in @($(if ($portfolio.PSObject.Properties['holdings']) { $portfolio.holdings } else { @() }))) {
    $raw = if ($h.PSObject.Properties['valueQuote']) { [string]$h.valueQuote } else { '' }
    $value = [decimal]0
    if (-not [decimal]::TryParse($raw, [Globalization.NumberStyles]::Number, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
      $problems.Add("$($h.symbol): value unknown")
    }
    elseif ($value -ge $DustQuote) { $problems.Add("$($h.symbol): about $value $($portfolio.currency)") }
    else { Write-CixInfo "$($h.symbol): residual of about $value $($portfolio.currency), below -DustQuote $DustQuote" }
  }
  $orders = if ($stateDoc -and $stateDoc.PSObject.Properties['value'] -and $stateDoc.value.PSObject.Properties['orders']) { @($stateDoc.value.orders) } else { @() }
  foreach ($o in $orders) { $problems.Add("order in flight: $($o.side) $($o.symbol)") }
  if ($problems.Count -gt 0) {
    throw "The live account isn't flat at the engine's last status ($($statusDoc.updatedAt)): $($problems -join '; '). Sell the coins and let orders finish, start the engine until the dashboard shows only cash, stop it, and run this again."
  }
  Write-CixInfo "Flat at the engine's last status ($($statusDoc.updatedAt)): no holding worth $DustQuote or more, no order in flight."
}

# --- Plan --------------------------------------------------------------------------------------------------------------
Write-CixStep 'Plan'
$plan = [System.Collections.Generic.List[object]]::new()
$kept = [System.Collections.Generic.List[string]]::new()

if ($containers -contains 'state') {
  $docs = Get-CixCosmosItems -Endpoint $endpoint -Key $key -Database $database -Container 'state' -Query 'SELECT c.id, c.type FROM c'
  foreach ($doc in $docs) {
    $isSpec = $doc.id -like 'spec-*'
    # Every per-mode document is named <name>-<mode>, so one added later is reset too; the paper account has no suffix.
    $ofMode = -not $isSpec -and (@($modes | Where-Object { $doc.id -like "*-$_" }).Count -gt 0 -or ($doc.id -eq 'paper-account' -and $modes -contains 'paper'))
    if ($ofMode -or ($isSpec -and $IncludeSpecArchive)) {
      $plan.Add([pscustomobject]@{ Container = 'state'; Id = $doc.id; PartitionKey = $doc.id; Type = if ($doc.PSObject.Properties['type']) { $doc.type } else { '' } })
    }
    else { $kept.Add("state/$($doc.id)") }
  }
}

if ($containers -contains 'journal') {
  $query = 'SELECT c.id, c.day FROM c'
  if ($JournalBefore) { $query = "SELECT c.id, c.day FROM c WHERE c.day < '$JournalBefore'" } # validated yyyy-MM-dd above
  $entries = Get-CixCosmosItems -Endpoint $endpoint -Key $key -Database $database -Container 'journal' -Query $query
  foreach ($e in $entries) { $plan.Add([pscustomobject]@{ Container = 'journal'; Id = $e.id; PartitionKey = $e.day; Type = 'journal-entry' }) }
}

if ($containers -contains 'requests') {
  $requests = Get-CixCosmosItems -Endpoint $endpoint -Key $key -Database $database -Container 'requests' -Query 'SELECT c.id, c.day, c.tradingMode FROM c'
  foreach ($r in $requests) {
    # A request without a tradingMode is for paper, as the engine reads it.
    $for = if ($r.PSObject.Properties['tradingMode'] -and $r.tradingMode) { "$($r.tradingMode)".ToLowerInvariant() } else { 'paper' }
    if ($modes -contains $for) { $plan.Add([pscustomobject]@{ Container = 'requests'; Id = $r.id; PartitionKey = $r.day; Type = 'manual-request' }) }
    else { $kept.Add("requests/$($r.id) ($for)") }
  }
}

$summary = $plan | Group-Object Container, Type | Sort-Object Name | ForEach-Object { [pscustomobject]@{ What = $_.Name; Documents = $_.Count } }
if ($summary) { $summary | Format-Table -AutoSize | Out-String | Write-Host }
else { Write-CixInfo 'Nothing to remove.' }
$plan | Where-Object Container -eq 'state' | ForEach-Object { Write-CixInfo "would remove state/$($_.Id)" }
if ($plan | Where-Object Container -eq 'journal') {
  $days = @($plan | Where-Object Container -eq 'journal' | Group-Object PartitionKey | Sort-Object Name)
  Write-CixInfo "would remove journal entries from $($days.Count) day(s): $(($days | Select-Object -First 1).Name) to $(($days | Select-Object -Last 1).Name)"
}
$requested = @($plan | Where-Object Container -eq 'requests')
if ($requested.Count -gt 0) { Write-CixInfo "would remove $($requested.Count) owner request(s)" }
foreach ($k in $kept) { Write-CixInfo "keeps $k" }

if (-not $Apply) {
  Write-CixStep 'Dry run: nothing was changed. Run again with -Apply to do it.'
  return
}
if ($plan.Count -eq 0) { Write-CixStep 'Nothing to do.'; return }

# --- Confirm and apply ---------------------------------------------------------------------------------------------------
$phrase = if ($LiveWhenFlat) { "reset $Environment real money live state" }
  elseif ($Mode -eq 'paper') { "reset $Environment" }
  else { "reset $Environment including live state" }
Write-Host ''
$typed = Read-Host "    This permanently removes the data above from '$Environment'. Type '$phrase' to continue"
if ($typed -cne $phrase) { throw 'Not confirmed: nothing was changed.' }

$removed = 0
$failed = 0
Write-CixStep 'Removing documents'
$i = 0
foreach ($item in $plan) {
  $i++
  if (Remove-CixCosmosItem -Endpoint $endpoint -Key $key -Database $database -Container $item.Container -Id $item.Id -PartitionKey $item.PartitionKey) { $removed++ }
  else { $failed++; Write-CixWarn "could not remove $($item.Container)/$($item.Id)" }
  if ($i % 200 -eq 0) { Write-CixInfo "$i of $($plan.Count)..." }
}
$key = $null

Write-CixStep 'Done'
Write-CixInfo "Removed: $removed document(s). Failed: $failed. Kept: $($kept.Count)."
if ($LiveWhenFlat) {
  Write-CixInfo 'When the live engine starts next, it starts halted with a fresh state: press Resume on the dashboard (Live) once it looks right.'
}
else { Write-CixInfo 'When the engine starts next, it creates a fresh state (a new paper account, kill switch released for paper).' }
if ($failed -gt 0) { exit 1 }
