# 0011. Owner manual requests through a requests container

- Status: proposed
- Date: 2026-10-26

## Context

The owner wants Buy and Sell buttons on the dashboard, for moments the strategy doesn't cover, such as selling after a sudden jump (#63). The constraints:

- Every order path goes through `RiskGate.Evaluate` (CLAUDE.md rule 1), and agents reach the exchange only through tools (rule 2).
- The dashboard is a static web app with a small API. The engine runs on the owner's machine (Docker) and listens on loopback only, so nothing on the internet can call it.
- The owner asked for two sell modes: **direct** (execute now, no agent) and **agent-assisted** (the trader treats the request as a signal to analyse and may decline or suggest something else). Buys are agent-assisted only.
- Duplicates must be prevented, and every request must stay distinguishable from automated trading in the journal.

## Decision

Requests are documents in a new Cosmos DB container, `requests` (partition `/day`). The dashboard creates them; the engine polls the container for new ones and is the only thing that acts on them, through `ExecutionService` and so the risk gate.

- **The engine trusts only itself after `new`:** it reads a request once, while it is `new` and carries no engine fields, and keeps everything after that in its own record in the `state` container (`manual-requests-<mode>`), which the dashboard can't write. The document gets a copy of each step for people. A document created with another status, or edited later, changes nothing.
- **Direct sell:** the engine sells the requested portion of the sellable position at the bid, as `manual-direct`. There is no owner limit price, and a manual order still unfilled after 4 minutes is cancelled: a resting sell could hold a position past its stop.
- **Agent-assisted:** the engine marks the request `awaiting-agent` and reports it on `/signals` (`manualKey`). The trader loop starts a run for it at once, with its own daily cap (so requests can't use up the strategy's runs), as agent `trader-requests`: only that agent may use the request tools, and the engine refuses it `place_order` and `cancel_order`, whatever the owner's note says. The trader reads it with `get_manual_requests` and answers with `answer_manual_request`: `execute` (for a sell, optionally a smaller portion; a buy needs a stop-loss) or `decline` with a rationale. The engine then places the order itself, as `manual-agent`. The trader can't exceed the request, and the tools are allowed whenever the spec allows `place_order`, which can do more.
- **Exactly once:** the engine claims a request in its record (`executing`, with its client order ID) before sending, so a restart never sends it twice; the execution service also refuses a reused client order ID. When the engine can't tell how a claimed order ended, it says so (`outcome-unknown`) rather than "not sent". Duplicates (same symbol and side while one is in flight), requests older than 5 minutes when first seen, more than 20 a day, and malformed documents are refused with a reason. An agent-assisted request unanswered in 20 minutes expires.
- **Paper only for now:** a request acts only on an engine of its `tradingMode` (`paper` when absent), and a Live engine ignores requests altogether unless the owner sets `ManualRequests:AllowLive`. That should stay off until requests are authenticated beyond write access to the database: today any holder of the shared account key (ADR-0009) could write one. Before live use, either sign requests in the dashboard API with a secret only it and the engine hold, or move to per-project access.
- **Record:** each step is journaled as `manual-request` (actor `owner`, `engine` or the agent) and as an event on the request document, which the dashboard shows as a history.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Dashboard API calls the engine | The engine listens on loopback only; exposing it means inbound access to the owner's machine and new authentication |
| Dashboard places orders itself | A second order path around the engine and the risk gate (rule 1) |
| Requests as journal entries | The journal is append-only and agent-written text lives there; a request needs a status the engine updates |
| Every request through the trader | The owner asked for a direct sell that doesn't wait for an LLM run |
| Trader places manual orders with `place_order` | It could change the symbol or size; answering a request keeps the order within what the owner asked |

## Consequences

- The dashboard needs a write identity that can only create documents in `requests` (PR 2), separate from its read-only key, behind owner sign-in.
- Existing Azure databases need `tools/azure/provision.ps1` once to add the container (the engine can't create containers there). Until then manual requests are off and the status says `unavailable`; everything else runs as before.
- The `requests` container is new data the reset script doesn't touch. (2026-10-26: it does with `-IncludeRequests`, the owner's choice for a fresh start.)
- A trader run for a request costs an LLM call, counted against its own daily cap (`TRADER_MAX_MANUAL_RUNS_PER_DAY`, 20); past it, agent-assisted requests expire.
- Owner text (`note`) reaches the trader as quoted data (`ownerNote`). It is cleaned and bounded, the trader's instructions treat it as context, and a request run has no order tools of its own to be steered into.
- The Windows script (`run-trader.ps1`) answers requests only when run with `-OwnerRequests`; the Docker trader loop is the supported path.
- Malformed, forged, oversized or excess requests are refused one by one (at most 50 read and 10 handled a poll, refusals journaled up to 50 a day, document copies written 10 at a time), so one bad document can't stop the others or use up the database's throughput. Requests for another trading mode aren't even read.
- The live-mode hook blocks agents from setting `ManualRequests:AllowLive` (outside tests and docs), as it does the trading mode.
