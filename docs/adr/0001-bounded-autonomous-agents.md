# 0001. Bounded autonomous agents behind a deterministic risk gate

- Status: accepted
- Date: 2026-10-22

## Context

The owner wants to allocate money and let agents trade it without approving each trade, then audit the history and retune the strategy without code changes. LLM output varies run to run, can contain hallucinated numbers, and can be steered by injected text.

## Decision

Claude agents make the trading decisions, but reach the exchange only through MCP tools served by our own tool server. Every order passes a deterministic risk gate that is the final authority. Strategy behaviour lives in a versioned strategy spec (prompt, parameters, allowed tools) that agents cannot change.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Deterministic engine only | No room for the agent-driven strategy the owner wants |
| Advisory AI analyst, coded strategies decide | Retuning still needs code changes |
| Agents with direct exchange tools | A hallucinated or injected instruction becomes an order with nothing in between |

## Consequences

- The risk gate, journal and paper execution must exist before any agent trades.
- Results are not reproducible run to run; quality is measured with agent evals that replay past markets.
- Losses are bounded by the allocation and the drawdown halt, not by agent judgement.
