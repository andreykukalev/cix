# 0024. Paper deploys wait for the owner's approval

- Status: proposed
- Date: 2026-10-29

## Context

Since ADR-0013 every push to `main` that changes the images deploys the paper engine and its jobs at once (`deploy-agents`), and live follows after the owner's approval (ADR-0019). A deploy restarts the paper engine on a new revision. That was harmless while paper was a sandbox, but paper now runs the owner's week-long evaluation (from 2026-10-28), and the stocks advisory (ADR-0023) has to keep moving meanwhile: its first PR changes CI, and any CI change runs every job, the deploy included. The owner asked (2026-10-29) to protect paper with his approval, as live is, rather than hold merges for a week.

## Decision

A job `approve-paper` sits between building the images and `deploy-agents`. It runs in a new GitHub environment, `paper`, whose rules the owner sets: himself as the only required reviewer, `main` only. It then runs `tools/ci/live-approved.sh` for `paper`, so an environment without its rules, a rejection or someone else's approval fails the job and nothing deploys (fail closed). `deploy-agents` needs it.

- `approve-paper` signs in to nothing. `deploy-agents` keeps its identity and its trust (workflows on `main`), so no Azure setup changes.
- Live is unchanged: `deploy-live` still needs its own approval in `prod`, after paper deployed. Two clicks for a change that should reach both.
- The repository variable `PAPER_APPROVAL=off` (the owner's to set) turns the gate off again, for times when paper is a sandbox.
- The dashboard's deploy is not gated: publishing it doesn't restart the engine.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Hold merges until the evaluation ends | The owner's choice is to keep working; the stocks work would wait a week. |
| Put `deploy-agents` itself in the `paper` environment | The job's OIDC subject would become the environment's, so its Azure identity would need a new federated credential, which only the owner sets up. A separate gate job changes nothing in Azure. |
| Deploy stocks-only changes without touching CI | The first stocks PR must change CI to classify `stocks/`; that change alone runs every job. |
| An approval rule on `main` or a manual-only deploy | Merges and deploys would be coupled again; an approval keeps the CI path and leaves the decision per run. |

## Consequences

- No change reaches the paper engine until the owner approves its run on the run's page. A run he won't approve should be rejected: one waiting holds `approve-paper`'s group, and the images artifact is kept for a day.
- Setup, once, by the owner in the repository's settings: create the environment `paper`, add himself as required reviewer, allow `main` only, allow approving runs he started. Before that, every paper deploy fails closed, with the reason in the run.
- Agents never approve or reject it, as for live (CLAUDE.md rule 8); `.claude/hooks/protect-live-deploy.sh` already blocks the approval APIs for every environment.
- The stocks advisory gets its own CI job (`stocks`), without the engine's tests and the Cosmos DB emulator. Its future deploy to its own resource group will get its own identity and environment.
