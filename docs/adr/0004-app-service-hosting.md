# 0004. App Service B1 Linux for the live engine

- Status: accepted
- Date: 2026-10-22

## Context

The engine runs 24/7, polling Revolut X and holding state. Agents run on a schedule and reach the exchange only through the tool server. The owner prefers low operational overhead within a $150 a month budget that includes Claude.

## Decision

Prototype locally. For live trading, host the engine and tool server on App Service B1 Linux with Always On, and run scheduled agents as Container Apps Jobs.

## Alternatives considered

| Option | Why not |
| --- | --- |
| B-series VM | Cheaper to stop, but OS patching and deployment are on us |
| Azure Functions | Poor fit for a long-running poller with in-memory state |
| Container Apps with NAT gateway | Needed only if a single static outbound IP is required; adds about $30–35 a month |

## Consequences

- No OS management; deployment, restarts and logs are built in.
- App Service outbound IPs are a fixed set; if Revolut X requires a single static IP, revisit this ADR.
