# 0003. Cosmos DB serverless for journal, specs and state

- Status: accepted
- Date: 2026-10-22

## Context

The data is mostly append-only JSON (journal entries, agent runs, spec versions) plus small operational state. The budget favours consumption pricing over an always-on database server.

## Decision

Use an Azure Cosmos DB for NoSQL serverless account with containers for `journal` (append-only), `specs`, `state` and `candles`. Use the Azure Cosmos DB .NET SDK.

## Alternatives considered

| Option | Why not |
| --- | --- |
| PostgreSQL Flexible Server | About $15 a month minimum even when idle |
| Azure SQL serverless | Better for cross-trade SQL, but the data is document-shaped |

## Consequences

- Cross-trade analytics (PnL by spec version) needs precomputed aggregates or export, not ad-hoc SQL joins.
- Partition keys must be chosen up front: `journal` by day, `candles` by symbol and interval, `specs` by spec name.
- Local development uses the Cosmos DB emulator or an in-memory implementation behind the same interface.
