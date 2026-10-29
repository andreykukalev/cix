# 0023. Stocks advisory: recommendations for manual trading on Revolut

- Status: proposed
- Date: 2026-10-29

## Context

The owner holds a Revolut brokerage account (stocks and ETFs) and wants CIX to help with medium-term trading there: protection from falls and riding uptrends, unlike the short crypto trades on Revolut X. Revolut offers no API for its brokerage: the Revolut X API covers crypto only, and its developer APIs cover payments, accounts and merchants. Brokers with an API for an EU retail client exist (Interactive Brokers, Trading 212), but the owner chose on 2026-10-29 to keep Revolut and place orders by hand: CIX recommends, the owner acts. He also wants a chat for advice during the day, and the module hosted in its own Azure resource group, with one dashboard for crypto and stocks. The feasibility study is in the project's "CIX for stocks" document.

Two facts shape the design. Stocks trade in sessions and can open with a gap below a stop, so protection is a stop order resting at the broker plus sizing that counts the gap, not a fast engine-side sale (ADR-0020). And every change under `src/` rebuilds and redeploys the crypto engine and agents (`tools/ci/plan.sh`), so stocks code there would restart paper on every push and queue a live deploy.

## Decision

A stocks advisory module in this repository, under its own top-level `stocks/` folder with its own solution, that recommends and never trades.

- **Advice, not orders.** `Cix.Stocks.Advisor` is deterministic: from adjusted daily bars, the market regime and the holdings the owner records, it lists actions (sell, set stop, raise stop, buy) with a quantity, a reference price and a stop. Rules: risk-on while the benchmark (default CSPX, S&P 500 UCITS) closes above its 200-day average; entries only then, on a close above the 200-day average, the 50-day above the 200-day and a new 50-day closing high, strongest 126-day momentum first; an initial stop 3 ATR under the entry, trailing 3 ATR under the highest close since entry (2 ATR while risk-off), raised only, never lowered; a sell on a close under the 200-day average or under the stop. Size: the loss at the stop plus a one-ATR gap at most 1% of the allocation, at most 20% of it per position, within free cash, converted through FX rates. Stale or missing data gives a note, never an action: missing benchmark data stops entries but leaves held positions on the normal trail, and a recorded stop that two closes already sit under is queried, not sold (a typo, or a split: recorded stops are raw prices, bars adjusted). These defaults are for the backtest, not calibrated.
- **No order path.** Nothing in the module can place an order, so the risk gate (CLAUDE.md rule 1) has nothing to guard yet. If a broker adapter is ever added, its orders go through `RiskGate.Evaluate` like the crypto engine's.
- **Data: EODHD**, "EOD Historical All-World" (about 20 USD a month): adjusted daily bars for US and European exchanges and UCITS ETFs, 30+ years of history, splits, dividends and news, 100,000 calls a day. Its free plan (20 calls a day) and public demo key are enough to develop against. Later, for the earnings calendar, Finnhub's free plan (US). The key travels as a query parameter, so the client never logs or reports request URLs.
- **Isolation.** `stocks/` builds and tests in the `build-test` job; `plan.sh` and `agents-tip.sh` treat it like `tests/`: it never deploys the engine or the agents. The module reuses `Cix.Domain` (candles, ATR) by reference and changes nothing in it.
- **Hosting** in its own resource group per environment, with its own host, identity and Key Vault, decided in the infrastructure step. The dashboard gets a Stocks tab.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Automate Revolut through its app (private API, screen automation) | Against its terms, breaks on every app update, risks the account. |
| Move the stocks to IBKR or Trading 212 now | The owner wants to keep Revolut. IBKR's gateway needs a 2FA login every one or two days; Trading 212's API is in beta and has no market data. Revisit if manual execution becomes the bottleneck. |
| A separate project or repository | Two dashboards, two deploy pipelines and a copy of the domain. The owner wants one dashboard. |
| Stocks code under `src/` | Each stocks change would redeploy the crypto engine and agents, and changing that rule per path in `plan.sh` and `agents-tip.sh` is more fragile than one folder. |
| Twelve Data or Finnhub for daily bars | Twelve Data's free plan is US only and European data starts at 29 USD; Finnhub's candles are not on its free plan. |
| Engine-side stops as for crypto | The engine can't sell on Revolut, and a gap at the open passes any engine-side stop. The stop rests at the broker; the advisor keeps it raised. |

## Consequences

- The owner places every order and stop by hand and records what he did; the advisor knows only what he records. Positions not recorded are not protected by its advice, though stops already at Revolut still work without CIX.
- Next steps, each isolated from the crypto engines: a backtest of these rules on 10+ years of daily bars; a host that runs the advisor once a day after the close and keeps a paper record of every recommendation; the Stocks tab and the advisor chat; the infrastructure in its own resource group.
- Merging the CI change runs every job once (a change to CI does), which redeploys the crypto engine and agents. It waits until the crypto evaluation week ends.
- The host must run after the last watched market has closed: before that, EODHD's daily endpoint may return a partial bar for today, which the advisor would read as a close. Many LSE stocks are quoted in pence: such an instrument's currency is `GBX`, with its own rate.
- An EODHD subscription and key are needed before the backtest: the owner buys it and stores the key in user secrets or Key Vault.
