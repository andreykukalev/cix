# CIX in Azure: database and dashboard

The engine and the agents keep running in Docker on your PC. Only the database moves to Azure, plus a read-only dashboard you can open from anywhere. The Cosmos DB account is shared with your other projects, in its own resource group; CIX keeps its own resource group and its own database in that account. Decision records: [ADR-0008](adr/0008-azure-database-local-agents-dashboard.md), [ADR-0009](adr/0009-shared-cosmos-account.md).

```
Your PC (Docker Compose)                                   Azure
                                                           rg-shared (shared by your projects)
┌───────────────────────────────┐   HTTPS 443, account key   ┌──────────────────────────────────────┐
│ engine ── trader, analyst     │ ─────────────────────────► │ Cosmos DB cosmos-shared-<suffix>     │
│ (paper exchange, risk gate)   │                            │ (free tier: 1000 RU/s for the account)│
└───────────────────────────────┘                            │   database cix (400 RU/s):           │
                                                             │     journal, state, requests         │
                                                             │   (other projects' databases)        │
                                                             └──────────────▲───────────────────────┘
                                                           rg-cix-dev       │ read-only key (server side)
Browser ── https://<name>.azurestaticapps.net ── /api (managed Functions) ───┘
           Static Web App stapp-cix-dev, Free plan
```

Nothing in this guide runs by itself. You provision, switch and reset, each with its own command.

## Adding the `requests` container to an existing database

Manual Buy and Sell requests (ADR-0011) need a third container, `requests`. The engine can't create containers in Azure, so a database provisioned before #63 needs the template once more. It adds the container and changes nothing else (no data is moved, reset or deleted):

```powershell
.\tools\azure\provision.ps1 -Environment dev -SkipEngineConfig
docker compose -f docker-compose.yml -f docker-compose.azure.yml restart engine
```

The what-if preview should show only the new container. Until it exists, the engine runs as before and its status reports manual requests as `unavailable`.

## What it costs

| Resource | Plan | Expected cost |
|---|---|---|
| Shared Cosmos DB account (`rg-shared`) | Free tier | **$0** for the first 1000 RU/s and 25 GB across all its databases |
| CIX's database `cix`, 400 RU/s shared by `journal`, `state` and `requests` | Inside the free tier | **$0**. It leaves 600 RU/s of the free tier for other projects. |
| Static Web App with managed API (`rg-cix-dev`) | Free | **$0** (includes 100 GB of bandwidth a month) |
| Data sent from Azure to your PC | | Well under the free 100 GB a month |

