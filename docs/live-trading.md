# Live trading: the owner's runbook

Live trading runs next to paper, never instead of it (epic #72). The live engine runs in Azure in its own resource group, `rg-cix-live`, with its own Key Vault, identities and database in the shared Cosmos DB account. CI and paper's agents have no role there (the live engine's own agent jobs run there, for it alone) (ADR-0013, [azure-hosting.md](research/azure-hosting.md), owner decisions 5 and 6). The first run is 100 EUR on the fixed Safe strategy.

Every step on this page is yours: agents can't set the live mode, add the keys, change the live limits or resume trading (CLAUDE.md rules 3, 5 and 6). This page grows as each part of #72 lands.

## The live limits (#66)

The live engine reads its own limits file, `config/risk-limits.live.json`, never paper's `config/risk-limits.json`. Without it, a Live engine refuses to start. Like paper's file, only you write it (CLAUDE.md rule 3; a hook stops agents from writing either file, whether by editing it or with a command), and the hard ceilings in `HardCeilings.cs` still cap whatever it says. It is built into the image, so a change reaches the live engine with the next image you promote.

The file as it stands, for the first run: 100 EUR on the fixed Safe strategy. Safe's own limits are tighter still: 5 EUR orders, at most 10 EUR per coin, 25% of the allocation in coins, and no buys after a 3 EUR loss in a day.

```json
{
  "Risk": {
    "AllocationQuote": 100,
    "MaxOrderQuote": 20,
    "MaxPositionQuotePerSymbol": 25,
    "MaxDailyLossQuote": 5,
    "MaxDrawdownFraction": 0.10,
    "MaxOrdersPerMinute": 5,
    "MaxPriceDeviationFraction": 0.01,
    "MaxEmergencySlippageFraction": 0.03,
    "AllowedQuoteCurrency": "EUR",
    "MaxTradableSymbols": 50
  }
}
```

- **Drawdown 10%**, not paper's 15%: when equity falls 10% below its peak (10 EUR from 100 EUR), the engine pauses trading itself, as your Pause would: open orders are cancelled, the agents and the strategy stop, stop-loss sells and your direct sells go on, and the Trading tile says why. Only your Resume returns to trading. After a Resume the gate still refuses new buys while equity stays 10% or more below the peak, and the engine doesn't pause again until equity has been back within 10% and falls to it once more. The 30% hard ceiling is the critical halt (below).
- **Daily loss 5 EUR**, half the ceiling (10% of the allocation).
- **Coins: any EUR pair, at most `MaxTradableSymbols`.** No pair list lives in the file (ADR-0021). You add coins on the Live view's Coins tab (with its confirmation); each is watched, and trades only while it passes the live strategy's filters. Coins the sub-account holds are added by the engine and can't be removed while held. Such a coin gets a stop when the engine first sees it priced: the live strategy's `stopAtr` hourly ATRs below the bid (at least 0.5% below it), saved in its plan and journaled as `stop-adopted`; the same goes for a coin you buy yourself without one. The protection then sells it if the bid falls to that stop, like any position; it waits for enough hourly candles for the ATR (a minute or so after a start). Agent buys always carry their own stop: `place_order` refuses a buy without one. The live engine starts watching only what it holds and what you add. Revolut X's smallest order on the liquid pairs is 0.1 EUR (its public pairs list, 27 Oct), well under Safe's 5 EUR.
- **Emergency slippage** (`MaxEmergencySlippageFraction`, optional): how far below mid a stop-loss sell may go. The protection sells with immediate-or-cancel limits at the bid, and refuses to quote when the bid is further below mid than this; without the setting it uses `MaxPriceDeviationFraction`, which on thin pairs leaves many stops unfilled in a fast fall. `0.03` is the agreed start, set in the file above; the hard ceiling (5%) caps it. The `Protection` section of `appsettings.json` holds the engine side (quote age, retry delay, five quick tries, then a retry every minute) with conservative defaults that need no change.

## The strategy

A Live engine runs only a spec whose `status` is `live`, and only you set that (the `protect-live-mode` hook stops agents). For the first run, `strategies/safe.v2.json` is Safe v1 with the same parameters, and instructions that no longer say "paper"; you marked it `live`. `safe.v1.json` stays as it is: the paper database holds a copy of v1, and a spec file that no longer matches its copy can't be selected on paper. A Paper engine can run a `live` spec configured at startup (`Strategy:Name` and `Strategy:Version`), but its Strategy tab switches only between `paper` specs.

Balanced and Risky have live versions the same way: `strategies/balanced.v2.json` is Balanced v1 with a daily loss stop of 5 EUR instead of 6, and `strategies/risky.v3.json` is Risky v2 with 25 EUR per coin instead of 50 and a daily loss stop of 5 EUR instead of 12, so both fit the limits above. Both are drafts: change each `"status"` to `"live"` you want to offer, merge, and promote that image. The live Strategy tab then lists them. Their orders are up to 10 and 20 EUR, so the sub-account needs that much cash before they can buy.

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
- **Its images come from paper.** You copy the images paper runs now, by digest, from dev's registry into live's own: `provision.ps1` refuses a tag paper doesn't run. After that, CI deploys each new image paper runs, once you approve the run ([Deploys from main](#deploys-from-main), ADR-0019); paper's CI identity has no role here.
- **Nothing outside reaches it.** Before deploying, `provision.ps1` lists everyone with a role on `rg-cix-live`, on anything in it, or above it (the subscription, a management group): service principals, groups and other users, all but you, the environment's own identities, and the live deploy's expected roles (anything else it holds is listed). It asks before going on, and with `-Yes` it stops instead.
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

This is the step that trades real money, and it is yours alone. No file in the repository holds it, CI's deploys keep the engine's settings as they are, and the `protect-live-mode` hook refuses any command an agent writes on this environment. That hook is a tripwire, not the control: what stops anyone else is that only your Azure sign-in has a role here, apart from the live deploy, which signs in only from runs you approve. A Claude session on your PC shares that sign-in, so keep it to sessions you watch, or keep your rights on `rg-cix-live` in a separate admin account (or a role you activate only for this step). One command, from your own sign-in, sets the engine's trading mode, the environment that allows it, and the strategy. In it, `<mode>` stands for the word `Live` (the `protect-live-mode` hook stops anyone writing the setting out in the repository, this page included). Type it exactly so, with a capital L: the engine refuses any other spelling, and the new revision then fails to start while the standby one runs on.

```powershell
az containerapp update --name ca-cix-prod-engine --resource-group rg-cix-live --set-env-vars Trading__Mode=<mode> ASPNETCORE_ENVIRONMENT=Production Strategy__Name=safe Strategy__Version=2
```

It starts a new revision, which reads the keys, checks them against the live limits and the strategy, keeps its state (the kill switch, loss baselines, the lease) in `cix-live`, syncs the sub-account's balances, and then trades. Watch its first minutes with the `logs show` command above. If it refuses to start (no limits file, a spec that isn't `live`, a key missing), the log says why without showing any key, and the revision stays unhealthy while the standby one keeps running: fix the cause, or put it back on standby.

