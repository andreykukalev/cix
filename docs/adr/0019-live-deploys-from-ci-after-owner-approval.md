# 0019. Live deploys from CI, after the owner approves each one

- Status: accepted
- Date: 2026-10-28

## Context

Under ADR-0017 every change reached the live engine by hand: the owner promoted the images paper ran (`provision.ps1 -PromoteFrom dev -ImageTag <commit SHA>`), which put the engine back on standby, then ran the switch command again and resumed it. Three commands for each merge, one of them easy to get wrong, and live trading stopped in between.

The owner asked, on 2026-10-28, that a change on `main` reach live without further commands, and chose to keep one click: each deploy waits for their approval in GitHub. The security review then found that Claude sessions act with the owner's own GitHub account, admin rights included, so GitHub can't tell their approval from the owner's. Offered one command on the owner's PC instead (their Azure sign-in as the lock), the owner chose the one click with guard rails.

What must not change: the switch to live stays the owner's alone (CLAUDE.md rule 6), live runs only images that ran on paper, and nothing but the owner can deploy to live.

## Decision

A CI job, `deploy-live`, deploys the images paper now runs to the live engine and its agent jobs, after the owner approves the run in GitHub.

- **When.** On `main`, after `deploy-agents` reports that paper runs this commit's images (`deployed=true`: just deployed, or already running). Nothing runs until the repository variable `LIVE_AZURE_CLIENT_ID` is set.
- **The owner's approval.** The job is in the GitHub environment `prod`: the owner is its only required reviewer, it allows only `main`, and admins can't skip the review. GitHub holds the job until the owner presses Approve. `setup-ci-deploy.ps1` sets those rules and reads them back before it lets Azure trust the environment, since GitHub creates a missing environment bare and a bare one would let any branch's job in. `tools/ci/live-approved.sh` then checks, through the run's approval history, that the run is a push or a manual run on `main` and that the owner (by account ID) approved it for `prod` and nobody rejected it, so this workflow fails closed if the rules were lost later. It can't stop a workflow that leaves it out: the environment's rules do that.
- **What the approval shows.** A job before it, `live-changes`, puts the files and commits since the commit live runs (its latest deployment to `prod` whose job reached the step "Live runs this commit") on the run's summary page, flagging first every file that could reach real money: code, limits, strategies, agents, build and deploy. When it can't tell what live runs, it says so. It comes with the commit it describes, so when `tools/ci/` or `.github/workflows/` change, the owner reads that diff.
- **Its own identity.** `cix-agents-deploy-prod`, made by `setup-ci-deploy.ps1 -Environment prod -For agents`, has the paper deploy's two roles on live's own resources: AcrPush on live's registry, and the custom role "CIX agents deployer (prod)" (read and update container apps and jobs, stop a revision) on the registry, the engine and the three jobs. Its only federated credential trusts the GitHub environment (`repo:<owner>@<id>/<repo>@<id>:environment:prod`), so a job outside it, on any branch, can't sign in as it. Its variables are `LIVE_*`, apart from paper's.
- **The same images.** It deploys the archives paper deployed, checked against the hashes from the job that built them, pushed to live's registry and deployed by digest. It runs only if this commit is still the tip of `main`, or the newer commits changed nothing in the images (`agents-tip.sh`); otherwise the newer run deploys.
- **Only the images change.** `deploy-agents.sh` patches the first container's image and sends the rest of the container back as Azure has it at that moment, so the engine keeps its trading mode, environment and strategy (or its standby), and its kill switch and state stay in `cix-live`. If the settings changed since the deploy began (the owner putting it on standby meanwhile), it sends nothing and rolls back. An engine stopped by hand is left alone, checked again just before the engine changes and before a rollback touches it. A failed deployment rolls back, as on paper. A protected environment also needs `APPROVED_RUN`, the approved run's ID, or the script refuses before any call: a guard against mistakes, not a control.
- **Agents never approve it.** CLAUDE.md rule 8, and a hook (`.claude/hooks/protect-live-deploy.sh`) that refuses agents' commands and GitHub tool calls on pending deployments, approvals, environments, the OIDC subject settings, the `LIVE_*` variables and the live deploy identity's credentials.
- **`provision.ps1` stays the owner's.** Infrastructure, settings, roles and keys change only through it, from the owner's sign-in, and it still puts the engine on standby. Its check of who else has a role on `rg-cix-live` leaves out the live deploy's expected roles, only while its service principal is the only one of that name, can sign in only as the setup made it (no secret or certificate, no other owner, no API permission or directory role, one trust: the environment), and its custom role grants no more than deploying images. Anything else it holds is listed.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Promote and switch by hand (ADR-0017) | Three commands for each change, and live stops trading between them. |
| Deploy every change to live with no approval | The owner chose to see each change before it reaches real money. A mistake that passed paper would reach live within minutes. |
| CI runs `provision.ps1` for live | It deploys the template, which pins the engine to paper: the switch would then be needed after each deploy. CI would also need rights on the whole group, Key Vault included. |
| Pull live's images from dev's registry | ADR-0017: a live identity with a role in the group CI's paper identity reaches. |
| A GitHub token, or a client secret, for the live identity | A secret stored in GitHub (ADR-0010). The federated credential needs none. |
| One command on the owner's PC per deploy, with their Azure sign-in | The lock agents can't open, but a command each time; the owner chose the click. It remains the fallback if the guard rails prove too weak. |
| A subject that also names the ref (the repository's OIDC subject template) | It changes the subject of every workflow in the repository, paper's deploys included; the environment's main-only rule covers the ref. Worth revisiting. |

## Consequences

- One click on GitHub per change that reaches paper's images: Review deployments, `prod`, Approve. GitHub notifies the required reviewer by email and in the app.
- The image artifact is kept for a day. A run approved later than that fails to download it: run CI on `main` by hand (Run workflow), which builds it again.
- Changes to `infra/`, app settings or the keys still need `provision.ps1` and then the switch, as in ADR-0017. So does a new live environment.
- A Live engine running a new image is still the image being trusted (ADR-0017): what reaches live money is code reviewed and merged on `main`, built by CI and run on paper first, and now approved again by the owner before it goes live.
- The approval is the control. In an approved run, the identity could run any image as the live engine, with its keys and its settings changed (Azure has no right that allows changing only the image), and `main` has no branch protection here: an approval runs whatever that commit's workflow does. So the owner approves only commits they recognise, after reading the summary, and is the only reviewer. Deleting `LIVE_AZURE_CLIENT_ID` turns the job off.
- **The risk that remains: agents use the owner's GitHub account.** A Claude session that misbehaved (a bug, or instructions planted in something it reads) could merge code to `main` and approve its run as the owner, or change the environment's rules, since GitHub sees the owner either way. Before this, reaching live money needed the owner's Azure sign-in, which cloud sessions don't have. CLAUDE.md rule 8 and the hook make that hard, not impossible: a hook is a tripwire. What it could reach is bounded by the live sub-account's balance (the API key can't withdraw). It would go away with a GitHub identity for agents of their own, without deployment or administration rights, or with branch protection on `main`.
- A run waiting for approval holds the job's concurrency group: newer runs wait behind it. The owner rejects a run they won't approve rather than ignore it. One rejection fails every later attempt of that run: run CI on `main` by hand instead.