Charges you could see:
- **More than 1000 RU/s across all databases in the shared account.** The part over 1000 is billed, about $5.80 a month per 100 RU/s. A database needs at least 400 RU/s, so CIX plus one more database at 400 stay free (a CIX prod environment counts as one), and a third adds about $12 a month (the 200 RU/s over 1000).
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
| `resourceGroup`, `location` | Its resource group (`rg-shared`) and region: `polandcentral`, close to Vilnius (North Europe had no Cosmos DB capacity for new accounts in October 2026). If a region refuses with "high demand", delete the failed `rg-shared` and pick another. Once the account exists, keep `location` equal to its region: an account or group can't move, so `-UpdateShared` with another region fails |
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
| `dashboardLocation` | Static Web Apps Free runs in a few regions only: `westeurope`, `centralus`, `eastus2`, `westus2`, `eastasia`. `eastus2`, because West Europe refused new customers in October 2026; only the dashboard's small API runs there, the page is served worldwide. Its reads cross to the database's region (about 0.1 s each; the few MB a day of cross-region traffic cost effectively nothing) |
| `sharedCosmos` | Which shared settings file to use (`shared` means `infra/shared/shared.json`) |
| `cosmos.databaseName`, `cosmos.databaseThroughput` | CIX's database in the shared account, and its throughput (400 to 1000 RU/s; 400 leaves room for other projects) |
| `deployDashboard` | Create the Static Web App |
| `agents` | The engine and the agent jobs in Azure (ADR-0013). `deploy`: `none` (the default), `platform` or `apps`, see [Engine and agents in Azure](#engine-and-agents-in-azure). `location`: their region, where the database is (`polandcentral`). `startEngine` stays `false` until the cutover. `traderModel`, `analystModel`, `claudeTokenExpires` as in Compose. `monthlyBudget` (40) and `budgetStartDate` set the resource group's monthly budget; the start date can't be moved once the budget exists |

No secrets go in these files. `prod.json` is the protected (live-money) environment: `rg-cix-live`, its own database `cix-live`, no dashboard, and an engine that holds the exchange keys and stands by until the owner switches it ([live-trading.md](live-trading.md), ADR-0017).

## 2. Provision

```powershell
.\tools\azure\provision.ps1 -Environment dev -SubscriptionId <id> -WhatIfOnly   # preview: what would be created
.\tools\azure\provision.ps1 -Environment dev -SubscriptionId <id>               # create it
```

The script:
1. Checks the Azure CLI.
2. Signs you in with `az login` if needed, and selects the subscription.
3. **Shared account.** If `rg-shared` has no account tagged `shared-role=cosmos` yet, it shows a what-if preview of `infra/shared/main.bicep` (the resource group and the account, no databases), asks, then deploys it. An existing shared account is left exactly as it is; pass `-UpdateShared` to apply the template to it (for example after changing `allowedIpRanges`).
4. **CIX.** It shows a what-if preview of `infra/main.bicep` (`rg-cix-dev`, the dashboard, and the `cix` database with its containers in the shared account), asks, then deploys it.
5. Prints the Cosmos DB endpoint and the dashboard URL.

With `-WhatIfOnly` and no shared account yet, only the shared account's preview is shown: CIX's preview needs the account to exist.

It then writes the engine's settings:
- `COSMOS_ENDPOINT` and `COSMOS_DATABASE` go into `.env`.
- The shared account's key goes into `secrets\cosmos_key`, readable only by you. The key is never printed. It is account-wide: it reaches every project's database in the shared account (see Limits).

Pass `-SkipEngineConfig` to leave these two files alone.

The deployment is safe to run again. It brings the resources back to what the template describes and keeps the data.

## Engine and agents in Azure

Planned in [the hosting memo](research/azure-hosting.md) and not yet live. `agents.deploy` in the environment file chooses how much of it exists:

- **`platform`** (`infra/modules/agents-platform.bicep`). This adds the pieces the engine and the jobs run on, in `agents.location`:
  - a Log Analytics workspace, capped at 0.1 GB a day;
  - a Container Apps environment (workload profiles, Consumption) with public network access disabled, and no VNet of its own: a VNet would bring a billed load balancer and IP (about $22 a month). An environment deployed before 2026-10-28 is still on one: `provision.ps1` says so, and after you confirm (or with `-Yes`, which the protected environment refuses for this) it deletes the engine, the jobs, the environment and the VNet. It first refuses if the group holds another app or job, or a lock, that would stop it halfway, and it shows the image digests to pass again if the deployment then fails, then recreates them without it (10 to 20 minutes). Then run `setup-ci-deploy.ps1 -For agents` again: CI's roles on the engine and the jobs go with them;
  - the container registry (Basic, about $5 a month);
  - a storage account with the four run queues, with shared keys off;
  - Key Vault, for `claude-token`. It holds no Cosmos DB key: the engine signs in to the database with its managed identity;
  - in the shared account, Cosmos DB roles for the engine's identity (`infra/modules/cosmos-engine-access.bicep`), one per container and within CIX's database only. In `journal` it can query and add entries, never replace or delete one. In `state` it can read, create, replace and upsert documents, and in `requests` read, query and replace them, with no delete anywhere. They can't create containers or reach other projects' databases. Cosmos DB may not know an identity created moments earlier, and a new assignment can take a few minutes to apply: if the deployment fails on the engine's principal, run it again shortly, and an engine started before its roles apply stops on a 403 and the platform starts it again. A Key Vault from an earlier template still holds `cosmos-key`, the account's read-write key: `provision.ps1` deletes it and the engine's access to it, after asking;
  - one managed identity each for the engine and the three jobs, with only the roles each needs;
  - the budget, with alerts to your sign-in's address, or to `-BudgetEmail` (a personal Microsoft account usually has no address in the tenant that gets mail, so pass it).

  Apart from the registry, nothing here costs anything until something runs. The names are fixed per resource group, and a deleted Key Vault is kept for 7 days: provisioning again within a week of deleting the group fails until you purge it (`az keyvault purge --name <vault>`). You get Key Vault Secrets Officer on the vault, so you can set `claude-token` yourself. `provision.ps1` grants it to your own sign-in by object ID, never to whoever runs the deployment, so a CI identity can't pick it up.
- **`apps`** (`infra/modules/agents-apps.bicep`). This adds the paper engine (internal ingress only, at most one replica, stopped while `startEngine` is false) and the jobs `trader`, `requests` and `analyst`. Each job is started by a message on its own queue and runs one at a time. Before deploying, `provision.ps1` makes these checks and changes, in this order, asking before each change:
  - checks that Container Apps is offered in `agents.location`, and that Bicep is 0.32 or later;
  - finds both images in the registry and their digests: with `-ImageTag <commit SHA>` (needed the first time) the images CI tagged with it, and without it the images the engine and the trader job run now. The apps get them by digest, so pushing a tag again can't change what runs. Apps deployed before digests run a tag, which the first run without `-ImageTag` resolves to whatever it points to then;
  - uploads `secrets/claude_token` to Key Vault as `claude-token` if it isn't there yet, straight from the file without showing it. Pass `-UploadClaudeToken` after renewing it;
  - creates the engine's Entra app registration, `cix-engine-<environment>`, if missing. It is single tenant, with no secrets and no permissions, and its application ID URI `api://<app ID>` is the audience the jobs' tokens are for. It makes you its owner (`az ad app create` doesn't). An existing one is used only if you are its only owner;

  After the deployment, and asking first if anything must change, `provision.ps1` gives that app an app role, `CIX.Agent`, makes its service principal require an assignment, and assigns the role to the three job identities only. Entra then gives no other identity in the tenant a token for the engine, and the engine checks the role in each token as well as the identity's object ID. If anyone else holds an assignment, or anyone but you owns the service principal (an owner could assign others), `provision.ps1` stops before changing anything and names them: remove them in Entra (*Enterprise applications*, the engine's app, *Users and groups* or *Owners*), then run it again. It checks this on every run, so run it after any change in Entra you didn't make. A token is cached for up to 24 hours, so a job that got one before its assignment is refused by the engine until it gets a new one, and removing an assignment takes as long to apply. In an environment that already runs `apps`, run `provision.ps1` before CI deploys an engine that checks the role, or every job is refused until then.

  Deploy `platform` first, set up CI's deploy identity (below), then deploy `apps` once CI has pushed images, and run the setup again. The jobs' access to `claude-token` is granted in the same deployment that creates them and takes a few minutes to apply, so the first `apps` deployment can fail on it; run it again shortly.

  While `startEngine` is false the engine has no replica and queues no agent runs (`AgentRuns:Mode` is `Off`). Azure still starts each new revision once, and a request to its internal address would start one, for a few minutes; with nothing queued, no job calls it and it scales back to zero.

### Deploying the engine and the agents from CI

Once its deploy identity is set up (below), CI deploys after a push to `main` that changes what goes into the images, or on a manual run of *CI* on `main`, once the .NET tests, the container checks and the secret scan pass (or already passed in the pull request) and you approve the run for paper (below). What goes into the images is what `tools/ci/plan.sh` says: `src/`, `config/`, `strategies/`, `docker/`, `tools/trader/`, `.claude/agents/`, `.claude/hooks/`, `.claude/settings.json`, the solution and build files, the Compose files, and also any change to CI itself or to a file it doesn't classify. Each deployment restarts the paper engine on a new revision (the lease carries its state over).

1. `agents-images` builds `cix-engine` and `cix-trader` in a job with no Azure access, and hands them on as an artifact, with their hashes as job outputs.
2. `deploy-agents` signs in as `cix-agents-deploy-<environment>`, checks the hashes, and runs `tools/ci/deploy-agents.sh`:
   - it pushes both images to the environment's registry, tagged with the commit SHA, and takes their digests from the push: the apps run the images by digest, never by a tag that could be pushed again;
   - with `agents.deploy` at `platform` it stops there: run `provision.ps1 -ImageTag <that SHA>` with `apps` to create the engine and the jobs from them;
   - with `apps`, it points the engine at its new image and, while the engine runs a replica (`startEngine` true), stops the old revision and waits up to 12 minutes for the new one to be healthy (its startup and readiness probes passed). Container Apps would keep the old revision running until the new one is ready, but the new engine gets ready only once it holds the engine lease, which the old one gives up only when it stops; so the paper engine is down for the new one's startup, usually under a minute. Then it points the three jobs at the new agent image. It sends only the containers, with their images changed, through the management API, and waits for Azure to apply each change;
   - if anything fails after the engine changed, it puts the engine and the jobs back on the images they ran before (stopping the failed revision) and fails the run. A run cancelled halfway does the same in its last step. If even that fails, the run says so: check the engine's revisions and the jobs in the portal, or run `provision.ps1` again;
   - an engine you stopped (its latest revision deactivated, or stopped with `az containerapp stop`) is left alone, with a warning, and nothing is deployed. Run *CI* by hand once it runs again.

**Paper deploys wait for your approval** (ADR-0024). Between the images and `deploy-agents`, the job `approve-paper` waits in the GitHub environment `paper` until you approve the run, then checks that approval (`tools/ci/live-approved.sh`); a rejection, someone else's approval or an environment without its rules fails it, and nothing deploys. Set it up once in the repository's *Settings*, *Environments*: create `paper`, add yourself as the only required reviewer (allow approving runs you started), and allow only the branch `main`. Reject a run you won't approve: a waiting one holds the job's group. The repository variable `PAPER_APPROVAL=off` turns the gate off. Live then needs its own approval as before.

A run that is no longer the tip of `main` still deploys when the newer commits changed nothing that goes into the images or CI; otherwise the newer run deploys. Runs deploy one at a time. Nothing deploys while `agents.deploy` is `none` or `AGENTS_AZURE_CLIENT_ID` isn't set. CI never deploys infrastructure, settings or roles: those stay with `provision.ps1`. The protected (live) environment gets the same images only after you approve the run, in a job of its own, `deploy-live`, with an identity of its own ([live-trading.md](live-trading.md#deploys-from-main), ADR-0019). The image artifact is kept for a day, so re-running only the deploy job later than that fails; run the whole workflow instead.

**Set it up** once `provision.ps1` has deployed `agents.deploy` `platform` (needs Owner on the subscription), and again after it has deployed `apps`:

```powershell
.\tools\azure\setup-ci-deploy.ps1 -For agents -Environment dev -SubscriptionId <id>
```

It works like the dashboard's setup above, with its own identity and role:
- `AcrPush` on the environment's registry, and a custom role, `CIX agents deployer (<environment>)`: read and update Container Apps and jobs, stop an app's revision, and read the registry. Run the script again to add a right a newer version needs (stopping revisions came with the first engine deploy, #80): it adds it to the role in place. Azure can take a few minutes to apply a changed role, so wait that long before merging. It is assigned on the registry and, once `apps` exists, on the engine and the three jobs, never on the resource group. So it can't create apps, jobs or any other resource, nor role assignments, nor move an app to another identity or environment, and it can't read Key Vault, storage or the database itself;
- it prints `AGENTS_AZURE_CLIENT_ID`, `AGENTS_REGISTRY` and `AGENTS_ENVIRONMENT` (with `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, the same as the dashboard's). Add them as repository variables, or pass `-SetGitHubVariables`.

Optionally set the repository variable `CLAUDE_CODE_VERSION` to pin the Claude Code version in the agent image (otherwise each build takes the latest).

**What the identity could still do**, if a workflow on `main` misused it. `main` has no branch protection here, so this trusts everyone who can push to `main`, merge into it or start a workflow on it, including the Claude coding agents (see the dashboard's paragraph above).
- Stop the paper engine by deactivating its revisions (it could already, by running a broken image). CI then takes it as stopped by hand and deploys nothing until it runs again.
- Run any image as the engine or a job, with their settings changed, since Azure has no right that allows changing only the image. That image runs with the engine's or the job's managed identity, so it can do whatever they can:
  - use the engine's Cosmos DB roles: read CIX's database, add journal entries (forged ones too), and overwrite any document in `state` and `requests`: the kill switch and halts, the paper account, the lease, the archived strategy specs, and your requests. It can't delete anything, change or remove a journal entry, or reach other projects' databases in the shared account;
  - read `claude-token`, through a job's identity, and use the subscription it belongs to;
  - send run messages on the queues.
- Change the trading mode and the environment name. What keeps it paper is that nothing in this resource group holds exchange keys: the live engine gets its own group, Key Vault, identity and database, where this identity has no role (see the hosting memo). The live deploy's own identity there signs in only from jobs the owner approved (ADR-0019). The paper engine's roles stop at CIX's paper database, so CI can't reach the live database (its own database in the same free account, owner decision 5 in the memo) through them.

Anyone who can push to `main` can already reach most of this through the code the engine runs. What is still to do before the cutover is in the [hosting memo](research/azure-hosting.md) ("Code changes", item 6).

If you suspect misuse:
1. Remove the identity's role assignments, or delete `AGENTS_AZURE_CLIENT_ID`.
2. Run `provision.ps1` again: it puts the apps' settings and the engine's Cosmos DB roles back. Then check the engine's state documents (the kill switch, halts, the paper account) and your requests for anything you didn't do. They may have been overwritten: the journal still has every entry the engine wrote, so rebuild from it, or ask Azure support to restore the account's periodic backup. Journal entries may have been added, but none changed or removed. If `cosmos-key` was ever in the vault while the engine app ran, regenerate the shared account's primary key too (`az cosmosdb keys regenerate --key-kind primary`), then run `provision.ps1` without `-SkipEngineConfig` so the PC's `secrets/cosmos_key` gets it, and give it to the other projects that use the account.
3. Renew `claude-token` (`claude setup-token`, then `provision.ps1 -UploadClaudeToken`).
4. Check the images the engine and the jobs run (by digest) against the registry, and the engine's revisions.

**To stop the engine in an emergency,** deactivate its revision (portal: the engine app, *Revisions*, *Deactivate*). CI then leaves it stopped. Remove `AGENTS_AZURE_CLIENT_ID` too if you don't want any image pushed meanwhile.

The engine's trading mode isn't a template parameter: these templates only deploy paper, and a test fails if any template sets another mode or `Production`, or names the exchange outside the two key files. Those are mounted in a protected environment's engine only, which then stands by in paper until the owner switches it from their own sign-in ([live-trading.md](live-trading.md), ADR-0017). CI deploys only new images there, after the owner approves each run, and the engine keeps the settings it was switched to (ADR-0019).

## 3. Switch the engine to Azure

The engine keeps using the local emulator until you switch it:

```powershell
docker compose stop
docker compose -f docker-compose.yml -f docker-compose.azure.yml up -d --build
docker compose ps        # the engine shows (healthy) once it has loaded its state from Azure
```

- Azure starts empty, so the engine starts a **fresh paper account** (1000 EUR, `Paper:InitialBalances`). The emulator's data stays in its Docker volume, untouched.
- Every later Compose command needs both `-f` files, for example `docker compose -f docker-compose.yml -f docker-compose.azure.yml logs -f engine`. To avoid typing them, add `COMPOSE_FILE=docker-compose.yml;docker-compose.azure.yml` to `.env`. On Windows the separator is `;`.
- To go back to the emulator, run `docker compose -f docker-compose.yml -f docker-compose.azure.yml stop`, then `docker compose up -d`.

Trading mode doesn't change. It stays paper unless you set live yourself (CLAUDE.md rule 6).

## 4. Publish the dashboard

```powershell
.\tools\azure\provision.ps1 -Environment dev -DeployDashboard -SkipInfrastructure -SkipEngineConfig
```

This installs the API's one dependency, `@azure/functions` (pinned, with install scripts disabled), into `dashboard/api`. It then publishes `dashboard/app` and `dashboard/api` with the Static Web Apps CLI, using the app's deployment token. The token is read into an environment variable for that command only, and never printed.

### Publishing from CI

After this first publish, CI publishes the dashboard by itself after a push to `main` that changes it (`dashboard/` or `infra/`), once the dashboard tests and the secret scan pass (or already passed in the pull request). You can also start it by hand: *Actions*, *CI*, *Run workflow* on `main`. A run that is no longer the tip of `main` still publishes when nothing in `dashboard/`, `infra/` or `.github/workflows/` changed since (the newer push didn't publish); otherwise it leaves publishing to the newer one. Until the setup below is done, pushes skip publishing with a notice, and a manual run fails, so a manual run really checks the setup.

**No secret is stored in GitHub** ([ADR-0010](adr/0010-dashboard-ci-deploy-oidc.md)). This matters because the repository is private on GitHub Free, which has no environment secrets limited to a branch: a repository secret would be readable by workflows on any branch.
- The `deploy-dashboard` job signs in to Azure as a dedicated identity, `cix-dashboard-deploy-dev`, through a GitHub OIDC token. The identity trusts any workflow run on the `main` ref of this repository, whatever the trigger. Pull requests, other branches and forks get different token subjects and can't use it. A test keeps `id-token` in that job and `deploy-agents` (once each) and allows only the `push`, `pull_request` and `workflow_dispatch` triggers, as a guard against accidental changes (not a security control: whoever can change `main` can change the test).
- The identity's only right is to read this Static Web App and its deployment token. The job reads the token at run time, signs out of Azure, and publishes with it.
- npm install scripts run in a separate job with no Azure access. The deploy job checks the bundle's hash before unpacking it. The pinned Static Web Apps CLI itself still runs with the deployment token (it has to). The job signs out of Azure first, but that is hygiene, not a boundary: what limits the damage is that the identity can't do more than read that same token.

Set it up once (needs Owner on the subscription):

```powershell
.\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id>
```

The script:
- creates the identity, its `main`-only federated credential, and a custom role (`Microsoft.Web/staticSites` read and listSecrets only) assigned on `stapp-cix-dev` alone, asking before each change;
- checks everything before changing anything, and creates the trust for `main` last;
- refuses to reuse an identity with any other way in: a secret, certificate or other owner on the app registration or its service principal, API permissions or directory roles, another trusted subject, issuer or audience, a broader role of the same name, or any other role assignment;
- uses GitHub's own spelling of the repository name when the GitHub CLI is signed in (Entra matches the subject case-sensitively);
- trusts GitHub's immutable subject, `repo:<owner>@<owner id>/<repo>@<repo id>:ref:refs/heads/main`, which a renamed or recreated repository can't match. It reads the IDs with the GitHub CLI, or takes `-RepositoryIds <owner id>/<repository id>`. GitHub sends only this form for repositories created, renamed or transferred since 15 July 2026; an older repository must opt in (its OIDC settings) first. A name-only trust from an earlier version of the script is removed, after asking, once the new one is in place;
- prints four repository variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and `DASHBOARD_ENVIRONMENT`. They are IDs, not secrets.

Add those variables in GitHub: *Settings*, *Secrets and variables*, *Actions*, *Variables*. Or add `-SetGitHubVariables` to the script, which sets them with the GitHub CLI (`gh`, signed in). Then run *CI* on `main` once by hand to check it publishes.

What the identity could still do, if a workflow on `main` misused it: read the deployment token, publish other code as the dashboard, and that code would run with the dashboard's settings, including the shared account's read-only key (which can read every project's database there) and the writer identity's secret (which can create paper requests the engine then checks and, once `-LiveRequests` has run, Pause and Resume requests for the live engine, ADR-0018). `main` has no branch protection on GitHub Free, so this trusts everyone who can push to `main`, merge into it, or start a workflow on it. That includes the Claude coding agents: their GitHub access could merge pull requests or push to `main` directly (in this repository they have merged at the owner's request). Only the rules in CLAUDE.md, not GitHub, stop them. GitHub Pro with branch protection on `main` would make that a technical limit. If you suspect misuse, remove the identity's role assignment, reset the deployment token (portal: `stapp-cix-dev`, *Manage deployment token*, *Reset*), and regenerate the account's read-only keys, then run `provision.ps1` again to update the dashboard's settings, and `setup-dashboard-requests.ps1 -RotateSecret`.

The script command above still works for publishing by hand.

The dashboard is organised in tabs: **Overview** (status, briefing, portfolio, trading cycle, errors), **Coins** (with the Buy and Sell buttons), **Activity** (volume, executed trades, decisions), **Requests** and **Journal** (journal browser, state documents). The tab is kept in the address (`#coins`), so a bookmark opens it. On a phone the tabs are a bar at the bottom, tables become cards, and the Buy and Sell dialog opens from the bottom with large choices. It shows:
- **Market briefing** (at the top, for stepping in by hand): the analyst's latest view (from the last 24 hours) and reasoning, up to five highlights (the most important news and scheduled events, each marked risk, opportunity or watch, with its coins and a source link), and a short **Needs attention** list picked from the latest scan: buy and sell signals, moves of 5% or more in 24 hours or 2% in an hour, volume at 2.5 times its usual level, an hourly range of 2% or more, coins the scanner excluded as too volatile, too thin or without fresh data, and coins the analyst said to avoid. A falling coin you hold ranks higher. The full scan of every coin is one click away. Highlights are only as fresh as the analyst's last run (every `ANALYST_INTERVAL_HOURS`, 6 by default); a shorter interval gives fresher news at the cost of more Opus runs.
- **Content language** (RU or EN in the header, Russian by default, remembered in the browser): the language of the financial content: the analyst's reasoning and highlights (from analyst v3, which writes them in Russian too), the Needs attention reasons, the decision explanations and the reconciliation problems. Labels, buttons, names, tickers, abbreviations and trading terms stay in English, and anything with no Russian (an older view, an agent's own words, the journal's text) shows in English.
- **Engine:** running, or not responding when the heartbeat is older than 3 minutes. Also its strategy, and the kill switch with who engaged it and why, as a category.
- **Mode:** paper or live, a toggle at the top. Live shows data only if a live engine has written it.
- **Market:** the analyst's market view, how fresh the prices are, and the last sync with the exchange and with the database.
- **Portfolio:** equity, cash, today's P&L, peak, drawdown, exposure, holdings with their entry, stop and target, and the paper account's balances.
- **Trading cycle:** the last scan and its signals, orders still working, with their limits, and the trader's and analyst's recent runs.
- **Coins:** each scanned coin's move over the last hour and the last 24 hours (up or down, with the scan time as the reference), and whether you hold it.
- **Activity:** executed trades only, by UTC day, week (Monday to Sunday) or calendar month: buy and sell volume, turnover (their sum, fees apart), fees, the number of trades, buys and sells and how many were your requests, this period against the previous one, and a chart of the periods shown.
- **History:** executed trades with their prices and fees, decisions with a plain-language reason first (what triggered it, the evidence, the risk plan, what the risk gate did) and the technical text one click away, and errors and safety events.
- **Records:** journal activity (collapsible, browsable by day and kind), and the state documents with what each one is.
- **Requests:** Buy and Sell buttons on each coin (paper), your requests and where each stands (see [Owner requests](#owner-requests)).

It is **private** ([ADR-0014](adr/0014-private-dashboard.md)): every page and API route needs your Microsoft sign-in, as the confirmed owner (see the setup below).
- Signed out, any page sends you to the Microsoft sign-in. Signed in with an account without the `owner` role, you get a page that says the dashboard is private, with Sign in and Sign out. That page and its stylesheet are the only things anyone can open. An account with the role that isn't the confirmed owner (someone who redeemed an invitation link) loads the empty page, and the API refuses it everything.
- The API checks again, in every function: a Microsoft sign-in with the `owner` role and the user ID the setup script confirmed (`DASHBOARD_OWNER_USER_ID`). Anything else gets `403` and nothing is read. With no confirmed ID, nobody gets in, you included: finish the setup below first.
- **Sign out** is at the top of the page. It ends the dashboard's session; your browser may still be signed in to your Microsoft account, so signing in again may not ask for a password. When the session ends while the page is open, it says so and offers to sign in again.
- It shows everything as recorded: entry, stop, target and fill prices, resting orders' limits, fees, and the journal's own text. Answers are never cached by a browser (`no-store`).
- The reads use the account's read-only key, kept in the app's settings on the server; the API keeps answers 20 seconds (activity a minute), and the journal browser accepts only the last 14 days, known kinds and three page sizes, so the page can't use up the database throughput the engine needs.
- **After a deployment that changes the sign-in rules,** check once in a private window: `/` sends you to the Microsoft sign-in and back to the page; `/api/dashboard` without signing in gets a redirect, not data; Sign out lands on the signed-out page.
- The briefing shows the analyst's text from web pages as plain text only, and opens only `https` source links (each must be one of the view's sources), in a new tab without access back to the dashboard.

## Owner requests

The Buy and Sell buttons send manual requests to the engine ([ADR-0011](adr/0011-owner-manual-requests.md)). Only you can use them, signed in with your Microsoft account ([ADR-0012](adr/0012-dashboard-owner-sign-in-and-writer.md)):
- **Sell:** pick 25%, 50% or 100% of the position, and **Direct** (the engine sells at the bid at once, no agent) or **Ask the trader** (the trader analyses it and may decline or sell less). **Buy** always asks the trader, with an amount in the quote currency; the trader sets the stop.
- A confirmation shows exactly what will be sent. Each dialog has its own ID and is locked once sent, so a retry or a double click is still one request (also across midnight UTC), and the page follows it to the end: placed, filled, declined with the trader's reason, refused (by the engine or the risk gate, with the rule), expired.
- The buttons pause, with the reason, when the engine isn't running or doesn't take requests (switched off, or the `requests` container missing).
- Your note goes to the trader with the request, and shows in the request's history and decisions.
- Buy and sell requests are paper only. The Trading tile's **Pause** and **Resume**, strategy switches and coin changes work in both views; in Live they need a tick in the box that names real money, and the one-time `-LiveRequests` setup below ([live-trading.md](live-trading.md#pausing-and-resuming-from-the-dashboard-adr-0018)). The engine checks each one again, and every order still goes through the risk gate.

One-time setup, per environment (needs rights to create app registrations and to manage Cosmos DB roles on the shared account):

```powershell
.\tools\azure\setup-dashboard-requests.ps1 -Environment dev -OwnerAccount you@example.com
```

It creates a **writer identity**, `cix-dashboard-writer-dev`, whose only right is a Cosmos DB role allowing it to create items in `cix/requests`: not to read, change or delete anything, there or in any other database of the shared account. Its client secret goes into the dashboard's server-side settings (`COSMOS_WRITER_*`) through a file only you can read, and is never shown. It then invites your Microsoft account to the dashboard's `owner` role: open the link it prints, signed in with that account, within 2 hours. **Then run the script again** with the same `-OwnerAccount`: it confirms that account as the owner by its user ID (`DASHBOARD_OWNER_USER_ID`). The API accepts only that account, so a link someone else redeemed gives them nothing, and the script refuses to go on while anyone else holds the role.

- **Rotate the secret** within a year (the script prints the date): run it again with `-RotateSecret`. The old secret stops working at once.
- **`provision.ps1`** keeps the writer settings through a deployment (a deployment replaces the app's settings); if it can't, it says so and you run the setup again with `-RotateSecret`.
- **Who has the role:** Azure portal, the Static Web App, *Role management*. Remove anyone you don't recognise. The script warns if more than one account has it.
- **Live Pause and Resume:** run it once with `-LiveRequests`. That adds a second create-only role, `CIX dashboard live request writer (dev)`, on `cix-live/requests` only. Delete that one role assignment to take it away again.
- Until the owner is confirmed, nobody can open the dashboard. Until the writer is set up, the buttons answer that it isn't; everything else works.

## How the PC connects to Azure

| What | How |
|---|---|
| Engine to Cosmos DB | HTTPS on port 443 in gateway mode, authenticated with the account key. The key is a Docker secret (`/run/secrets/Cosmos__AccountKey`, from `secrets/cosmos_key`), never in an image, `.env`, the repository or the logs. |
| What the key can do | Read and write documents. It can't create or delete containers: key-based metadata writes are disabled, and schema changes go through Azure Resource Manager with your Azure sign-in. |
| Dashboard API to Cosmos DB | The read-only key for reading, and for your requests the writer identity (create in `requests` only, signed in with its secret), both in the Static Web App's settings, used server side only. |
| Your scripts | Your Azure CLI sign-in (`az login`). The scripts read keys into memory when needed and never print them. |
| Rotating the engine's key | `az cosmosdb keys regenerate --kind primary ...`, then run `provision.ps1 -Environment dev -SkipInfrastructure` (it only rewrites `secrets/cosmos_key`), then `docker compose ... restart engine`. The key belongs to the shared account, so every project using it needs the new key. |

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

# 2. Stop the engine (the script refuses while the engine's heartbeat is fresh), then wait 3 minutes.
#    In Azure: deactivate the engine app's active revisions (the jobs then can't reach it either).
$revisions = az containerapp revision list -n ca-cix-dev-engine -g rg-cix-dev --query "[?properties.active].name" -o tsv
foreach ($r in $revisions) { az containerapp revision deactivate -n ca-cix-dev-engine -g rg-cix-dev --revision $r }
#    Or, for the engine running locally against Azure:
docker compose -f docker-compose.yml -f docker-compose.azure.yml stop engine trader analyst

# 3. Reset, then type the confirmation it asks for ("reset dev")
.\tools\azure\reset-database.ps1 -Environment dev -Target State -Apply      # bot state only (paper)
.\tools\azure\reset-database.ps1 -Environment dev -Target Journal -Apply    # journal only
.\tools\azure\reset-database.ps1 -Environment dev -Target All -Mode all -IncludeSpecArchive -IncludeRequests -Apply   # a fresh start

# 4. Start again: activate the same revisions (or run provision.ps1), or `docker compose ... up -d` locally
foreach ($r in $revisions) { az containerapp revision activate -n ca-cix-dev-engine -g rg-cix-dev --revision $r }
```

| Option | Effect |
|---|---|
| `-Target State` | Removes every state document of the mode (`engine-state-<mode>`, `engine-status-<mode>`, `engine-lease-<mode>`, `agent-runs-<mode>`, `manual-requests-<mode>`, `scanner-filters-<mode>`: any `<name>-<mode>`) and, for paper, `paper-account`. The engine then starts with a fresh paper account. It also forgets the requests it was handling, which then stay at their last status on the dashboard: add `-IncludeRequests` to remove them too. |
| `-Target Journal` | Removes journal entries: all of them, or with `-JournalBefore 2026-10-20` only earlier days. |
| `-Target All` | Both. |
| `-Mode paper` (default), `live`, `all` | Which mode's state documents (and requests, with `-IncludeRequests`). Removing a live state makes the engine start halted until you confirm, and the confirmation to type becomes `reset dev including live state`. |
| `-IncludeSpecArchive` | Also removes the archived strategy specs (`spec-*`). Kept by default, because journal entries refer to them. |
| `-IncludeRequests` | Also removes your requests from the dashboard (the `requests` container) for the mode: `paper` takes those without a `tradingMode`. Kept by default. |

Safeguards:
- It refuses environments marked `protected`, in the settings file or in the `cix-protected` tag on CIX's resource group (set by the template from the same setting), unless you pass `-LiveWhenFlat` (below). The group must also be tagged `cix-environment=<environment>`.
- The database must be this environment's alone: its group's `cix-database` and `cix-cosmos-account` tags must name it, and no other group may claim it (the group Azure makes for this environment's Container Apps environment, `ME_cae-...`, copies the tags and is ignored). So a copied or mistyped settings file can't point a reset (or a deployment) at another environment's or project's database. Two environment files sharing one database name are refused outright.
- Agents can't run it: a hook blocks the script for every Claude agent, along with deleting Cosmos DB resources, regenerating keys, or `docker compose down -v`. It is the one thing allowed to delete journal entries, an owner-approved exception to the append-only rule ([ADR-0008](adr/0008-azure-database-local-agents-dashboard.md)).
- It acts only on CIX's database (`cosmos.databaseName`) in the single shared account (tagged `shared-role=cosmos` in the shared resource group), and checks the database exists first. Other projects' databases are never touched.
- It touches only the `journal` and `state` containers, and the owner's `requests` only with `-IncludeRequests`. It deletes documents one by one and never a container, the database or the shared account, so it works with a delete lock on the account.
- It refuses while the engine is running.
- It deletes nothing without `-Apply` and the typed confirmation, and prints a summary of what was planned and what was done.

### Live too: only with an all-cash account

The protected environment (`prod`, real money) is reset only with `-LiveWhenFlat`, and only its live state, journal and requests. A reset forgets every position's entry price, stop and target, so it is refused unless the account has nothing to protect:
- the live engine must be stopped (no `-IgnoreRunningEngine`); its last status must list no holding worth `-DustQuote` or more (1 in the quote currency by default, at most 5; a smaller residual can't be sold anyway), from a valuation at most 10 minutes older than that status; and its state must hold no order in flight and no position plan (the engine drops a plan once less than the exchange's minimum is held). Both checks run again after you type the confirmation, just before anything is deleted;
- `-IncludeSpecArchive` is refused (the archive holds the specs you reviewed for live), and the group must be tagged `cix-protected=true`;
- the confirmation to type is `reset prod real money live state`.

Afterwards the live engine starts halted with a fresh state and peak equity until you press Resume on the dashboard's Live view. Add the coins again on the Coins tab: the watched list is part of the state.

`-Target Journal` or `All` deletes the real-money journal for good, every buy and sell in it. Look through it or copy what you need first (Data Explorer in the portal), or keep the older days with `-JournalBefore`.

To reset both databases in one go, sell the live coins first, stop both engines (as in step 2, for `ca-cix-dev-engine` in `rg-cix-dev` and `ca-cix-prod-engine` in `rg-cix-live`), then:

```powershell
.\tools\azure\reset-paper-and-live.ps1 -Target All -IncludeRequests           # dry run of both
.\tools\azure\reset-paper-and-live.ps1 -Target All -IncludeRequests -Apply    # live first, then paper; one confirmation each
```

Live goes first, so if it refuses, paper is left as it was.

The local emulator is reset differently: `docker compose down -v` removes its volume.

## Limits

- **One free-tier account per subscription** (see costs), which is why it is shared.
- **The live (protected) environment's database is in the shared account too** (owner decision 5, 2026-10-26), so the account's keys reach it. `provision.ps1` says so when it deploys `prod`, and checks that no service principal holds a role on its group. What risk remains is in [azure-hosting.md](research/azure-hosting.md).
- **The shared account's keys reach every project's database in it.** CIX's key (and the dashboard's read-only key) could read, and CIX's key write, other projects' data, and theirs CIX's. Acceptable while every project is yours and on paper or test data. Per-project access would need Cosmos DB role-based access with an Entra identity per project ([ADR-0009](adr/0009-shared-cosmos-account.md)).
- **Other projects** add their own database to the shared account from their own templates (as `infra/modules/cosmos-database.bicep` does), with `existing` references to the account, and must keep the total throughput within 1000 RU/s to stay free.
- **No managed identity.** Static Web Apps managed functions can't use one, so the dashboard holds the read-only key in its settings. A separate Function App on the Consumption plan could use a managed identity if that ever matters.
- **No data migration.** Data isn't copied from the emulator; Azure starts fresh by design.
- **No lockfile for the API yet.** The package registry wasn't reachable where this was built, so `@azure/functions` is pinned to an exact version but its own dependencies aren't locked. Run `npm install --package-lock-only` in `dashboard/api` once and commit `package-lock.json`. The Static Web Apps CLI version is pinned in `provision.ps1` (`$SwaCliVersion`).
- **Not tested against Azure here.** The templates and scripts are validated (Bicep build and lint, PowerShell parsing, request signing against Microsoft's documented example, dashboard API tests), but nothing has been deployed from this repository yet. Run `-WhatIfOnly` first.
