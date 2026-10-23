# 0007. Market analyst with news, limited to making trading more cautious

- Status: accepted
- Date: 2026-10-23

## Context

The scanner (ADR-0006) sees only prices and volumes. Events it can't see, such as a Fed decision, an exchange or stablecoin failure, a token exploit or a delisting, can make a technically good entry a bad one. The owner asked to use news. News text is untrusted: a planted page can carry instructions aimed at an AI agent.

## Decision

A third agent, the **market analyst**, runs every 6 hours in its own container (the same image as the trader, a different loop).

1. **Inputs.**
   - `get_analyst_brief`: its spec, the current view, features for every tradable symbol, and the latest scan.
   - Web search and fetch.
   - No order tools, files or shell.
2. **Output.** One call to `record_market_view`:
   - **Regime:** risk-on, neutral or risk-off.
   - **Avoided symbols:** symbols to keep out of new entries.
   - **Validity:** 1 to 12 hours.
   - **For the owner:** a summary and the https sources it used.
3. **Effect is one-way.**
   - Risk-off stops new entries, and an avoided symbol gets no new entry.
   - Exits are never blocked. Risk-on changes nothing.
   - The view expires, and an expired or missing view counts as neutral.
   - Even a fully manipulated analyst can therefore only pause entries for at most 12 hours at a time. It can never cause a trade or keep one open.
4. **Free text stays away from the trader.**
   - The scanner applies only the structured fields.
   - The trader sees fixed strings: `risk-off (market view)`, and reason `news` with a timestamp.
   - The analyst's words and sources go to the journal for the owner.
5. **Separate spec.** `strategies/analyst.vN.json` governs the analyst's tools. The trading spec can't grant them, and the analyst spec can't grant trading tools.
6. **Persistence.** Each view is a `market-view` journal entry. The latest active one is restored on restart.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Let the trader search the news on each run | Puts untrusted web text next to the order tools; slower and costlier on every signal |
| A news sentiment score used to boost entries | Lets manipulated news cause trades; crypto news is mostly priced in before it can be read |
| A news API feed in the engine | Paid feeds, and keyword rules miss context; can be added later as an analyst input |

## Consequences

- One more container and about 4 Opus runs a day. The model is set with `ANALYST_MODEL`.
- Reports can compare signals blocked by risk-off or `news` with what happened afterwards (#8), to check whether the analyst helps.
