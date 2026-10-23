# Strategy specs

A strategy spec is what the trading agents follow: instructions, parameters and the tools they may call. It is the only thing you change to retune the strategy; no code changes.

- One file per version: `<name>.v<N>.json`. Never edit a version that has run. Copy it to `v<N+1>` and change the copy, so the journal's record of what ran stays true. Each journal entry carries `name@vN#hash`; the hash shows if a file was edited anyway.
- `status`: `draft` (never runs), `paper` (paper trading only), `live` (paper or live). Only the owner sets `live`; the `protect-live-mode` hook stops agents from writing it.
- `allowedTools`: the tool server refuses any agent tool not listed here, except `get_strategy_spec`, which every agent may read to learn its own rules.
- v3 is the default. Pick the active spec with `Strategy:Name` and `Strategy:Version` in the engine's configuration.

`observer.v1.json` only reads; it checks the tool server works before a trading spec exists.

`trend.v1.json` trades BTC-EUR and ETH-EUR from the hourly market view with rules the agent applies itself.

`trend.v2.json` is signal-driven (ADR-0006).
- The engine's scanner does the arithmetic across the universe: filters, ranking, risk-based size, stop and target. It reads its settings from `parameters.scanner`.
- The trader acts only on `scan_market` signals and cites the signal in every order's rationale.
- Size is capped by the top-level `orderQuote`, which the order-size hook also reads. Set it there only, never inside `scanner`.
- Because v2 allows `scan_market`, the trader container runs only when the signals change, plus a review every 4 hours, instead of every hour. With no signals there is no Claude call.

`trend.v3.json` is v2 with three wider scanner filters, so more coins can be ranked on Revolut X's thin EUR books. Everything else is the same.
- `minQuoteVolume24h` 100000 → 20000. Orders are 5 to 20 EUR, so the floor is a sanity check, not a size limit. It relies on the engine's assumption that candle volume is in the coin, not in euros.
- `maxSpreadFraction` 0.002 → 0.004. The cost filter still counts the spread, so a wide-spread coin must also be volatile enough to pass it.
- `minAtrToCostRatio` 2 → 1.5. Admits calmer coins, with a thinner margin over fees.
- Wider filters rank more coins; they don't create entries. A buy still needs an uptrend and a pullback.
