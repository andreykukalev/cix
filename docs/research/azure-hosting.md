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
GitHub Actions ─OIDC─► ACR (images) ─► Container Apps environment: workload profiles, Consumption profile, own VNet
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
2. **The environment uses workload profiles, the Consumption profile, and its own VNet** from the start. It costs the same as a plain Consumption environment. A fixed outbound IP for the live engine (decided with the live engine) later needs a NAT gateway on that VNet, and a VNet can't be added to an existing environment.
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
8. **Secrets live in Key Vault** and are read through managed identities: the Claude token and the Cosmos DB key. The Revolut X keys come later, for the live engine only, in a Key Vault that CI has no access to. Nothing is stored in GitHub.
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
| VNet | no NAT gateway | $0 | $0 |
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
| Merge to `main`, application change | CI as `cix-deploy-<env>` | Push images to ACR tagged with the commit SHA. Update the paper engine app and the jobs to that image. Wait for the new engine's status document (image version, lease held, `manualRequests.state` ok). If it doesn't report healthy, redeploy the previous commit's image and fail the run. |
| Infrastructure change (`infra/`) | The owner, with `provision.ps1` | What-if first, then apply, as today. The template is subscription-scoped and creates role assignments, Key Vault and the budget. Applying it needs Owner-level rights, which CI must not hold. |
| Bootstrap, secret rotation, anything about the live engine | The owner | Run from the owner's own sign-in. The live engine goes into its own resource group and Key Vault, where CI's identities have no role. |

- **`cix-deploy-<env>`** uses an OIDC credential trusted for `main` only, as in ADR-0010. It has `AcrPush` on the registry and a custom role on the paper engine app and the three jobs: read, update, and start or stop executions. A run started by hand on `main` signs in with the same identity, which is why that identity has no rights over infrastructure or the live engine.
- **The merge is the approval.** GitHub Free can't require a reviewer for deployments in a private repository. Agents develop on branches and open pull requests, CI must pass, the owner merges, and CI deploys paper.
- **No real orders in CI or validation.** Smoke checks only read state.
- **Cost guard.** An Azure budget on the CIX resource group alerts at $40 a month.

## Code changes the migration needs

1. **Tool server:** an Entra mode that validates tokens for the configured audience and identities and takes the agent role from the token. Options validation refuses a non-loopback address without that configuration. Authentication also covers `/signals` and the run-check and report endpoints. Engine tests, plus the security review.
2. **The single-engine lease**, per mode, guarding every exchange-side and shared-state action. It reloads state when taken and is released on SIGTERM. Tests drive time with `MutableTime`. The PC engine gets this before the cutover.
3. **An agent-run scheduler in the engine:** caps, gap, heartbeat, refunds and retries, carried over from the shell loops. It works with queue publishers behind an interface (a fake in tests) and a run-outcome endpoint. (Built: "Agent runs in Azure" in [architecture.md](../architecture.md). The engine's identity needs "Storage Queue Data Message Sender" on the three run queues.)
4. **Job entrypoints:** a run-once mode in `run-trader.sh` and `run-analyst.sh` that claims one message (deleting it, and moving it to the poison queue after 3 dequeues), checks with the engine, runs and reports the outcome. Compose keeps today's loops. (Built: `tools/trader/agent-job.sh`. The jobs' identities need "Storage Queue Data Message Processor" on their own queue and "Storage Queue Data Message Sender" on `runs-poison`. The jobs reach the engine at its internal https address: Container Apps redirects plain http to https unless ingress allows insecure traffic, and the job script accepts plain http only for a single-label name inside the environment.)
5. **Images:** configuration files built in, logs to stdout. (Already true of both images; in job mode a failed run's stderr goes to stdout too, masked.)
6. **Bicep:** the workload-profiles environment with its VNet, the engine app (explicit environment name and paper mode), three jobs, ACR, a storage account with the queues, Key Vault, Log Analytics, identities and role assignments, and the budget.
7. **CI:** image push, deploy and roll-back jobs, planned by `tools/ci/plan.sh` so that unrelated changes don't deploy.

## Cutover

1. With the lease already shipped in the PC engine, the owner provisions Azure. The engine app's revision is deactivated and the jobs are disabled. Setting the minimum replicas to 0 isn't enough, because an internal request would start the engine again.
2. Stop the PC stack with `docker compose down`, without `-v`, so the local data is kept. Then activate the Azure engine and enable the jobs. The lease and the saved state in Cosmos DB carry the paper account over unchanged.
3. Watch for a day: engine status, agent runs, the journal and the cost meter.
4. To roll back, deactivate the Azure revision, disable the jobs and start Compose again. Both use the same database.

## Owner decisions (2026-10-26)

1. **Region: Poland Central**, where the Cosmos DB account is. If Container Apps environments with workload profiles aren't offered there, use **Germany West Central**. Microsoft's workload-profiles page lists the Consumption profile for "all supported regions" without naming them. Before provisioning, `provision.ps1` checks that `Microsoft.App/managedEnvironments` lists the region (`az provider show -n Microsoft.App`) and stops if it doesn't. Prices are re-checked for the chosen region.
2. **Claude credentials:** the `claude setup-token` subscription token, kept in Key Vault. Switch to an API key only if the subscription's limits get in the way.
3. **No NAT gateway now.** The fixed outbound IP is decided together with the live engine. The environment still gets its own VNet, so a NAT gateway can be added later without rebuilding it.
4. **The owner's merge is the only gate.** There is no auto-merge for agent pull requests.

## Still open

1. **Engine size.** Running `docker stats` on the PC for a day shows whether 0.25 vCPU / 0.5 GiB is enough. The cost table assumes it is.
2. **The live-mode hook.** It doesn't yet see `'Live'` written in Bicep (`infra/**`). Hooks are the owner's to change. Extending it, together with `ManualRequests:AllowLive`, would close that gap before infrastructure files can mention the live engine.
