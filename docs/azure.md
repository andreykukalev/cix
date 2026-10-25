# CIX in Azure: database and dashboard

The engine and the agents keep running in Docker on your PC. Only the database moves to Azure, plus a read-only dashboard you can open from anywhere. The Cosmos DB account is shared with your other projects, in its own resource group; CIX keeps its own resource group and its own database in that account. Decision records: [ADR-0008](adr/0008-azure-database-local-agents-dashboard.md), [ADR-0009](adr/0009-shared-cosmos-account.md).

```
Your PC (Docker Compose)                                   Azure
                                                           rg-shared (shared by your projects)
┌───────────────────────────────┐   HTTPS 443, account key   ┌──────────────────────────────────────┐
│ engine ── trader, analyst     │ ─────────────────────────► │ Cosmos DB cosmos-shared-<suffix>     │
│ (paper exchange, risk gate)   │                            │ (free tier: 1000 RU/s for the account)│
└───────────────────────────────┘                            │   database cix (400 RU/s):           │
                                                             │     journal, state                   │
                                                             │   (other projects' databases)        │
                                                             └──────────────▲───────────────────────┘
                                                           rg-cix-dev       │ read-only key (server side)
Browser ── https://<name>.azurestaticapps.net ── /api (managed Functions) ───┘
           Static Web App stapp-cix-dev, Free plan
```

Nothing in this guide runs by itself. You provision, switch and reset, each with its own command.

## What it costs

| Resource | Plan | Expected cost |
|---|---|---|
| Shared Cosmos DB account (`rg-shared`) | Free tier | **$0** for the first 1000 RU/s and 25 GB across all its databases |
| CIX's database `cix`, 400 RU/s shared by `journal` and `state` | Inside the free tier | **$0**. It leaves 600 RU/s of the free tier for other projects. |
| Static Web App with managed API (`rg-cix-dev`) | Free | **$0** (includes 100 GB of bandwidth a month) |
| Data sent from Azure to your PC | | Well under the free 100 GB a month |

Charges you could see:
- **More than 1000 RU/s across all databases in the shared account.** The part over 1000 is billed, about $5.80 a month per 100 RU/s. A database needs at least 400 RU/s, so CIX plus one more project at 400 stay free, and a third project adds about $23 a month.
- **Another free-tier account already exists in the subscription.** Only one account per subscription can have the free tier, so the first deployment of the shared account fails. Use another subscription, or set `enableFreeTier` to `false` in `infra/shared/shared.json` and expect about $23 a month for CIX's 400 RU/s.
- **Storage over 25 GB**, at about $0.25 per GB a month. The journal grows by a few MB a day.

Nothing else is created: no Key Vault, no App Service, no Container Apps, no monitoring workspace.

## Before you start

- Windows PowerShell 5.1 (built into Windows 10 and 11) or PowerShell 7. The scripts run on both; CI tests them on both. If Windows blocks scripts, start them with `powershell -ExecutionPolicy Bypass -File .\tools\azure\provision.ps1 ...`.
- Azure CLI 2.60 or later (`winget install Microsoft.AzureCLI`). The scripts install Bicep for it when needed.
- Node.js 22 LTS, only to publish the dashboard (`winget install OpenJS.NodeJS.LTS`).
- An Azure subscription where you can create resource groups.

## 1. Settings

Two settings files, both ready to use:

`infra/shared/shared.json`, the shared Cosmos DB account (one per subscription, used by all your projects):

| Setting | Meaning |
|---|---|
| `resourceGroup`, `location` | Its resource group (`rg-shared`) and region (`northeurope` is close to Vilnius) |
| `accountName` | Empty gives a generic name, `cosmos-shared-<suffix>`. The scripts find the account by its `shared-role=cosmos` tag |
| `enableFreeTier` | See costs above |
| `allowedIpRanges` | Empty: any address may connect, and the key protects it. Add your public IP to restrict access; trading then stops (safely) whenever your ISP changes it, until you update the setting and redeploy with `-UpdateShared` |

`infra/environments/dev.json`, CIX's environment:

