# 0010. CI publishes the dashboard with a main-only Azure identity

- Status: accepted
- Date: 2026-10-25

## Context

The owner wants CI to publish the dashboard after changes reach `main`, instead of running `provision.ps1` by hand. Publishing needs the Static Web App's deployment token. The repository is private on GitHub Free. That plan has neither environment secrets limited to a branch nor branch protection. A repository secret is readable by workflows on every branch, including branches that agents push and pull requests that edit the workflow. Whoever holds the token can publish arbitrary API code, and that code runs with the dashboard's settings, including the shared Cosmos DB account's read-only key.

## Decision

No secret in GitHub. CI signs in to Azure with GitHub OIDC as a dedicated identity (`cix-dashboard-deploy-<env>`).

- **Who can use it.** Its only federated credential trusts subject `repo:<owner>/<repo>:ref:refs/heads/main`, which Entra ID enforces whatever the GitHub plan. That subject covers any workflow run on the `main` ref, under any trigger. Pull requests, other branches and forks get other subjects and can't sign in. A test keeps `id-token: write` in the deploy job only, and forbids triggers driven by outside input (`pull_request_target`, `workflow_run`, `issue_comment`, `repository_dispatch`).
- **What it can do.** It has one custom role, assigned on the one Static Web App: `Microsoft.Web/staticSites/read` and `listSecrets`. The job reads the deployment token at run time, masks it, and publishes.
- **Isolation from npm.**
  - npm installs run in a separate job (`dashboard-bundle`) with no `id-token` permission.
  - The deploy job verifies the bundle's hash, which it gets from the bundle job's output, before unpacking it.
  - It signs out of Azure, and removes the OIDC request variables, before running the pinned SWA CLI. The CLI still holds the deployment token, which it needs.
  - The CLI's transitive dependencies and the API's are not locked yet: no lockfile could be generated where this was built. Committing `package-lock.json` files is a follow-up.
- **Order.** A run that is no longer the tip of `main` doesn't publish, so an older run can't overwrite a newer dashboard. This is checked in the bundle job, and again just before publishing.
- **Setup.** `tools/azure/setup-ci-deploy.ps1` creates the identity, credential and role, asking before each change. It refuses to reuse an identity with any other way in: a secret or certificate, another owner, another subject or issuer, a broader role, or other role assignments. The repository variables hold only IDs.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Deployment token as a repository secret | Readable from any branch's workflow on GitHub Free |
| Environment secret limited to `main` | Needs GitHub Pro (about $4 a month) for a private repository |
| Keep publishing by hand | Works; the owner wanted CI to do it |

## Consequences

- **Anyone who can change `main` is trusted.** That means anyone who can push or merge to `main`, or start a workflow on it. Such a person can publish the dashboard, and so read the shared read-only key through it.
  - This includes the Claude coding agents. They merge pull requests at the owner's request and could push to `main` directly. Only CLAUDE.md's rules stop them, not GitHub.
  - GitHub Pro with branch protection would make that a technical limit.
- **Actions pinned by tag.** The actions are pinned by tag, not by commit SHA, and SHAs couldn't be looked up where this was built. Pinning them is a follow-up.
- **Owner setup.** The owner runs the setup script once and sets four repository variables.
- **Incident response.** Remove the role assignment, reset the deployment token, and regenerate the account's read-only keys (docs/azure.md).
