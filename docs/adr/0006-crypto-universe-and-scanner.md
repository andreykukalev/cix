# 0006. Curated crypto universe, deterministic scanner, signal-triggered agents

- Status: accepted
- Date: 2026-10-23

## Context

v1 traded BTC-EUR and ETH-EUR from an hourly LLM run that mostly concluded "do nothing". The owner wants more opportunity (liquid altcoins with higher volatility), frequent scans, and concise AI analysis used only where it adds value, while keeping costs low and avoiding low-quality tokens. Revolut X public endpoints allow 1 request a second.

## Decision

1. **Curated universe.** Candidate symbols are listed in configuration (initially BTC, ETH, SOL, LINK, AVAX, SUI, NEAR against EUR; issue #22). The engine validates each against the exchange's active pairs and drops any it can't trade. Only symbols in the risk gate's allowlist (owner-only `config/risk-limits.json`) can be traded; every allowlisted symbol is polled and scanned, even if it isn't in the universe. No automatic discovery, so new or obscure tokens can't appear.
2. **Deterministic scanner in the engine.** Every few minutes, from hourly candles and order books, it computes per symbol: liquidity (24h traded value as the sum of hourly volume × close, assuming candle volume is in the base currency until the spike confirms it, and the spread), volatility (ATR %), momentum (EMA trend, 1h and 24h returns), volume anomaly (last hour vs average), and a round-trip cost estimate (taker fee both ways plus spread plus slippage allowance). Symbols failing the liquidity, spread or cost filters are excluded; the rest are ranked by a risk-adjusted score. It also checks held positions against their stop, target and trend.
3. **Signal-triggered LLM runs.** The scan produces concrete entry and exit signals. The trader agent runs only when there is a signal (and on a slow heartbeat), so routine "nothing to do" checks cost nothing.
4. **Three agents, each short:** the *market analyst* gives a regime view (risk-on, neutral, risk-off) a few times a day that the scanner and trader respect; the *trader* acts on signals; the *daily reviewer* summarises the journal and proposes spec changes.
5. **Polling budget.** Order-book quotes every 10 seconds per symbol and hourly candles every 5 minutes; 1-minute candles are off by default. 43.4 of the 48 allowed requests a minute for 7 symbols, checked at startup. Tickers were planned for 24h volume, but their response shape isn't confirmed yet and the candles already give it, so they are left out (amended 2026-10-23, issue #22).

## Alternatives considered

| Option | Why not |
| --- | --- |
| Let the LLM scan every symbol every run | Slow, costly, inconsistent; arithmetic belongs in code |
| Discover tokens automatically by volume | Lets new, thin or manipulated tokens in |
| Order-book polling for every exchange pair | Exceeds the 1 request a second public limit |

## Consequences

- Strategy specs gain scanner parameters (filters, weights, costs, sizing) next to the agent instructions.
- Adding a symbol needs two steps: the universe list (configuration) and the risk allowlist (owner-only).
- The scanner's signals are journaled, so a report can compare signals taken and not taken.