**Every `provision.ps1` deployment of the live environment puts the engine back on standby**, because the template pins it to paper. After promoting a new image (step 2 again, without the key files) or uploading new keys, run the switch command again. CI's deploys (below) don't: they change only the images.

### Deploys from main

Once set up, each change on `main` that paper deploys waits in CI for your approval, then goes to the live engine and its jobs with the same images (ADR-0019). The engine keeps its mode, environment and strategy, its kill switch and its state: no promotion, switch or Resume needed.

**Set it up once**, from your own sign-in, in PowerShell from the repository root, with the GitHub CLI signed in (`gh auth login`):

```powershell
.\tools\azure\setup-ci-deploy.ps1 -Environment prod -For agents -SetGitHubVariables
```

It asks before each change. It creates the identity `cix-agents-deploy-prod` with the paper deploy's two roles on live's registry, engine and jobs. It gives the GitHub environment `prod` its rules (you as its only required reviewer, `main` only, no admin bypass, and you may approve runs you started) and reads them back, and only then lets Azure trust jobs in that environment. Then it sets the repository variables `LIVE_AZURE_CLIENT_ID`, `LIVE_REGISTRY`, `LIVE_TENANT_ID` and `LIVE_SUBSCRIPTION_ID`, which switch the job on (without `-SetGitHubVariables` it prints them for you to add). Run it from your own terminal: Claude sessions are blocked from it.

**Each deploy:**

1. A change merges to `main`. CI deploys it to paper as before.
2. The run's `deploy-live` job then waits, and GitHub notifies you. Open the run (Actions, CI). Its summary, *What this live deploy changes*, lists the commits and files since live's last deploy, with changes to risk, limits, strategies, orders, tools or the deploy flagged. Then press **Review deployments**, tick `prod`, **Approve and deploy**. Approving runs that commit's workflow with the live deploy's rights, so approve only changes you recognise.
3. The job checks your approval again, pushes the images to live's registry, stops the old engine revision, waits for the new one to be healthy, then updates the jobs. If the new engine fails, it puts the old images back. The engine is down for the new one's startup, usually under a minute.

