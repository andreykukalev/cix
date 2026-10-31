# 0025. Stocks: a market-wide search for candidates

- Status: proposed
- Date: 2026-10-31

## Context

The stocks advisor (ADR-0023) watches a fixed list of about fifteen stocks, because the free EODHD key allows 20 calls a day. The owner wants the advisor to look at the whole US market each night and hand him a short, ready list of stocks worth buying, with a sum and a stop for each, and to judge the ones he holds first. Later, a Claude analyst picks the best few and explains the risks, and a chat records his real buys.

EODHD's free key has no whole-market download (`eod-bulk-last-day`: "Bulk downloads are not part of the Free plan"). The EOD All World plan ($19.99 a month, 100,000 calls a day) has it: one request for the whole US market costs 100 calls and, with `filter=extended`, carries each stock's 50- and 200-day EMAs, 250-day high, 50-day average volume and market cap. The symbol list (`exchange-symbol-list`, 1 call) gives each stock's type and venue. Sector and industry need the Screener API (a $29.99 plan) or fundamentals (10 calls a stock), so they are left to the analyst.

## Decision

After each nightly advice, the stocks host runs a market-wide search (`MarketSearch`, `Cix.Stocks.Screener`), off until `screener.enabled` is set in `stocks/config/stocks.json`:

1. **Shortlist** from the whole market's last session (100 calls) and the symbol list (1 call, refetched weekly): common stocks on NYSE, NASDAQ and NYSE MKT, priced from 5 to the owner's price cap, worth 2 billion or more, traded for 20 million a day, in an uptrend by EODHD's EMAs and within 15% of their 250-day high. Nearest the high first, at most 40, never one already watched or held.
2. **The advisor's own rule** on each shortlisted stock's year of bars (1 call each, or the cache): `Advisor.Entry`, the single place the entry rule lives, also used for the watchlist. A breakout, or an uptrend within 3% of its prior high, is a candidate.
3. **Sized by the advisor's rules** on the paper portfolio's equity: the stop is 3 ATR under the close, risk 1% of the capital, at most 20% in one stock. The analyst will choose among the candidates but never computes a sum: money stays in code and `decimal`.
4. **Written** to `candidates-latest` (and once to `candidates-YYYY-MM-DD`) in the stocks database. The Stocks page shows them, and a status when the search is off, out of calls, refused or early.

The search keeps its own call budget (`screener.maxCallsPerDay`, 300 by default) in its own state document, reserved before any call, like the daily run. It never changes the paper portfolio and never places an order.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Fetch every stock's bars (about 6,000 calls) | Twenty times the calls of the download for the same shortlist; the download's EMAs are enough to shortlist. |
| Keep each stock's history in the database from daily downloads | About 6,000 documents updated every night, a year to fill, and split adjustments to handle ourselves; the shortlist's 40 fetches are cheap and already adjusted. |
| EODHD's Screener API | A $29.99 plan, at most 1,000 results a screen, and its signals don't match the advisor's rule. |
| Let Claude choose the size | Sums must be exact and repeatable; Claude explains and chooses, code computes. |

## Consequences

- The owner buys the EOD All World plan, replaces the key in Key Vault (`provision-stocks.ps1 -ReplaceKey`), sets `screener.enabled` and redeploys. Until then the page says the search is off.
- The fixed watchlist goes once the owner's real holdings are recorded (the "my portfolio" form) and the search is on: holdings are checked first, then the candidates.
- EODHD's venue names and the download's fields come from its documentation, not a real answer yet: check the first night's page and fix `screener.venues` if a main exchange is missing.
- Next: the owner's real portfolio and capital, then the Claude analyst (picks, risks for holdings), then the chat.
