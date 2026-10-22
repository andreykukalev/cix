# CIX: Crypto Intelligence eXchange

Claude agents trade an allocated budget on [Revolut X](https://revolut.com/revolut-x) on their own, on top of a deterministic .NET engine that enforces the budget, executes orders and records every decision. You review the history later and retune the strategy by editing a versioned prompt and parameters, not code.

> Status: milestone 1 (paper trading prototype). Nothing here trades real money yet. Not financial advice.

## How it fits together

- **Engine** (`src/Cix.Engine`): market data, paper and live execution, portfolio, decision journal.
- **Risk gate** (`src/Cix.Domain/Risk`): the final authority on every order; hard ceilings in code.
- **Revolut X client** (`src/Cix.Exchange.RevolutX`): signed REST calls.
- **Tool server** (coming in M1): the MCP server agents use; the only path to the exchange.
- **Agents**: Claude Code during the prototype; hosted on Azure later.

Details: [`docs/architecture.md`](docs/architecture.md) and the ADRs in [`docs/adr`](docs/adr).

## Getting started

Requires the .NET 10 SDK.

```bash
dotnet build Cix.slnx
dotnet test Cix.slnx
dotnet run --project src/Cix.Engine
```

Risk limits live in [`config/risk-limits.json`](config/risk-limits.json) and are validated at startup against [`HardCeilings.cs`](src/Cix.Domain/Risk/HardCeilings.cs). Only the owner edits either file.

## Revolut X API spike

Before building the client, confirm the API works for your account. See [`tools/spike/revx_spike.py`](tools/spike/revx_spike.py) and issue #1. The script runs on your machine and your keys stay there.

## Working with Claude Code

This repo is set up for Claude Code:

- [`CLAUDE.md`](CLAUDE.md): rules and conventions every session loads.
- [`.claude/agents`](.claude/agents): `researcher`, `reviewer` and `security-reviewer` subagents (read-only).
- [`.claude/skills`](.claude/skills): `architect` (ADRs), `test-plan`, `agent-eval`, `backtest-report`, `security-checklist`.
- [`.claude/settings.json`](.claude/settings.json): hooks that block edits to the risk ceilings and commits containing secrets.

A typical loop: pick an issue, run `/test-plan`, implement, ask the `reviewer` subagent (and `security-reviewer` for order, key or tool changes), then open a PR that says `Closes #N`.
