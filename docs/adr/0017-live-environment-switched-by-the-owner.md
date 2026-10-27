# 0017. The live environment: paper's templates, switched on by the owner

- Status: accepted
- Date: 2026-10-27

## Context

Live trading runs next to paper in Azure (epic #72): the live engine and its Key Vault in their own resource group, `rg-cix-live`, with their own database in the shared Cosmos DB account, where CI and the agents have no role (owner decisions 5 and 6 in the [hosting memo](../research/azure-hosting.md)). The first run is 100 EUR on the fixed Safe strategy.

Three things pull against each other:

- **The switch to live must be the owner's alone** (CLAUDE.md rule 6). The templates in `infra/` are files agents edit, and the `protect-live-mode` hook and a CI check keep any `Live` or `Production` out of them. A template or parameter file that set the live mode would be one agent edit away from trading.
- **Paper holds no trading credentials.** A Paper engine with the exchange keys refused to start (#68).
- **A container that keeps exiting fails the deployment.** Azure Resource Manager waits for the new revision, and a crash-looping one ends the deployment with an error, so a template that deploys a Paper engine with the keys could never complete.

The owner chose, on 2026-10-27, to switch the live engine on with one command of their own.

## Decision

The live environment, `prod` (`infra/environments/prod.json`), uses paper's templates, marked `protected`. The engine stays pinned to Paper and to the `Azure` environment there as everywhere, and the owner switches it with one `az containerapp update` from their own sign-in ([live-trading.md](../live-trading.md), "Switching the engine on").

- **The keys, in the engine only.** In a protected environment, `agents-apps.bicep` gives the engine's identity read access to `revolutx-api-key` and `revolutx-private-key` in the environment's own Key Vault, and mounts them as the engine's secret files `RevolutX__ApiKey` and `RevolutX__PrivateKeyPem` (`exchangeKeys: protectedEnvironment` in `main.bicep`). Paper's engine and every agent job never get them. A CI check allows those two lines in that one file only, and requires the guard.
- **Standby.** A Paper engine that finds either key file reads neither and starts nothing: no market data, database, tool server or agent runs. It answers `/healthz` with `standby` and the reason, so the revision is healthy and the deployment completes, and logs the file names, never their contents. The live engine is in that state after every deployment of its environment, until the owner switches it again.
- **The switch.** The owner's command sets the trading mode, `ASPNETCORE_ENVIRONMENT=Production` (the only environment where the engine accepts the live mode) and the strategy (Safe v2, which only the owner can mark `live`). It starts a new revision, which reads the keys, the live limits file and the spec, or refuses to start.
- **Images promoted from paper.** `provision.ps1 -PromoteFrom dev -ImageTag <commit SHA>` copies the images CI tagged with that commit from dev's registry into live's own registry by digest (`az acr import`), then deploys those digests, and refuses if the images it would deploy aren't the ones it copied. Live never pulls by a tag CI can move, and its registry is its own, so no live identity needs a role in the group CI reaches.
- **The keys uploaded from files.** `provision.ps1 -ExchangeApiKeyFile ... -ExchangePrivateKeyFile ...` passes each file to `az keyvault secret set --file`, so a key is never in a variable, on a command line or in output. The apps deployment refuses to start while either secret is missing.
- **Nothing outside reaches it.** CI never deploys a protected environment (`setup-ci-deploy.ps1` refuses). Before deploying one, `provision.ps1` lists every service principal with a role on its group or above it, the subscription included, and asks before going on. The environment's own identities, whose roles are on single resources inside the group, aren't listed.
- **The shared account.** This replaces the refusal of a protected environment in the shared account (`-AllowProtectedInSharedAccount`, ADR-0009) with a warning, per owner decision 5. The live engine signs in with its own identity, with roles on the live database only.

## Alternatives considered

| Option | Why not |
| --- | --- |
| A live template, or a mode parameter the owner sets in a parameters file | The live mode would then be written in the repository, in files agents edit. The hook and the CI check would need an exception for it, and an agent's edit could switch live on. |
| A Paper engine with the keys refuses to start (#68 as it was) | The deployment fails on the crash-looping revision, so the template could never deploy the live engine with its keys mounted. Standby keeps the same rule (Paper never reads the keys) and lets the deployment complete. |
| Mount the keys only when the owner switches (a second command adding the volume) | A longer command that is easier to get wrong, with the secret references and role assignments made by hand instead of by the template. |
| Pull from dev's registry by digest | Needs the live identity to hold a role in dev's group, which CI reaches, and ties live to dev's registry. A registry of live's own costs about $5 a month. |

## Consequences

- Every deployment of the live environment, a promotion included, puts the engine back on standby: the owner runs the switch again after it. That is the fail-safe direction.
- The live environment has its own Container Apps environment, registry, Key Vault, queues, logs and budget (20 a month), and the three agent jobs for the live engine, with their own identities.
- Its outbound IP is probably not paper's (another Container Apps environment), but that isn't guaranteed. The market-data budget across both engines is #67.
- No dashboard is deployed for live yet: the owner watches the live engine through its logs, and its journal and balance snapshots in `cix-live`.
- The switch command needs the owner's own Azure sign-in, and `az containerapp` (an Azure CLI extension it may offer to install).
