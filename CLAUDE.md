# CIX: instructions for coding agents

CIX runs Claude agents that trade an allocated budget on Revolut X, on a deterministic .NET engine. Read `docs/architecture.md` before changing behaviour, and the ADRs in `docs/adr/` before revisiting a decision.

## Non-negotiable rules

1. **The risk gate is final.** Every order path goes through `RiskGate.Evaluate`. Never add a code path that places an order without it.
2. **Agents reach the exchange only through tool server tools.** No MCP tool may call the exchange directly; tools call engine services.
3. **Hard ceilings and limits change only when the owner asks.** Edit `src/Cix.Domain/Risk/HardCeilings.cs`, `config/risk-limits.json` or the live engine's `config/risk-limits.live.json` only on the owner's explicit request, never on your own initiative and never to make a strategy or a test pass. If a change seems needed, explain why and ask the owner. Say plainly in the PR which limits change.
4. **The journal is append-only.** No update or delete operations on journal entries.
5. **No secrets in the repo, prompts, logs or tool output.** Keys come from user secrets locally and Key Vault in Azure. Never print a key, even partially.
6. **Paper trading is the default.** Live trading needs an explicit configuration switch that only the owner sets; a hook blocks agents from setting it in files or commands. Test against `PaperExchange`, never the real account.
7. **Money is `decimal`.** Never use `double` or `float` for prices, sizes or balances.
8. **Live and paper deploys are approved by the owner alone** (ADR-0019, ADR-0024). You act with the owner's GitHub account, so GitHub can't tell your approval from theirs: never approve or reject a pending deployment, and never change GitHub environments, the repository's OIDC settings, the `LIVE_*` variables or the `cix-agents-deploy-prod` identity. A hook blocks it. Code you merge reaches live money once the owner approves its run, so say plainly in the PR when a change touches orders, risk, strategies or the deploy.

## Layout

- `src/Cix.Domain`: models, risk gate, exchange interfaces. No I/O.
- `src/Cix.Exchange.RevolutX`: Revolut X REST client and request signing.
- `src/Cix.Engine`: the host: market data, execution, portfolio, journal.
- `tests/`: xUnit tests, one project per source project.
- `stocks/`: the stocks advisory (ADR-0023), its own solution `stocks/Cix.Stocks.slnx`; changes there never deploy the crypto engine.
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

## Agent workflow notes

Learned while building #1–#3 in cloud sessions; follow them unless the environment has changed.

- **Building:** cloud sessions may have no .NET SDK and no NuGet access, so CI (`.github/workflows`) is the build. Push the branch and open a draft PR to trigger it; it runs on `pull_request` and on push to `main`. Work through test arithmetic by hand, since a wrong expectation only shows up in CI.
- **Actions minutes are limited:** CI runs only the jobs the changed files need (`tools/ci/plan.sh`), skips on `main` the jobs a PR already passed on the same content, and a new push cancels the PR's run in progress. Batch fixes into one push rather than pushing each small change. Order: commit locally as you go; run gitleaks, the tests you can run, `reviewer` and (when it applies) `security-reviewer` on the local branch; fix their findings; only then push and open the PR. Push again only for what CI itself finds, all of it in one push, and never while a review is still running. Engine (.NET) changes still need CI as the build: push those once, after the local reviews. A green run covers only the areas it ran; for a full check, run *CI* by hand (workflow_dispatch).
- **GitHub:** use `gh api` with REST endpoints; GraphQL (and so `gh pr ready`, `gh pr create`) may be blocked. Create PRs with `POST repos/{owner}/{repo}/pulls` (`-F draft=true`), mark ready with `POST repos/{owner}/{repo}/pulls/{n}/ccr/ready_for_review`, and poll CI with `GET repos/{owner}/{repo}/commits/{sha}/check-runs`. Job logs and artifacts are on a host sessions can't reach: read failed tests from `GET repos/{owner}/{repo}/check-runs/{id}/annotations` (CI reports them there).
- **Before every PR:** run gitleaks with `.gitleaks.toml` locally and check its exit code (don't pipe it into `tail`, which hides a failure); test fixtures that look like keys should be built at runtime, then the `reviewer` subagent, plus `security-reviewer` for anything touching orders, market data that prices orders, keys, limits or tools. Fix the findings, wait for green CI, then mark ready. The owner merges; never merge yourself.
- **After a merge:** check whether the PR merged before your last push (`GET .../pulls/{n}` → `merged`). If it did, move the extra commits to a new branch from `origin/main` and open a follow-up PR.
- **Commits:** author as the owner (`git -c user.name=... -c user.email=...`) with the session's co-author trailers; one issue per branch (`feature/{n}-short-name`).
- **Testing time:** inject `TimeProvider` and drive loops from tests (`MutableTime`, `PollOnceAsync`); never sleep in tests.
- **Spike results:** when the owner runs `tools/spike/revx_spike.py`, record the output on the issue and turn real responses into test fixtures.
- **Exchange quirks:** see `docs/research/revolut-x-api.md` before touching the Revolut X client.
