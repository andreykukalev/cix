---
name: agent-eval
description: Evaluate a strategy spec version by replaying past market windows through the trader agent on paper, and compare it with a baseline. Use before promoting any spec version.
---

# Agent eval

1. Pick the spec version to evaluate and the comparison: the current live version and buy-and-hold BTC.
2. Use the fixed eval windows in `evals/windows.json` (create it if missing: at least 10 windows of 7 days, covering trend up, trend down and sideways markets).
3. For each window, run the trader agent on paper with candles replayed hour by hour. Record every journal entry with the window ID.
4. Report per version:
   - Return after fees, maximum drawdown, number of trades, share of orders rejected or resized by the risk gate.
   - Invalid tool calls and schema errors.
   - Token cost per window.
5. Results vary run to run, so run each window at least 3 times and report the median and range.
6. Write the report to `docs/evals/YYYY-MM-DD-spec-version.md`. Recommend promotion only if the version beats the current one on return after fees without a worse drawdown.
