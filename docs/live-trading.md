# Live trading: the owner's runbook

Live trading runs next to paper, never instead of it (epic #72). The live engine runs in Azure in its own resource group, `rg-cix-live`, with its own Key Vault, identities and database in the shared Cosmos DB account. CI and paper's agents have no role there (the live engine's own agent jobs run there, for it alone) (ADR-0013, [azure-hosting.md](research/azure-hosting.md), owner decisions 5 and 6). The first run is 100 EUR on the fixed Safe strategy.

Every step on this page is yours: agents can't set the live mode, add the keys, change the live limits or resume trading (CLAUDE.md rules 3, 5 and 6). This page grows as each part of #72 lands.

## The live limits (#66)

The live engine reads its own limits file, `config/risk-limits.live.json`, never paper's `config/risk-limits.json`. Without it, a Live engine refuses to start. Like paper's file, only you write it (CLAUDE.md rule 3; a hook stops agents from writing either file, whether by editing it or with a command), and the hard ceilings in `HardCeilings.cs` still cap whatever it says. It is built into the image, so a change reaches the live engine with the next image you promote.

A proposal for the first run, 100 EUR on the fixed Safe strategy. Safe's own limits are tighter still: 5 EUR orders, at most 10 EUR per coin, 25% of the allocation in coins, and no buys after a 3 EUR loss in a day.

```json
{
  "Risk": {
    "AllocationQuote": 100,
    "MaxOrderQuote": 20,
    "MaxPositionQuotePerSymbol": 25,
    "MaxDailyLossQuote": 5,
    "MaxDrawdownFraction": 0.10,
    "MaxOrdersPerMinute": 4,
    "MaxPriceDeviationFraction": 0.01,
    "AllowedSymbols": [ "BTC-EUR", "ETH-EUR", "SOL-EUR", "XRP-EUR" ]
  }
}
```

- **Drawdown 10%**, not paper's 15%: new buys stop after a 10% fall from peak equity (10 EUR from 100 EUR) until you resume. Sells, stops and targets carry on.
- **Daily loss 5 EUR**, half the ceiling (10% of the allocation).
- **Four of the most liquid pairs.** There is no `AllowedQuoteCurrency` for now, because adding pairs from the dashboard is paper-only until #71. Revolut X's smallest order on these pairs is 0.1 EUR (its public pairs list, 27 Oct), well under Safe's 5 EUR.

## The strategy

A Live engine runs only a spec whose `status` is `live`, and only you set that (the `protect-live-mode` hook stops agents). For the first run, `strategies/safe.v2.json` is Safe v1 with the same parameters, and instructions that no longer say "paper". It is a `draft`, which never runs: change its `"status"` to `"live"`. Leave `safe.v1.json` as it is. The paper database holds a copy of v1, and a spec file that no longer matches its copy can't be selected on paper. A `live` spec runs on paper too, so paper can switch to Safe v2 and keep the same baseline as live.

## The Revolut X keys (#68)

### The sub-account

Create a dedicated Revolut X sub-account for CIX and move only the allocation into it: 100 EUR for the first run. The aim is that the engine's key reaches this sub-account's funds and nothing else you hold. Check, when you create the key, that Revolut X ties it to the sub-account: if it doesn't, the key reaches everything in the account, and the engine's reconciliation halts trading at your first trade elsewhere. The live engine halts trading whenever the holdings change outside its own orders, so don't trade in this sub-account by hand either. Record what you find in [revolut-x-api.md](research/revolut-x-api.md).

### Creating the key pair and the API key

Create the key pair on your own machine. The private key never leaves it, except into the live Key Vault.

```bash
openssl genpkey -algorithm ed25519 -out revx_private.pem
openssl pkey -in revx_private.pem -pubout -out revx_public.pem
```

In the Revolut X web app, open the sub-account's API settings and add a key with the contents of `revx_public.pem`. Revolut X then shows the 64-character API key. If Revolut X offers permission choices, choose trading only, never withdrawals or transfers. If Revolut X offers an IP allowlist, leave it for now: the live engine has no fixed outbound IP yet, and that is decided together with its deployment (owner decision 3).

Keep `revx_private.pem` outside the repository (`.gitignore` excludes `*.pem` anyway). Never paste either key into a chat, an issue or a pull request.

### Where the keys go

| Secret | Key Vault secret (live vault) | Engine setting (secret file) |
|---|---|---|
| The API key | `revolutx-api-key` | `RevolutX__ApiKey` |
| The private key, PEM | `revolutx-private-key` | `RevolutX__PrivateKeyPem` |

The live engine's container app references the two secrets in its own Key Vault and mounts them as files under `/run/secrets`, named as the engine reads them (`infra/modules/agents-apps.bicep`, only in a protected environment's engine: never in paper's, never in the agent jobs). The engine reads them only in Live mode:

- **Paper stands by in Azure.** A Paper engine in Azure with either file in its secrets folder reads neither, starts nothing (no market data, database, tools or agent runs) and only answers its health check with `standby`, logging which files it found. That is the live engine as its template deploys it, until you switch it (below). Elsewhere (Compose on your PC), a Paper engine with either file refuses to start, and either setting from any other source stops a Paper engine anywhere. Paper mode keeps holding no trading credentials.
- **Live needs both.** A Live engine without both refuses to start, rather than failing at its first order. Both are trimmed first, so a file saved with a Windows line ending works, and an API key with a space or line break inside refuses to start.
- **Nothing prints them.** The engine's settings print as `<set>` or `<missing>`, and startup errors name the settings without their values.

`provision.ps1` uploads the two files to the live vault with `-ExchangeApiKeyFile` and `-ExchangePrivateKeyFile` (below). It passes each file to `az` by its path, so the keys are never on a command line or in a variable, and never printed.

### Rotating the keys

Rotate the keys at least once a year, and at once if you think a key was exposed.

1. Create a new key pair and API key as above. If Revolut X lets the sub-account have two keys at once, the old one keeps working meanwhile; otherwise pause trading first.
2. Upload both: `.\tools\azure\provision.ps1 -Environment prod -ExchangeApiKeyFile <api key file> -ExchangePrivateKeyFile revx_private.pem`. Without `-ImageTag` it keeps the images that run now. Like every deployment of the live environment, it puts the engine back on standby.
3. Container Apps picks up a new secret version only within 30 minutes, so a revision started straight away can still mount the old key. Wait 30 minutes, then switch the engine on again (below), which starts a new revision.
4. Check that the new revision uses the new key: in `logs show`, a successful balance sync logged after the new revision started, with the old key disabled in Revolut X if it lets you. Container Apps doesn't show which secret version a revision mounted. If the sync fails, the revision still has the old key: restart it (`az containerapp revision restart`, below) and check again.
5. Delete the old API key in Revolut X, then delete the old `revx_private.pem`.

If a key may be exposed, delete it in Revolut X first, and read the next section: open orders stay on the book. Then stop live trading until the new key is in place.

### Stopping live trading at once

Deleting the API key in Revolut X stops the engine from placing or cancelling orders, whatever the engine does. Orders already open stay on the book and can still fill, so cancel them in the Revolut X web app. Without the key the engine can't sell at a stop either: whatever the sub-account holds stays there until you sell it yourself or a new key is in place.

## The live environment in Azure (#66)

The live environment is `prod` (`infra/environments/prod.json`): resource group `rg-cix-live`, database `cix-live` in the shared account, no dashboard of its own: paper's dashboard shows it under **Live**, reading `cix-live` with the shared account's read-only key. It uses the same templates as paper's `dev`, marked `protected`, which changes four things (ADR-0017):

- **The engine gets the exchange keys** from the live Key Vault, as files, and stands by in paper until you switch it.
- **Its images come from paper.** You copy the images paper runs now, by digest, from dev's registry into live's own: `provision.ps1` refuses a tag paper doesn't run. CI never deploys here (`setup-ci-deploy.ps1` refuses it), and has no role here.
- **Nothing outside reaches it.** Before deploying, `provision.ps1` lists everyone with a role on `rg-cix-live`, on anything in it, or above it (the subscription, a management group): service principals, groups and other users, all but you and the environment's own identities. It asks before going on, and with `-Yes` it stops instead.
- **Your PC's engine settings stay paper's.** `provision.ps1` doesn't write `.env` or `secrets/cosmos_key` for it.

The agent jobs run here too, for the live engine only, with their own identities and the same `claude-token` (uploaded from `secrets/claude_token` when the live vault has none).

### Before the first deployment

1. `config/risk-limits.live.json` and `strategies/safe.v2.json` with `"status": "live"` are committed and merged (above). Both are built into the image, so the image you promote must come from that commit or a later one.
2. CI has deployed that commit to paper, and paper has run it. Its commit SHA is the image tag.
3. The Revolut X key pair and API key exist (above), in files on your PC. Save the API key file as plain text without a byte-order mark (`provision.ps1` refuses one, since it would stay in the secret): for example `Set-Content -NoNewline -Encoding ascii` in PowerShell, or Notepad's plain UTF-8.
4. The shared Cosmos DB account's read-write key has been rotated since your PC's engine last used it, so the only copies left are where they are still needed ([azure-hosting.md](research/azure-hosting.md), Cutover, step 5). The live engine doesn't use it: it signs in with its own identity.

### Deploying it

From the repository root, signed in to Azure as yourself:

```powershell
# 1. What the engine and the jobs run on: registry, Key Vault, queues, environment, logs, budget; and the database.
.\tools\azure\provision.ps1 -Environment prod -WhatIfOnly
.\tools\azure\provision.ps1 -Environment prod

#    (Only for a new environment: set "deploy": "platform" under agents in infra/environments/prod.json for this step.)
# 2. With "deploy": "apps" (the repository's setting): copy the images, upload the keys, deploy.
.\tools\azure\provision.ps1 -Environment prod -PromoteFrom dev -ImageTag <commit SHA> -ExchangeApiKeyFile <api key file> -ExchangePrivateKeyFile revx_private.pem
```

Step 2 checks everything before it changes anything, then asks before each change: copying each image, uploading each key, creating the engine's Entra app, and the deployment. It refuses if the images it would deploy aren't the ones it copied. When it ends, the engine is on standby. Check it:

```powershell
az containerapp revision list --name ca-cix-prod-engine --resource-group rg-cix-live --query "[?properties.active].{name:name, health:properties.healthState, running:properties.runningState}" -o table
az containerapp logs show --name ca-cix-prod-engine --resource-group rg-cix-live --type console --tail 20   # "Standing by: ..."
```

`az containerapp` may first offer to install its Azure CLI extension: say yes.

### Switching the engine on

This is the step that trades real money, and it is yours alone. No file in the repository holds it, CI has no role here, and the `protect-live-mode` hook refuses any command an agent writes on this environment. That hook is a tripwire, not the control: what stops anyone else is that only your Azure sign-in has a role here. A Claude session on your PC shares that sign-in, so keep it to sessions you watch, or keep your rights on `rg-cix-live` in a separate admin account (or a role you activate only for this step). One command, from your own sign-in, sets the engine's trading mode, the environment that allows it, and the strategy. In it, `<mode>` stands for the word `Live` (the `protect-live-mode` hook stops anyone writing the setting out in the repository, this page included). Type it exactly so, with a capital L: the engine refuses any other spelling, and the new revision then fails to start while the standby one runs on.

```powershell
az containerapp update --name ca-cix-prod-engine --resource-group rg-cix-live --set-env-vars Trading__Mode=<mode> ASPNETCORE_ENVIRONMENT=Production Strategy__Name=safe Strategy__Version=2
```

It starts a new revision, which reads the keys, checks them against the live limits and the strategy, keeps its state (the kill switch, loss baselines, the lease) in `cix-live`, syncs the sub-account's balances, and then trades. Watch its first minutes with the `logs show` command above. If it refuses to start (no limits file, a spec that isn't `live`, a key missing), the log says why without showing any key, and the revision stays unhealthy while the standby one keeps running: fix the cause, or put it back on standby.

