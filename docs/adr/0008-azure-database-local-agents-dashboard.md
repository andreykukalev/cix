# 0008. Azure Cosmos DB with local agents, and a Static Web Apps dashboard

- Status: accepted; the Cosmos DB account layout is superseded by [0009](0009-shared-cosmos-account.md) (a shared account, CIX in its own database)
- Date: 2026-10-25
- Amends: 0003 (free tier with shared throughput instead of serverless), 0005 (agents stay on the owner's PC for now)

## Context

Paper trading runs on the owner's PC in Docker Compose. The owner wants the data in Azure, where it outlives the PC and can be watched from anywhere, and a dashboard showing what the bot is doing. The engine and the three agents should keep running locally for now. Cost should stay at or near zero.

## Decision

- **Database:** one Azure Cosmos DB for NoSQL account on the **free tier** (1000 RU/s and 25 GB free per subscription), with one database whose 1000 RU/s are shared by the `journal` and `state` containers. The data model is unchanged (docs/persistence.md).
  - The engine connects from Docker over HTTPS in gateway mode, with the account key as a Docker secret (`docker-compose.azure.yml`).
  - Network access is open to any address, so a changing home IP never stops trading; the key is the protection. Key-based metadata writes are disabled, so a leaked key can't delete containers.
- **Dashboard:** a static page on **Azure Static Web Apps, Free plan**, with a **managed Functions API** (Node.js 22) under `/api`.
  - The API reads Cosmos DB through the REST API with the account's **read-only key**, held in the app's settings on the server. The browser never sees a key.
  - The page is public by the owner's choice, so the API leaves out stops, targets, entry prices, resting order prices and free-text halt reasons.
- **Provisioning:** Bicep in `infra/`, settings per environment in `infra/environments/<name>.json`, applied by `tools/azure/provision.ps1` (what-if first, then confirmation).
- **Test resets, an owner-approved exception to CLAUDE.md rule 4:** the owner asked for a way to clear the journal and state when testing a new version. `tools/azure/reset-database.ps1` does it, and it is the only thing that ever deletes journal entries. It is owner-only:
  - A hook (`.claude/hooks/block-database-reset.sh`) blocks agents from running it, deleting Cosmos DB resources, regenerating keys, or wiping the emulator's volume.
  - It refuses environments marked protected, in both the settings file and the account's `cix-protected` tag.
  - It is a dry run unless given `-Apply` and a typed confirmation.
  - Rule 4 still holds for the engine and every agent: nothing they run updates or deletes a journal entry.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Cosmos DB serverless (ADR-0003) | No free tier; pay per request. At this volume either is cents, but the free tier is zero and has predictable throughput |
| Containers with their own throughput | 400 RU/s minimum each; two containers would exceed the free 1000 RU/s together with the database |
| Dashboard on App Service or Container Apps | Always-on cost; Static Web Apps Free hosts the page and a small API for nothing |
| A .NET isolated API (supported on Static Web Apps) | Consistent with the engine, but needs the .NET SDK and NuGet to build and test. Node.js with no runtime packages is testable anywhere, and the Static Web Apps CLI needs Node.js anyway |
| Cosmos DB SDK in the API | A dependency to install and patch for a handful of reads; the REST API with master-key signing is 100 lines and tested against Microsoft's documented example |
| Browser reading Cosmos DB directly | Would expose a key; ruled out |
| Managed identity for the API | Not available to Static Web Apps managed functions; a bring-your-own Function App (Consumption) could use one later |
| Restrict Cosmos DB to the home IP | Trading would stop (safely) whenever the ISP changes the address; offered as a setting (`allowedIpRanges`) |

## Consequences

- One free-tier account per subscription: if the subscription already has one, `enableFreeTier` must be false and the database costs about $58 a month at 1000 RU/s (or about $23 at the 400 RU/s minimum). docs/azure.md lists the charges.
- The engine depends on the internet for its database. A short outage refuses new orders (each must be recorded first) and is retried; it never half-writes state. A PC reboot recovers from Azure as before.
- A public dashboard reveals the portfolio's size, holdings and decisions. It can't reveal keys or trigger anything. Its API caches responses for 20 seconds, so heavy traffic can't use up the throughput the engine needs.
- Moving the agents to Azure later (ADR-0005) reuses the same database and dashboard.
