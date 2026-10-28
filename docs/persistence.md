# Persistence and recovery

How CIX stores what it knows, and how it picks up after a restart, a crash or a PC reboot without placing a trade twice, missing a fill or leaving a position without its stop.

## Three containers, three jobs

The Cosmos DB database `cix` has three containers.

| Container | Holds | Changes | Partition key |
|---|---|---|---|
| `state` | What is true **now**: the engine's operational state, the paper account, archived strategy specs. One document per id. | Documents are replaced as things change. | `/id` |
| `journal` | What **happened**: every decision, order, fill and safety event, with who did it and why. | Append-only. Nothing is ever updated or deleted (CLAUDE.md rule 4). | `/day` (UTC date) |
| `requests` | The owner's **manual buy and sell requests** from the dashboard, each with its outcome and history (ADR-0011). | The dashboard creates a request; the engine updates it as it handles it. Only the owner's test reset deletes one (`reset-database.ps1 -IncludeRequests`). | `/day` (UTC date) |

The rule of thumb is that **state** answers "what must the engine know to carry on right now?" and the **journal** answers "what happened, and why?". The engine recovers from state and reconciles it with the exchange. It doesn't rebuild state by replaying the journal, except for one fallback described below.

Without Cosmos DB (`Cosmos:Enabled` false, outside Compose), the same records live in files: the engine state in a JSON file and the journal in one `.jsonl` file per day, under a `paper` or `live` subfolder of `Journal:Directory`.

## The `state` container

Every document has `id`, `type`, `about` (a one-line description for anyone browsing), `updatedAt` and `value`. Documents written before this change lack `type`, `about` and `updatedAt` until they are next saved.