- Approve within a day: the images are kept that long. After that the job fails; run CI on `main` by hand (Run workflow) and approve that run.
- If a newer commit changed the images meanwhile, approving the older run deploys nothing: its own run deploys live.
- To keep live where it is, **reject** the run (Review deployments, Reject): a run left waiting holds back every later one. After a rejection, that run can't deploy again; run CI on `main` by hand for a new one.
- An engine you stopped by hand (stopped, or its revision deactivated) is left alone, and settings you change while a deploy runs are never sent back: the deploy stops and rolls back instead.
- Claude sessions use your GitHub account, so GitHub can't tell their approval from yours. They are told never to approve (CLAUDE.md rule 8) and a hook blocks the calls, but that is a tripwire: approve only in the GitHub app or site yourself, and if a live deploy ever runs that you didn't approve, delete `LIVE_AZURE_CLIENT_ID` and look (ADR-0019, "The risk that remains").
- Changes to `infra/`, the apps' settings or the keys still need `provision.ps1` and then the switch.
- To turn it off, delete the repository variable `LIVE_AZURE_CLIENT_ID`. If you suspect misuse, also delete the identity's role assignments on `rg-cix-live`.

### Pausing and resuming from the dashboard (ADR-0018)

On its first start the live engine has no state yet, so it starts halted until you confirm ("Waiting for the owner to confirm" on the Trading tile). The dashboard's Live view confirms it, and pauses and resumes trading later:

1. Once, from your own sign-in: `.\tools\azure\setup-dashboard-requests.ps1 -Environment dev -LiveRequests`. It gives the dashboard's writer identity a second create-only role, on `cix-live`'s `requests` container only, and asks before each change. Until then the live buttons answer that live requests aren't set up.
2. On the dashboard, switch to Live and press **Resume** (or **Pause**) on the Trading tile, tick the box that names real money, and send. The engine takes it within about 10 seconds and the line under the header says what it did.

- **Pause** engages the kill switch as the owner: open orders are cancelled and the risk gate refuses every new order from the agents, the strategy and the dashboard until you resume. Two kinds of sell are the exception: the engine's protection keeps selling a coin that falls through its stop while paused, so a pause never leaves a position unprotected; and your own direct sells from the dashboard (Sell, Direct) still go through, within the usual price band and never more than is held, so you can close positions while halted. A sell you ask the trader to decide on waits for Resume, like the agents. Your note stays on the request; the halt's reason is a fixed sentence, since the agents read it.
- **Critical halt**: when equity falls 30% from its peak (the hard ceiling), the engine halts as it would for Pause, logs it as critical and the Trading tile says so. Buys, the strategy and the agents stop; stop-loss sells go on. Nothing sells off the rest of the portfolio by itself: only your Resume returns to trading.
- **Resume** releases only the halt the page showed you. If trading halted again, or a check (the reconciler, a safety limit) found something while it was halted, the Trading tile shows it ("Also, …") and a Resume sent from an older view is refused with the reason: look at it, then press Resume again if you still want to.
- A request more than 5 minutes old when the engine sees it expires, and at most 20 act a day. Each change is journaled as the owner's (kind `kill-switch`).
- To take the dashboard's live access away, delete the `CIX dashboard live request writer (dev)` role assignment in the shared Cosmos DB account (`az cosmosdb sql role assignment list`, then `delete`). Paper requests keep working.

The Live view switches strategy and coins the same way, each with the real-money box ticked:

- **Strategy tab**: lists only the spec files in `strategies/` you marked `live` (today Safe v2), and switches between them before the next trader run. A version no live-marked file holds, exactly as merged, is refused, so a new live strategy is a spec file you mark `live` in a pull request, then a promoted image.
- **Coins tab**: turns pairs off and on again within `config/risk-limits.live.json`. A pair can be removed only when nothing is held or working in it; the risk gate checks every order as before.
- Both are journaled (kinds `strategy` and `pairs`) and survive a restart.

