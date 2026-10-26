# 0015. Strategies in the database, switched at run time

- Status: proposed
- Date: 2026-10-26

## Context

The engine ran one strategy spec, chosen in configuration and loaded from a file at startup. Changing it meant a redeploy. The owner wants to choose between Safe, Balanced and Risky from the dashboard, with every parameter explained, then tune them by hand (#104) and let the engine tighten them on extreme markets (#103), all on paper before live trading. Some of the parameters this needs (order size, positions per run, cooldown, exposure) were enforced only by the agent's instructions or by a hook in the trader container.

## Decision

**Strategy versions are documents in the `state` container, and the owner selects one per mode.**
- `strategy-<name>-v<N>` holds a version. The engine seeds the paper specs in `strategies/` at startup and never overwrites a version: a change is a new version, so the journal's `name@vN#hash` stays true.
- `strategy-selection-paper` names the version to run. The engine polls it, validates the version against a parameter schema and the mandatory limits in `config/risk-limits.json`, journals the switch (kind `strategy`) before applying it, and applies it between trader runs. A refused version changes nothing and is journaled once.
- `strategy-schema-paper` describes every parameter (explanation, range, unit, whether it applies to open positions, which mandatory limit bounds it) for the dashboard (#108).
- **The engine enforces the strategy's own limits** (`StrategyGate`, before the risk gate): order size, per-coin position, total exposure, daily loss stop, new positions per run and cooldown. They can only reduce or refuse a buy; sells always pass. The risk gate remains final and unchanged.
- Each scan and each `place_order` reads the active spec once, so one decision never mixes two versions.
- **Live is out of scope.** Validation refuses live mode and live-status versions, and the selection service runs in paper only. Live strategy changes, with explicit confirmation, come with #71.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep files and redeploy on change | No switching from the dashboard; every change is a deployment. |
| Switch at once, even during a trader run | A run could read one spec and place orders under another. Waiting for the run to end costs at most a few minutes. |
| Let the dashboard write any parameter values directly | Unversioned changes break the journal's record of what ran. Overrides (#104) will create new versions instead. |
| Leave the new limits to the agent's instructions and hooks | An agent can misread them, and the hook reads the configured spec only. The engine enforcing them makes the dashboard's promise true. |

## Consequences

- The owner switches paper strategies with one document, and each switch and refusal is in the journal.
- Strategy limits are now engine-enforced; `config/risk-limits.json` and the risk gate are untouched.
- The order-size hook in the trader container still reads the configured spec's `orderQuote` (5.5 EUR with `good` v1). It needs the owner's change to allow Balanced and Risky orders above that.
- After a restart, the cooldown knows only buys still working or with an open plan.
- Editing a version document in place is ignored until the selection is saved again; editing one is against the rule anyway.
