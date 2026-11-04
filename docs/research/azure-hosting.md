# Moving CIX to Azure: hosting design at the lowest cost

Oct 26, 2026 · design memo for the owner's review; the decision is [ADR-0013](../adr/0013-engine-and-agents-in-container-apps.md) (proposed). Nothing here is provisioned yet. Issue #80.

## What the owner asked for

- Move everything that runs in Docker on the PC (engine, trader, analyst) to Azure.
- Automate development, provisioning and deployment, so that later features are built and deployed without manual steps.
- Redesign first to keep running and operating costs low.
- Use Azure Storage Queues or other messaging only where they clearly add value.

## What runs today

| Piece | Where | What it does |
| --- | --- | --- |
| Engine (.NET 10) | Docker on the PC, always on | Polls Revolut X public endpoints, writes to Cosmos DB, runs the tool server on `127.0.0.1:5077` |
| Trader loop | Docker, sharing the engine's network | Polls the engine's `/signals` every 60 s. Starts a Claude Code run on new signals, on owner requests and every 4 h. Applies daily caps, refunds runs that never reached Claude, and retries owner requests after 5 minutes |
| Analyst loop | Docker, sharing the engine's network | One Claude Code run every 6 h with web access, retried after 30 minutes if it fails |
| Cosmos DB | Azure, shared free-tier account | Already in Azure, unchanged |
| Dashboard | Azure Static Web Apps Free | Already in Azure and deployed by CI (ADR-0010), unchanged |

Only the engine has to run all the time. The agents run for a few minutes, about 15 to 50 times a day, and do nothing in between. The cheapest design pays for the engine all the time and for the agents only while they run.

## Recommended design

```
GitHub Actions ─OIDC─► ACR (images) ─► Container Apps environment: workload profiles, Consumption profile, no VNet (since 2026-10-28)
                                      ┌───────────────────────────────────────────────────────────┐
 Key Vault ─ managed identities ────► │ app cix-engine  1 replica, 0.25 vCPU / 0.5 GiB           │──► Revolut X
 (Claude token, Cosmos key)           │     tool server: internal ingress, Entra tokens only      │
                                      │           ▲  (role from the token)                        │
 Storage queues ◄── engine schedules ─┼───────────┘                                               │
   runs-trader ─────────────────────► │ job cix-trader            strategy runs                   │
   runs-requests ───────────────────► │ job cix-trader-requests   owner-request runs, no orders   │
   runs-analyst ────────────────────► │ job cix-analyst           market view, web access         │
                                      └───────────────────────────────────────────────────────────┘
 Cosmos DB (shared account, database cix) ◄── engine, dashboard API        Static Web App (unchanged)
```

1. **The engine is a Container App with exactly one replica.** Minimum and maximum replicas are both 1, it uses 0.25 vCPU and 0.5 GiB to start with, and it runs in single-revision mode.
   - Its only ingress is internal: apps and jobs in the same environment can reach it, and the internet can't.
   - A health probe uses the existing `/healthz`.
   - The app sets `ASPNETCORE_ENVIRONMENT=Azure` and `Trading__Mode=Paper` explicitly. .NET treats a missing environment name as Production, which would remove the guard in `TradingMode.cs` that refuses Live outside Production.