| `id` | `type` | What it is | Written |
|---|---|---|---|
| `engine-state-paper` (`engine-state-live` in Live) | `engine-state` | The engine's operational state (below). | On every change: an order sent, confirmed or finished, a plan opened or closed, a new loss baseline, the kill switch. |
| `engine-status-paper` (or `-live`) | `engine-status` | The engine's status for people and the dashboard (below). Built from what the engine already holds in memory, with no exchange call and no state change, and never read back, so it can't affect trading. | Every minute. |
| `paper-account` | `paper-account` | The simulated Revolut X account: balances (`available`, `reserved`) per currency, and its orders (all open ones and the latest 500 finished). Paper mode only. | Every second when something changed, and at shutdown. |
| `manual-requests-paper` (or `-live`) | `manual-requests` | The engine's own record of owner requests (ADR-0011): those it accepted and still acts on (`active`: the request as first read, its status, since when, the claim's `clientOrderId`), today's `accepted` and `refused` counts, and the requests accepted since yesterday (`seen`), so none is taken twice. | Before a request's order is sent, and at each later step. |
| `scanner-filters-paper` (or `-live`) | `scanner-filters` | The coin filters in force (`value.filters`, from the active spec's `scanner` parameters), the spec they came from, since when (`since`), and the coins that passed them (`passing`) or not (`excluded`, with the reason) at the last change. Read back at startup, so a restart doesn't count as a change. | Only when the filters or the set of passing coins change (see the `scanner-filters` and `scan-eligibility` journal entries). |
| `engine-lease-paper` (or `-live`) | `engine-lease` | Which engine may trade in this mode (`holder`: the host name and a random suffix, or null once released; `acquiredAt`). See "One engine per mode" below. | Every 15 seconds by the holder, and at shutdown (released). |
| `agent-runs-paper` (or `-live`) | `agent-runs` | Azure only (`AgentRuns:Mode` `Queue`): when the engine queues agent runs. Today's counts against the daily caps (`runs`, `refunds`, `requestRuns`, `requestRefunds`), the signal and request keys last handled, the next heartbeat, scheduled run and analyst slot, and the runs queued that haven't ended (`inFlight`, with `claimedAt` once their job took them). See "Agent runs in Azure" in [architecture.md](architecture.md). | When anything in it changes, and always before a run is queued, so a run is never queued twice. Each write is conditional on the version the engine read. |
| `strategy-<name>-v<N>` | `strategy-version` | A strategy version the owner can select (`value` is the spec: `name`, `version`, `status`, `purpose`, `riskProfile`, `instructions`, `parameters`, `allowedTools`). The paper engine adds Safe, Balanced, Risky and the other `paper` specs in `strategies/` at startup when they are missing, and never overwrites one; the live engine adds only the specs marked `live` to its own database (ADR-0018). See [strategies/README.md](../strategies/README.md). | Once per version, by the engine's seeding or by hand. Never changed afterwards: a change is a new version. |
| `strategy-selection-<mode>` | `strategy-selection` | Which version the mode should run: `value` is `{name, version, requestedBy, requestedAt, note}`. Written by the engine from the owner's dashboard request (or by hand in Data Explorer). Without it, the engine runs the spec in `Strategy:SpecPath`. Live runs only a version a spec file marked `live` holds, exactly as reviewed (ADR-0018). | By the owner, whenever they switch. The engine polls it every 30 seconds and acts on each `name`, `version` and `requestedAt` once. |
| `tradable-pairs-<mode>` | `tradable-pairs` | The pairs the mode trades now (ADR-0016; live too, within its own limits file, ADR-0018): `value` is `{symbols, updatedAt, updatedBy, adopted}`. Written by the engine when the owner adds or removes a pair from the dashboard, and when it adds a coin the account holds (journal `pairs` / `adopted`, ADR-0021). `adopted` lists those held coins the owner hasn't added: watched and protected, but not bought by the strategy until the owner adds them (journal `pairs` / `confirmed`); restored at startup, dropping any pair the mode's limits file (`config/risk-limits.json`, or `config/risk-limits.live.json` for live) no longer permits. Without it, only the coins the account holds are watched. | At each accepted change. |
| `pair-catalog-<mode>` | `pair-catalog` | For the dashboard's pair picker: the tradable pairs, the most there may be (`cap`), the permitted quote currency, and the exchange's active pairs in that currency with whether each is tradable and permitted, and the held coins the owner hasn't added (`adopted`). | At startup, hourly and after each change. |
| `strategy-schema-<mode>` | `strategy-schema` | Every strategy parameter for the dashboard: its group, label, explanation, kind, valid range, unit, whether it applies to new trades only or to open positions too, and which mandatory limit bounds it. Also the mandatory safety limits (from the mode's limits file and the engine) and the tools a spec may allow. | At every startup. |
| `spec-<name>@v<N>-<hash>` | `strategy-spec` | An immutable copy of a strategy spec version that ran. Journal entries name it in `specVersion` (`good@v1#3f2a9c1b04de`). | Once, the first time a version runs. Never overwritten. |

### `engine-state-*`

| Field | Meaning |
|---|---|
| `killSwitch` | Whether trading is halted (`engaged`), why (`reason`), by whom (`by`) and since when (`since`). A new Live state starts engaged until the owner confirms. `alsoBy`, `alsoReason` and `alsoAt`: the latest other cause found while it was already engaged (journaled as `also-triggered`), so the dashboard's Resume can't release it unseen (ADR-0018). |
| `peakEquity` | Highest equity seen, for the drawdown limit. |
| `day`, `dayStartEquity` | The UTC day and the equity at its start, for the daily loss limit. |
| `holdings` | Balances per currency at the last reconciliation, and the value of the engine's open orders then. Used to spot deposits, withdrawals or outside trades, including while the engine was down. |
| `orders` | Orders the engine **sent and hasn't yet seen finish**. Each is readable: `clientOrderId`, `symbol`, `side`, `size`, `limitPrice`, `sentAt`, `source` (`place_order` for an agent's order, `protective-exit` for the engine's own stop or target sell), `venueOrderId` (the exchange's ID, once confirmed), and for a buy its `stopLoss`, `takeProfit`, `runId` and `strategy`. Written **before** the order is sent. Removed once reconciliation has seen it finished for a full interval. |
| `positions` | The **plan for each position**, by symbol: `status` (`entry-pending` until the buy fills, then `open`), `entryClientOrderId` (the buy behind it), `stopLoss`, `takeProfit`, `since`, and once filled `entryPrice` and `filledAt`. Written before the buy is sent. A further buy for a position still held puts its levels in `pendingClientOrderId`, `pendingStopLoss` and `pendingTakeProfit`; they replace the plan's only if that buy fills (a level it doesn't set keeps the old one), so a buy that never fills can't move the stop. A buy after the position was sold out starts a fresh plan, or none. A held position without a stop (a coin the engine adopted, the owner's own buy, an older buy) gets one from the protection (`entryClientOrderId` `adopted` when it had no plan): the active strategy's `stopAtr` hourly ATRs below the bid when first seen, set once. Removed once less than the exchange minimum of the coin is held and its buy is no longer working. |
| `knownClientOrderIds` | Older format: bare client order IDs from before `orders` existed. Still read, and dropped as those orders finish. |

