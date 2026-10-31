# Strategy specs

A strategy spec is what the trading agents follow: instructions, parameters and the tools they may call. It is the only thing you change to retune the strategy; no code changes.

- One file per version: `<name>.v<N>.json`. Never edit a version that has run. Copy it to `v<N+1>` and change the copy, so the journal's record of what ran stays true. Each journal entry carries `name@vN#hash`; the hash shows if a file was edited anyway.
- `status`: `draft` (never runs), `paper` (paper trading only), `live` (paper or live). Only the owner sets `live`; the `protect-live-mode` hook stops agents from writing it.
- `allowedTools`: the tool server refuses any agent tool not listed here, except `get_strategy_spec`, which every agent may read to learn its own rules.
- Pick the starting spec with `Strategy:Name` and `Strategy:Version` in the engine's configuration. The default is `balanced` v3. In paper trading, a selection in the database overrides it at run time (below).

## Safe, Balanced and Risky

The Strategy tab offers three presets, the same in paper and live (ADR-0022): the newest version of `safe`, `balanced` and `risky` that the owner marked `live`, plus the strategy running now if it is another one. Older and paper-only versions stay here and in the journal, unlisted.

### Calibrated presets: Safe v3, Balanced v3, Risky v4

Calibrated on Revolut X hourly candles for every EUR pair from December 2025 to October 2026, with fees, spreads sampled from the live tickers and slippage (the report is linked from the pull request). All three share one signal, from the only region of settings that came close to breaking even after costs in that period: a deep pullback (price at or under the 30-hour EMA) in an uptrend (EMA 30 above EMA 55), in a coin whose hourly ATR is at least 7 times its round-trip cost, with a wide stop and the trend exit doing most of the exiting. No setting showed a reliable profit: these presets trade rarely and roughly break even, where the earlier ones lost steadily. They differ in size, exposure, frequency and how many coins qualify.

| | Safe v3 | Balanced v3 | Risky v4 |
|---|---|---|---|
| Order size / risk per trade | 5 / 0.25 EUR | 10 / 0.5 EUR | 20 / 1 EUR |
| Per coin / total in coins | 10 EUR / 25% | 20 EUR / 40% | 25 EUR / 60% |
| Daily loss stop | 2 EUR | 3 EUR | 5 EUR |
| Buys per run / signals per scan / cooldown | 1 / 1 / 4 h | 1 / 2 / 2 h | 2 / 3 / 1 h |
| Trend EMAs / entry | 30 / 55; at or under the fast EMA | same | same |
| Stop / reward-to-risk | 5 ATR / 2 | 4 ATR / 2 | 4 ATR / 2 |
| 24h volume, spread, hourly ATR (max; min as × cost) | ≥ 250,000 EUR, ≤ 0.1%, ≤ 4%; 7× | ≥ 100,000 EUR, ≤ 0.15%, ≤ 6%; 7× | ≥ 50,000 EUR, ≤ 0.15%, ≤ 8%; 7× |

They start as `draft`, fitted to `config/risk-limits.live.json`; once the owner marks them `live`, both modes offer them.

Two things to watch when they run:

- **The trend exit is the trader agent's.** In the backtest almost every position closed on the trend turning down; the engine itself sells only on a stop or a target, and with a 4–5 ATR stop that leaves a position open longer if the agent can't run. With trend exits off, the backtest lost 1.6% (Safe) and 7.7% (Balanced) instead of breaking even.
- **The slow EMA is 55, the most the scan's 60 hourly candles allow** (`ScannerParameters.MaxEmaPeriod`), so it is seeded from only a few hours more than its period. The backtest computed it the same way.

### Earlier versions

Three paper strategies to choose from. Each states its purpose and risk profile in the file. None promises a return, and a riskier one is not expected to earn more: it takes larger and more frequent positions, so both gains and losses can be bigger.

