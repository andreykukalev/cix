# Running CIX with Docker Compose (Windows)

One command starts the Cosmos DB emulator, the engine and the trader. They restart on their own after a reboot. Trading is on paper, and no Revolut X keys are needed or stored. See ADR-0005.

```
cosmos  (emulator, data in a Docker volume)  ◄── engine (paper exchange, risk gate, tool server on 127.0.0.1:5077)
                                                   ▲ same network namespace, loopback only
                                                 trader (Claude Code, runs every hour)
```

## Once

1. Install **Docker Desktop** with the WSL 2 backend.
   - Turn on *Start Docker Desktop when you sign in*, so containers come back after a reboot.
   - Give it at least 4 GB of memory, because the emulator needs it.
2. Get Claude credentials for the trader. Choose one:
   - **Setup token (recommended).** Install Claude Code on Windows, run `claude setup-token`, and sign in. It prints a token that lasts a year and uses your Claude subscription.
   - **API key.** Create one at console.anthropic.com. It is billed per use.
3. If you followed the older guide and started an emulator with `docker run --name cosmos ...`, remove it first: `docker rm -f cosmos`. Compose uses its own emulator and volume, so the paper account starts fresh.
4. In the repo folder, run:
   ```powershell
   .\tools\setup-secrets.ps1
   ```
   If PowerShell blocks scripts, use `powershell -ExecutionPolicy Bypass -File .\tools\setup-secrets.ps1`.
   - It creates `secrets\operator_key` (random) and `secrets\claude_token` (you paste the token; nothing is shown).
   - It writes the token's expiry date to `.env`.
   - All three files are git-ignored. The two secret files are readable only by you; `.env` holds no secret.
5. Choose what can be traded by editing `config\risk-limits.json` → `AllowedSymbols`. Only you edit this file (see paper-trading.md).

## Start and stop

```powershell
docker compose up -d --build      # build and start everything (first build takes a few minutes)
docker compose ps                 # what is running
docker compose logs -f trader     # each run's report and cost
docker compose logs -f analyst    # the market view every 6 hours
docker compose logs -f engine     # scans, orders, safety events
docker compose stop trader        # pause the agent (the engine keeps watching the market)
docker compose down               # stop everything; the paper account stays in the cosmos-data volume
```

Run `docker compose down -v` only if you want to wipe the paper account and journal.

## Where things are

