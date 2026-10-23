<#
.SYNOPSIS
  Runs the CIX trader agent against the local engine: once, or every hour.

.DESCRIPTION
  Each run is one headless Claude Code session (claude -p) that can use only the cix tools, with a cap on turns.
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
    [int]$MaxTurns = 30
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$logs = Join-Path $repo 'logs\trader'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

# The trader's standing instructions: the body of .claude/agents/trader.md, without its front matter.
$agentFile = Get-Content (Join-Path $repo '.claude\agents\trader.md') -Raw
$instructions = ($agentFile -split "(?m)^---\s*$", 3)[2].Trim()

$tools = @(
    'mcp__cix__get_strategy_spec', 'mcp__cix__get_portfolio', 'mcp__cix__get_position_plans',
    'mcp__cix__get_open_orders', 'mcp__cix__get_market_view', 'mcp__cix__get_features',
    'mcp__cix__place_order', 'mcp__cix__cancel_order'
) -join ','

function Invoke-TraderRun {
    $runId = 'run-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm')
    $env:CIX_RUN_ID = $runId
    $log = Join-Path $logs "$runId.json"
    Write-Host "[$(Get-Date -Format 'HH:mm')] $runId starting"

    Push-Location $repo
    try {
        # Only the cix tools are allowed; anything else is refused in headless mode.
        claude -p 'Run one trading cycle of the active strategy now.' `
            --append-system-prompt $instructions `
            --mcp-config .mcp.json --strict-mcp-config `
            --allowedTools $tools `
            --max-turns $MaxTurns `
            --output-format json | Out-File -FilePath $log -Encoding utf8
        $result = Get-Content $log -Raw | ConvertFrom-Json
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

while ($true) {
    Invoke-TraderRun
    Start-Sleep -Seconds ($IntervalMinutes * 60)
}
