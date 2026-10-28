# Paper trading locally

> The simplest way to run everything is Docker Compose: see [docker.md](docker.md). The steps below run the engine and trader directly with `dotnet run` and PowerShell, which is handy while developing.

The engine runs on your PC with simulated money and live Revolut X prices. A Claude Code agent trades through the engine's tools once an hour, following the active strategy (`strategies/balanced.v3.json` by default). Nothing here can touch your real account: paper mode holds no trading keys.

## Once

1. Set the owner key (unlocks pause, resume and journal tools; agents don't get it):
   ```powershell
   $bytes = New-Object byte[] 32
   [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   dotnet user-secrets set ToolServer:OperatorKey ([Convert]::ToBase64String($bytes)) --project src/Cix.Engine
   ```
2. Remove any test connection you added by hand: `claude mcp remove cix-test`. The trader's connection is in `tools/trader/mcp.json` and is used only by the run script, so ordinary Claude Code sessions in this repo don't get trading tools.
3. Hooks need Git Bash (Claude Code's default on Windows), not WSL.
4. Persistence (needed to trade): run the Cosmos DB emulator. The watched coins live there and are added from the dashboard (ADR-0021), so without Cosmos the engine watches and trades nothing; it also keeps the paper account, journal and engine state across restarts. With Docker Desktop:
   ```powershell
   docker run --detach --name cosmos --restart unless-stopped -p 8081:8081 -p 1234:1234 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest --protocol https
   dotnet user-secrets set Cosmos:Enabled true --project src/Cix.Engine
   ```
   The engine creates the `cix` database on start. Browse it at http://localhost:1234. `--restart unless-stopped` brings the emulator back after a reboot; its data lives in the container, so don't remove the container. Without Cosmos, paper balances reset whenever the engine stops.

5. Choose what can be traded. No pair list lives in configuration (ADR-0021). `config/risk-limits.json`, which only you edit, holds one rule: `"AllowedQuoteCurrency": "EUR"` and `"MaxTradableSymbols"` (1 to 50). You add and remove coins on the dashboard's Coins tab: every added coin is watched (prices, candles, the scan), and it trades only while it passes the running strategy's filters. Each coin shows its level: trading, filtered out (with the reason), flagged by the market analyst, waiting for data, held outside the strategy, just added, or on the account (held but not added by you, so the strategy doesn't buy it until you add it). Coins the account holds are always watched, and a held coin can't be removed. Before you switch strategy, the switch dialog lists which coins would enter and leave trading and which of those you hold; after a switch that changes the coin filters, the Coins tab shows what changed until you dismiss it. A new environment starts with nothing watched. Symbols the exchange doesn't list or has suspended are skipped at every scan.

## Every session

Terminal 1, the engine (with Cosmos enabled it resumes the same paper account after a restart):
```powershell
dotnet run --project src/Cix.Engine
```

Terminal 2, the trader:
```powershell
.\tools\trader\run-trader.ps1 -Once                 # one run now (Sonnet by default; -Model opus to change)
.\tools\trader\run-trader.ps1 -IntervalMinutes 60   # every hour until you stop it (Ctrl+C)
```
Each run prints the agent's report and its cost, and saves the full output in `logs\trader\`.

## Scanner

Every 5 minutes the engine scans the tradable universe with fixed arithmetic, no AI: it drops symbols with thin volume, a wide spread, wild volatility, or hourly moves too small to pay the fees and spread; ranks the rest by momentum adjusted for volatility and cost; suggests sized buys for uptrend pullbacks (stop 1.5 ATR, target 2R, about €1 at risk and at most €5 per order); and flags held positions at their stop, target or in a downtrend. New signals go to the journal as `scan-signal` entries. Agents read the same scan with the `scan_market` tool when their strategy spec allows it. Signals are suggestions: the risk gate still decides every order.

## Reviewing

Owner tools need the owner key, and no agent may get it. So don't add an owner connection with `claude mcp add` (that would load it into every Claude Code session in this repo, coding agents included). Instead, keep a file **outside the repo**, for example `%USERPROFILE%\cix-owner.json`:
```json
{ "mcpServers": { "cix-owner": { "type": "http", "url": "http://127.0.0.1:5077/mcp",
  "headers": { "X-Cix-Operator-Key": "${CIX_OPERATOR_KEY}" } } } }
```
and start an owner session only when you need one, with the key in that terminal alone:
```powershell
$env:CIX_OPERATOR_KEY = Read-Host "Owner key"
claude --mcp-config "$env:USERPROFILE\cix-owner.json" --strict-mcp-config
```
Then ask things like "summarise today's journal" or "pause trading". Or read the journal files in `%LOCALAPPDATA%\cix\journal\paper\` (one JSON line per event: tool calls, orders with the agent's rationale, fills, kill-switch events).

To pause everything: stop the run script, or call `pause_trading` with the owner key. The engine also halts on its own at the hard drawdown ceiling, when a held coin, or every watched coin, has had no fresh market data for 15 minutes (a quiet coin nobody holds is only left out of trading), after repeated risk-gate rejections, or when it finds an order it didn't place.

## Changing the strategy

The presets are Safe, Balanced and Risky (see `strategies/README.md`); the engine starts with the one named by `Strategy:Name` and `Strategy:Version` in `src/Cix.Engine/appsettings.json`. To change one, copy it to the next version (for example `balanced.v4.json`), edit the copy (raise the version inside too), and set `Strategy:Version`. Never edit a version that has already run.
