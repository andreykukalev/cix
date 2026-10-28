# 0020. Stop-loss protection in the engine, independent of the strategy

- Status: proposed
- Date: 2026-10-28

## Context

Until #129 a stop-loss was a number in the position plan that the 5-minute scan compared with the latest quote. A fall between two scans went unseen for up to five minutes; a Pause (ADR-0018) stopped the scan's sells with everything else; and the public market data refreshed each coin once in 20 seconds. The owner's experience from manual trading was that the losses that hurt came not from bad entries but from coins not sold in time while the market fell and the money stayed locked in them.

The owner asked (2026-10-28) for emergency exits that work whatever the strategy does: a check on every quote, sells that pass the kill switch, immediate-or-cancel orders bounded in slippage, no duplicate sells, reconciliation before a retry, no sells on stale data, and a plain statement of where a sale cannot be guaranteed. Position sizes in live are 25 EUR a coin on pairs whose order books hold hundreds of thousands of euros within 3% of the mid (`docs/research/revolut-x-api.md`, the depth report of 2026-10-28), so the design can afford to be simple and conservative.

## Decision

The engine protects every held position itself, in `ProtectionService` (#129), with quotes fast enough to matter (#130, #131). The strategy sets the stop; the engine sells when the stop is hit, through the risk gate, whatever else is paused or halted. Three cases where a sale cannot be guaranteed are accepted and documented in the owner's runbook rather than engineered away.

- **Trigger on every quote.** A quote listener compares each coin's bid with its armed stop; a hit starts one exit per coin, never two. In Live, quotes come every 2 seconds for all coins in one signed tickers request and every 20 seconds from the signed order book; paper keeps the public endpoints.
- **Confirm, then sell.** The exit fetches a fresh order book first. A stale (older than `MaxQuoteAge`, 1 min), invalid or unconfirming book sells nothing: a wick that the book does not confirm is not a stop. The sell is an immediate-or-cancel limit for everything the balance says is free, priced at mid × (1 − `MaxEmergencySlippageFraction`), so the exchange fills at the bids down to that floor and leaves nothing resting. The limit is a floor, not the price: the live account fills such orders at the bid (spike, 2026-10-28). The engine's own open orders on the coin are cancelled first; coins an order still reserves are not called sold.
- **Through the risk gate, past the kill switch.** A protective exit is an order like any other except that it passes the kill switch (Pause and the 30% critical halt), uses the emergency band instead of the normal one, and has its own rate backstop. It still needs a held position, fresh market data and an allowed symbol. Nothing sells off a portfolio by itself: a breached limit blocks buys only, and a full freeze is a dashboard action the owner takes on purpose (a later ADR).
- **Retry from the facts, not from memory.** Partial fills are re-quoted on a new book every 2 s, at most one order per coin per 2 s and five a second in all. An uncertain placement (timeout, 5xx) is never re-sent blindly: the next try reads the balance and the engine's orders first. After five tries without a fill the exit is reported blocked (journal, critical log, dashboard) and retried every minute until the position is gone. When the exchange or the API comes back, the sale completes without a Resume.
- **Restart-safe.** Stops live in the position plans, in the journal. After a start the service reloads them (and again every `PlanRefreshInterval`), checks each against the latest quote, and sells a stop already crossed on the first fresh quote. A sell sent just before a crash leaves nothing to resell: the next try reads the balance.
- **Journaled latencies.** Each attempt records the trigger bid, the confirming book, the limit, the fill, the slippage against the trigger bid, and the times from detection to send, answer and read-back, so the paper journal shows how often wicks trigger exits and how fast the exchange answers before any further tuning.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Leave stops to the 5-minute scan | A fall between scans is unseen for up to five minutes, and Pause stops the scan's sells. The failure the owner saw in manual trading. |
| Native exchange-side stop orders (`conditional`, `tpsl`) | Would also cover the restart window and an engine outage, but not an exchange outage, and whether they reserve balance, show in active orders and exist on every pair is unconfirmed. A spike of about 5 EUR decides; if they hold up, they become a second layer under this one, not a replacement, since they can't apply the risk gate or the engine's reconciliation. |
| Market orders for the exit | No slippage bound: a thin book or a liquidity vacuum fills at any price. The IOC limit at mid minus the band is a market order with a floor. |
| Widen the band automatically when the exit is blocked | Turns a bounded loss into an unbounded one without the owner's decision. The band is the owner's number; a blocked exit is shown and retried, not forced. |
| Sell everything on a limit breach or the critical halt | The owner wants limit breaches to stop new risk, not to liquidate: a halt during a flash crash would sell every coin at the bottom. Full freeze stays a deliberate dashboard mode. |
| A separate watchdog process | Another process to deploy, lease and keep in step with the journal; the gain over a hosted service in the same engine is small, and the real gap (the engine down) is the one a native stop would close. |

## Consequences

- A stop is sold within seconds of the price crossing it, through Pause and the critical halt, with the loss bounded by the band when there are bids within it. The scan still sells take-profit targets; it leaves stops to the protection.
- `MaxEmergencySlippageFraction` is a new owner-only limit in `config/risk-limits.live.json` (0.03 to start; the hard ceiling 5% caps it). The protective backstop of 120 orders a minute sits above `HardCeilings.MaxOrdersPerMinute` (10) by the owner's decision.
- The exchange's immediate-or-cancel behaviour, the signed tickers filter, the signed order book's level shape and the meaning of `metadata.timestamp` are confirmed on the live account and recorded in `docs/research/revolut-x-api.md`. A change in any of them shows up as `ioc-rested`, a tickers log line, or a failing order-book parse.
- **Three cases where a sale is not guaranteed**, described for the owner in `docs/live-trading.md`:
  1. *The exchange or the API is unreachable.* No quote, no order. The exit retries every 2 s, then every minute, and completes when the API is back; the position is exposed meanwhile. Only an exchange-side stop would act during an engine-side outage, and nothing acts during an exchange-side one.
  2. *A gap or a liquidity vacuum below the band.* When the price jumps past the stop between two quotes, the sale happens at the new level: the band bounds the slippage against the current mid, not the distance from the stop. When the book holds no bids within the band at all, the exit is blocked and retried every minute; it sells when bids return, at whatever the price is then.
  3. *The restart window.* Between the engine stopping and its stops reloading after a start (a deploy or a crash, usually a minute or two), no stop is watched. A stop crossed meanwhile is sold on the first fresh quote after the restart, at that price.
- Still to do: the tpsl spike (about 5 EUR) to decide on a second, exchange-side layer; a full-freeze dashboard mode with warning and confirmation; and, after paper has journaled some exits, a decision on a dwell time before a confirmed stop sells, if wicks turn out to trigger exits too often.