| Setting | Meaning |
|---|---|
| `environmentName` | Short name, part of resource names and the `cix-environment` tag the scripts check |
| `protected` | `true` for an environment with real money: the reset script refuses it |
| `subscriptionId` | Pin a subscription, or leave empty to choose at the prompt (or pass `-SubscriptionId`). Don't commit a real ID |
| `resourceGroup`, `location` | CIX's own resource group (`rg-cix-dev`), which holds the dashboard |
| `dashboardLocation` | Static Web Apps Free runs in a few regions only: `westeurope`, `centralus`, `eastus2`, `westus2`, `eastasia` |
| `sharedCosmos` | Which shared settings file to use (`shared` means `infra/shared/shared.json`) |
| `cosmos.databaseName`, `cosmos.databaseThroughput` | CIX's database in the shared account, and its throughput (400 to 1000 RU/s; 400 leaves room for other projects) |
| `deployDashboard` | Create the Static Web App |

No secrets go in these files. `prod.example.json` shows a protected environment, with its own database name.

## 2. Provision

```powershell
.\tools\azure\provision.ps1 -Environment dev -SubscriptionId <id> -WhatIfOnly   # preview: what would be created
.\tools\azure\provision.ps1 -Environment dev -SubscriptionId <id>               # create it
```

The script:
1. Checks the Azure CLI.
2. Signs you in with `az login` if needed, and selects the subscription.
3. **Shared account.** If `rg-shared` has no account tagged `shared-role=cosmos` yet, it shows a what-if preview of `infra/shared/main.bicep` (the resource group and the account, no databases), asks, then deploys it. An existing shared account is left exactly as it is; pass `-UpdateShared` to apply the template to it (for example after changing `allowedIpRanges`).
4. **CIX.** It shows a what-if preview of `infra/main.bicep` (`rg-cix-dev`, the dashboard, and the `cix` database with its two containers in the shared account), asks, then deploys it.
5. Prints the Cosmos DB endpoint and the dashboard URL.

With `-WhatIfOnly` and no shared account yet, only the shared account's preview is shown: CIX's preview needs the account to exist.

It then writes the engine's settings:
- `COSMOS_ENDPOINT` and `COSMOS_DATABASE` go into `.env`.
- The shared account's key goes into `secrets\cosmos_key`, readable only by you. The key is never printed. It is account-wide: it reaches every project's database in the shared account (see Limits).

Pass `-SkipEngineConfig` to leave these two files alone.

The deployment is safe to run again. It brings the resources back to what the template describes and keeps the data.

## 3. Switch the engine to Azure

The engine keeps using the local emulator until you switch it:

```powershell
docker compose stop
docker compose -f docker-compose.yml -f docker-compose.azure.yml up -d --build
docker compose ps        # the engine shows (healthy) once it has loaded its state from Azure
```

- Azure starts empty, so the engine starts a **fresh paper account** (200 EUR). The emulator's data stays in its Docker volume, untouched.
- Every later Compose command needs both `-f` files, for example `docker compose -f docker-compose.yml -f docker-compose.azure.yml logs -f engine`. To avoid typing them, add `COMPOSE_FILE=docker-compose.yml;docker-compose.azure.yml` to `.env`. On Windows the separator is `;`.
- To go back to the emulator, run `docker compose -f docker-compose.yml -f docker-compose.azure.yml stop`, then `docker compose up -d`.

Trading mode doesn't change. It stays paper unless you set live yourself (CLAUDE.md rule 6).

## 4. Publish the dashboard

```powershell
.\tools\azure\provision.ps1 -Environment dev -DeployDashboard -SkipInfrastructure -SkipEngineConfig
```

This installs the API's one dependency, `@azure/functions` (pinned, with install scripts disabled), into `dashboard/api`. It then publishes `dashboard/app` and `dashboard/api` with the Static Web Apps CLI, using the app's deployment token. The token is read into an environment variable for that command only, and never printed. Run the same command again after changing the dashboard.

