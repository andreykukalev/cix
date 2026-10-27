# 0018. The dashboard controls live trading through requests

- Status: accepted
- Date: 2026-10-27

## Context

The live engine runs in its own environment (ADR-0017). The dashboard shows it, reading `cix-live` with the account's read-only key, but can't change anything there. Owner decision 6 kept CI and the agents out of live.

On its first start, the live engine's state document didn't exist, so its kill switch engaged until the owner confirmed. In Azure the owner had no way to confirm:
- operator tools stay off the network under Entra (ADR-0013);
- the dashboard was read-only for live;
- editing the state document by hand bypasses the journal.

Strategy switching (ADR-0015) and tradable pairs (ADR-0016) were paper-only until #71.

On 2026-10-27 the owner chose, on a decision card, to let the dashboard control live trading (strategy, coins, pause and resume) rather than keep live owner-only. He accepted the cost: code that CI deploys to the dashboard can then send requests to the live engine.

## Decision

**Live controls are requests, as paper's are.** The dashboard creates a request document in `cix-live`'s `requests` container, and the live engine, which alone writes `state`, checks and acts on it, journaling what it did. Nothing in the dashboard reaches the exchange or the live engine's state.

- **The writer identity gets a second create-only role.** `cix-dashboard-writer-<env>` keeps its role on its own database's `requests` and gets a separate Cosmos DB role, "CIX dashboard live request writer (<env>)", allowing `readMetadata` and `items/create` on `<live database>/requests` only.
  - `setup-dashboard-requests.ps1 -LiveRequests` creates it, with its own confirmation, and refuses anything wider.
  - Without that role, live requests fail with 403 and nothing else changes.
  - The owner revokes it by deleting that one role assignment.
- **The API writes live requests to the live database only**, named by `COSMOS_LIVE_DATABASE` (as the reads, PR #124).
  - It takes a live request only when the page sends `confirmLive: true`, which the page sends only after the owner ticks a box that names real-money trading.
  - Manual buy and sell requests stay paper-only: the live engine still ignores them unless `ManualRequests:AllowLive` is set (ADR-0011).
- **Pause and Resume** are `trading-control` requests (`action` `pause` or `resume`), taken by the engine in both modes.
  - The newest valid request acts; older ones are superseded, also when seen after a newer one acted. A request older than 5 minutes when first seen expires.
  - At most 10 documents are handled a poll and 20 requests act a day (a Pause that halts trading is never capped), so a flood can't keep toggling the switch or load the live database.
  - Pause engages the kill switch as the owner (open orders cancelled). Its reason is fixed ("Paused by the owner from the dashboard (request <id>)"): the halt reason reaches the agents, so the owner's note stays on the request document only.
  - Resume releases it, exactly like the operator tool `resume_trading`: the risk gate's limits and the automatic triggers still apply.
  - **Resume releases only the halt the page showed.** The page sends that halt's `since` and the later cause it showed (`haltAlsoAt`), and the engine refuses any difference, with the halt's category. This compares the halt itself, not the clocks of the dashboard and the engine.
  - **A cause found while trading is already halted is recorded beside the halt** (`alsoBy`, `alsoAt` on the kill switch, journaled as `also-triggered`; a finding repeated with the same reason stays quiet), and the Trading tile shows it. A cause that can't be saved is kept in memory until it is. Without this, a one-off finding such as the reconciler's "the account changed outside the engine" during a pause would leave no trace, and Resume would release it unseen. The halt's first reason is kept.
  - A halt without a start time (an old or hand-edited state document) is refused: resume it with the operator tool.
  - Each request acts at most once per engine run and keeps its outcome if its document can't be updated at once. A restart re-reads only requests younger than 5 minutes, and acting twice changes nothing.
- **Strategies and pairs in live** follow in a second step (#71): the same request types, taken by the live engine.
  - A live engine runs only versions whose spec file in `strategies/` the owner marked `live`, exactly as reviewed.
  - Pairs stay within `config/risk-limits.live.json`.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep live owner-only (Azure sign-in, Data Explorer, environment variables) | The owner's choice was the dashboard. The Data Explorer edit also skips the journal. |
| Release the first-start halt from a setting in the switch command | A setting that stays in place would release a later halt too, for example after the state document is lost. |
| Sign live requests with a secret only the dashboard and the engine hold | Code deployed to the dashboard can use the secret anyway. Against other holders of the account key it adds little, since they can already rewrite live state. |
| Widen the existing writer role to both databases | One role, one assignment: revoking live would take paper with it, and the setup script couldn't check each scope on its own. |

## Consequences

- The owner runs `setup-dashboard-requests.ps1 -Environment dev -LiveRequests` once. Until then the live buttons answer that live requests aren't set up.
- The Data Explorer step for the first start isn't needed: Resume does it, journaled as the owner's.
- **CI-deployed dashboard code can now send requests to the live engine**:
  - Pause or Resume;
  - (next step) a switch between live-marked strategies, or a pair change within the live limits.
- It still can't place an order, read live state through the writer, or exceed any risk limit, and the engine journals every request.
- The live writer role is in the shared Cosmos DB account, not in `rg-cix-live`, so `provision.ps1`'s outside-access check for the live environment doesn't list it. `setup-dashboard-requests.ps1` checks that only the writer identity holds it.
