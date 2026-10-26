# 0014. A private dashboard: the owner's sign-in for every page and API route

- Status: proposed
- Date: 2026-10-26
- Supersedes: the public view of 0012 (and the public page of 0008). The writer identity, the confirmed owner and the request rules of 0012 stand.

## Context

ADR-0008 made the dashboard public by the owner's choice, and ADR-0012 kept it that way: anyone with the URL saw the portfolio's size, holdings, decisions and activity, with every price masked, and the owner signed in for prices and requests. That needed two of everything (masked and full decisions, counts and amounts for activity, `/api/owner/*` copies of public reads) and a masking scheme for the trader's free text that has to stay right as the engine's formats change.

The owner now wants the whole application behind sign-in: balances, positions, history, analytics and controls, enforced in the API, not only in the page, with secure sessions and sign-out. Live trading is coming, which makes the account's figures sensitive in their own right.

## Decision

**Everything needs the confirmed owner.**
- `staticwebapp.config.json` gives every route, pages and `/api/*` alike, to the `owner` role only. No rule names methods, so no method slips past one. The one exception is `/signed-out.html` (with `/styles.css`), a static page with no script and nothing about the account.
- Anyone not signed in is redirected to the Microsoft sign-in (`401` override). Anyone signed in without the role sees the signed-out page (`403` override). GitHub and X sign-in stay off.
- Every API handler checks the owner again, before it reads the database or its cache: a Microsoft sign-in, with the role, whose user ID is the one the setup script confirmed (`DASHBOARD_OWNER_USER_ID`, ADR-0012). With no ID confirmed, nobody gets anything.
- Every API answer is `no-store`, so no browser or proxy keeps the owner's data. The API still keeps answers a few seconds per instance (one minute for activity), only ever handed to the owner, so refreshes and several tabs share one database read.

**One view, the owner's.** The masked public view is dropped: the API passes on levels, prices and the journal's own text as written (holdings' entry, stop and target, resting orders' limits, fill prices and fees, decisions' prices and risk, activity amounts). `/api/owner/decisions` and `/api/owner/activity` are gone; `/api/dashboard`, `/api/activity` and `/api/journal` now serve what they served. `/api/owner/requests` is unchanged.

**Sessions.** Static Web Apps' own sign-in session (an HTTP-only cookie it sets and checks). The page has a Sign out button (`/.auth/logout`, back to the signed-out page). When the session ends, the page's API calls get a redirect, which they don't follow: the page says the sign-in has ended and offers to sign in again, instead of showing stale figures as current.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Keep the public, masked page and add a private one | Two of everything to keep in step, and the masking has to keep up with every new text format; the owner asked for everything behind sign-in |
| Allow any signed-in (`authenticated`) user in the route rules, checking the owner only in the API | Anyone with a Microsoft account would load the page's code; the role rule turns them away first, and the API checks the confirmed ID anyway |
| Custom Entra ID provider restricted to the owner's tenant | Needs the Standard plan (ADR-0012); the role plus the confirmed ID give the same result on Free |
| Our own session cookies or tokens in the API | More code to get wrong; the platform's session is already checked on every request before the API runs |

## Consequences

- Nothing is public any more. Until the owner has accepted the invitation and run `setup-dashboard-requests.ps1` again to confirm the account, nobody can open the dashboard, the owner included (the page loads with the role, but the API answers 403 until the ID is confirmed).
- The role is given by invitation (ADR-0012): if it expires or is removed, the owner runs the setup script again.
- Signing out ends the dashboard's session. It doesn't sign the browser out of the Microsoft account, so signing in again may not ask for a password.
- Simpler code: one set of reads, no masking, and tests that check every route refuses anyone but the owner.
- Anyone who can change the app's settings or code can still change who gets in. That is the same trust as deploying the dashboard (ADR-0010).