The dashboard shows:
- **Market briefing** (at the top, for stepping in by hand): the analyst's latest view (from the last 24 hours) and reasoning, up to five highlights (the most important news and scheduled events, each marked risk, opportunity or watch, with its coins and a source link), and a short **Needs attention** list picked from the latest scan: buy and sell signals, moves of 5% or more in 24 hours or 2% in an hour, volume at 2.5 times its usual level, an hourly range of 2% or more, coins the scanner excluded as too volatile, too thin or without fresh data, and coins the analyst said to avoid. A falling coin you hold ranks higher. The full scan of every coin is one click away. Highlights are only as fresh as the analyst's last run (every `ANALYST_INTERVAL_HOURS`, 6 by default); a shorter interval gives fresher news at the cost of more Opus runs.
- **Engine:** running, or not responding when the heartbeat is older than 3 minutes. Also its strategy, and the kill switch with who engaged it and why, as a category.
- **Mode:** paper or live, a toggle at the top. Live shows data only if a live engine has written it.
- **Market:** the analyst's market view, how fresh the prices are, and the last sync with the exchange and with the database.
- **Portfolio:** equity, cash, today's P&L, peak, drawdown, exposure, holdings, and the paper account's balances.
- **Trading cycle:** the last scan and its signals, orders still working (no prices), and the trader's and analyst's recent runs.
- **History:** executed trades, decisions with the agent's reasoning, and errors and safety events.
- **Records:** journal activity, browsable by day and kind, and the state documents with what each one is.

It is **read-only and public** (anyone with the URL can open it):
- Its API can only `GET`, and it reads with the account's read-only key, kept in the app's settings on the server.
- It shows **no order levels**: no entry, stop, target or fill prices, and no limits of resting orders. In the journal's free text (summaries, the trader's reasoning) every number that could be a price is masked as `…`. Holdings show their value, and whether a stop is set. The journal in Cosmos DB keeps everything.
- The briefing shows the analyst's own text unmasked: the analyst never sees the account's positions, stops or targets, so its text can't hold them, though it may quote public market prices. It is written from web pages, so the page shows it as plain text only, and opens only `https` source links (each must be one of the view's sources), in a new tab without access back to the dashboard. The coins' moves are percentages, never prices.
- Responses are cached for 20 seconds, and identical requests share one database read. The journal browser accepts only the last 14 days, known kinds and three page sizes, so heavy traffic can't use up the database throughput the engine needs.
- To make it private later, add `"allowedRoles": ["owner"]` to the `/*` and `/api/*` routes in `dashboard/app/staticwebapp.config.json`, then invite yourself under *Role management* in the Azure portal (the Free plan supports this).

## How the PC connects to Azure

| What | How |
|---|---|
| Engine to Cosmos DB | HTTPS on port 443 in gateway mode, authenticated with the account key. The key is a Docker secret (`/run/secrets/Cosmos__AccountKey`, from `secrets/cosmos_key`), never in an image, `.env`, the repository or the logs. |
| What the key can do | Read and write documents. It can't create or delete containers: key-based metadata writes are disabled, and schema changes go through Azure Resource Manager with your Azure sign-in. |
| Dashboard API to Cosmos DB | The read-only key, in the Static Web App's settings, used server side only. |
| Your scripts | Your Azure CLI sign-in (`az login`). The scripts read keys into memory when needed and never print them. |
| Rotating the engine's key | `az cosmosdb keys regenerate --kind primary ...`, then run `provision.ps1 -Environment dev` again (it rewrites `secrets/cosmos_key`), then `docker compose ... restart engine`. The key belongs to the shared account, so every project using it needs the new key. |

## Reliability

