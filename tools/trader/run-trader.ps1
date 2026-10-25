<#
.SYNOPSIS
  Runs the CIX trader agent against the local engine: once, or every hour.

.DESCRIPTION
  Each run is one headless Claude Code session (claude -p) that can use only the cix tools (everything else is denied),
  with caps on turns and wall-clock time. The trading connection lives in tools/trader/mcp.json, not .mcp.json, so other
  Claude Code sessions in this repo don't get trading tools.
  The run ID goes to the engine in the X-Cix-Run header, so the journal groups each run's tool calls and orders.
  Output, including Claude's reported cost, is saved under logs\trader\.

  Start the engine first: dotnet run --project src/Cix.Engine

.EXAMPLE
  .\tools\trader\run-trader.ps1 -Once
.EXAMPLE
  .\tools\trader\run-trader.ps1 -IntervalMinutes 60
.EXAMPLE
  .\tools\trader\run-trader.ps1 -OwnerRequests     # one run that answers the owner's waiting dashboard requests
#>
param(
    [switch]$Once,
    [switch]$OwnerRequests,
    [int]$IntervalMinutes = 60,
    [int]$MaxTurns = 30,
    [ValidatePattern('(?-i)^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$')]
    [string]$Model = 'sonnet',
    [int]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$logs = Join-Path $repo 'logs\trader'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

# The trader's standing instructions: the body of .claude/agents/trader.md, without its front matter.
$agentFile = Get-Content (Join-Path $repo '.claude\agents\trader.md') -Raw
$instructions = ($agentFile -split "(?m)^---\s*$", 3)[2].Trim()
# Windows PowerShell 5.1 doesn't escape quotes in native arguments: keep the prompt one safe argument.
$instructions = ($instructions -replace '"', "'") -replace "`r?`n", ' '
$mcpConfig = Join-Path $repo 'tools\trader\mcp.json'

# Everything except the cix agent tools is denied, so the trader can't read files, run commands or browse.
$denied = 'Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,WebFetch,WebSearch,Task,TodoWrite,mcp__cix__get_analyst_brief,mcp__cix__record_market_view'


$tools = @(
    'mcp__cix__get_strategy_spec', 'mcp__cix__get_portfolio', 'mcp__cix__get_position_plans',
    'mcp__cix__get_open_orders', 'mcp__cix__get_market_view', 'mcp__cix__get_features', 'mcp__cix__scan_market',
    'mcp__cix__place_order', 'mcp__cix__cancel_order'
) -join ','
$denied += ',mcp__cix__get_manual_requests,mcp__cix__answer_manual_request'
if ($OwnerRequests) {
    # A run for owner requests reads and answers only: the owner's note can't steer it into placing or cancelling orders.
    $tools = ($tools -replace ',mcp__cix__place_order,mcp__cix__cancel_order', '') + ',mcp__cix__get_manual_requests,mcp__cix__answer_manual_request'
    $denied = ($denied -replace ',mcp__cix__get_manual_requests,mcp__cix__answer_manual_request', '') + ',mcp__cix__place_order,mcp__cix__cancel_order'
}

$prompt = if ($OwnerRequests) {
    'The owner has sent requests from the dashboard. Read get_strategy_spec, then get_manual_requests, and answer every request with answer_manual_request as your instructions describe. Do not run the regular trading cycle in this run.'
} else { 'Run one trading cycle of the active strategy now.' }

function Invoke-TraderRun {
    $runId = 'run-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $env:CIX_RUN_ID = $runId
    $log = Join-Path $logs "$runId.json"
    Write-Host "[$(Get-Date -Format 'HH:mm')] $runId starting (model $Model)"

    Push-Location $repo
    try {
        # A job, so a hung run is stopped after TimeoutMinutes instead of blocking the next one.
        $job = Start-Job -ScriptBlock {
            param($dir, $instructions, $mcpConfig, $tools, $denied, $maxTurns, $runId, $model, $prompt)
            Set-Location $dir
            $env:CIX_RUN_ID = $runId
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            claude -p $prompt `
                --append-system-prompt $instructions `
                --mcp-config $mcpConfig --strict-mcp-config `
                --allowedTools $tools --disallowedTools $denied `
                --model $model `
                --max-turns $maxTurns `
                --output-format json
        } -ArgumentList $repo, $instructions, $mcpConfig, $tools, $denied, $MaxTurns, $runId, $Model, $prompt

        if (-not (Wait-Job $job -Timeout ($TimeoutMinutes * 60))) {
            Stop-Job $job
            Write-Warning "$runId stopped after $TimeoutMinutes minutes."
        }

        $output = Receive-Job $job
        Remove-Job $job -Force
        $output | Out-File -FilePath $log -Encoding utf8
        $result = ($output -join "`n") | ConvertFrom-Json
        if ($result.is_error) {
            Write-Warning "$runId ended with an error: $($result.subtype)"
        }
        $models = if ($result.modelUsage) { ($result.modelUsage.PSObject.Properties.Name) -join ', ' } else { '?' }
        Write-Host "[$(Get-Date -Format 'HH:mm')] $runId done. Models used: $models, cost: $($result.total_cost_usd) USD, turns: $($result.num_turns)"
        Write-Host $result.result
    }
    catch {
        Write-Warning "$runId failed: $_"
    }
    finally {
        Pop-Location
    }
}

if ($Once -or $OwnerRequests) {
    Invoke-TraderRun
    return
}

# Runs start on a fixed cadence, so a slow run doesn't push every later run back.
$next = Get-Date
while ($true) {
    Invoke-TraderRun
    $next = $next.AddMinutes($IntervalMinutes)
    $wait = ($next - (Get-Date)).TotalSeconds
    if ($wait -gt 0) { Start-Sleep -Seconds ([int]$wait) }
}