2. **The environment uses workload profiles, the Consumption profile, and its own VNet** from the start. It costs the same as a plain Consumption environment. (Corrected 2026-10-28: it doesn't. On a VNet, Azure adds a Standard Load Balancer and a static egress IP, about $22 a month per environment, so the environment no longer has a VNet; ADR-0013, amended.) A fixed outbound IP for the live engine (decided with the live engine) later needs a NAT gateway on that VNet, and a VNet can't be added to an existing environment.
3. **Each agent is a Container Apps Job with its own managed identity**, billed only while it runs.
   - The three jobs are `cix-trader` (strategy runs), `cix-trader-requests` (runs that answer owner requests) and `cix-analyst`.
   - Each job watches its own queue, with `maxExecutions: 1` so runs happen one at a time.
   - `replicaTimeout` is 20 minutes, above the 10-minute run limit, and `replicaRetryLimit` is 0, so a failed run is never repeated behind the engine's back.
4. **The engine schedules every agent run.** The rules now in `run-trader.sh` and `run-analyst.sh` move into a tested scheduler in the engine:
   - the minimum gap, the heartbeat, the analyst's 6 h interval and 30-minute retry;
   - the daily caps, with refunds for runs that never reached Claude;
   - the 5-minute retry for owner requests.

   When a run is due, the engine puts a small message on that job's queue: the run kind, the reason and a dedupe key, never a secret. The job then works like this:
   1. **Claim.** It reads one message and deletes it. If the message has been dequeued 3 times or more, the job moves it to `runs-poison` itself, because Storage queues have no automatic poison queue.
   2. **Check.** It asks the engine whether the run is still wanted. An old or repeated message costs one container start and no Claude usage.
   3. **Report.** It tells the engine how the run ended (not run, done or failed), so the engine can refund the cap and schedule a retry.
5. **The tool server authenticates callers instead of relying on loopback.** Jobs can't share the engine's network namespace the way Compose services do.
   - In Entra mode, the tool server accepts only Entra tokens issued for its audience to the three jobs' identities. Each identity maps to one agent role: `trader`, `trader-requests` or `analyst`.
   - The role comes from the token, and the `X-Cix-Agent` header is ignored. Today `ToolCallContext.RequireAllowedAsync` already keeps each agent to its own tools, keyed on that header. Entra makes the caller's identity unforgeable, so a run carrying the owner's note can never reach `place_order`.
   - `ToolServerOptions` still refuses a non-loopback address unless an audience and identities are configured.
   - `/signals` and the run-check endpoints sit behind the same authentication.
   - Loopback stays the default for Compose.
6. **Owner tools stay off the network in Azure.** `ToolServer:OperatorKey` is not set there, so any request carrying `X-Cix-Operator-Key` is refused. The owner works through the dashboard. The emergency stop is to deactivate the engine's revision and disable the jobs: open orders stay on the exchange, the saved state stays in Cosmos DB, and the kill switch and halts already persist across restarts. A Pause button on the dashboard comes with the live trading switch.
7. **A single-engine lease, one per mode.** Revision changes, restarts and the PC-to-Azure cutover can briefly run two engines.
   - **Holding it.** The engine holds a lease document in `state` (`engine-lease-<mode>`, with an ETag), renewed every 15 s and expiring after 2 minutes.
   - **What needs it.** Anything that acts on the exchange or on shared state: placing orders, direct manual sells, protective exits, reconciliation, claiming owner requests and publishing runs. A second engine of the same mode waits and does none of these.
   - **Handover.** After taking the lease, the engine reloads its state from Cosmos DB. On SIGTERM it releases the lease.
   - Paper and live have separate leases, so they never block each other.
8. **Secrets live in Key Vault** and are read through managed identities: the Claude token. The engine needs no Cosmos DB key: it signs in to the database with its identity. The Revolut X keys come later, for the live engine only, in a Key Vault that CI has no access to. Nothing is stored in GitHub.
9. **Configuration is built into the image.** Strategy files, `appsettings.json` and `config/risk-limits.json` go into the engine image instead of being mounted. Changing them is a pull request that the owner merges and CI deploys, and the hooks that keep risk limits and the live mode owner-only still apply. Runtime strategy selection, a later step, moves the choice of strategy into Cosmos DB.
10. **Logs** go to a Log Analytics workspace with a 0.1 GB daily cap and 30 days' retention, within the free 5 GB a month. The journal in Cosmos DB stays the record. Agent run output, now in `logs/trader`, goes to stdout.
11. **Unchanged:** Cosmos DB (free tier, `cix` at 400 RU/s), the Static Web App and its CI deployment.

## Messaging: where a queue helps and where it doesn't

| Flow | Today | In Azure | Why |
| --- | --- | --- | --- |
| Engine → agent run | Shell loops poll `/signals` or sleep | **Storage queues**, one per job (`runs-trader`, `runs-requests`, `runs-analyst`) | Without a queue, a job would start on a timer just to ask whether there is work: about 1,440 container starts a day. A queue starts a job only when there is work, at a cost of a few cents a month, and the Container Apps queue trigger is built in. The alternative is for the engine to start jobs through the Azure management API, which needs a role that can change resources. The queue needs only send rights on three queues. |
| Owner requests (dashboard → engine) | Cosmos `requests` container, polled by the engine | **Unchanged** | It already works as a durable queue, with the engine's ledger, idempotent retries and a dedupe UUID, verified end to end. A second queue would add a second source of truth. |
| Engine status → dashboard | Cosmos `state` | **Unchanged** | It is read-only state, not messages |
| Journal | Cosmos `journal` | **Unchanged** | It is an append-only record, not a stream to consume |
| Service Bus, Event Grid, Event Hubs | — | **Not used** | Nothing here needs sessions, topics, fan-out or high throughput |

The analyst runs from the engine's schedule rather than a cron job, so its 30-minute retry and the engine's view of when it last ran carry over.

## Monthly cost

Consumption prices (USD, East US, Oct 2026; Poland Central may differ slightly and is re-checked before provisioning): $0.000024 per active vCPU-second and $0.000003 per GiB-second. Each subscription gets 180,000 vCPU-seconds and 360,000 GiB-seconds free a month, and jobs are billed at the same rates while they run. A 30-day month has 2,592,000 seconds.

| Item | Assumption | Typical | At the run caps |
| --- | --- | --- | --- |
| Engine app | 0.25 vCPU / 0.5 GiB, always on, at the active rate, after the free grant | ≈ $14 | ≈ $14 |
| Trader jobs | 0.5 vCPU / 1 GiB; about 15 runs a day × 5 min (caps: 50 × 10 min) | ≈ $2 | ≈ $14 |
| Analyst job | 0.5 vCPU / 1 GiB, 4 runs a day × 10 min | ≈ $1 | ≈ $1 |
| Container Registry | Basic | ≈ $5 | ≈ $5 |
| Storage account (queues), Key Vault | a few thousand operations a day | < $1 | < $1 |
| Log Analytics | under the free 5 GB with a daily cap | $0 | $0 |
| VNet | none since 2026-10-28 (with one: a load balancer and an IP, ≈ $22) | $0 | $0 |
| Cosmos DB, Static Web App | free tier, Free plan | $0 | $0 |
| **Total** | | **≈ $23 a month** | **≈ $35 a month** |

Claude usage is separate and doesn't change. The idle rate is unlikely to apply to the engine: a replica only counts as idle while it uses under 0.01 vCPU and receives under 1,000 bytes a second, and the engine polls all the time.

### Options compared

| Option | Per month | Why not chosen |
| --- | --- | --- |
| **Engine app + agent jobs + queues (recommended)** | ≈ $23–35 | — |
| Agents as sidecars in the engine's Container App | ≈ $73 | Keeps the loopback tool server, but pays for about 1 vCPU / 2 GiB all day for agents that run a few minutes an hour |
| One VM (B2als v2) running Docker Compose as today | ≈ $27–35 + disk | Least code to change, but OS patching, Docker updates, the disk and restarts become the owner's job, and CI deployment needs SSH or run-command access |
| Azure Container Instances | ≈ €37 | No scheduler, no revisions, and only basic restarts (ADR-0005) |
| App Service B1 + Functions | ≈ $13 + jobs | The engine and agents would run in different ways, and Functions suit short runs, not Claude Code sessions of 5 to 10 minutes |

## Automated delivery

Goal: application changes merged to `main` reach Azure with no manual step, nothing reaches Azure without the owner's merge, and CI can't change infrastructure or touch the live engine.

| When | Who | What happens |
| --- | --- | --- |
| Pull request | CI, no Azure access | Build and test (as now), build the images, `bicep build` and lint |
| Merge to `main`, application change | CI as `cix-agents-deploy-<env>` | Push images to ACR tagged with the commit SHA. Update the paper engine app to that image and wait for its new revision to be healthy (its startup and readiness probes), then the jobs. If anything fails, put back the images that ran before and fail the run. |
| Infrastructure change (`infra/`) | The owner, with `provision.ps1` | What-if first, then apply, as today. The template is subscription-scoped and creates role assignments, Key Vault and the budget. Applying it needs Owner-level rights, which CI must not hold. |
| Bootstrap, secret rotation, anything about the live engine | The owner | Run from the owner's own sign-in. The live engine goes into its own resource group and Key Vault, where CI's identities have no role, with its own identity and database. The live database sits in the shared free account (owner decision 5); the paper engine's Cosmos DB roles reach CIX's paper database only, so CI can't reach the live one through them. |

- **`cix-agents-deploy-<env>`** uses an OIDC credential trusted for `main` only, as in ADR-0010. It has `AcrPush` on the registry and a custom role assigned on the registry, the paper engine app and the three jobs: read and update them. It holds nothing on the resource group, so it can't create resources or role assignments. An image it deploys runs with the engine's or a job's identity, and so reaches what they reach (CIX's paper database through the engine's Cosmos DB roles, and `claude-token` through a job's). A run started by hand on `main` signs in with the same identity, which is why that identity has no rights over infrastructure or the live engine.
- **The merge is the approval.** GitHub Free can't require a reviewer for deployments in a private repository. Agents develop on branches and open pull requests, CI must pass, the owner merges, and CI deploys paper.
- **No real orders in CI or validation.** Smoke checks only read state.
- **Cost guard.** An Azure budget on the CIX resource group alerts at $40 a month.

