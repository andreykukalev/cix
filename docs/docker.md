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
3. In the repo folder, run:
   ```powershell
   .\tools\setup-secrets.ps1
   ```
   - It creates `secrets\operator_key` (random) and `secrets\claude_token` (you paste the token; nothing is shown).
   - It writes the token's expiry date to `.env`.
   - All three files are git-ignored and readable only by you.
4. Choose what can be traded by editing `config\risk-limits.json` → `AllowedSymbols`. Only you edit this file (see paper-trading.md).

## Start and stop

```powershell
docker compose up -d --build      # build and start everything (first build takes a few minutes)
docker compose ps                 # what is running
docker compose logs -f trader     # each run's report and cost
docker compose logs -f engine     # scans, orders, safety events
docker compose stop trader        # pause the agent (the engine keeps watching the market)
docker compose down               # stop everything; the paper account stays in the cosmos-data volume
```

Run `docker compose down -v` only if you want to wipe the paper account and journal.

## Where things are

- **Journal, paper account, engine state:** in Cosmos. Browse them at http://localhost:1234 (database `cix`, container `journal`).
- **Trader run output:** `logs\trader\run-*.json`.
- **Strategy and risk limits:** mounted read-only from `strategies\` and `config\risk-limits.json`. After changing them, run `docker compose restart engine trader`.
- **Code or appsettings changes:** need a rebuild with `docker compose up -d --build`.

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
- Cosmos ports are published to `127.0.0.1` only. The emulator doesn't check keys, so never publish them more widely. Live trading refuses to run against the emulator.
- The engine reads only `ToolServer__OperatorKey` and `Cosmos__AccountKey` from `/run/secrets`. Any other file there is ignored, so a stray file can't switch to live trading or change a limit.
- The trader image contains no keys. It can use only the `cix` tools (file, shell and web tools are denied), and the order-size hook still applies.