**Every deployment of the live environment puts the engine back on standby**, because the template pins it to paper. After promoting a new image (step 2 again, without the key files) or uploading new keys, run the switch command again.

### Back to standby, and stopping

- **Stop at once**: deactivate the active revision. Nothing runs until you switch it on again (or run `provision.ps1`):
  `az containerapp revision deactivate --name ca-cix-prod-engine --resource-group rg-cix-live --revision <active revision from revision list>`
- **Standby** (stops trading, keeps the engine up): `az containerapp update --name ca-cix-prod-engine --resource-group rg-cix-live --set-env-vars Trading__Mode=Paper ASPNETCORE_ENVIRONMENT=Azure`, or run `provision.ps1` again, which puts every setting back. Then check with `revision list` that the only active revision is the new one: the platform keeps the live revision running until the new one is healthy, so if the new one can't start, the live one carries on. Deactivate it then.
- Either way, orders already open stay on the book: cancel them in the Revolut X web app if you mean to stop for a while. Don't disable or delete the Key Vault secrets while a live revision runs: a new revision then can't start, and the live one keeps running.
- **Restart** the current revision (for example after a key rotation): `az containerapp revision restart --name ca-cix-prod-engine --resource-group rg-cix-live --revision <name from revision list>`.
- **Whatever the engine does**: delete the API key in Revolut X (above).