- **Journal, paper account, engine state:** in Cosmos. Browse them at http://localhost:1234 (database `cix`, container `journal`).
- **Agent run output:** `logs\trader\run-*.json` and `logs\analyst\analyst-*.json`.
- **Strategy, settings and risk limits:** mounted read-only from `strategies\`, `src\Cix.Engine\appsettings.json` and `config\risk-limits.json`. After changing them, run `docker compose restart engine trader`.
- **Code changes:** need a rebuild with `docker compose up -d --build`.

## Market analyst

The `analyst` container runs every 6 hours (`ANALYST_INTERVAL_HOURS`) on Opus (`ANALYST_MODEL`).
- **What it reads:** the market data and recent news (macro events, exchange incidents, token hacks and delistings).
- **What it records:** a view of risk-on, neutral or risk-off, plus any symbols to avoid.
- **Effect:**
  - Risk-off stops new entries until the view expires (at most 12 hours), and avoided symbols get no new entry.
  - Exits always run.
  - It can never cause a trade. A failed run just means no view, which counts as neutral.
- **See its reasoning:**
  - `docker compose logs analyst` shows the run itself.
  - The `market-view` entries in the journal (Data Explorer) show its summary and sources.
  - The engine's scan lines show `risk-off (market view)` or `news` when it is in effect.
- **Its rules:** `strategies/analyst.v1.json` (ADR-0007).

## When Claude isn't available

- **Protective exits.** Usage limits, an outage or an expired token never leave a position unprotected.
  - The engine itself sells a position that reaches its stop-loss or take-profit, every 5 minutes, through the risk gate and with no Claude call.
  - An unfilled sell is re-priced at the next scan (within about 5 minutes) at the new bid.
  - A sell the risk gate refuses is retried for that coin after 15 minutes, and refusals of these sells never trip the kill switch.
  - The trader isn't started for stop or target exits, because the engine handles them.
  - Trend exits and all buys still need the trader.
  - You can turn this off with `Scanner:ProtectiveExits` in `appsettings.json`, but it is on by default.
- **Recovery.**
  - When a trader attempt fails because of a usage or rate limit, the signal stays pending and is retried every 15 minutes. Such attempts don't count toward the 30-a-day cap.
  - The analyst retries every 30 minutes (`ANALYST_RETRY_MINUTES`) until it gets through. Meanwhile its old view expires to neutral.
  - Both pick up again by themselves once the limit resets.
- **News sites.** The analyst can search anything but can open full pages only from `ANALYST_WEB_DOMAINS` (subdomains included). Claude Code refuses fetches elsewhere.
  - The default list is federalreserve.gov, bls.gov, bea.gov, ecb.europa.eu, tradingeconomics.com, reuters.com, apnews.com, cnbc.com, coindesk.com, theblock.co, decrypt.co, rekt.news, defillama.com, tokenomist.ai, status.solana.com and revolut.com.
  - To change it, set the whole list in `.env`, for example `ANALYST_WEB_DOMAINS=reuters.com,coindesk.com,federalreserve.gov`, then run `docker compose up -d analyst`.

## Strategy versions (signal-driven v2 and v3)

1. The default is v4 (v3 with a lower cost ratio, so BTC qualifies). To change it, in `src\Cix.Engine\appsettings.json`, set `"Strategy": { ..., "Version": 2 }` (or 1).
2. Run `docker compose restart engine trader`.

The trader then calls Claude only when the scanner's signals change (checked every minute), plus a position review every 4 hours (`TRADER_HEARTBEAT_MINUTES` in `.env`). The engine log shows each scan's ranking and why each symbol did or didn't signal (`Scan: ... ranked [...] excluded [...]`). To go back, set the version to 1 and restart.

Version 3 is v2 with wider scanner filters (more coins pass the volume, spread and cost checks); see `strategies\README.md`. It is the default. Each symbol in `AllowedSymbols` is polled, so the number of coins is limited by the market-data request budget (the `MarketData` section in `appsettings.json`): quotes every 20 seconds fit up to 15 symbols, and the engine refuses to start above that.

## Notes

- The trader uses **Sonnet** by default. Its work is mostly following the scanner's signals, and Sonnet does that fast and within your plan's limits. To change it, set `TRADER_MODEL=opus` (or `haiku`, or a full model name) in `.env`, then run `docker compose up -d trader`. Each run's log line lists the models used; Claude Code may also call a small model for side tasks.

- When the engine restarts, the trader restarts too. With v1 it runs a cycle straight away and then hourly. With v2 it waits for the engine's first scan, runs a review, and then runs on new signals.
- v2 runs are limited: at least 15 minutes apart for signals and at most 30 a day (`TRADER_MIN_GAP_MINUTES`, `TRADER_MAX_RUNS_PER_DAY` in `.env`). Signals are checked every `TRADER_POLL_SECONDS` (60).
- On a Linux host, create `logs/trader` before the first start (the setup script does this on Windows), or Docker creates it owned by root and the trader can't write logs.

## Claude token expiry

A setup token expires after a year.
- From 14 days before, each run's log starts with a warning.
- Once it has expired, the trader skips runs and says so.
- If Claude rejects the credentials early, the log says that too.

To renew:
```powershell
claude setup-token
.\tools\setup-secrets.ps1 -Claude
docker compose restart trader
```

## Security notes

- The tool server is never published. Only the trader, which shares the engine's network namespace, can reach it.
- Only the Data Explorer (port 1234) is published, and only to `127.0.0.1`. The emulator's own port isn't published, because it doesn't check keys.
- This stack can only paper trade. The engine runs in a non-Production environment, which is where it refuses Live, and Live also refuses the emulator.
- Owner tools (pause, resume, journal queries) aren't reachable from Windows. Never publish port 5077 to "fix" that. To pause, run `docker compose stop trader`; to review, use the Data Explorer and the logs.
- The trader image installs the latest Claude Code unless you pin a version: add `CLAUDE_CODE_VERSION=x.y.z` to `.env` (check yours with `claude --version`) and rebuild.
- The engine reads only `ToolServer__OperatorKey` and `Cosmos__AccountKey` from `/run/secrets`. Any other file there is ignored, so a stray file can't switch to live trading or change a limit.
- The trader image contains no keys. It can use only the `cix` tools (file, shell and web tools are denied), and the order-size hook still applies.
