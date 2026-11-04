# 0028. No paper trading in Azure: Crypto is the live engine, beside Stocks

- Status: accepted
- Date: 2026-11-03
- Supersedes [ADR-0024](0024-paper-deploys-after-owner-approval.md) (paper deploys after the owner's approval)
- Amends [ADR-0017](0017-live-environment-switched-by-the-owner.md) (live runs next to paper, images promoted from paper), [ADR-0019](0019-live-deploys-from-ci-after-owner-approval.md) (live deploys after paper), [ADR-0022](0022-same-presets-in-paper-and-live.md) (the same presets in both modes), [ADR-0011](0011-owner-manual-requests.md) and [ADR-0018](0018-dashboard-controls-live-trading.md) (the dashboard's paper requests)

## Context

Since ADR-0013 two crypto engines ran in Azure: paper (`dev`, simulated money) and live (`prod`, real money, ADR-0017). Paper was the place to try strategies before risking money, and the canary every image went through before live (ADR-0019, ADR-0024).

The first week's evaluation (2026-10-28 to 2026-11-03) showed paper doesn't earn its keep any more. The strategies trade rarely (a no-buys diagnosis, then a backtest that found hourly moves about equal to costs), the next one is a daily swing strategy that trades a few times a month, and a paper engine doing the same thing for a month says little that backtests don't. Meanwhile it costs an always-on Container App, its three agent jobs (whose Claude runs share the owner's usage limit), its logs, and a second approval for each merge.

On 2026-11-03 the owner decided: "Paper trading нафиг не нужен ввиду редкого трединга, только занимает ресурсы. Paper trading убираем, на дашборке будет crypto и stocks." Asked what the engine itself keeps, he chose to keep paper as the engine's safe mode.

## Decision

**No paper engine runs in Azure, and the dashboard has two modes: Crypto (the live engine) and Stocks.**

- **Dashboard.** The switch is Crypto | Stocks. Crypto shows the live engine only; its API reads and writes the live engine's database alone and answers paper with a 400. Without `COSMOS_LIVE_DATABASE` it refuses (503) rather than read another database. What existed only for paper goes: the simulated account's balances, the Buy and Sell buttons with their dialog and the Requests tab (live never took them: ADR-0011, `ManualRequests:AllowLive` off), and the Owner requests tile. Strategy switches, pair changes, Pause, Resume and Run now work as before, with the real-money confirmation (ADR-0018).
- **CI.** `approve-paper` and `deploy-agents` are removed. `agents-images` builds when the live deploy is set up (`LIVE_AZURE_CLIENT_ID`) and decides from `prod.json`; `deploy-live` needs the checks and the images directly, waits in `prod` for the owner's approval as before (ADR-0019) and fails on a failed or cancelled check. One approval per merge instead of two. The `AGENTS_*` variables, the `paper` environment and the `PAPER_APPROVAL` switch are no longer read.
- **Azure.** `dev` keeps the dashboard and, with `agents.deploy` `platform`, the registry the stocks image is built in (infra/stocks), with `startEngine` false. Its engine and three jobs are deleted by hand: the owner's step, since a deletion can't be undone (docs/azure.md, "Removing the paper engine"). `provision.ps1` loses `-PromoteFrom`: the live engine runs images CI pushed to its own registry. `reset-paper-and-live.ps1` goes; `reset-database.ps1 -Environment prod -LiveWhenFlat` stays.
- **The engine keeps paper as its safe mode** (CLAUDE.md rule 6, unchanged): the default when `Trading:Mode` is unset, the standby the live engine starts in until the owner switches it (ADR-0017), and the mode of Docker Compose, `dotnet run` and the tests (`PaperExchange`). No engine code changes.
- **Strategy specs** keep their `paper` and `live` statuses (ADR-0015, ADR-0022): only live runs in Azure, so a new preset is still a `draft` until the owner marks it `live`.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep paper running, only hide it on the dashboard | The cost and the Claude runs stay, which is what the owner wants gone. |
| Delete the whole `dev` resource group | It hosts the dashboard (Static Web App) and the registry the stocks image is built in. |
| Remove paper from the engine too (Live and Standby only) | The owner chose to keep it: it is the live engine's standby, the local mode and the test double, and removing it means rewriting order and risk code for no gain. |
| Keep paper's database and requests on the dashboard as history | The journal stays in Cosmos DB for anyone who wants it; showing it beside live invites mixing the two. |
| Bring Buy and Sell to live now | A real-money feature on its own (ADR-0011's authentication condition): a separate decision. |

## Consequences

- Each merge that changes the images waits for one approval, the live one, right after its checks. There is no paper canary any more: `deploy-agents.sh` still waits for the new live revision to be healthy and rolls back to the previous images if it isn't, and the run's summary (`live-changes`) lists what changes. Local Compose remains the place to try an image before approving it.
- The owner's one-time steps: delete the paper engine and its jobs (docs/azure.md, "Removing the paper engine"); delete `cix-agents-deploy-dev`, CI's old paper deploy identity, which still trusts any workflow on `main` and can push to dev's registry; optionally delete the `paper` GitHub environment, the `AGENTS_*` and `PAPER_APPROVAL` variables and the `cix-engine-dev` app registration. Agents never touch GitHub environments or variables (CLAUDE.md rule 8).
- Paper's database `cix` stays in the shared free-tier account: it costs nothing, `provision.ps1` deploys it for `dev`, and the dashboard's writer still holds a role on its requests container. Removing it is a later change if wanted.
- Re-running a paper engine in Azure would mean setting `dev`'s `agents.deploy` to `apps` and restoring a deploy job: this ADR would be superseded first.
- The stocks advisory's simulated portfolio (ADR-0023) is a separate thing and stays: it is the benchmark beside the owner's real holdings.
