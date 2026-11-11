# 0032. Stocks: Stop and Start for the whole advisory

- Status: accepted
- Date: 2026-11-11

## Context

The stocks advisory (ADR-0023) runs by itself: the nightly run after each US close (EODHD bars, the market-wide search of ADR-0025, the brief with news), the Claude analyst after it and its check before the US open (ADR-0027). The analyst runs on the owner's Claude subscription, which the project's threads share (a night that hits the limit is rules-only), and the search spends EODHD requests. The owner wants to stop all of it at times, to save Claude usage, because there is nothing to invest, or to take a break from the market, and later to start it again. While stopped, nothing should run and nothing should be deleted: the Stocks page shows what it had, frozen. Crypto is untouched.

## Decision

1. **One switch for the whole advisory, on the Stocks page.** The Next update tile has Stop; while stopped it says Stopped, and a banner over every Stocks tab says since when and has Start. Each asks for confirmation in a dialog that says what stops (and that the stops placed at Revolut keep working, with no advice raising them meanwhile) or what resumes and when.
2. **Written the way the owner's portfolio is.** `POST /api/owner/stocks-switch` (`{ action: "pause" | "resume" }`) creates the next press, `switch-N`, in a new `control` container of the stocks database, with the dashboard's writer identity (ADR-0012), whose role there allows creating items and nothing else. A press is never changed or removed, so the presses are also the record of when stocks were stopped. A press for the state the advisory is in already (a second click, another tab) writes nothing. Numbers have no gaps, so the newest is found by doubling and then halving: about 2 log₂ N reads.
3. **Both jobs read it first.** The nightly job and the analyst job each get read-only access to `control`. Stopped, the nightly run exits before any EODHD request or write, and the analyst's `--brief` reports nothing to analyse, so Claude never starts. After Start the next nightly run carries on as after a holiday, and the analyst waits for that run's brief: a brief written before the Start is never analysed, even by the check before the open, which would otherwise analyse a weeks-old brief.
4. **A newest press that can't be read counts as started**, the state before any press; the page says nothing special and the next press replaces it.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Suspending the Container Apps jobs' schedules | The dashboard has no Azure management rights and shouldn't get them; it would need the owner's `az` each time. |
| A setting in `config/stocks.json` | A code change and a new image for each stop and start. |
| One switch document the writer replaces | The writer identity is create-only everywhere (ADR-0012); numbered presses keep that, and the history. |
| Presses in the `requests` container | The analyst's identity would have to read the owner's portfolio submissions; `control` keeps it to the switch. |
| Checking the switch in the nightly run only | The check before the open analyses the latest brief every weekday, however old: stopped nights would still run Claude. |

## Consequences

- The owner reruns `provision-stocks.ps1` once: it makes the `control` container and the roles, and builds the new host image. Until then the page shows the advisory as started and Stop answers that the setup is needed.
- Start takes effect at the next scheduled run (02:00 UTC, Tuesday to Saturday); the page can't start a job at once. Claude's first view after Start follows that run.
- A short stop between the nightly run and the check before the open skips that day's check, even when started again before it.