| | Safe v1 | Balanced v1 | Risky v2 |
|---|---|---|---|
| Order size (`orderQuote`) | 5 EUR | 10 EUR | 20 EUR |
| Risk per trade (`scanner.riskPerTradeQuote`) | 0.5 | 1 | 2 |
| Per coin (`maxPositionQuotePerSymbol`) | 10 EUR | 25 EUR | 50 EUR |
| Total exposure (`maxExposureFraction` of the allocation) | 25% | 50% | 80% |
| Daily loss stop (`dailyLossStopQuote`) | 3 EUR | 6 EUR | 12 EUR |
| New positions per run | 1 | 1 | 2 |
| Cooldown between buys | 4 h | 1 h | 15 min |
| Trend EMAs (`fastEmaPeriod` / `slowEmaPeriod`) | 20 / 50 | 20 / 50 | 12 / 36 |
| Stop / reward-to-risk | 1.5 ATR / 2.5 | 1.5 ATR / 2 | 1.2 ATR / 1.5 |
| Entries | uptrend pullback, within 0.3 ATR of the fast EMA | uptrend pullback, within 0.5 ATR | uptrend pullback, within 1 ATR |
| Trend exits | yes | yes | yes |
| 24h volume, spread, hourly ATR (max; min as × cost) | ≥ 50,000 EUR, ≤ 0.3%, ≤ 3%; 1.5× | ≥ 20,000 EUR, ≤ 0.4%, ≤ 5%; 1× | ≥ 20,000 EUR, ≤ 0.5%, ≤ 7%; 0.8× |

The engine enforces each strategy's own limits before the risk gate: a buy larger than the order size, or beyond the per-coin, exposure, daily loss, per-run or cooldown limits, is reduced or refused. They can only be tighter than the mandatory limits in `config/risk-limits.json`, which the risk gate enforces whatever the strategy says, and which change only on the owner's request. The order-size hook in the trader container still reads the configured spec's `orderQuote`, so with `balanced` v3 configured it blocks agent buys above 5.5 EUR.

### Switching (paper)

At startup the engine adds every paper spec in this folder to the database as `strategy-<name>-v<N>` (never overwriting one), and writes the parameter schema with every parameter's explanation and range as `strategy-schema-paper`. To switch, use the dashboard's Strategy tab (paper): it lists every strategy with each parameter explained, shows what a switch changes and asks you to confirm, then sends a request the engine acts on. Or write the selection document in the `state` container by hand:

```json
{ "id": "strategy-selection-paper", "type": "strategy-selection",
  "value": { "name": "balanced", "version": 1, "requestedBy": "owner", "requestedAt": "2026-10-27T09:00:00Z", "note": "why" } }
```

Within 30 seconds the engine reads it, validates the version against the schema and the mandatory limits, waits for any trader run in progress to end, journals the switch (kind `strategy`) and applies it. A version written by hand may change parameters within the schema, but its instructions must be those of a file here, and `strategy-safe-v1` and the like must match their file exactly: instructions change only through a pull request. A refused version leaves the running strategy as it is, and the reason is in the journal and in `strategySelection` on the engine status. Change `requestedAt` to have the same selection read again.

What a switch affects:
- New trades, from the next scan and trader run: order size, limits, filters, entry rules, stop and target distances.
- Open positions too: the trend EMAs and `trendExits` decide trend exits from the next scan. Their stops and targets stay as they were when bought.
- The engine scans at once instead of waiting for the next 5-minute scan, so sizes, signals and the coin ranking follow the new strategy straight away.
- Holdings above the new strategy's caps are trimmed: in that scan the engine sells, at the bid and through the risk gate, only the part of a coin above `maxPositionQuotePerSymbol`, then the part of the total (with buys still working, as the strategy gate counts it) above `maxExposureFraction` of the allocation, largest holdings first. Coins within the caps, coins with an open order or a protective sell, and amounts below the exchange minimum are left alone, so the total can stay up to about one minimum order above its cap. Each sell is journaled (kind `order`, tool `strategy-trim`). It runs once per switch; while trading is halted or the portfolio can't be read, it waits for the next scan. A refused sell isn't tried again, and one still unfilled after 4 minutes is cancelled like a protective sell, so it never keeps a stop from selling; the strategy's limits then only block new buys.
- Nothing is cancelled by a switch.

