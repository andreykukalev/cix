# 0027. Stocks: the Claude analyst

- Status: proposed
- Date: 2026-11-02

## Context

The stocks advisor (ADR-0023) follows fixed rules on prices alone: trend, breakout, stop, size. It can't know that a company reports next week after cutting its guidance twice, that a short-seller published a report, or that a sector is falling on news. The owner asked where the AI assistant is: he wants Claude to read the news on what he holds and on the night's buys and candidates, and to say what it would do. The crypto engine already runs a Claude analyst the same way (ADR-0013, `tools/trader/run-analyst.sh`): headless Claude Code with web search, no shell or files, whose view can only make trading more cautious.

## Decision

1. **A separate nightly job after the advice.** `caj-cix-stocks-analyst` runs after the daily run (which ends by about 02:15 UTC), on the same image's host and a pinned Claude Code. It signs in with the owner's Claude subscription token (`claude-token` in the stocks Key Vault), as the crypto agents do; a run that hits the usage limit leaves that night to the rules, marked "Claude limit". It runs twice a night (02:40 and 06:40 UTC), so a night the limit stopped is tried again before the US open; a close already analysed is skipped. It has its own identity, which reads the Claude token and no other secret reads the advice and writes its own `analysis` container alone (the brief and the analyses live there, so it can't change the advice the owner trades from): it never reaches the EODHD key, the portfolios or the trades. The host processes in it never get the token; only Claude Code does.
2. **The nightly run builds the brief.** With the analyst deployed, the nightly run, which holds the EODHD key already, ends by saving what Claude reads as `brief-latest`: the night's advice for the owner's real portfolio (the paper one before he sent his), the rules' action on each holding, their buys with stops, the market-wide search's candidates of the same close, and up to 5 EODHD news items of the last 7 days on each of up to 30 of those stocks (holdings first). Sizes are fractions of the capital, never amounts: the brief sits beside untrusted news in a session that can search the web, so the owner's money stays out of it. Claude needs no key and no database. The analyst job's `Cix.Stocks.Host --brief <file>` copies it to a file only when it's for the latest advice and not yet analysed, and otherwise exits with 3, and the job stops there.
3. **Claude answers in a fixed shape.** For each holding: `hold`, `sell-half`, `sell-all` or `add`, with a reason and the next earnings day when it found one. Up to 5 picks, ranked, among the rules' buys and the candidates, and the ones it would leave out, with reasons. It may search the web, and fetch pages only from listed sites. Its reasons come in English and Russian.
4. **The host checks the answer before saving it.** `--accept <brief> <answer>` keeps only lines for stocks the brief named, actions from the list, a reason, an earnings day within 60 days of the close; cleans the text of control and bidi characters and cuts it; drops and counts a malformed line rather than the whole answer. An answer that isn't such an object is saved as `refused` (`unreadable`), and so is one that keeps no line for the holdings, or none at all (`empty`), since an empty view would read as Claude finding nothing wrong; a run that couldn't use Claude, as `not-run` with why (`--not-run <brief> usage-limit | credentials | timeout | failed`). A done analysis is never replaced by a failed run of the same close, or by an older close.
5. **Claude can only advise less risk.** It sets no amount and no stop: the shares and stops stay the rules'. `add` is a view, never a bigger buy. Nothing places an order: the owner reads it and decides, as with the rules' advice.
6. **The page shows it first.** The Stocks page reads `analysis-latest` with the advice. Red cards on top for what Claude would sell, orange ones for a held stock reporting within 14 days; its market note; tables for the holdings, the picks and what it leaves out; a Claude column on the candidates. When Claude didn't run, or its view is of an older close, a banner says so and the rules' advice stands alone.

## Alternatives considered

| Option | Why not |
| --- | --- |
| An Anthropic API key of its own | A separate bill and secret; the owner chose his subscription token, shared with the crypto agents' limit. |
| Claude reading the database or EODHD itself | It would need keys and tools that can write; the host hands it a file and checks what comes back. |
| Letting Claude size or place buys | The rules' sizing and stops are the risk control; the analyst can only take risk away. |
| EODHD's earnings calendar | Only in the €59.99 and €99.99 plans; Claude finds the dates in the news and on the web. |

## Consequences

- Each brief costs up to 30 news requests (5 EODHD calls each), counted by EODHD against the paid plan's 100 000 a day, not against the run's own budget.
- The owner puts his Claude token into the stocks Key Vault once (provision-stocks.ps1) and renews it when it expires; the page says when it's missing or expired.
- The analyst's runs share the owner's Claude usage limit with the crypto agents and the project's threads.
- Claude Code's version is pinned (`claudeCodeVersion` in `infra/stocks/stocks.json`); a new release reaches the job only when the owner changes it and redeploys.
- News is untrusted: an injected instruction could at most steer Claude's view, which can only advise less risk, or put what the brief holds (symbols and fractions of the capital, no amounts, no keys) into a search query or a fetch from a listed site. Claude has no shell, files or MCP servers, and its process has no Azure identity.
- The chat on the Stocks page comes later, on the same brief.
