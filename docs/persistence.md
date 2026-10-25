# Persistence and recovery

How CIX stores what it knows, and how it picks up after a restart, a crash or a PC reboot without placing a trade twice, missing a fill or leaving a position without its stop.

## Two containers, two jobs

The Cosmos DB database `cix` has two containers.

| Container | Holds | Changes | Partition key |
|---|---|---|---|
| `state` | What is true **now**: the engine's operational state, the paper account, archived strategy specs. One document per id. | Documents are replaced as things change. | `/id` |
| `journal` | What **happened**: every decision, order, fill and safety event, with who did it and why. | Append-only. Nothing is ever updated or deleted (CLAUDE.md rule 4). | `/day` (UTC date) |

The rule of thumb is that **state** answers "what must the engine know to carry on right now?" and the **journal** answers "what happened, and why?". The engine recovers from state and reconciles it with the exchange. It doesn't rebuild state by replaying the journal, except for one fallback described below.

Without Cosmos DB (`Cosmos:Enabled` false, outside Compose), the same records live in files: the engine state in a JSON file and the journal in one `.jsonl` file per day.

## The `state` container

Every document has `id`, `type`, `about` (a one-line description for anyone browsing), `updatedAt` and `value`. Documents written before this change lack `type`, `about` and `updatedAt` until they are next saved.

| `id` | `type` | What it is | Written |
|---|---|---|---|
| `engine-state-paper` (`engine-state-live` in Live) | `engine-state` | The engine's operational state (below). | On every change: an order sent, confirmed or finished, a plan opened or closed, a new loss baseline, the kill switch. |
| `paper-account` | `paper-account` | The simulated Revolut X account: balances (`available`, `reserved`) per currency, and its orders (all open ones and the latest 500 finished). Paper mode only. | Every second when something changed, and at shutdown. |
| `spec-<name>@v<N>-<hash>` | `strategy-spec` | An immutable copy of a strategy spec version that ran. Journal entries name it in `specVersion` (`good@v1#3f2a9c1b04de`). | Once, the first time a version runs. Never overwritten. |

### `engine-state-*`

| Field | Meaning |
|---|---|
| `killSwitch` | Whether trading is halted (`engaged`), why (`reason`), by whom (`by`) and since when (`since`). A new Live state starts engaged until the owner confirms. |
| `peakEquity` | Highest equity seen, for the drawdown limit. |
| `day`, `dayStartEquity` | The UTC day and the equity at its start, for the daily loss limit. |
| `holdings` | Balances per currency at the last reconciliation, and the value of the engine's open orders then. Used to spot deposits, withdrawals or outside trades, including while the engine was down. |
| `orders` | Orders the engine **sent and hasn't yet seen finish**. Each is readable: `clientOrderId`, `symbol`, `side`, `size`, `limitPrice`, `sentAt`, `source` (`place_order` for an agent's order, `protective-exit` for the engine's own stop or target sell), `venueOrderId` (the exchange's ID, once confirmed), and for a buy its `stopLoss`, `takeProfit`, `runId` and `strategy`. Written **before** the order is sent. Removed once reconciliation has seen it finished for a full interval. |
| `positions` | The **plan for each position**, by symbol: `status` (`entry-pending` until the buy fills, then `open`), `entryClientOrderId` (the buy behind it), `stopLoss`, `takeProfit`, `since`, and once filled `entryPrice` and `filledAt`. Written before the buy is sent. A further buy for a position still held puts its levels in `pendingClientOrderId`, `pendingStopLoss` and `pendingTakeProfit`; they replace the plan's only if that buy fills (a level it doesn't set keeps the old one), so a buy that never fills can't move the stop. A buy after the position was sold out starts a fresh plan, or none. Removed once less than the exchange minimum of the coin is held and its buy is no longer working. |
| `knownClientOrderIds` | Older format: bare client order IDs from before `orders` existed. Still read, and dropped as those orders finish. |

### Why some IDs are still GUIDs

| ID | Format | Why |
|---|---|---|
| `clientOrderId` | GUID | The engine names each order with a fresh GUID, sent to the exchange as `client_order_id`. It must be unique for ever: the engine relies on it to recognise its own orders after a restart and never to send one twice. Its meaning comes from the `orders` record and the journal, which carry the symbol, side, size, source and reason. |
| `venueOrderId` (`orderId` in the journal) | GUID | The exchange's own ID. The paper exchange imitates it. |
| Journal entry `id` | `20261025-124650-order-3f2a9c1b04de` | Readable: UTC time, kind, and a random suffix for uniqueness. Entries from before this change keep their 32-character GUIDs. |
| State document `id` | `engine-state-paper`, `paper-account`, `spec-good@v1-…` | Names, not GUIDs. |

## The `journal` container

Each document is `{ id, day, entry }`. The entry has `id`, `at`, `kind`, `actor` (an agent such as `trader` or `analyst`, `owner`, or an engine part such as `engine`, `scanner`, `reconciler`), `runId` (the agent run), `specVersion`, a one-line `summary`, and `data` (string fields that depend on the kind).

| `kind` | Written when | Key `data` fields |
|---|---|---|
| `tool-call` | An agent used a read tool. | `tool` |
| `tool-denied` | A tool was refused (not allowed by the spec, wrong caller). | `tool` |
| `order` | An order was placed or refused (`place_order`, `cancel_order`, or the engine's `protective-exit`). | `tool`, `clientOrderId`, `orderId`, `symbol`, `side`, `requestedSize`, `requestedPrice`, `status`, `rule`, `reason`, `rationale`, `stopLoss`, `takeProfit` |
| `fill` | An order finished with something filled. Written once per order, even across restarts. | `clientOrderId`, `orderId`, `symbol`, `side`, `filledSize`, `averagePrice`, `fee`, `status`, `source`, `stopLoss`, `takeProfit`; `runId` and `specVersion` of the order |
| `order-closed` | An order finished with nothing filled (cancelled, expired, rejected). | as `fill` |
| `kill-switch` | Trading halted or resumed. | `action`, `reason`, `cancelled` |
| `scan-signal` | The scanner found a new entry or exit signal. | `symbol`, signal fields |
| `market-view` | The analyst recorded a view. | `regime`, `validUntil`, `avoid`, `sources` |

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
2. **Load the saved state:** the engine state, then the paper account. Each step can be retried, and the paper account is restored once only.
3. **Rebuild what memory held.** Every order in `orders` that the exchange had confirmed is tracked again, so its fill is journaled even if it filled while the engine was down. Their IDs can't be reused, and they never count as "someone else's order".
4. **Start listening.** The tool server and `GET /healthz` answer only now, so "healthy" means ready to trade.
5. **Reconcile** within a minute, then every two minutes, as described next.

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

- `GET http://127.0.0.1:5077/healthz` returns `{"status":"ready","mode":"paper","halted":false}` once the engine has loaded its state. It is reachable only inside the engine's network, like the tool server.
- Docker marks the engine **healthy** from it. In Compose, the trader and analyst start only when the engine is healthy, and are restarted with it, because they share its network.
- The agents also wait for `/healthz` before each run. While the engine is starting they log `Waiting for the engine to be ready` once a minute. They exit (so Docker restarts them) only when the engine's network is gone or nothing has answered for 10 minutes.
- After a PC reboot, Docker starts everything again by its restart policy. The engine waits for the emulator, the agents wait for the engine, and trading resumes from the saved state, with no step done by hand.
