# 0021. Any pair from the dashboard: watched coins, strategy-filtered trading, no pair lists in configuration

- Status: proposed
- Date: 2026-10-28
- Amends ADR-0006 (the curated universe) and ADR-0016 (pairs added within `AllowedSymbols`)

## Context

Which coins the engine may touch is still decided in three places: `AllowedSymbols` in the owner-only limits file (`config/risk-limits.json`, `config/risk-limits.live.json`), `Scanner:Universe` in `appsettings.json`, and the run-time list the owner edits on the Coins tab (ADR-0016). The fixed lists exist for one reason: the public market data allows 1 request a second, and with an order book per coin every 20 seconds that fits 15 coins (`MarketDataOptions`, ADR-0006 as amended). The dashboard's picker therefore shows only pairs the file already permits, an add is refused unless the coin traded at least the strategy's `minQuoteVolume24h` in the last 24 hours, and the cap is 20 (`RiskLimits.MaxTradableSymbolsLimit`).

The owner asked (2026-10-28) for a different model, and agreed to the design below on the same day, with two answers: the cap is 50, and a held coin that falls outside the strategy keeps its stop-loss.

- The picker shows every pair the exchange lists.
- An added coin appears on the dashboard and the engine watches it (prices, candles, the scan's statistics).
- A coin that passes the strategy's filters trades; one that doesn't is shown with the reason it was filtered out.
- A held coin can't be removed. If a strategy change drops it, it stays in the portfolio and the owner decides what to do with it.
- When the strategy changes, the coins that enter or leave trading are shown somewhere.
- No pair lists in configuration: only add, remove and the strategy.

Since #130 the live engine already reads every coin's bid and ask in one signed tickers request every 2 seconds, and the public tickers endpoint (`/api/1.0/public/tickers`, 1 request a second) returns the same shape for every pair at once: 375 rows, 61 of them in EUR, on 2026-10-28. The stop-loss protection (ADR-0020) confirms a stop on a freshly fetched order book, so it never depends on the polling schedule.

## Decision

**A coin is watched because the owner added it, and trades because it passes the strategy's filters now.** The owner's limits file keeps one rule (`AllowedQuoteCurrency` and `MaxTradableSymbols`, at most 50) and no pairs; the watched list lives in the database, held coins are always on it, and market data comes from one tickers request for all coins plus order books only where an order could happen.

### Two levels

1. **Watched.** The list the owner edits on the Coins tab (`tradable-pairs-<mode>`). The engine polls, scans and values every watched coin; the agents see them all; the risk gate permits orders in any of them (within everything else it checks). Sells, the owner's manual sells and the protective exits work for every watched coin.
2. **Trading.** The watched coins that passed every market filter of the active strategy at the latest scan (`minQuoteVolume24h`, `maxSpreadFraction`, `maxAtrFraction`, `minAtrToCostRatio`, `minHourlyCandles24h`; the scanner's exclusions, recorded in `scanner-filters-<mode>`). Only these get entry signals, and **only these can be bought**: the engine refuses a strategy buy (an agent's `place_order`, including one for an owner's agent-assisted request) in a coin the latest scan excluded, with the rule `strategy-filter` and the scanner's reason. This check sits with the strategy's own limits, before the risk gate, which stays final. It never applies to sells or protective exits. A coin without data yet (`stale`, `insufficient-history`) is not trading either: a buy in it is refused the same way, as the gate's own stale-data rule would refuse it anyway.

### The rule in the limits file, and nothing else

- `AllowedQuoteCurrency` (for example `EUR`) and `MaxTradableSymbols` (1 to 50, the hard limit `RiskLimits.MaxTradableSymbolsLimit`) are **required** in both limits files. A pair is permitted when it is quoted in that currency; nothing else widens or narrows permission. The files stay human-only, and still nothing outside them can set a `Risk` key.
- `AllowedSymbols` is removed from the limits and from the engine. A limits file that still has it fails startup with a message naming the key, and a test loads both files from the repository, so a stale file fails CI before it can be deployed. `Scanner:Universe` and the built-in default universe are removed too: the scan covers the watched list, and the `not-allowed` exclusion disappears.
- The watched list may be **empty**: a new environment starts with nothing watched, polls nothing but writes the catalog, and trades once the owner adds a coin. `TradableSymbols` and the poller accept an empty list; the safety monitor's stale rule has nothing to check.
- The cap is a risk constant: changing `MaxTradableSymbolsLimit` from 20 to 50 is a change to the domain's limits, with its own test, made on the owner's decision.

### Adding, removing, and coins the account holds

- **An add** needs a pair the file permits, room under the cap, and an active pair on the exchange in that currency. The 24-hour liquidity check at add time is gone: liquidity is the strategy's filter, applied at every scan, and a thin coin is simply shown as filtered out. Every other check of ADR-0016 stays: the request's age and shape, the journal entry before the save, the save before the list changes.
- **A remove** is refused while anything is held, reserved or working in the coin (unchanged). So a coin the strategy no longer wants stays watched, stays valued, keeps its stop and target (ADR-0020) and can be sold by hand; the dashboard marks it **in the portfolio, outside the strategy**.
- **Held coins are always watched.** At startup and at every pairs poll, a coin with a balance on the account (paper or live) in a permitted pair that isn't on the list is added by the engine, journaled as `pairs` / `adopted`. This replaces the first-start seed from `AllowedSymbols`: after this change the list is the saved one plus whatever is held, so a deposit or a residual never sits outside the risk numbers, the equity or the protection. Only a pair the file no longer permits (the owner changed the quote currency) is dropped at startup, with the kill switch engaged as before, so the owner looks for holdings in it before resuming.
- The portfolio's currency is `AllowedQuoteCurrency`, no longer the quote of the first allowed symbol.

### Market data for up to 50 coins

| Data | Live (signed, 1,000 a minute) | Paper (public, 1 a second) | For |
| --- | --- | --- | --- |
| Bid and ask of every watched coin | one tickers request every 2 s | one public tickers request every 5 s | prices, the scan, the gate's reference price, the stop-loss trigger |
| Hourly candles, per coin | every 5 min (0.2 a minute each) | the same | indicators, the strategy's filters |
| Order book, per coin | every 20 s, only for the **active set** | the same, spread out when the budget needs it | sizes for paper fills, the stop-loss confirmation, the protection's slippage band |
| Order book on demand | before every order, and when a stop is confirmed | the same | pricing an order on a book no older than the quote interval |

- The **active set** is the coins with a position or an order (open or just sent). Every other watched coin has tickers and candles only. The public budget stays 48 requests a minute: at 50 coins, 12 for tickers and 10 for candles, so about 8 active coins keep the 20-second books; more stretch the interval (the poller computes it from what is left), and the stop-loss protection never waits for it because it fetches its own book.
- **A fresh book before an order.** The execution service refreshes a coin's order book before the risk gate prices an order in it when the latest quote is a ticker (no sizes) or older than the quote interval. The gate's price band and the paper exchange's fill then rest on a book seconds old, not on a ticker or a book from a coin's last periodic poll.
- **Paper fills against books only.** The paper exchange ignores quotes without sizes (tickers); its resting orders fill on the active set's books and its taker orders on the book fetched before placement. Paper's results stay as conservative as before.
- Startup validates the schedule for the cap: tickers, candles for `MaxTradableSymbols` coins and at least one book interval must fit the public budget, and in live also the signed one. The first load of 7 days of hourly candles for 50 coins takes about a minute at one series a poll.
- A watched coin the exchange's tickers leave out keeps the periodic book if it is active; otherwise it shows as stale and is filtered out, never halting anything.

### Stale data: per coin first, global only when it matters

- A coin without a fresh quote is already refused by the gate (`stale-market-data`, data older than 5 minutes) and excluded by the scan (`stale`), so stale data blocks buys and signals **in that coin** by itself.
- The safety monitor halts everything only when a **held** coin has had no fresh quote for `StaleDataKillAfter` (15 minutes), because its stop can't be watched, or when **every** watched coin is that stale, because the feed is down. A watched coin nobody holds going quiet no longer stops trading in the other 49. A coin just added, or held for less than 15 minutes, gets the same grace as the engine's start.

### A strategy change shows which coins enter and leave

- At every scan the engine evaluates the market filters of **every strategy the owner could switch to** (the mode's switchable specs) against the same inputs, and publishes the result with the status: for each spec, the coins that would pass and the reason each other coin wouldn't. The dashboard's switch dialog shows, before the owner confirms: **enter trading**, **leave trading**, and **of those, in the portfolio**. One implementation of the filters, in the engine, serves both the scan and the preview.
- After a switch the existing `scanner-filters` journal entry (ADR-0015's recorder) carries `added`, `removed` and `heldOutside`, and the state document keeps the last change. The Coins tab shows it as a banner until the owner dismisses it.

### Dashboard

- The picker lists every active pair in the permitted currency not yet watched; the note above the list says what the limits allow (currency, cap, how many are watched) and that trading is the strategy's call.
- Each row shows the level: trading (rank), filtered out with the scanner's reason, waiting for data, held outside the strategy, or just added. Removing is offered only when the engine would allow it.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep the liquidity check at add time | The owner wants to watch thin coins and see why they don't trade; the strategy already filters them at every scan, and the add-time number was a snapshot of one day. |
| Books for every watched coin | 50 coins at 20 s is 150 public requests a minute against a budget of 48. Tickers carry every price in one request; books matter only where an order could happen. |
| Tickers for everything, no periodic books at all | Paper needs sizes to fill resting orders, and a stop confirmation on a book that is minutes old is what ADR-0020 rules out. The active set is small and gets both. |
| Seed the list from `AllowedSymbols` once more, then ignore the key | Keeps pairs in a configuration file the owner asked to be free of them, and a key that is read on one start and ignored on the next is a trap. Held coins are adopted instead, and the owner re-adds the rest from the dashboard. |
| Let agents buy any watched coin, trusting the signals | A coin the filters excluded could still be bought by an agent that ignores the scan or by an owner's agent-assisted request; the owner's rule is that a filtered coin does not enter trading. The engine enforces it. |
| Compute the switch preview in the dashboard from the target spec's parameters | A second copy of the filter arithmetic in JavaScript, drifting from the engine's. The engine already has the inputs and the code at every scan. |
| Keep the global stale halt for any watched coin | At 50 coins a quiet pair would halt trading several times a day; the per-coin rules already stop buys in it, and the halt stays where it protects something: a held coin or the whole feed. |

## Consequences

- Adding a coin is one dashboard request and needs no file, no deploy and no liquidity on the day. Whether it trades is visible on the Coins tab with the strategy's reason, and changes with the strategy, not with the owner's list.
- The owner edits both limits files once: remove `AllowedSymbols`, set `"AllowedQuoteCurrency": "EUR"` and `"MaxTradableSymbols": 50` (or less). CI refuses the change until the files fit, so the engines can't be deployed with a stale file. After the deploy each engine's list is the saved one plus the coins it holds; coins that were only in the file are added again from the Coins tab (live with its confirmation).
- The risk gate's symbol check is unchanged in shape (on the list and permitted) but the list can now hold up to 50 coins. Size, position, exposure, daily loss, drawdown, price band and order rate bound every one of them as before; the strategy's filters bound which can be bought.
- A held coin outside the strategy costs nothing to keep: it is valued, protected and sellable by hand. The owner sees it marked and decides.
- Market data costs the same at 50 coins as at 10 for prices (one request), and grows only with candles (0.2 a minute per coin) and the active set. Paper's fills and the stop-loss confirmation keep their order books; everything else prices from tickers, which the gate accepts within its 5-minute freshness rule.
- A quiet coin no longer halts trading; the halt protects held coins and the feed. The dashboard's status still lists coins without a quote.
- The agents' briefs and `get_market_features` grow to one row per watched coin, up to 50; `scan_market` describes the watched list, not a curated universe.
- Security review points for the change, checked in each PR: the dashboard's writer still only creates requests; permission still comes from the owner's file alone; held coins can't leave the risk numbers; filtered coins can't be bought; the public tickers are unauthenticated data validated like any quote (positive, uncrossed, not from the future); the global halt is narrowed only where a per-coin rule already blocks the same order; every list change, adoption included, is journaled first.