### `engine-status-*`

| Field | Meaning |
|---|---|
| `updatedAt` | When the engine wrote it: its heartbeat, and its last successful database write. Older than a few minutes means the engine is down or can't reach the database. |
| `startedAt`, `mode`, `strategy` | When this engine process started, paper or live, and the active strategy spec. |
| `halted`, `haltedBy`, `haltCategory`, `haltedSince`, `haltAlsoCategory`, `haltAlsoAt` | The kill switch: which part of the engine engaged it and a fixed description of why. The exact reason (which can name orders or amounts) is only in the journal's `kill-switch` entry. `haltAlso*`: a later cause found while already halted, and when. |
| `lastReconciledAt`, `lastReconcileAttemptAt`, `lastReconcileProblem` | The last full comparison with the exchange (the portfolio's last sync), the last attempt, and a problem code: `exchange-unreadable`, `unknown-open-orders`, `unexplained-holdings-change`, `holdings-not-compared` (no fresh prices to value the holdings), or empty. |
| `lastScan` | The latest scan: when, how many coins ranked and excluded, entry and exit signals, and the signal key the trader watches. `market` lists every coin: rank, trend, 1h and 24h return, hourly range (ATR) and volume ratio as fractions, its signal (`buy`, `sell (stop)`, ...) and why it has no entry or was excluded. Excluded coins also carry their 1h and 24h return when their data is fresh. No prices. |
| `marketView` | The analyst's current view: regime, until when, coins to avoid. |
| `oldestQuoteAt`, `unquotedSymbols` | The oldest of the latest prices across the tradable coins that have one, and the coins with none yet. Minutes old means market data has stalled. |
| `filters` | The coin filters in force: spec, `since` (when first applied), `minQuoteVolume24h`, `maxSpreadFraction`, `maxAtrFraction`, `minAtrToCostRatio`, `minHourlyCandles24h`, `requireUptrendPullback`, `trendExits`, `maxEntrySignals`, `orderQuote`. |
| `strategySelection` | The state of the owner's strategy selection (`config` when there is none and the configured spec runs, `active`, `pending` while a trader run is in progress or the journal can't be written, `refused` with the `problems` found, `unreadable` during a database outage), the version requested, by whom and when, when it was last checked, and when the running spec was activated. |
| `manualRequests` | Whether manual requests are on (`state`: `ok`, `off`, `unavailable` when the `requests` container doesn't exist yet, `not-used` without a database), how many wait for the trader, and the last poll. |
| `portfolio` | As the safety monitor last valued it (every minute): equity, cash (equity minus holdings, including what open buys reserve), peak equity, today's P&L, exposure, and each holding with its value and plan (status, entry, stop, target). Absent until the first valuation. |

### Why some IDs are still GUIDs

| ID | Format | Why |
|---|---|---|
| `clientOrderId` | GUID | The engine names each order with a fresh GUID, sent to the exchange as `client_order_id`. It must be unique for ever: the engine relies on it to recognise its own orders after a restart and never to send one twice. Its meaning comes from the `orders` record and the journal, which carry the symbol, side, size, source and reason. |
| `venueOrderId` (`orderId` in the journal) | GUID | The exchange's own ID. The paper exchange imitates it. |
| Journal entry `id` | `20261025-124650-order-3f2a9c1b04de` | Readable: UTC time, kind, and a random suffix for uniqueness. Entries from before this change keep their 32-character GUIDs. |
| State document `id` | `engine-state-paper`, `paper-account`, `spec-good@v1-…` | Names, not GUIDs. |

## The `journal` container

Each document is `{ id, day, entry, mode }`. `mode` is `paper` or `live`, the mode of the engine that wrote it (#65). A paper and a live engine can share the container, so every engine read filters on its own mode: the stops it recovers from fills and orders, reconciliation's check for a journaled fill, the market view it restores at startup, and the operator tools, `get_daily_summary` included. The analyst's `market-view` is per mode too, since each engine runs its own analyst. Documents from before the tag have no `mode` and count as paper; they are never rewritten. The dashboard reads one mode's entries the same way. The entry has `id`, `at`, `kind`, `actor` (an agent such as `trader` or `analyst`, `owner`, or an engine part such as `engine`, `scanner`, `reconciler`), `runId` (the agent run), `specVersion`, a one-line `summary`, and `data` (string fields that depend on the kind).

| `kind` | Written when | Key `data` fields |
|---|---|---|
| `tool-call` | An agent used a read tool. | `tool` |
| `tool-denied` | A tool was refused (not allowed by the spec, wrong caller). | `tool` |
| `order` | An order was placed or refused (`place_order`, `cancel_order`, the engine's `protective-exit`, or an owner request: `manual-direct`, `manual-agent`). | `tool`, `clientOrderId`, `orderId`, `symbol`, `side`, `requestedSize`, `requestedPrice`, `status`, `rule`, `reason`, `rationale`, `stopLoss`, `takeProfit`; `requestId` for owner requests |
| `fill` | An order finished with something filled. Written once per order, even across restarts. | `clientOrderId`, `orderId`, `symbol`, `side`, `filledSize`, `averagePrice`, `fee`, `status`, `source`, `stopLoss`, `takeProfit`; `runId` and `specVersion` of the order |
| `order-closed` | An order finished with nothing filled (cancelled, expired, rejected). | as `fill` |
| `kill-switch` | Trading halted or resumed. | `action`, `reason`, `cancelled` |
| `stop-adopted` | The protection gave a held position without a stop one (actor `engine`; pre-live assessment F-02). | `symbol`, `stopLoss`, `bid`, `atr`, `stopAtr`, `saved` (`false` while it is armed but not yet in the engine state) |
| `scan-signal` | The scanner found a new entry or exit signal. | `symbol`, signal fields |
| `manual-request` | An owner request was received (actor `owner`), refused, sent to the trader, decided by the trader (actor `trader-requests`), placed, cancelled unfilled, filled or closed. | `requestId`, `mode` (`direct`, `agent`), `side`, `symbol`, `requestedPortion` or `quoteAmount`, `status`, `rule`, `reason`, `rationale`, `clientOrderId`, `note` |
| `scanner-filters` | The coin filters in force changed (a new spec version), or were recorded for the first time. | `spec`, `previousSpec`, `changes` (`minQuoteVolume24h 100000 → 20000; ...`), `filters` and `previousFilters` (JSON), `passing`, `excluded` (`SYMBOL:reason,...`) |
| `scan-eligibility` | The set of coins that pass the filters changed under the same filters, in two scans in a row. A coin without data yet (`stale`, `insufficient-history`) keeps its last result. | `added`, `removed`, `passing`, `excluded`, `spec` |
| `agent-run` | Azure only: the engine queued an agent run, its job claimed it, or it ended (actor `engine`, `runId` the run). Also the daily cap being reached. | `step` (`queued`, `not-sent`, `claimed`, `done`, `failed`, `not-run`, `capped`), `job` (`trader`, `trader-requests`, `analyst`), `purpose` (`signals`, `heartbeat`, `scheduled`, `requests`, `analyst`), `key` (the signals or requests it was for) |
| `pairs` | The owner asked to add or remove a trading pair (actor `engine`). Written before the change applies; a refusal is written too. | `action` (`added`, `removed`, `refused`), `mode`, `symbol`, `requested` (`add`, `remove`), `requestedBy`, `requestedAt`, `from` and `to` (the lists, comma-separated), `note`, `problem` |
| `strategy` | The owner's strategy selection was activated or refused (actor `engine`; `specVersion` is the new spec, or the running one when refused). Written before the switch, so a switch is never unrecorded. A refused selection is written once. | `action` (`activated`, `refused`), `mode`, `requested` (`name@vN`), `from` and `to` (spec labels with hash), `requestedBy`, `requestedAt`, `note`, `applies` (what the change affects), `problems` |
| `balance-snapshot` | Once each UTC hour the engine holds the lease and has a valuation under 5 minutes old (actor `engine`; #70). The engine checks the journal for the hour's snapshot first, so a restart or a second engine in the same hour adds none. The history behind the dashboard's `/api/balance-history`, since `engine-status-<mode>` keeps only the latest. | `source` (`exchange` for live, `simulated` for paper), `currency`, `valuedAt`, `equity`, `cash`, `peakEquity`, `pnlToday`, `exposure`, `holdings` (how many), `value:<symbol>` and `base:<symbol>` (each holding's value and the coins that can be sold) |
| `market-view` | The analyst recorded a view. | `regime`, `validUntil`, `avoid`, `sources`, `highlights` (from analyst v2: a JSON list of up to five items with `title`, `impact`, `direction`, `symbols`, `detail`, `source`, and from v3 `titleRu` and `detailRu`, for the owner's dashboard; the trader never sees them), `summaryRu` (from analyst v3: the summary in Russian, for the owner's dashboard) |

## The `requests` container

One document per owner request, partitioned by its UTC `day`. The dashboard (or, for testing, Data Explorer) creates it; the engine polls every 5 seconds for new ones and copies each step it takes onto the document. ADR-0011 explains the design.

| Field | Written by | Meaning |
|---|---|---|
| `id` | requester | 8 to 64 letters, digits or dashes; a GUID from the dashboard. Creating the same id twice fails, so a double click can't make two requests. |
| `day`, `createdAt` | requester | The UTC date and time of the request. A request more than 5 minutes old when the engine first sees it expires instead of trading on a stale intent. |
| `side`, `mode` | requester | `sell` with `direct` (the engine sells at once) or `agent` (the trader decides); `buy` with `agent` only. |
| `symbol` | requester | A watched coin, such as `XRP-EUR`. |
| `portion` | requester | Sells: the share of the sellable position, more than 0 and at most 1. |
| `quoteAmount` | requester | Buys: how much quote currency (EUR) to spend. |
| `note`, `requestedBy` | requester | Optional text for the trader and the history (cleaned, at most 280 characters), and who asked. |
| `tradingMode` | requester | `paper` (the default) or `live`. An engine acts only on requests for its own mode, and a Live engine only with `ManualRequests:AllowLive`, which is off. |
| `status` | both | `new` (requester), then the engine's: `awaiting-agent`, `executing`, `placed`, `unconfirmed`, `filled`, `closed`, `declined`, `rejected`, `failed`, `invalid`, `duplicate`, `expired`. A new request must have status `new` and none of `outcome`, `events` or `updatedAt`. |
| `outcome` | engine | `rule`, `reason`, and as they apply `decidedBy`, `runId`, `rationale`, `clientOrderId`, `requestedSize`, `requestedPrice`, `approvedSize`, `approvedPrice`, `orderId`, `filledSize`, `averagePrice`. |
| `events` | engine | The history: one `{ at, status, by, detail }` per step. |

The engine reads a request document once, while it is `new`. From then on it trusts only its own record, `manual-requests-paper` in the `state` container (below), and writes each step onto the document as a copy for people. So a document written with another status, or edited later, changes nothing.

A request is claimed in that record, with its `clientOrderId`, **before** its order is sent. If the engine stops at that moment, it finds the claim at the next start: the order is reconciled if it was sent (`unconfirmed`, then `filled` or `closed`). If there is no trace of it after 2 minutes, the request is closed as `outcome-unknown`, with a reminder to check the exchange's order history before sending it again. The same happens to a placed order the engine stops tracking without a record of how it ended. A manual order still unfilled after 4 minutes is cancelled, and the request closes with what filled. A request is never executed twice.

### Strategy switch requests

The dashboard's Strategy tab writes a switch as a request too (`type: "strategy-selection"`), because its writer identity can only create documents in this container: `id`, `day`, `createdAt`, `name`, `version` (a JSON number), `note`, `requestedBy` (`owner`), `tradingMode` (`paper`, or `live` in `cix-live`'s container when the owner ticked the real-money box) and `status: "new"`. Manual requests skip this type. Each mode's strategy selection service reads its own new ones every 30 seconds: the newest valid one is saved as `strategy-selection-<mode>` and handled like any selection (validated, journaled as kind `strategy`, applied between trader runs); its document becomes `accepted`. Earlier ones become `superseded`; a malformed one `invalid`; one more than 5 minutes old when the engine first sees it `expired`. Whether the switch then ran or was refused is in the journal and in `strategySelection` on the engine status.

### Pause and Resume requests

The Trading tile's Pause and Resume buttons write `type: "trading-control"` (ADR-0018): `id`, `day`, `createdAt`, `action` (`pause` or `resume`), `note`, `requestedBy` (`owner`), `tradingMode` (`paper` or `live`) and `status: "new"`; a Resume also carries `haltSince` and `haltAlsoAt`, the halt the page showed (from `haltedSince` and `haltAlsoAt` on the engine status). Unlike the other types, a Live engine takes its own mode's: the dashboard writes those to `cix-live`'s `requests`, only after the owner confirms on the page. The control service reads new ones every 10 seconds (at most 10 a poll, 20 acting a day), and the newest valid one acts: Pause engages the kill switch as the owner with a fixed reason (the note stays on the document); Resume releases it only if the halt is still the one the page showed, and otherwise becomes `rejected` with the halt's category. The document becomes `accepted` or `rejected` with what happened, older ones `superseded`, a malformed one `invalid`, and one more than 5 minutes old when first seen `expired`. The kill switch change itself is journaled (kind `kill-switch`, actor `owner`).

### Pair requests

The Coins tab adds and removes trading pairs the same way (`type: "tradable-pair"`, ADR-0016): `id`, `day`, `createdAt`, `action` (`add` or `remove`), `symbol` (`SOL-EUR`), `note`, `requestedBy` (`owner`), `tradingMode` (`paper`, or `live` as above) and `status: "new"`. The pairs service reads new ones every 30 seconds, in the order they were made. An add needs a pair the mode's limits file permits (`config/risk-limits.json`, or `config/risk-limits.live.json` for live), room under its `MaxTradableSymbols`, an active pair on the exchange in the account's quote currency, and the 24h traded value the running strategy's scanner asks for (`minQuoteVolume24h`). A remove needs another pair left, no open order in the coin and no sellable holding of it. The document becomes `accepted`, `rejected` (with the reason), `invalid` or `expired` (more than 5 minutes old when first seen).

Example, for testing in Data Explorer (`requests` container, New Item; set `day` and `createdAt` to now, in UTC):

```json
{
  "id": "test-sell-0001",
  "day": "2026-10-26",
  "createdAt": "2026-10-26T10:15:00Z",
  "side": "sell",
  "mode": "direct",
  "symbol": "XRP-EUR",
  "portion": 0.5,
  "note": "testing a direct sell",
  "requestedBy": "owner",
  "status": "new"
}
```

### Tracing one trade

Everything about one order shares its `clientOrderId`. In Data Explorer (http://localhost:1234), run on the `journal` container:

```sql
SELECT c.entry.at, c.entry.kind, c.entry.actor, c.entry.summary
FROM c WHERE c.entry.data.clientOrderId = "<clientOrderId>"
```

From there, `runId` leads to everything that agent run did (`WHERE c.entry.runId = "run-20261025-124644"`), and `specVersion` leads to the exact strategy text in `state` (`spec-good@v1-<hash>`). A position's plan in `engine-state-paper` names its buy in `entryClientOrderId`.

## Recovery

### Startup

1. **Wait for the database.** The engine connects to Cosmos DB and retries with growing pauses (2, 4, 8, 16, then every 30 seconds) while it is starting, which takes minutes after a reboot. It logs `Waiting for the database: attempt N failed (...)` each time. Only a failure that waiting can't fix (a bad setting, an unreadable saved state) stops startup.
2. **Take the engine lease** for this mode (see below). While another engine holds it, the engine waits and logs `Another engine holds the lease for this trading mode; waiting…` once.
3. **Load the saved state:** the engine state, then the paper account. Each step can be retried, and the paper account is restored once only.
4. **Rebuild what memory held.** Every order in `orders` that the exchange had confirmed is tracked again, so its fill is journaled even if it filled while the engine was down. Their IDs can't be reused, and they never count as "someone else's order".
5. **Start listening.** The tool server and `GET /healthz` answer only now, so "healthy" means ready to trade.
6. **Reconcile** within a minute, then every two minutes, as described next.

### One engine per mode

With a shared database, two engines of the same mode can briefly run at once: a deployment starts the new one before stopping the old one, a crashed host may not have noticed yet, or the engine moves from the PC to Azure (ADR-0013). Only the engine holding `engine-lease-<mode>` loads the state and acts on the exchange.

- **Holding it.** The holder renews the document every 15 seconds. If it goes 60 seconds without a successful renewal, for example during a database outage, it stops sending or cancelling orders and stops saving the paper account. The engine then shuts down and the host restarts it as a waiting engine. If a renewal finds that another engine took the lease, it shuts down at once.
- **Taking it.** A waiting engine takes the lease when there is none, when it was released, or when the document has stayed unchanged for 2 minutes by the waiting engine's own clock. The gap between 60 seconds and 2 minutes lets the old holder stop first. No two clocks are compared, and every write is an ETag check, so two engines can't both take it.
- **Shutting down.** A clean shutdown releases the lease after everything else has stopped, so the next engine starts at once. After a crash, the next engine waits the 2 minutes.
- **Compose.** `docker compose up -d --build` stops the old engine before starting the new one, so the lease passes over at once.
- **Container Apps.** A new revision waits for the lease while the old one runs, and Container Apps keeps the old one running until the new one is ready. So the deployment (`tools/ci/deploy-agents.sh`) stops the old revision once the new one exists, and the lease passes over as with Compose. A liveness probe mustn't restart a waiting engine within 2 minutes, because the wait starts again with each restart (#80). A revision changed by hand (in the portal, or `provision.ps1`) needs its old revision stopped the same way.
- **Without Cosmos DB** there is nothing to share, and the engine always holds the lease.

### Reconciliation (every 2 minutes, and right after startup)

The exchange (paper or Revolut X) is the truth for orders and balances. The engine compares it with its state:

- **Open orders it didn't send** halt trading (kill switch). An order it sent just before a restart is recognised by its `clientOrderId` in `orders`.
- **Holdings that changed more than its fills explain** (a deposit, withdrawal or outside trade, even while it was down) halt trading.
- **Finished orders** are looked up, and how each ended is journaled as `fill` or `order-closed`. For an order picked up again after a restart the journal is checked first, so a fill is never journaled twice.
- **Position plans** follow the fills. A filled buy opens its plan with the fill price. A buy that ended unfilled drops a plan that was waiting for it. A plan whose coin is no longer held is removed.
- **Orders gone for a full interval**, and whose ending is journaled, are dropped from `orders`.

### Restart scenarios

| Engine stops… | After restart |
|---|---|
| Before an order is sent | Nothing was sent. The agent's next run sees no order and the signal again. |
| After the order was recorded, before or during sending | The order is in `orders`. If it reached the exchange it is recognised as the engine's own and tracked; if not, it is dropped after an interval. Never sent twice: the same `clientOrderId` can't be reused. |
| After the order was confirmed, before its fill was journaled | It is tracked again from `orders`, and its fill is journaled once at the next reconciliation. The position's stop and target were saved before the order was sent. |
| While an order rests or a position is held | Orders and plans are reloaded. The engine keeps watching stops and targets and sells by itself (protective exits). |
| During an agent run | The run's tool calls up to then are journaled. Orders it placed are handled as above. The next run starts from the scanner's signals. A coin already held, or with an order working, gets no new entry signal, so a repeated run can't buy twice. |
| Paper mode: within a second of a paper fill | The paper account is saved every second, so at most that second is lost. The order is then still resting in the restored account and fills again at its limit. The journal records the fill once. |

The agents' containers keep no trading state. The trader remembers only which signals it has already acted on, and after a restart it acts on the current ones again. That is safe for the reasons above.

### Positions opened before this change

A position with no plan in `positions` (opened before plans were kept in state) takes its stop and target from the journal: the latest `buy` fill for that coin and the order behind it. Once such a position is sold, this fallback is no longer needed.

## Health and readiness

- `GET http://127.0.0.1:5077/healthz` answers once the engine has loaded its state, for example `{"status":"ready","mode":"paper","halted":false,"database":"ok","databaseOkAt":"…","lastReconciledAt":"…"}`. It is reachable only inside the engine's network, like the tool server.
  - `database` is `ok`, `unreachable` (the last status write failed; `status` is then `degraded`), `unknown` (not checked yet) or `not-used`.
  - While the database is unreachable, the engine keeps running. Orders are refused, because each must be recorded first. Protective sells are refused too and retried. Writes are retried as the connection returns. Nothing is half-written: each state change is one document write.
- Docker marks the engine **healthy** from it. In Compose, the trader and analyst start only when the engine is healthy, and are restarted with it, because they share its network.
- The agents also wait for `/healthz` before each run. While the engine is starting they log `Waiting for the engine to be ready` once a minute. They exit (so Docker restarts them) only when the engine's network is gone or nothing has answered for 10 minutes.
- After a PC reboot, Docker starts everything again by its restart policy. The engine waits for the emulator, the agents wait for the engine, and trading resumes from the saved state, with no step done by hand.
