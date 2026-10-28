# 0019. Live deploys from CI, after the owner approves each one

- Status: accepted
- Date: 2026-10-28

## Context

Under ADR-0017 every change reached the live engine by hand: the owner promoted the images paper ran (`provision.ps1 -PromoteFrom dev -ImageTag <commit SHA>`), which put the engine back on standby, then ran the switch command again and resumed it. Three commands for each merge, one of them easy to get wrong, and live trading stopped in between.

The owner asked, on 2026-10-28, that a change on `main` reach live without further commands, and chose to keep one click: each deploy waits for their approval in GitHub.

What must not change: the switch to live stays the owner's alone (CLAUDE.md rule 6), live runs only images that ran on paper, and nothing but the owner can deploy to live.

## Decision

A CI job, `deploy-live`, deploys the images paper now runs to the live engine and its agent jobs, after the owner approves the run in GitHub.

- **When.** On `main`, after `deploy-agents` reports that paper runs this commit's images (`deployed=true`: just deployed, or already running). Nothing runs until the repository variable `LIVE_AZURE_CLIENT_ID` is set.
- **The owner's approval.** The job is in the GitHub environment `prod`: the owner is its required reviewer, it allows only `main`, and admins can't skip the review. GitHub holds the job until the owner presses Approve. `tools/ci/live-approved.sh` then checks, through the run's approval history, that the run is on `main` and that the owner (the repository's owner, by login) approved it for `prod` and nobody rejected it. So an environment created without its rules, which GitHub does for a missing one, fails closed. `setup-ci-deploy.ps1 -SetGitHubVariables` gives the environment its rules before it sets the variables.
- **Its own identity.** `cix-agents-deploy-prod`, made by `setup-ci-deploy.ps1 -Environment prod -For agents`, has the paper deploy's two roles on live's own resources: AcrPush on live's registry, and the custom role "CIX agents deployer (prod)" (read and update container apps and jobs, stop a revision) on the registry, the engine and the three jobs. Its only federated credential trusts the GitHub environment (`repo:<owner>@<id>/<repo>@<id>:environment:prod`), so a job outside it, on any branch, can't sign in as it. Its variables are `LIVE_*`, apart from paper's.
- **The same images.** It deploys the archives paper deployed, checked against the hashes from the job that built them, pushed to live's registry and deployed by digest. It runs only if this commit is still the tip of `main`, or the newer commits changed nothing in the images (`agents-tip.sh`); otherwise the newer run deploys.
- **Only the images change.** `deploy-agents.sh` patches the first container's image and sends the rest of the container back as it was, so the engine keeps its trading mode, environment and strategy (or its standby), and its kill switch and state stay in `cix-live`. A protected environment needs `APPROVED_RUN`, the approved run's ID, or the script refuses before any call. An engine stopped by hand is left alone, and a failed deployment rolls back, as on paper.
- **`provision.ps1` stays the owner's.** Infrastructure, settings, roles and keys change only through it, from the owner's sign-in, and it still puts the engine on standby. Its check of who else has a role on `rg-cix-live` leaves out the live deploy's expected roles, only while its service principal is the only one of that name and its custom role grants no more than deploying images. Anything else it holds is listed.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Promote and switch by hand (ADR-0017) | Three commands for each change, and live stops trading between them. |
| Deploy every change to live with no approval | The owner chose to see each change before it reaches real money. A mistake that passed paper would reach live within minutes. |
| CI runs `provision.ps1` for live | It deploys the template, which pins the engine to paper: the switch would then be needed after each deploy. CI would also need rights on the whole group, Key Vault included. |
| Pull live's images from dev's registry | ADR-0017: a live identity with a role in the group CI's paper identity reaches. |
| A GitHub token, or a client secret, for the live identity | A secret stored in GitHub (ADR-0010). The federated credential needs none. |

## Consequences

- One click on GitHub per change that reaches paper's images: Review deployments, `prod`, Approve. GitHub notifies the required reviewer by email and in the app.
- The image artifact is kept for a day. A run approved later than that fails to download it: run CI on `main` by hand (Run workflow), which builds it again.
- Changes to `infra/`, app settings or the keys still need `provision.ps1` and then the switch, as in ADR-0017. So does a new live environment.
- A Live engine running a new image is still the image being trusted (ADR-0017): what reaches live money is code reviewed and merged on `main`, built by CI and run on paper first, and now approved again by the owner before it goes live.
- The approval is the control. In an approved run, the identity could run any image as the live engine, with its keys and its settings changed (Azure has no right that allows changing only the image), and `main` has no branch protection here: an approval runs whatever that commit's workflow does. So the owner approves only commits they recognise, is the only reviewer, and the check refuses anyone else's approval. Deleting `LIVE_AZURE_CLIENT_ID` turns the job off.