## Code changes the migration needs

1. **Tool server:** an Entra mode that validates tokens for the configured audience and identities and takes the agent role from the token. Options validation refuses a non-loopback address without that configuration. Authentication also covers `/signals` and the run-check and report endpoints. Engine tests, plus the security review.
2. **The single-engine lease**, per mode, guarding every exchange-side and shared-state action. It reloads state when taken and is released on SIGTERM. Tests drive time with `MutableTime`. The PC engine gets this before the cutover.
3. **An agent-run scheduler in the engine:** caps, gap, heartbeat, refunds and retries, carried over from the shell loops. It works with queue publishers behind an interface (a fake in tests) and a run-outcome endpoint. (Built: "Agent runs in Azure" in [architecture.md](../architecture.md). The engine's identity needs "Storage Queue Data Message Sender" on the three run queues.)
4. **Job entrypoints:** a run-once mode in `run-trader.sh` and `run-analyst.sh` that claims one message (deleting it, and moving it to the poison queue after 3 dequeues), checks with the engine, runs and reports the outcome. Compose keeps today's loops. (Built: `tools/trader/agent-job.sh`. The jobs' identities need "Storage Queue Data Message Processor" on their own queue and "Storage Queue Data Message Sender" on `runs-poison`. The jobs reach the engine at its internal https address: Container Apps redirects plain http to https unless ingress allows insecure traffic, and the job script accepts plain http only for a single-label name inside the environment.)
5. **Images:** configuration files built in, logs to stdout. (Already true of both images; in job mode a failed run's stderr goes to stdout too, masked.)
6. **Bicep:** the workload-profiles environment with its VNet (no VNet since 2026-10-28, see item 2), the engine app (explicit environment name and paper mode), three jobs, ACR, a storage account with the queues, Key Vault, Log Analytics, identities and role assignments, and the budget. (Templates built: `infra/modules/agents-platform.bicep` and `agents-apps.bicep`, behind `agents.deploy`, off by default. `provision.ps1` deploys both; for `apps` it creates the engine's Entra app, uploads `claude-token` and checks the images.) Before CI deploys the apps, or anything live runs there, the security review asks for:
   - the engine on Cosmos DB data-plane RBAC, scoped to its database and without delete on the journal, instead of the account key, which writes and deletes across the whole shared account (done: `infra/modules/cosmos-engine-access.bicep` gives the engine's identity one role per container, within CIX's database, with no delete and the journal append-only. Key Vault keeps no Cosmos DB key, and `provision.ps1` deletes the `cosmos-key` an earlier template left there);
   - CI deploying with a custom role that only updates the apps' images, with every role assignment left to the owner's deployments;
   - the engine's Entra app requiring an app role assignment, held only by the three job identities (done: `provision.ps1` sets up the `CIX.Agent` role and its assignments after each deployment, and the engine checks the role in each token as well as the identity);
   - images deployed by digest rather than by tag (done: CI deploys the digests its push gave, and `provision.ps1` resolves them from `-ImageTag` or from what runs);
   - private access to Key Vault and storage (the owner decided on 2026-10-26 that this waits for live: paper's Key Vault holds only `claude-token`, and its storage only the run queues);
7. **CI:** image push, deploy and roll-back jobs, planned by `tools/ci/plan.sh` so that unrelated changes don't deploy. (Built: the `agents-images` and `deploy-agents` jobs and `tools/ci/deploy-agents.sh`, as `cix-agents-deploy-<env>`; see "Deploying the engine and the agents from CI" in [azure.md](../azure.md). Health is the new revision's probes rather than the status document. An engine stopped by hand is left alone. Of the review's asks in item 6, the deploy role and the engine's Cosmos DB roles are done. The deploy role holds no rights on the resource group or over role assignments, only on the registry, the engine and the jobs. It can still change the apps' settings, not only their images, since Azure has no narrower right. The engine's app role and images by digest are done too. Private access to Key Vault and storage waits for live.)

## Cutover

1. With the lease already shipped in the PC engine, the owner provisions Azure. The engine app's revision is deactivated and the jobs are disabled. Setting the minimum replicas to 0 isn't enough, because an internal request would start the engine again.
2. Stop the PC stack with `docker compose down`, without `-v`, so the local data is kept. Then activate the Azure engine and enable the jobs. The lease and the saved state in Cosmos DB carry the paper account over unchanged.
3. Watch for a day: engine status, agent runs, the journal and the cost meter.
4. To roll back, deactivate the Azure revision, disable the jobs and start Compose again. Both use the same database.
5. Once the PC is no longer needed as a fallback, rotate the shared account's read-write key the PC engine used, and keep it only where it is still needed:
   - regenerate the primary key (`az cosmosdb keys regenerate --key-kind primary`) and delete `secrets/cosmos_key` on the PC;
   - give the new key to the other projects that still use one, and to nothing of CIX's: the paper engine and, later, the live engine sign in with their identities;
   - regenerate the secondary key as well if nothing uses it, so no old copy of it works.
   Before live trading starts, do it again for anything that got the key since, and regenerate the read-only keys too, then run `provision.ps1` so the dashboard gets the new one.

## Owner decisions (2026-10-26)

1. **Region: Poland Central**, where the Cosmos DB account is. If Container Apps environments with workload profiles aren't offered there, use **Germany West Central**. Microsoft's workload-profiles page lists the Consumption profile for "all supported regions" without naming them. Before provisioning, `provision.ps1` checks that `Microsoft.App/managedEnvironments` lists the region (`az provider show -n Microsoft.App`) and stops if it doesn't. Prices are re-checked for the chosen region.
2. **Claude credentials:** the `claude setup-token` subscription token, kept in Key Vault. Switch to an API key only if the subscription's limits get in the way.
3. **No NAT gateway now.** The fixed outbound IP is decided together with the live engine. The environment still gets its own VNet, so a NAT gateway can be added later without rebuilding it. (Amended 2026-10-28: the VNet is gone to save its load balancer and IP, so a NAT gateway now means rebuilding the environment on a VNet; ADR-0013.)
4. **The owner's merge is the only gate.** There is no auto-merge for agent pull requests.
5. **Cosmos DB stays free: one account, no second one.** When live comes, the live engine uses its own database in the shared free account, and signs in to it with its own identity and roles, as the paper engine does. The live engine and its Key Vault (the Revolut X keys) go in a separate resource group, `rg-cix-live`, where CI and the agents have no role. The account's read-write key reaches every database, so the cutover rotates it and keeps it only where it is still needed (Cutover, step 5). What risk remains is below.
6. **Paper stays as the dev environment.** Paper keeps running in `rg-cix-dev`, deployed by CI on each merge that changes the images, next to live. Live moves only when the owner promotes an image that has run on paper, pinned by digest, from their own sign-in. Live pulls either from the dev registry by that digest or from a registry of its own (about $5 a month), never by a tag CI can move.

## Risk that remains with live in the shared account

- **The account's keys reach every database in it**, the live one included. Whoever holds the read-write key can change or delete anything there: live state, the kill switch, the lease, and journal entries too. That includes another project, wherever it keeps the key (its own CI, a laptop). Rotating at the cutover and before live trading keeps the number of holders small, but each holder stays trusted with live data. Removing this entirely needs key access turned off for the account (`disableLocalAuth`), which only works once every project signs in with an identity.
- **The read-only key reads the live database**: positions, orders and balances. The dashboard holds it, and the dashboard's CI deploy identity can deploy code that runs with it. This exposes the data, but it can't trade or change state.
- **Control of the account is shared.** Anyone with a role on the account or `rg-shared` can list its keys, change its firewall, or delete it, along with every project's database. Keep those roles to the owner alone. A `CanNotDelete` lock on the account (free) guards against a mistaken delete; it is not set yet.
- **Throughput is shared within the free tier's 1000 RU/s.** Each database has its own throughput, so another project can't throttle live. But a live database at 400 RU/s next to paper's 400 leaves 200 for everything else, and going past 1000 is billed.
- **Backup is the account's.** It uses the account's periodic backups, which only Azure support can restore (into a new account), on the account's schedule and retention. Live's own history is its journal.
- **`provision.ps1` no longer refuses a protected environment in the shared account** (ADR-0017). It warns that the account's keys reach the live database, and lists any service principal with a role on `rg-cix-live` or above it before deploying. The live engine signs in with its own identity. Rotating the account's read-write key before live money is still the owner's step (Cutover, step 5).

## Still open

1. ~~**Engine size.**~~ Checked (2026-10-26): `docker stats` on the PC showed the engine at 0.41% CPU and 125.6 MiB, well inside 0.25 vCPU / 0.5 GiB, so the cost table stands. That was a quiet moment; a reading during an agent run would confirm the peak.
2. ~~**The live-mode hook.**~~ Done (owner's go-ahead, 2026-10-26): it checks the templates under `infra/` as they will be after an edit (no quoted `'Live'` or `'Production'`, and the Paper and Azure lines stay), and blocks `Production` and `ManualRequests:AllowLive` outside tests and docs.
3. ~~**A live database in the shared account.**~~ Decided (owner, 2026-10-26): yes, in its own database, with the live engine in `rg-cix-live` (owner decisions 5 and 6).
