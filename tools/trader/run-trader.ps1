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
#>
param(
    [switch]$Once,
    [int]$IntervalMinutes = 60,
    [int]$MaxTurns = 30,
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
$denied = 'Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,WebFetch,WebSearch,Task,TodoWrite'


$tools = @(
    'mcp__cix__get_strategy_spec', 'mcp__cix__get_portfolio', 'mcp__cix__get_position_plans',
    'mcp__cix__get_open_orders', 'mcp__cix__get_market_view', 'mcp__cix__get_features', 'mcp__cix__scan_market',
    'mcp__cix__place_order', 'mcp__cix__cancel_order'
) -join ','

function Invoke-TraderRun {
    $runId = 'run-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $env:CIX_RUN_ID = $runId
    $log = Join-Path $logs "$runId.json"
    Write-Host "[$(Get-Date -Format 'HH:mm')] $runId starting"

    Push-Location $repo
    try {
        # A job, so a hung run is stopped after TimeoutMinutes instead of blocking the next one.
        $job = Start-Job -ScriptBlock {
            param($dir, $instructions, $mcpConfig, $tools, $denied, $maxTurns, $runId)
            Set-Location $dir
            $env:CIX_RUN_ID = $runId
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            claude -p 'Run one trading cycle of the active strategy now.' `
                --append-system-prompt $instructions `
                --mcp-config $mcpConfig --strict-mcp-config `
                --allowedTools $tools --disallowedTools $denied `
                --max-turns $maxTurns `
                --output-format json
        } -ArgumentList $repo, $instructions, $mcpConfig, $tools, $denied, $MaxTurns, $runId

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
        Write-Host "[$(Get-Date -Format 'HH:mm')] $runId done. Cost: $($result.total_cost_usd) USD, turns: $($result.num_turns)"
        Write-Host $result.result
    }
    catch {
        Write-Warning "$runId failed: $_"
    }
    finally {
        Pop-Location
    }
}

if ($Once) {
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