Pause is not the emergency stop: it needs the engine running and the dashboard reachable. For that, use the steps below and [Stopping live trading at once](#stopping-live-trading-at-once).

### Where a stop-loss sale is not guaranteed (ADR-0020)

The engine's protection sells a coin that falls through its stop within seconds, through Pause and the critical halt, with an immediate-or-cancel limit no lower than `MaxEmergencySlippageFraction` under the mid. Three cases are outside what it can promise. None of them is fixed by Resume or by a setting; each is shown on the dashboard and in the journal as it happens.

1. **Revolut X or its API is unreachable.** Without a quote and an order there is no sale. The exit keeps trying every 2 seconds, then every minute, and completes by itself when the API answers again; the position stays exposed meanwhile. Only a stop order held by the exchange would act while the engine is down, and nothing acts while the exchange is. What you can do: nothing from the engine's side; the Revolut X app, if it works, is the fallback.
2. **A gap, or no bids within the band.** When the price jumps past the stop between two quotes (a few seconds apart in live), the sale happens at the new level: the band bounds the slippage against the mid at the time of the sale, not the distance from the stop, so the loss beyond the stop is the gap. When the book holds no bids at all within the band, the exit is reported blocked ("Выход заблокирован" on the dashboard, a critical log line) after five tries and retried every minute; it sells when bids return, at that price. The band is yours: the engine never widens it by itself.
3. **The restart window.** While the engine is down and until its stops reload after a start (a deploy or a crash: usually a minute or two, longer if the image can't start), no stop is watched. A stop crossed meanwhile is sold on the first fresh quote after the restart, at that price. Deploys from `main` replace the engine in place and hold the live revision until the new one is healthy, so this window is short, but it exists. After a start the engine first compares the account with the exchange, so an order or a deposit from elsewhere while it was down halts it before anything trades: the agents', the strategy's and the trader's orders wait for that comparison (seconds, at most five minutes), while stop-loss sells and your direct sells don't.

Targets and trend exits are not part of this protection. A position's take-profit is sold by the background scan, which looks every 5 minutes: when the bid is at or above the target it places a limit sell at the bid (re-priced if it rests), and it doesn't while trading is halted. A trend exit (`trend-down`) is left to the trader agent. Only the stop is watched on every quote.

Everything else the protection handles: partial fills (re-quoted every 2 s), orders the exchange left open (cancelled before the next try), uncertain placements (the balance is read before any retry), stale or invalid quotes (never a sale), a crash right after a sell was sent (nothing to resell), and a Pause or critical halt (stops keep selling, buys stop).

### Alerts and health

You hear about trouble without opening the dashboard (`infra/modules/engine-alerts.bicep`). `provision.ps1` sends them to the address it uses for the budget (your sign-in, or `-BudgetEmail`), by email and as a push to the Azure mobile app signed in with that address:

- **Engine needs attention**: a stop-loss exit blocked or failing, the kill switch engaged (by a safety rule, the reconciler or your own Pause), drawdown at the hard ceiling, the engine lease lost (once on every deploy too, when the new revision takes over), the safety checks failing, the engine state not saved, or the container restarting (also during a deploy, as the old revision stops and the new one starts). The alert's `Event` says which.
- **No heartbeat**: the engine runs (`startEngine`, and not standing by) but has logged no heartbeat for 15 minutes: it stopped, hangs, or the workspace reached its 0.1 GB daily log cap. After two days of total silence it resolves by itself (log alerts look back two days at most).

Each alert fires once and resolves by itself when the condition clears. They cost about 3 USD a month per environment. They and the engine's health probe change only when you run `provision.ps1` (CI deploys images, not templates).

The engine's liveness probe asks `/healthz`, which answers 503 (`stalled`) when the safety checks have made no progress for 15 minutes (no step of a round started or finished): the platform then restarts the engine. Each exchange call gives up within about a minute, so an outage of the exchange or the database alone shouldn't do that.

### Back to standby, and stopping

- **Stop at once**: deactivate the active revision. Nothing runs until you switch it on again (or run `provision.ps1`):
  `az containerapp revision deactivate --name ca-cix-prod-engine --resource-group rg-cix-live --revision <active revision from revision list>`
- **Standby** (stops trading, keeps the engine up): `az containerapp update --name ca-cix-prod-engine --resource-group rg-cix-live --set-env-vars Trading__Mode=Paper ASPNETCORE_ENVIRONMENT=Azure`, or run `provision.ps1` again, which puts every setting back. Then check with `revision list` that the only active revision is the new one: the platform keeps the live revision running until the new one is healthy, so if the new one can't start, the live one carries on. Deactivate it then.
- Either way, orders already open stay on the book: cancel them in the Revolut X web app if you mean to stop for a while. Don't disable or delete the Key Vault secrets while a live revision runs: a new revision then can't start, and the live one keeps running.
- **Restart** the current revision (for example after a key rotation): `az containerapp revision restart --name ca-cix-prod-engine --resource-group rg-cix-live --revision <name from revision list>`.
- **Whatever the engine does**: delete the API key in Revolut X (above).

