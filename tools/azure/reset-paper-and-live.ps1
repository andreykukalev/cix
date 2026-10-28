#Requires -Version 5.1
<#
.SYNOPSIS
  Resets both CIX databases for a fresh test run: live's (the protected environment, only while its account holds nothing
  but cash) and paper's. A dry run unless you pass -Apply; each database then asks for its own typed confirmation.

.DESCRIPTION
  Runs reset-database.ps1 twice with the same -Target and -IncludeRequests:
    1. -Environment prod -LiveWhenFlat: live's state, journal and requests in its own database. Refused unless the live
       engine is stopped and its last status shows no holding worth -DustQuote or more and no order in flight. A reset
       forgets every position's entry, stop and target, so sell the coins first. Live then starts halted until you press
       Resume on the dashboard.
    2. -Environment dev: paper's state (a new paper account), journal and requests. The watched coins are part of the
       state: add them again on the Coins tab afterwards, for each mode.
  Live goes first: if it refuses, paper is left as it was. Every safeguard of reset-database.ps1 applies to both. Stop
  both engines first (docs/azure.md, "Resetting the database").

.EXAMPLE
  .\tools\azure\reset-paper-and-live.ps1 -Target All -IncludeRequests          # dry run of both
.EXAMPLE
  .\tools\azure\reset-paper-and-live.ps1 -Target All -IncludeRequests -Apply   # both, after two confirmations
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('State', 'Journal', 'All')][string]$Target,
  [switch]$IncludeRequests,
  [ValidatePattern('^[a-z0-9-]{2,12}$')][string]$PaperEnvironment = 'dev',
  [ValidatePattern('^[a-z0-9-]{2,12}$')][string]$LiveEnvironment = 'prod',
  [ValidateRange(0, 100)][decimal]$DustQuote = 1,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$reset = Join-Path $PSScriptRoot 'reset-database.ps1'
$common = @{ Target = $Target; IncludeRequests = $IncludeRequests; Apply = $Apply }
if ($SubscriptionId) { $common.SubscriptionId = $SubscriptionId }

$global:LASTEXITCODE = 0
& $reset -Environment $LiveEnvironment -LiveWhenFlat -DustQuote $DustQuote @common
if ($LASTEXITCODE -ne 0) { throw "Live's reset didn't remove everything (see above): paper was left as it was." }
& $reset -Environment $PaperEnvironment -Mode paper @common
