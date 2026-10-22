# CIX: instructions for coding agents

CIX runs Claude agents that trade an allocated budget on Revolut X, on a deterministic .NET engine. Read `docs/architecture.md` before changing behaviour, and the ADRs in `docs/adr/` before revisiting a decision.

## Non-negotiable rules

1. **The risk gate is final.** Every order path goes through `RiskGate.Evaluate`. Never add a code path that places an order without it.
2. **Agents reach the exchange only through tool server tools.** No MCP tool may call the exchange directly; tools call engine services.
3. **Hard ceilings are human-only.** Do not edit `src/Cix.Domain/Risk/HardCeilings.cs` or `config/risk-limits.json`; a hook blocks it. If a change seems needed, explain why and ask the owner.
4. **The journal is append-only.** No update or delete operations on journal entries.
5. **No secrets in the repo, prompts, logs or tool output.** Keys come from user secrets locally and Key Vault in Azure. Never print a key, even partially.
6. **Paper trading is the default.** Live trading needs an explicit configuration switch that only the owner sets.
7. **Money is `decimal`.** Never use `double` or `float` for prices, sizes or balances.

## Layout

- `src/Cix.Domain`: models, risk gate, exchange interfaces. No I/O.
- `src/Cix.Exchange.RevolutX`: Revolut X REST client and request signing.
- `src/Cix.Engine`: the host: market data, execution, portfolio, journal.
- `tests/`: xUnit tests, one project per source project.
- `tools/spike`: the Revolut X API spike script.
- `docs/`: architecture, ADRs, research memos.

## Commands

```bash
dotnet build Cix.slnx
dotnet test Cix.slnx
```

Run the tests before finishing any task. A change to the risk gate needs a new or changed test in `tests/Cix.Domain.Tests`.

## Conventions

- .NET 10, C# latest, nullable enabled, file-scoped namespaces, records for immutable data.
- Async all the way; pass `CancellationToken`.
- Keep `Cix.Domain` free of package dependencies.
- Small PRs that reference an issue (`Closes #N`).

## Roles

Use the subagents in `.claude/agents` for fresh-context work: `researcher` for library, API or Azure questions; `reviewer` before opening a PR; `security-reviewer` for any change touching keys, orders, risk limits or tools. Use the skills in `.claude/skills` for ADRs, test plans, agent evals, backtest reports and security checklists.
