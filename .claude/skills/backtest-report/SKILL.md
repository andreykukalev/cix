---
name: backtest-report
description: Produce a backtest or paper-trading report from journal data or replay results, with honest comparison to a baseline. Use when summarising strategy performance.
---

# Backtest report

1. State the period, symbols, spec version, starting allocation and fee model (0% maker, 0.09% taker).
2. Report: return after fees, buy-and-hold return over the same period, maximum drawdown, number of trades, win rate, average holding time, fees paid.
3. Break down risk gate verdicts: accepted, resized, rejected, by rule.
4. List the five largest losing decisions with the agent's rationale from the journal.
5. Call out anything that makes the result unreliable: short period, few trades, a single market regime, look-ahead in features.
6. End with one recommendation: keep, change (what) or stop.

Never report returns without fees, and never present a result without the baseline next to it.
