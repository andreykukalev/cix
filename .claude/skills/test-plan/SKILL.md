---
name: test-plan
description: Design unit, integration and failure-scenario tests for a feature before or while implementing it. Use at the start of each issue.
---

# Test plan

For the feature in hand, write the plan as a checklist in the PR description, then implement the tests.

1. **Unit tests** for pure logic (`Cix.Domain`): happy path, each rejection rule, boundary values (exactly at a limit, one step over), `decimal` rounding.
2. **Contract tests** for the Revolut X client: request signing against the documented example, JSON (de)serialisation against recorded responses in `tests/**/Fixtures`.
3. **Failure scenarios** — pick those that apply:
   - HTTP 429 with `Retry-After`; 5xx; timeouts.
   - Duplicate submission of the same client order ID.
   - Partial fills, fills arriving after a cancel.
   - Stale market data; clock skew.
   - Restart mid-order: reconciliation must recover the true state.
   - Kill switch engaged mid-run.
4. **Agent-facing tools:** invalid arguments, oversized orders (must be resized or rejected, with a reason the agent can read).

Name tests `Method_Condition_Expected`. Never call the real exchange in CI.
