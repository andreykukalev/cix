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
5. Choose what can be traded on the dashboard's Coins tab once the engine runs. `config\risk-limits.json`, which only you edit, sets the quote currency and the most coins at once; it lists no pairs (see paper-trading.md).

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

**After a reboot or restart** nothing needs doing by hand.
- The engine waits for the database (the emulator can take a few minutes) and logs `Waiting for the database` meanwhile, instead of crashing.
- Docker marks it healthy once it has loaded its saved state. The trader and analyst start only then, and restart with it.
- Orders, fills and each position's stop and target carry on from where they were. See [persistence.md](persistence.md#recovery).
- `docker compose ps` shows `(healthy)` next to the engine when it is ready.

## Where things are

- **Journal, paper account, engine state:** in Cosmos. Browse them at http://localhost:1234 (database `cix`: `state` is what is true now, `journal` is what happened). What each record means, how to trace a trade, and how the engine recovers after a restart: [persistence.md](persistence.md).
- **Agent run output:** `logs\trader\run-*.json` and `logs\analyst\analyst-*.json`.
- **Strategy, settings and risk limits:** mounted read-only from `strategies\`, `src\Cix.Engine\appsettings.json` and `config\risk-limits.json`. After changing them, run `docker compose restart engine trader`.
- **Code changes:** need a rebuild with `docker compose up -d --build`.

## Database in Azure

To keep the journal and state in Azure Cosmos DB (free tier) instead of the local emulator, and to get a read-only dashboard, see [azure.md](azure.md). The engine and agents still run here; only the database moves, using `docker-compose.azure.yml`.

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
- **Its rules:** `strategies/analyst.v4.json` (ADR-0007); v2 added the highlights shown in the dashboard's market briefing, v3 writes the summary and highlights in Russian as well, and v4 keeps risk-off short (3 hours) and for market-wide shocks only.

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

## Manual requests

You can ask the engine to sell (or the trader to consider a buy or a sell) without waiting for a signal (ADR-0011). The dashboard's buttons create these requests; until then, or to test, create one in Data Explorer (http://localhost:1234, `cix` › `requests` › New Item). The fields and an example are in [persistence.md](persistence.md#the-requests-container).

- **Direct sell** (`"mode": "direct"`): the engine sells the `portion` of the position at the bid within about 5 seconds, through the risk gate.
- **Agent-assisted** (`"mode": "agent"`, for sells and buys): the trader is started at once, looks at the request, and executes or declines it with its reasoning. Buys are agent-assisted only. These runs have their own daily cap (`TRADER_MAX_MANUAL_RUNS_PER_DAY`, 20) and can't place or cancel orders themselves.
- **Watch it:** the request document's `status`, `outcome` and `events`, the journal's `manual-request` and `order` entries, `docker compose logs trader`, and the engine status's `manualRequests`.
- A request more than 5 minutes old when the engine sees it expires; one the trader doesn't answer in 20 minutes expires too; a second request for the same coin and side while one is in progress is refused; at most 20 a day. To change these, add a `"ManualRequests"` section to `appsettings.json` (`Enabled`, `MaxPerDay`, `MaxAgeAtPickup`, `AgentAnswerTimeout`, `PollInterval`); `"Enabled": false` leaves requests untouched. Requests are for paper trading: a Live engine ignores them (`AllowLive`, off, see ADR-0011).

## Strategies: good and risky

There are two strategies, both on the coins you watch (the Coins tab), each with its own filters:
- **good** (the default): strict rules, for results close to what real money would get. It buys only uptrend pullbacks, so it can stay in cash for days in a falling market.
- **risky**: loose rules, for testing the order mechanics (buys, stops, targets, protective sells). It buys whatever passes the filters, even in a downtrend, so expect small losses. Risk per trade and the daily loss stop are the same as good, and the risk gate still applies.

To switch:
1. In `src\Cix.Engine\appsettings.json`, set `"Strategy": { ..., "Name": "risky", "Version": 1 }` (or `"good"`).
2. Run `docker compose restart engine trader`.

The coin filters come with the strategy (its `scanner` parameters), so changing them means a new spec version. The engine records the filters in force in the database (`scanner-filters-paper`) and journals a `scanner-filters` entry with each changed value when they change, and a `scan-eligibility` entry when the coins that pass change. Restarts don't reset either.

The trader calls Claude only when the scanner's signals change (checked every minute), plus a position review every 4 hours (`TRADER_HEARTBEAT_MINUTES` in `.env`). The engine log shows each scan's ranking and why each symbol did or didn't signal (`Scan: ... ranked [...] excluded [...]`). See `strategies\README.md` for the settings.

Every watched coin's price comes from one public tickers request every 5 seconds, so the number of coins is limited only by the candles (one request per coin every 5 minutes) and by the order books, which go to the coins with a position or an order (the `MarketData` section in `appsettings.json`, ADR-0021). The engine refuses to start if the schedule for the most coins the limits allow doesn't fit the public budget of 48 requests a minute.

## Notes

- The trader uses **Sonnet** by default. Its work is mostly following the scanner's signals, and Sonnet does that fast and within your plan's limits. To change it, set `TRADER_MODEL=opus` (or `haiku`, or a full model name) in `.env`, then run `docker compose up -d trader`. Each run's log line lists the models used; Claude Code may also call a small model for side tasks.

- When the engine restarts, the trader restarts too. It waits for the engine's first scan, runs a review, and then runs on new signals.
- Runs are limited: at least 15 minutes apart for signals and at most 30 a day (`TRADER_MIN_GAP_MINUTES`, `TRADER_MAX_RUNS_PER_DAY` in `.env`). Signals are checked every `TRADER_POLL_SECONDS` (60).
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
