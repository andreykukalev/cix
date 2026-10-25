# 0009. A Cosmos DB account shared across projects

- Status: accepted
- Date: 2026-10-25

## Context

ADR-0008 gave CIX its own free-tier Cosmos DB account. Only one account per subscription can use the free tier, so a second project in the same subscription would pay full price. The owner will run future projects in the same subscription. They want the free tier in one shared place, with each project keeping its own resources. The first attempt to deploy failed for exactly this reason: the subscription already had a free-tier account.

## Decision

One Cosmos DB account, with a generic name (`cosmos-shared-<suffix>`), lives in its own resource group (`rg-shared`). It has the free tier and holds no databases of its own. Each project adds its own database with its own throughput. CIX's database is `cix` at 400 RU/s, with the `journal` and `state` containers. CIX's other resources (the dashboard) stay in CIX's resource group (`rg-cix-<env>`).

- **Templates.**
  - `infra/shared/main.bicep` (subscription scope) creates the shared group and account.
  - `infra/main.bicep` (subscription scope) creates CIX's group, its dashboard, and its database in the shared account, which it references as `existing`.
  - Settings: `infra/shared/shared.json` and `infra/environments/<env>.json`.
- **Provisioning.**
  - `provision.ps1` deploys the shared account only when it doesn't exist yet, or with `-UpdateShared`. Otherwise it leaves the account as it is, because other projects may depend on it.
  - The scripts find the account by its `shared-role=cosmos` tag in the shared group, never by name alone.
- **Protection moves to CIX's resource group.** The `cix-environment` and `cix-protected` tags are now on `rg-cix-<env>` rather than on the account. The reset script checks them, then touches only CIX's database. It never deletes a database or the account.
- **Keys stay account-wide (the owner's choice).** The engine uses the shared account's key, and the dashboard the read-only key. Either key reaches every project's database in the account. That is acceptable while every project is the owner's and on paper or test data.
- **Throughput.** The free tier's 1000 RU/s covers CIX's 400 plus one more 400 RU/s project. A third project costs about $23 a month.

## Alternatives considered

| Option | Why not |
| --- | --- |
| One account per project (ADR-0008) | Only one can be free per subscription; the next costs about $23 to $58 a month |
| One shared database at 1000 RU/s, per-project containers (`cix-journal`, ...) | All projects compete for the same 1000 RU/s, and container names must become configurable in the engine, dashboard and scripts. The owner preferred a clean database per project |
| Serverless account | Not compatible with the free tier; pennies a month, but billed |
| Per-project access with Cosmos DB role-based access (Entra service principal per project, scoped to its database) | Engine and dashboard would move from keys to Entra credentials. Deferred until a project holds data that others mustn't reach |

## Consequences

- **Free for CIX and one more project;** each project has its own throughput, so one can't starve the other.
- **Shared keys.** A leaked CIX key exposes the other projects' data, and rotating the key affects every project.
- **Shared settings.** IP restrictions and similar account settings now apply to all projects. Changing them is an owner decision, applied with `-UpdateShared`.
- **Other projects** reference the account as `existing` and add a database from their own templates. They mustn't redeploy `infra/shared/main.bicep` with different settings.
- **The engine is unchanged.** It still uses `COSMOS_ENDPOINT`, `Cosmos:Database` and the account key.
