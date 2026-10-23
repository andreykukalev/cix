# Paper trading locally

The engine runs on your PC with simulated money and live Revolut X prices. A Claude Code agent trades through the engine's tools once an hour, following the active strategy (`strategies/trend.v1.json`). Nothing here can touch your real account: paper mode holds no trading keys.

## Once

1. Set the owner key (unlocks pause, resume and journal tools; agents don't get it):
   ```powershell
   $bytes = New-Object byte[] 32
   [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   dotnet user-secrets set ToolServer:OperatorKey ([Convert]::ToBase64String($bytes)) --project src/Cix.Engine
   ```
2. Remove any test connection you added by hand: `claude mcp remove cix-test`. The repo's `.mcp.json` defines the `cix` connection; Claude Code asks once whether to trust it.

## Every session

Terminal 1, the engine (keep it running; paper balances live in memory and reset when it stops):
```powershell
dotnet run --project src/Cix.Engine
```

Terminal 2, the trader:
```powershell
.\tools\trader\run-trader.ps1 -Once                 # one run now
.\tools\trader\run-trader.ps1 -IntervalMinutes 60   # every hour until you stop it (Ctrl+C)
```
Each run prints the agent's report and its cost, and saves the full output in `logs\trader\`.

## Reviewing

Add an owner connection once, in your local Claude Code settings only (never in `.mcp.json`, which is in the repo):
```powershell
claude mcp add --transport http cix-owner http://127.0.0.1:5077/mcp --header "X-Cix-Operator-Key: <your key>"
```
Then ask Claude Code things like "with cix-owner, summarise today's journal" or "pause trading". Or read the journal files in `%LOCALAPPDATA%\cix\journal\paper\` (one JSON line per event: tool calls, orders with the agent's rationale, fills, kill-switch events).

To pause everything: stop the run script, or call `pause_trading` with the owner key. The engine also halts on its own at the hard drawdown ceiling, after stale market data, after repeated risk-gate rejections, or when it finds an order it didn't place.

## Changing the strategy

Copy `strategies/trend.v1.json` to `trend.v2.json`, edit the copy (raise the version inside too), and set `Strategy:Version` to 2 in `src/Cix.Engine/appsettings.json`. Never edit a version that has already run.
