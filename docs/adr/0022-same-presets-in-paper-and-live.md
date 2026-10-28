# 0022. The same Safe, Balanced and Risky presets in paper and live

- Status: proposed
- Date: 2026-10-28
- Amends ADR-0015 (paper switches between `paper` specs) and ADR-0018 ("Paper is unchanged: it switches between `paper` specs only")

## Context

The owner wants three strategy presets, Safe, Balanced and Risky, and the same three in both modes, so that paper can run one and live another and the results can be compared (2026-10-28: "Все стратегии должны быть синхронизированы на paper и live ... Оба енва содержат идентичные").

Until now the two modes offered different lists. Live switched only to the spec files the owner marked `live` (safe v2, balanced v2, risky v3). Paper refused those: `StrategySchema.Validate` accepted only `paper` status in paper mode, although the paper engine already ran a `live` spec when it was configured at startup. The paper Strategy tab therefore listed every paper version in the database (safe v1, balanced v1, risky v1 and v2, good v1), which are older copies with other limits.

## Decision

**Both modes offer the same presets: the newest version of each strategy the owner marked `live`.**

- Paper may activate a `paper` or a `live` spec from the database; a `draft` never runs. A database version that says `live` must match a spec file the owner marked `live`, in paper too, so a document can't claim the status for itself. Live is unchanged: only spec files marked `live`, exactly as merged, with the real-money confirmation.
- Draft files are skipped quietly when the engines seed the database.
- The paper engine seeds the `live` spec files as well as the `paper` ones, so the same versions are in both databases.
- The Strategy tab, in both modes, lists the newest `live` version of each name, plus the strategy running now if it is something else. Older versions and paper-only specs stay in the files, the database and the journal; they are no longer offered.
- A new preset version starts as a `draft` fitted to `config/risk-limits.live.json` (the test `EverySpecFileMarkedLiveOrDrafted_FitsTheLiveLimitsFile` checks it). Marking it `live` stays the owner's edit, and from then on both modes offer it. Neither engine switches by itself: each keeps running its selection until the owner switches it.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Copy each preset as a `paper` and a `live` file | Two files per preset that must be kept identical by hand; the journal labels would differ (`safe@v3` and `safe@v4`) for the same parameters, which defeats the comparison. |
| Keep paper on `paper` specs only | The two modes would never run the same versions, which is what the owner asked for. |
| Delete the older spec files | The paper week running now may use one of them; a deleted file would make its selection refused at the next restart. Hiding them from the tab gives the same three presets without that risk. |

## Consequences

- Paper and live list the same three presets; the owner can run, say, Balanced on live and Risky on paper and compare their journals by label.
- Sizes are the same in EUR in both modes, but `maxExposureFraction` is a share of each mode's allocation (200 EUR on paper, 100 EUR on live today), so the paper total cap is twice the live one. For a like-for-like comparison the owner can set paper's `AllocationQuote` to match live's in `config/risk-limits.json`, his edit only.
- The trader's order-size hook already bounds a buy by the largest `orderQuote` of any spec file, so it needs no change.
