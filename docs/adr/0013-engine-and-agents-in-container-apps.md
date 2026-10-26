# 0013. Engine and agents in Azure Container Apps, at the lowest cost

- Status: proposed
- Date: 2026-10-26
- Amends: 0005 (agents run as queue-triggered jobs; the tool server authenticates callers instead of relying on loopback), 0008 (the engine and agents leave the owner's PC)

## Context

The engine, trader and analyst run in Docker Compose on the owner's PC; the database and the dashboard are already in Azure. The owner wants everything in Azure, with development, provisioning and deployment automated, and with low running and operating costs.

- Only the engine needs to be always on. The agents run for a few minutes, about 15 to 50 times a day.
- In Compose, the agents reach the tool server over loopback by sharing the engine's network namespace. Container Apps Jobs can't do that.

The research memo is [docs/research/azure-hosting.md](../research/azure-hosting.md).

## Decision

Run the engine as a Container App and the agents as Container Apps Jobs. They share one environment with workload profiles, the Consumption profile and its own VNet.

- **Region.** Poland Central, next to Cosmos DB. If workload-profile environments aren't offered there, Germany West Central.
- **Engine.** Exactly one replica in single-revision mode, with internal ingress only. `ASPNETCORE_ENVIRONMENT=Azure` and `Trading__Mode=Paper` are set explicitly.
- **Lease.** The engine takes a lease per mode in `state` before any exchange-side or shared-state action. It renews the lease every 15 s, and the lease expires after 2 minutes. On taking the lease the engine reloads its state, and on shutdown it releases the lease.
- **Agents.** There are three jobs, `cix-trader`, `cix-trader-requests` and `cix-analyst`. Each has its own managed identity and its own Storage queue, with `maxExecutions: 1` and no retries.
- **Scheduling.** The engine schedules every run: the caps, gap, heartbeat, refunds and retries that live in the shell loops today. A job claims and deletes its message, checks with the engine that the run is still wanted, and reports how it ended.
- **Tool server.** It accepts Entra tokens from the three identities only, and takes the agent role from the token rather than from `X-Cix-Agent`. No operator key is set in Azure. Loopback stays the default for Compose.
- **Secrets** live in Key Vault, read through managed identities. Claude uses the `claude setup-token` subscription token, and an API key only if the subscription's limits get in the way.
- **Configuration** is built into the image.
- **Messaging.** Only the three run queues. Owner requests keep the Cosmos DB `requests` container.
- **Delivery.**
  - CI uses an OIDC identity trusted for `main`, as in ADR-0010. With it, CI pushes images and rolls the paper app and jobs forward after the owner merges.
  - If the new engine doesn't report healthy, CI redeploys the previous image.
  - Infrastructure changes and anything about the live engine stay with the owner's own sign-in (`provision.ps1`).

## Alternatives considered

| Option | Why not |
| --- | --- |
| Agents as sidecars of the engine app | Keeps loopback, but pays for about 1 vCPU / 2 GiB all day (≈ $73 a month) |
| One VM running Compose | Little code change; OS patching, Docker updates and deployment access become the owner's work |
| Azure Container Instances | No scheduler or revisions (ADR-0005) |
| Jobs on a timer that poll `/signals` | About 1,440 container starts a day just to find out there's nothing to do |
| The engine starting jobs through the Azure management API | Gives the engine a role that can change Azure resources; the queues need only send rights |
| Service Bus, Event Grid | Nothing here needs topics, sessions or fan-out |
| CI applying Bicep | The subscription-scoped template creates role assignments, so CI would need Owner-level rights that anyone who can change `main` would hold |
| One job for strategy and owner-request runs | One identity for both, so a token couldn't keep owner-request runs away from order tools |

## Consequences

- **Cost.** About $23 a month in typical use and about $35 at the run caps, plus Claude usage (memo, "Monthly cost").
- **Security review.** The tool server can be reached over the network inside the environment, so its authentication is security-critical.
- **Testable run rules.** The trader and analyst loops' rules move into the engine, where they can be tested. Compose keeps the loops.
- **Approval gate.** A merge to `main` deploys paper, and there is no auto-merge. The owner's merge is the gate, because GitHub Free has no required reviewers for deployments in a private repository.
- **Infrastructure stays manual.** Infrastructure changes remain a manual, owner-run step.
- **No NAT gateway yet.** The live engine's fixed outbound IP is decided with the live engine. The VNet is there from the start.
- **Paper and live later** means a second engine app with its own lease, identity, Key Vault and resource group, which CI can't reach.
