# Strategy specs

A strategy spec is what the trading agents follow: instructions, parameters and the tools they may call. It is the only thing you change to retune the strategy; no code changes.

- One file per version: `<name>.v<N>.json`. Never edit a version that has run. Copy it to `v<N+1>` and change the copy, so the journal's record of what ran stays true. Each journal entry carries `name@vN#hash`; the hash shows if a file was edited anyway.
- `status`: `draft` (never runs), `paper` (paper trading only), `live` (paper or live). Only the owner sets `live`; the `protect-live-mode` hook stops agents from writing it.
- `allowedTools`: the tool server refuses any agent tool not listed here, except `get_strategy_spec`, which every agent may read to learn its own rules.
- Pick the active spec with `Strategy:Name` and `Strategy:Version` in the engine's configuration. The default is `good` v1.

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

Size is capped by the top-level `orderQuote`, which the order-size hook also reads. Set it there only, never inside `scanner`.

## Other specs

- `analyst.v1.json`: the market analyst (ADR-0007). It cannot trade.
- `observer.v1.json`: only reads; it checks the tool server works.

The earlier `trend` v1 to v4 were removed. The journal keeps their instructions and labels (`trend@vN#hash`) for runs that used them.
