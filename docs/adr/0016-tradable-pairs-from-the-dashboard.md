# 0016. Tradable pairs added from the dashboard, within a rule in the risk limits

- Status: proposed
- Date: 2026-10-27

## Context

The coins the engine may trade were the `AllowedSymbols` list in `config/risk-limits.json`, which only the owner edits and which the risk gate enforces. Adding a coin meant editing that file and redeploying. The owner wants to add pairs from the dashboard with no configuration change or redeploy, while every safety control keeps applying to the new pairs. Polling capacity allows about 15 pairs (ADR-0006, amended for #35).

## Decision

**The owner's limits state a rule, and the tradable list lives in the database within it.** (The owner chose the rule over a longer fixed list, 2026-10-27.)
- `config/risk-limits.json` may add `AllowedQuoteCurrency` (for example `EUR`) and `MaxTradableSymbols` (at most 20, at least the length of `AllowedSymbols`). Then any pair in that currency is *permitted*, up to that many at once; `AllowedSymbols` stay permitted and are the list at first start. Without the two keys only `AllowedSymbols` are permitted, and the dashboard can switch them off and on again. The file stays human-only, and the engine now reads the limits from it alone: startup fails if an environment variable, the command line or appsettings sets any `Risk` key, so nothing outside the file can widen them.
- `TradableSymbols` (domain) holds the list of the moment and accepts only lists the limits permit: never empty, never over the cap, never an unpermitted pair. **The risk gate checks both**: on the list now, and permitted by the limits. A list can narrow trading but never widen it past the file.
- **The dashboard asks through a request** (`type: "tradable-pair"`, `action` `add` or `remove`), as for strategies (ADR-0015): its writer can only create documents in `requests`. The engine, which alone writes `state`, checks each request in order and saves `tradable-pairs-<mode>`. It writes `pair-catalog-<mode>` (the exchange's active pairs in the permitted currency) for the picker.
- **An add** needs a permitted pair, room under the cap, an active exchange pair in the account's quote currency, and at least the running strategy's `minQuoteVolume24h` traded over the last 24 hours (from the exchange's hourly candles), so thin markets stay out. **A remove** needs another pair left, no open order in the coin, no order just sent in it and no sellable holding, so equity, exits and the reconciler never lose sight of a position. It is checked (with fresh exchange reads), journaled, saved and applied while no order can be placed.
- **Journal first** (kind `pairs`), then save, then apply; refusals are journaled too. A restart restores the saved list. If a pair had to be dropped (the owner narrowed the limits), the kill switch is engaged so the owner checks for holdings in it before resuming. A request already acted on is never applied twice.
- Market data, the scanner, the safety monitor, the agents' tools, owner requests and the status all read the same list. The poller's rate check at startup covers `MaxTradableSymbols` pairs, so an engine that starts can poll the most there may be. A new pair gets the same 15 minutes as the engine's start before stale data halts trading.
- Paper only, like strategy switching: live changes come with #71. (ADR-0018 later added them, within the live limits file.)

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep the fixed list and redeploy on change | What the owner asked to stop. |
| A longer fixed list, switched on and off from the dashboard | A coin outside it still needs an edit and a redeploy. The owner preferred the rule. |
| Let the dashboard write the list in `state` directly | The writer would then be able to overwrite engine state (ADR-0015). |
| Discover pairs automatically by volume | Lets new or manipulated tokens in (ADR-0006); here the owner chooses each pair and the engine checks liquidity. |

## Consequences

- Once the owner adds the two keys, a new EUR pair is one dashboard request; order size, per-coin position, allocation, daily loss, drawdown, price band and order rate apply to it as to any pair.
- A pair's scanner signals start once it has a day of hourly candles; its prices load at once.
- `MaxTradableSymbols` above what the polling schedule fits stops the engine at startup with the reason, rather than throttling quotes later.
- Removing a pair with a sellable holding is refused: the owner sells first. Dust below the exchange minimum doesn't block a removal and drops out of equity.
