# 0010. CI publishes the dashboard with a main-only Azure identity

- Status: accepted
- Date: 2026-10-25

## Context

The owner wants CI to publish the dashboard after changes reach `main`, instead of running `provision.ps1` by hand. Publishing needs the Static Web App's deployment token. The repository is private on GitHub Free. That plan has neither environment secrets limited to a branch nor branch protection. A repository secret is readable by workflows on every branch, including branches that agents push and pull requests that edit the workflow. Whoever holds the token can publish arbitrary API code, and that code runs with the dashboard's settings, including the shared Cosmos DB account's read-only key.

## Decision

No secret in GitHub. CI signs in to Azure with GitHub OIDC as a dedicated identity (`cix-dashboard-deploy-<env>`).

- **Who can use it.** Its only federated credential trusts subject `repo:<owner>/<repo>:ref:refs/heads/main`. Entra ID enforces that whatever the GitHub plan, so pull requests, other branches and forks can't sign in.
- **What it can do.** It has one custom role, assigned on the one Static Web App: `Microsoft.Web/staticSites/read` and `listSecrets`. The job reads the deployment token at run time, masks it, and publishes.
- **Isolation from npm.** npm installs, including the pinned SWA CLI, run in a separate job (`dashboard-bundle`) with no `id-token` permission. Their install scripts can't request an OIDC token. The deploy job only unpacks the result.
- **Order.** A run that is no longer the tip of `main` doesn't publish, so an older run can't overwrite a newer dashboard.
- **Setup.** `tools/azure/setup-ci-deploy.ps1` creates the identity, credential and role, asking before each change. It refuses an identity that also trusts other subjects. The repository variables hold only IDs.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Deployment token as a repository secret | Readable from any branch's workflow on GitHub Free |
| Environment secret limited to `main` | Needs GitHub Pro (about $4 a month) for a private repository |
| Keep publishing by hand | Works; the owner wanted CI to do it |

## Consequences

- **Pushers to `main` are trusted.** Anyone who can push to `main` can publish the dashboard, and so read the shared read-only key through it. On GitHub Free nothing stops a direct push to `main`. GitHub Pro with branch protection would close that gap.
- **Owner setup.** The owner runs the setup script once and sets four repository variables.
- **Incident response.** Remove the role assignment, reset the deployment token, and regenerate the account's read-only keys (docs/azure.md).
