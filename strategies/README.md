# Strategy specs

A strategy spec is what the trading agents follow: instructions, parameters and the tools they may call. It is the only thing you change to retune the strategy; no code changes.

- One file per version: `<name>.v<N>.json`. Never edit a version that has run. Copy it to `v<N+1>` and change the copy, so the journal's record of what ran stays true. Each journal entry carries `name@vN#hash`; the hash shows if a file was edited anyway.
- `status`: `draft` (never runs), `paper` (paper trading only), `live` (paper or live). Only the owner sets `live`.
- `allowedTools`: the tool server refuses any agent tool not listed here.
- Pick the active spec with `Strategy:Name` and `Strategy:Version` in the engine's configuration.

`observer.v1.json` only reads; it checks the tool server works before a trading spec exists.
