#Requires -Version 5.1
<#
.SYNOPSIS
  Resets CIX's Azure Cosmos DB data for testing (for example after releasing a new version): bot state, journal, or both.
  A dry run unless you pass -Apply, and then only after you type the confirmation it asks for.

.DESCRIPTION
  What it can remove, in the environment's database (cosmos.databaseName) in the shared Cosmos DB account (found by its
  shared-role=cosmos tag in the shared resource group, never by name alone). Other projects' databases are never touched:
    -Target State    the engine's state documents for -Mode (paper by default): engine-state-<mode>, engine-status-<mode>,
                     and for paper the paper-account. With -IncludeSpecArchive, also the archived strategy specs (spec-*).
    -Target Journal  every journal entry (optionally only days before -JournalBefore).
    -Target All      both.
  With -ResetSchema, the selected containers are deleted and created again from infra/main.bicep (same partition keys and
  settings), which is quicker for a large journal. Without it, the containers, database and account are never touched;
  the database itself and the shared account are never deleted.

  Safeguards:
    - The environment must have a settings file in infra/environments, and must not be marked "protected" there (live money).
    - CIX's resource group must be tagged cix-environment=<Environment>, cix-protected=false, and cix-database and
      cix-cosmos-account naming this database and account (all set by infra/main.bicep); no other group may claim the
      database; and it must exist in the single shared account.
    - It refuses while the engine is running (its status heartbeat is under 3 minutes old) unless -IgnoreRunningEngine.
    - It lists what it would remove first; nothing is deleted without -Apply and the typed confirmation.
    - It is never run by deployment, the engine, or the dashboard.

  Local emulator: reset it with `docker compose down -v` instead (that removes the emulator's volume).

.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target All                 # dry run: lists what would go
.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target State -Apply        # paper state only
.EXAMPLE
  .\tools\azure\reset-database.ps1 -Environment dev -Target Journal -ResetSchema -Apply
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [Parameter(Mandatory)][ValidateSet('State', 'Journal', 'All')][string]$Target,
  [ValidateSet('paper', 'live', 'all')][string]$Mode = 'paper',
  [switch]$IncludeSpecArchive,
  [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$JournalBefore,
  [switch]$ResetSchema,
  [switch]$IgnoreRunningEngine,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

$containers = switch ($Target) { 'State' { @('state') } 'Journal' { @('journal') } 'All' { @('state', 'journal') } }
if ($ResetSchema -and $JournalBefore) { throw '-ResetSchema recreates whole containers; it can''t keep entries after -JournalBefore.' }
if ($ResetSchema -and $containers -contains 'state' -and $Mode -ne 'all') {
  throw '-ResetSchema recreates the whole state container (paper, live and archived specs); pass -Mode all to make that explicit.'
}

Write-CixStep "Reset in environment '$Environment' ($(if ($Apply) { 'APPLY' } else { 'dry run' }))"
$settings = Read-CixEnvironment -Name $Environment
if ($settings.protected) {
  throw "Environment '$Environment' is marked protected in $($settings.path). This script never resets a protected environment."
}

Assert-CixAzureCli
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
# CIX's resource group tags must agree: a settings file edited locally can't unprotect a live environment.
$tags = Get-CixEnvironmentTags -Settings $settings
$say = { param($name) if ($tags -and $tags.PSObject.Properties[$name]) { $tags.$name } else { 'nothing' } }
if (-not $tags -or -not $tags.PSObject.Properties['cix-environment'] -or $tags.'cix-environment' -ne $Environment) {
  throw "Resource group $($settings.resourceGroup) isn't tagged cix-environment=$Environment (it says '$(& $say 'cix-environment')'). Refusing."
}
if (-not $tags.PSObject.Properties['cix-protected'] -or $tags.'cix-protected' -ne 'false') {
  throw "Resource group $($settings.resourceGroup) isn't tagged cix-protected=false (it says '$(& $say 'cix-protected')'). Refusing."
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
  throw "The engine ($($running -join ', ')) wrote its status less than 3 minutes ago: stop it first (docker compose stop engine trader analyst), or pass -IgnoreRunningEngine."
}

# --- Plan --------------------------------------------------------------------------------------------------------------
Write-CixStep 'Plan'
$plan = [System.Collections.Generic.List[object]]::new()
$kept = [System.Collections.Generic.List[string]]::new()

if ($containers -contains 'state') {
  $stateIds = @()
  foreach ($m in $modes) {
    $stateIds += "engine-state-$m"
    $stateIds += "engine-status-$m"
    if ($m -eq 'paper') { $stateIds += 'paper-account' }
  }
  $docs = Get-CixCosmosItems -Endpoint $endpoint -Key $key -Database $database -Container 'state' -Query 'SELECT c.id, c.type FROM c'
  foreach ($doc in $docs) {
    $isSpec = $doc.id -like 'spec-*'
    if ($ResetSchema -or $stateIds -contains $doc.id -or ($isSpec -and $IncludeSpecArchive)) {
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

$summary = $plan | Group-Object Container, Type | Sort-Object Name | ForEach-Object { [pscustomobject]@{ What = $_.Name; Documents = $_.Count } }
if ($summary) { $summary | Format-Table -AutoSize | Out-String | Write-Host }
else { Write-CixInfo 'Nothing to remove.' }
$plan | Where-Object Container -eq 'state' | ForEach-Object { Write-CixInfo "would remove state/$($_.Id)" }
if ($plan | Where-Object Container -eq 'journal') {
  $days = @($plan | Where-Object Container -eq 'journal' | Group-Object PartitionKey | Sort-Object Name)
  Write-CixInfo "would remove journal entries from $($days.Count) day(s): $(($days | Select-Object -First 1).Name) to $(($days | Select-Object -Last 1).Name)"
}
foreach ($k in $kept) { Write-CixInfo "keeps $k" }
if ($ResetSchema) { Write-CixWarn "-ResetSchema: the container(s) $($containers -join ', ') will be deleted and created again from infra/main.bicep." }

if (-not $Apply) {
  Write-CixStep 'Dry run: nothing was changed. Run again with -Apply to do it.'
  return
}
if ($plan.Count -eq 0 -and -not $ResetSchema) { Write-CixStep 'Nothing to do.'; return }

# --- Confirm and apply ---------------------------------------------------------------------------------------------------
$phrase = if ($Mode -eq 'paper') { "reset $Environment" } else { "reset $Environment including live state" }
Write-Host ''
$typed = Read-Host "    This permanently removes the data above from '$Environment'. Type '$phrase' to continue"
if ($typed -cne $phrase) { throw 'Not confirmed: nothing was changed.' }

$removed = 0
$failed = 0
if ($ResetSchema) {
  Write-CixStep 'Recreating containers'
  foreach ($c in $containers) {
    Invoke-CixAz @('cosmosdb', 'sql', 'container', 'delete', '--account-name', $cosmos.name, '--resource-group', $settings.shared.resourceGroup,
      '--database-name', $database, '--name', $c, '--yes', '--only-show-errors') | Out-Null
    Write-CixInfo "deleted container $c"
  }
  try {
    Invoke-CixDeployment -Settings $settings -SharedAccountName $cosmos.name | Out-Null # creates them again exactly as the template defines
  }
  catch {
    Write-CixWarn "Recreating the containers failed: $($_.Exception.Message)"
    Write-CixWarn "The engine can't create them itself (key-based metadata writes are off). Run: .\tools\azure\provision.ps1 -Environment $Environment -SkipEngineConfig"
    exit 1
  }
  Write-CixInfo "created $($containers -join ', ') again from infra/main.bicep"
  $removed = $plan.Count
}
else {
  Write-CixStep 'Removing documents'
  $i = 0
  foreach ($item in $plan) {
    $i++
    if (Remove-CixCosmosItem -Endpoint $endpoint -Key $key -Database $database -Container $item.Container -Id $item.Id -PartitionKey $item.PartitionKey) { $removed++ }
    else { $failed++; Write-CixWarn "could not remove $($item.Container)/$($item.Id)" }
    if ($i % 200 -eq 0) { Write-CixInfo "$i of $($plan.Count)..." }
  }
}
$key = $null

Write-CixStep 'Done'
Write-CixInfo "Removed: $removed document(s)$(if ($ResetSchema) { ' (containers recreated)' }). Failed: $failed. Kept: $($kept.Count)."
Write-CixInfo 'When the engine starts next, it creates a fresh state (a new paper account, kill switch released for paper).'
if ($failed -gt 0) { exit 1 }
