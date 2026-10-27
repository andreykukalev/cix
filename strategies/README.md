# Strategy specs

A strategy spec is what the trading agents follow: instructions, parameters and the tools they may call. It is the only thing you change to retune the strategy; no code changes.

- One file per version: `<name>.v<N>.json`. Never edit a version that has run. Copy it to `v<N+1>` and change the copy, so the journal's record of what ran stays true. Each journal entry carries `name@vN#hash`; the hash shows if a file was edited anyway.
- `status`: `draft` (never runs), `paper` (paper trading only), `live` (paper or live). Only the owner sets `live`; the `protect-live-mode` hook stops agents from writing it.
- `allowedTools`: the tool server refuses any agent tool not listed here, except `get_strategy_spec`, which every agent may read to learn its own rules.
- Pick the starting spec with `Strategy:Name` and `Strategy:Version` in the engine's configuration. The default is `good` v1. In paper trading, a selection in the database overrides it at run time (below).

## Safe, Balanced and Risky

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

The engine enforces each strategy's own limits before the risk gate: a buy larger than the order size, or beyond the per-coin, exposure, daily loss, per-run or cooldown limits, is reduced or refused. They can only be tighter than the mandatory limits in `config/risk-limits.json`, which the risk gate enforces whatever the strategy says, and which only the owner edits. The order-size hook in the trader container still reads the configured spec's `orderQuote`, so with `good` v1 configured it blocks agent buys above 5.5 EUR.

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
- Nothing is cancelled or sold by a switch.

Live trading has no selection yet: it runs the configured spec until live strategy changes are designed with #71.

## Trading strategies

Both are signal-driven (ADR-0006), trade the same coins (`AllowedSymbols`) and have the same instructions, order size (`orderQuote` 5), risk per trade (1) and daily loss stop (5). The engine's scanner does the arithmetic: filters, ranking, risk-based size, stop and target, from `parameters.scanner`. The trader acts only on `scan_market` signals, and runs only when the signals change, plus a review every 4 hours.

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
- `analyst.v3.json` (active): v2 plus the summary and each highlight in Russian (`summaryRu`, `titleRu`, `detailRu`), with names, tickers, abbreviations and trading terms kept in English. The dashboard shows them when its content language is RU; a translation that doesn't fit is left out and the English shows instead.
- `observer.v1.json`: only reads; it checks the tool server works.

The earlier `trend` v1 to v4 were removed. The journal keeps their instructions and labels (`trend@vN#hash`) for runs that used them.