Recovery works the same against Azure as against the emulator: [persistence.md](persistence.md#recovery).
- **After a PC reboot**, Docker starts everything again. The engine waits for Azure, retrying with growing pauses, and loads its state. It reconciles with the paper account, then resumes. The agents start when the engine reports healthy.
- **When Azure can't be reached briefly:**
  - New orders are refused, because each must be recorded in the state first. The engine's own stop and target sells are retried.
  - Journal writes that fail are logged.
  - The status record marks the database `unreachable`, so `/healthz` answers `degraded` and the dashboard shows the heartbeat going stale.
  - Nothing is half-written: each change is one document write, guarded by an ETag for the engine state.
  - When the connection returns, everything carries on by itself.
- **Long outages:** after 15 minutes without fresh market data the safety monitor halts trading, as before.

## Resetting the database (testing only)

`tools/azure/reset-database.ps1` clears data for a fresh test run, for example after releasing a new version. It never runs by itself: not during deployment, engine startup, or from the dashboard.

```powershell
# 1. See what would go (always a dry run without -Apply)
.\tools\azure\reset-database.ps1 -Environment dev -Target All

# 2. Stop the engine and agents (the script refuses while the engine's heartbeat is fresh)
docker compose -f docker-compose.yml -f docker-compose.azure.yml stop engine trader analyst

# 3. Reset, then type the confirmation it asks for ("reset dev")
.\tools\azure\reset-database.ps1 -Environment dev -Target State -Apply      # bot state only (paper)
.\tools\azure\reset-database.ps1 -Environment dev -Target Journal -Apply    # journal only

# 4. Start again
docker compose -f docker-compose.yml -f docker-compose.azure.yml up -d
```

| Option | Effect |
|---|---|
| `-Target State` | Removes `engine-state-<mode>`, `engine-status-<mode>` and, for paper, `paper-account`. The engine then starts with a fresh paper account. |
| `-Target Journal` | Removes journal entries: all of them, or with `-JournalBefore 2026-10-20` only earlier days. |
| `-Target All` | Both. |
| `-Mode paper` (default), `live`, `all` | Which mode's state documents. Removing a live state makes the engine start halted until you confirm, and the confirmation to type becomes `reset dev including live state`. |
| `-IncludeSpecArchive` | Also removes the archived strategy specs (`spec-*`). Kept by default, because journal entries refer to them. |
| `-ResetSchema` | Deletes the selected containers and creates them again from `infra/main.bicep`, with the same partition keys and settings. Quicker for a big journal. Needs `-Mode all` with State. |

Safeguards:
- It refuses environments marked `protected`, in the settings file or in the `cix-protected` tag on CIX's resource group (set by the template from the same setting). The group must also be tagged `cix-environment=<environment>`.
- Agents can't run it: a hook blocks the script for every Claude agent, along with deleting Cosmos DB resources, regenerating keys, or `docker compose down -v`. It is the one thing allowed to delete journal entries, an owner-approved exception to the append-only rule ([ADR-0008](adr/0008-azure-database-local-agents-dashboard.md)).
- It acts only on CIX's database (`cosmos.databaseName`) in the single shared account (tagged `shared-role=cosmos` in the shared resource group), and checks the database exists first. Other projects' databases are never touched.
- It touches only the `journal` and `state` containers. It never deletes the database or the shared account.
- It refuses while the engine is running.
- It deletes nothing without `-Apply` and the typed confirmation, and prints a summary of what was planned and what was done.

The local emulator is reset differently: `docker compose down -v` removes its volume.

## Limits

- **One free-tier account per subscription** (see costs), which is why it is shared.
- **The shared account's keys reach every project's database in it.** CIX's key (and the dashboard's read-only key) could read, and CIX's key write, other projects' data, and theirs CIX's. Acceptable while every project is yours and on paper or test data. Per-project access would need Cosmos DB role-based access with an Entra identity per project ([ADR-0009](adr/0009-shared-cosmos-account.md)).
- **Other projects** add their own database to the shared account from their own templates (as `infra/modules/cosmos-database.bicep` does), with `existing` references to the account, and must keep the total throughput within 1000 RU/s to stay free.
- **No managed identity.** Static Web Apps managed functions can't use one, so the dashboard holds the read-only key in its settings. A separate Function App on the Consumption plan could use a managed identity if that ever matters.
- **No data migration.** Data isn't copied from the emulator; Azure starts fresh by design.
- **No lockfile for the API yet.** The package registry wasn't reachable where this was built, so `@azure/functions` is pinned to an exact version but its own dependencies aren't locked. Run `npm install --package-lock-only` in `dashboard/api` once and commit `package-lock.json`. The Static Web Apps CLI version is pinned in `provision.ps1` (`$SwaCliVersion`).
- **Not tested against Azure here.** The templates and scripts are validated (Bicep build and lint, PowerShell parsing, request signing against Microsoft's documented example, dashboard API tests), but nothing has been deployed from this repository yet. Run `-WhatIfOnly` first.
