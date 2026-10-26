# 0010. CI publishes the dashboard with a main-only Azure identity

- Status: accepted
- Date: 2026-10-25

## Context

The owner wants CI to publish the dashboard after changes reach `main`, instead of running `provision.ps1` by hand. Publishing needs the Static Web App's deployment token. The repository is private on GitHub Free. That plan has neither environment secrets limited to a branch nor branch protection. A repository secret is readable by workflows on every branch, including branches that agents push and pull requests that edit the workflow. Whoever holds the token can publish arbitrary API code, and that code runs with the dashboard's settings, including the shared Cosmos DB account's read-only key.

## Decision

No secret in GitHub. CI signs in to Azure with GitHub OIDC as a dedicated identity (`cix-dashboard-deploy-<env>`).

- **Who can use it.** Its only federated credential trusts subject `repo:<owner>/<repo>:ref:refs/heads/main`, which Entra ID enforces whatever the GitHub plan. That subject covers any workflow run on the `main` ref, under any trigger. Pull requests, other branches and forks get other subjects and can't sign in. A test keeps `id-token` in the deploy job only, forbids `write-all`, and allows only the `push`, `pull_request` and `workflow_dispatch` triggers in any workflow. It guards against accidents; it is not a security control, since anyone who can change `main` can change the test.
- **What it can do.** It has one custom role, assigned on the one Static Web App: `Microsoft.Web/staticSites/read` and `listSecrets`. The job reads the deployment token at run time, masks it, and publishes.
- **Isolation from npm.**
  - npm installs run in a separate job (`dashboard-bundle`) with no `id-token` permission.
  - The deploy job verifies the bundle's hash, which it gets from the bundle job's output, before unpacking it.
  - It signs out of Azure, and removes the OIDC request variables, before running the pinned SWA CLI. This is hygiene, not a boundary: code running on the runner could still reach the runner's secrets. The real limit is the identity's role, which grants no more than the deployment token the CLI holds anyway.
  - The CLI's transitive dependencies and the API's are not locked yet: no lockfile could be generated where this was built (follow-up #61).
- **Order.** An older run can't overwrite a newer dashboard. A run that is no longer the tip of `main` publishes only when `dashboard/`, `infra/` and `.github/workflows/` are unchanged between it and the tip (amended in #73: since CI skips publishing on pushes that don't change the dashboard, the run that did change it must still publish when it finishes after one of those). This is checked in the bundle job, and again just before publishing.
- **Setup.** `tools/azure/setup-ci-deploy.ps1` checks everything before it changes anything, and creates the trust for `main` last.
  - It refuses to reuse an identity with any other way in: secrets, certificates or other owners on the app registration or its service principal; API permissions or directory roles; another subject, issuer or audience; a broader same-named role; or other role assignments.
  - It asks before each change.
  - The repository variables hold only IDs.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Deployment token as a repository secret | Readable from any branch's workflow on GitHub Free |
| Environment secret limited to `main` | Needs GitHub Pro (about $4 a month) for a private repository |
| Keep publishing by hand | Works; the owner wanted CI to do it |

## Consequences

- **Anyone who can change `main` is trusted.** That means anyone who can push or merge to `main`, or start a workflow on it. Such a person can publish the dashboard, and so read the shared read-only key through it.
  - This includes the Claude coding agents. Their GitHub access could merge pull requests or push to `main` directly (in this repository they have merged at the owner's request). Only CLAUDE.md's rules stop them, not GitHub.
  - GitHub Pro with branch protection would make that a technical limit.
- **Actions pinned by tag.** The actions are pinned by tag, not by commit SHA, because SHAs couldn't be looked up where this was built (follow-up #62).
- **Owner setup.** The owner runs the setup script once and sets four repository variables.
- **Incident response.** Remove the role assignment, reset the deployment token, and regenerate the account's read-only keys (docs/azure.md).