Live trading switches the same way from the dashboard's Live view, with a real-money confirmation, but only between the files here whose `status` the owner set to `live`, exactly as merged (ADR-0018). The live engine adds only those to its database; marking a spec `live` is the owner's edit.

### Live versions

A live spec must fit `config/risk-limits.live.json`, which is tighter than paper's (a 100 EUR allocation, 20 EUR per order, 25 EUR per coin and a 5 EUR daily loss), and its instructions don't say "paper". Each is a copy of a paper version, so the paper one stays as it ran:

| Live version | Copy of | What changed |
|---|---|---|
| `safe.v2.json` | Safe v1 | Instructions only |
| `balanced.v2.json` | Balanced v1 | Daily loss stop 6 → 5 EUR |
| `risky.v3.json` | Risky v2 | Per coin 50 → 25 EUR, daily loss stop 12 → 5 EUR |

Balanced v2 and Risky v3 start as drafts, which never run: the owner sets their `status` to `live`. Orders of 10 and 20 EUR need that much cash in the sub-account.

## Trading strategies

Both are signal-driven (ADR-0006), scan the coins the owner watches (the Coins tab, ADR-0021) and have the same instructions, order size (`orderQuote` 5), risk per trade (1) and daily loss stop (5). The engine's scanner does the arithmetic: filters, ranking, risk-based size, stop and target, from `parameters.scanner`. The trader acts only on `scan_market` signals, and runs only when the signals change, plus a review every 4 hours.

`good.v1.json`: strict, for results close to real money.
- Filters: 24h volume at least 20,000 EUR, spread at most 0.4%, hourly ATR at least 1× the round-trip cost (so BTC qualifies).
- Entries: only an uptrend pullback (EMA 20 above EMA 50, price within 0.5 ATR of EMA 20, last hour not falling).
- Stop 1.5 ATR, target 2× the risk. Exits at the stop, the target, or when the trend turns down.

`risky.v1.json`: loose, for testing the order mechanics on paper. Expect losses.
- Same filters, except hourly ATR need only be 0.5× the cost.
- `requireUptrendPullback: false`: any coin that passes the filters gets an entry, in any trend.
- Stop 1 ATR, target 1× the risk, so stops and targets are reached often and the engine's protective sells get exercised.
- `trendExits: false`: positions leave at their stop or target. A position with no recorded stop (bought without one, or held from before a switch) still leaves on a downtrend.
- `entryMaxAboveEma20Atr` is ignored while `requireUptrendPullback` is false.
- In both, the scanner skips an entry whose stop or target would be under 0.5% from the price, the distance `place_order` requires.

Size is capped by the top-level `orderQuote`, which the engine enforces and the order-size hook also reads. Set it there only, never inside `scanner`.

## Other specs

- `analyst.v1.json`: the market analyst (ADR-0007). It cannot trade.
- `analyst.v2.json`: v1 plus up to five sourced highlights (important news and events) for the owner's dashboard. Highlights never reach the trader.
- `analyst.v3.json`: v2 plus the summary and each highlight in Russian (`summaryRu`, `titleRu`, `detailRu`), with names, tickers, abbreviations and trading terms kept in English. The dashboard shows them when its content language is RU; a translation that doesn't fit is left out and the English shows instead.
- `analyst.v4.json` (active): v3 with risk-off kept for a market-wide shock under way now, for at most `riskOffHours` (3, enforced by `record_market_view`) instead of 6, and a shock that hits only some coins recorded as neutral plus `avoidSymbols`, so the other coins can still be traded. The engine runs the analyst again `AgentRuns:RiskOffRecheckMinutes` (20) before a risk-off view ends, so the pause ends or is renewed on fresh news.
- `observer.v1.json`: only reads; it checks the tool server works.

The earlier `trend` v1 to v4 were removed. The journal keeps their instructions and labels (`trend@vN#hash`) for runs that used them.
