# 0012. Dashboard owner sign-in and a create-only writer identity

- Status: proposed
- Date: 2026-10-26

## Context

ADR-0011 has the dashboard create manual requests in the `requests` container. Until now the dashboard was public and read-only, with the shared account's read-only key (ADR-0008, ADR-0009). Adding Buy and Sell buttons (#75) raises two questions:

- **Who may press them.** The page is public, and it masks every price.
- **What the API writes with.** The owner set three conditions. The browser must never reach Cosmos DB with privileged credentials. No IDs or credentials go in the repository. And nothing is provisioned without his approval.

Two limits come from the platform:
- The Static Web Apps **Free** plan has no managed identity for its managed functions.
- On the Free plan, custom roles can only be assigned by invitation (up to 25 users).

The shared account's read-write key reaches every project's database.

## Decision

**Sign-in.** The owner signs in with a Microsoft account through Static Web Apps' built-in provider (`/.auth/login/aad`), and holds a custom `owner` role by invitation.
- `staticwebapp.config.json` allows `/api/owner/*` only for that role and blocks GitHub sign-in.
- Every owner handler checks the role again, from the `x-ms-client-principal` header, which the platform sets and a browser can't.
- The public page and API stay as they are, prices masked. The owner's views (decisions with levels, request history) are served only from `/api/owner/*`, with `no-store`.

**Writes.** A dedicated service principal, `cix-dashboard-writer-<env>`, with one client secret.
- Its only right is a Cosmos DB data-plane custom role: `readMetadata` and `items/create`, scoped to `<database>/requests`.
- It can't read, replace, upsert or delete anything, in CIX's database or any other database in the shared account.
- The API signs in with client credentials and calls the Cosmos DB REST API with the AAD token.
- The secret lives only in the Static Web App's server-side settings (`COSMOS_WRITER_*`).
- `tools/azure/setup-dashboard-requests.ps1` creates all of this. The owner runs it, it checks a reused identity for any other access first, and it confirms each change.
- The secret reaches Azure through a file only the owner can read, never on a command line, and is never printed.

**Requests.**
- The API builds the document itself: server time, `status: new`, `requestedBy: owner`, `tradingMode: paper` only. It takes only the fields it validates from the page.
- A UUID per click makes a retry the same request: Cosmos DB answers 409, and the API returns the first request.
- Posts must be JSON and carry a custom header, so a cross-site page can't send them without a preflight the API never answers.
- The engine checks everything again (ADR-0011).

## Alternatives considered

| Option | Why not |
| --- | --- |
| The account's read-write key in the API | Reaches every project's database in the shared account, with every operation |
| Managed identity for the API | Not available to managed functions on the Free plan; Standard costs about $9 a month, or a separate Function App |
| Resource tokens (permission per container) | Need a read-write key to mint them, so the same key would sit in the API |
| Custom Entra ID provider (owner's tenant only) | Needs the Standard plan; invitations give the same role on Free |
| GitHub sign-in | The owner chose a Microsoft account; one provider is less to secure |
| Browser writes to Cosmos DB directly | Ruled out by the owner: no privileged credentials in the browser |

## Consequences

- The owner runs `setup-dashboard-requests.ps1` once per environment, accepts the invitation, and rotates the secret yearly (`-RotateSecret`). Until then the buttons answer that the writer isn't set up.
- `provision.ps1` replaces the app's settings on each deployment. It now reads the writer settings first and puts them back, and stops before deploying if it can't read them.
- Anyone who can change the app's settings or code can still write requests. That is the same trust as deploying the dashboard (ADR-0010), and the engine is still the only one that acts, behind the risk gate.
- Requests stay paper only, from both sides: the API refuses `tradingMode: live`, and a live engine ignores requests unless `ManualRequests:AllowLive` is set (ADR-0011).
- The invitation link printed by the script is a one-time sign-up link for the owner's own account. It isn't a credential to Azure, but it shouldn't be shared.
