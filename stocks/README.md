# CIX stocks advisory

Recommendations for medium-term trading on the owner's Revolut brokerage account, which has no API: CIX says what to buy, where to put the stop and when to raise or sell, and the owner places the orders by hand (ADR-0023).

| Project | What it holds |
| --- | --- |
| `src/Cix.Stocks` | The advisor: market regime, trend entries, stops that only rise, sizing with gap risk and FX. No I/O, no packages; reuses `Cix.Domain`'s candles and ATR. |
| `src/Cix.Stocks.MarketData` | The EODHD client for adjusted daily bars. |
| `src/Cix.Stocks.Backtest` | A console backtest: the advisor replayed day by day through a paper portfolio. |
| `src/Cix.Stocks.Host` | The daily run after the US close: bars within the EODHD call budget, the paper portfolio, the advice for the dashboard. |
| `config/stocks.json` | The daily run's watchlist, budget and limits (no secrets). |
| `tests/` | xUnit tests, one project per source project. |

```bash
dotnet build stocks/Cix.Stocks.slnx
dotnet test stocks/Cix.Stocks.slnx
```

Changes here never rebuild or redeploy the crypto engine and agents: CI builds and tests this solution in its own job, `stocks`, which also runs for changes to `Cix.Domain`.

## Backtest

The advisor's advice after each close is carried out at the next open by `Simulation.PaperPortfolio`: sells, then stops (raised only), then buys within the cash. A stop rests at the broker and sells on the day the low reaches it, at the stop or at the open when the price gaps under it. Commission, FX fee and slippage come off every fill; FX rates are fixed for the run. The advisor's capital is the portfolio's equity that day, so sizes follow the account and gains compound. The benchmark is bought and held from the first close.

```bash
export EODHD_API_TOKEN=...   # without it, EODHD's public demo key: AAPL.US, MSFT.US, TSLA.US, AMZN.US, VTI.US, MCD.US
dotnet run --project stocks/src/Cix.Stocks.Backtest -- \
  --symbols AAPL.US,MSFT.US,SAP.XETRA,CSPX.LSE:USD --benchmark CSPX.LSE \
  --from 2014-01-01 --to 2026-10-28 --capital 10000 --currency EUR --fx USD=0.92
```

A symbol's currency comes from its exchange (`US` is USD, `XETRA`, `PA`, `AS` and the other euro exchanges are EUR, `SW` is CHF); give it after a colon otherwise, as LSE lines trade in USD, GBP or GBX. Options: `--step` (smallest quantity, 1 for whole shares), the advisor's `--initial-stop-atr`, `--trail-atr`, `--risk` (per trade, as a fraction), `--max-position` (fraction) and `--max-positions`, the entry filters `--max-above-fast`, `--max-recent-gain` and `--max-atr` (a fraction, or `off`), `--commission`, `--min-commission`, `--fx-fee`, `--slippage`, `--out` (default `backtest-out`), `--cache` (default `.cache/eodhd`). Bars are cached per symbol and range, so a rerun spends no API calls; a range ending within two days of today isn't cached, as its last bar may be partial. The report is `summary.json` (return, CAGR, max drawdown, round trips, win rate, time in the market, for the strategy and the benchmark), `equity.csv` and `trades.csv`.

## Daily run

`Cix.Stocks.Host` runs once a night after the US close (a Container Apps job in `rg-cix-stocks`, set up by `tools/azure/provision-stocks.ps1`, see `docs/azure.md`, "Stocks advisory"), and must start after `dataReadyUtc` (01:00 UTC the next day), when EODHD has the session's bars. It never places an order. The advice is for the paper portfolio, which carries every piece of advice out by itself: the owner, following the advice at the broker, holds the same, and the record shows what following it earns.

1. The night's EODHD calls are reserved in the saved portfolio before any is made (`maxCallsPerRun` 100, and `maxCallsPerDay` 200 across the UTC day, on the paid plan's 100 000), so a run overlapping another fails instead of spending them twice.
2. Bars for the benchmark, then the held symbols, then the rest of `config/stocks.json`'s watchlist, the longest unfetched first. Bars fetched after the latest session's data was ready are reused from the database, so a repeated or manual run the same night spends no calls. A symbol over budget, or whose fetch fails or comes back empty, keeps its older bars with the note `call-budget` or `data-error`.
3. A split or dividend re-adjusts the history: the run compares the bars with closes saved with the portfolio, on several days (one revised bar isn't an adjustment), and moves the position and the pending advice into the new terms (a split changes the quantity, a dividend is paid in cash; note `adjusted`).
4. The previous close's advice is carried out in the paper portfolio at the next session's open (sells, then stops, then buys), and the stops are checked on every session since, including a held symbol's sessions whose bars came a night late.
5. The advisor runs on the latest close, sized on the portfolio's equity, within the owner's caps. A breakout is bought only when it isn't overheated (`maxAboveFastAverage`: the close at most 20% above the 50-day average; `maxRecentGain`: at most 15% up in `recentGainDays`, 5) or too jumpy (`maxAtrFraction`: the 20-day ATR at most 5% of the close); `null` turns one off. The watchlist shows such stocks as `overextended` or `too-volatile`, and the market search leaves them out. Other caps: `maxSharePrice` (dearer shares are noted `too-expensive`) and `maxThemeFraction` per watchlist theme (`theme-limit`; a holding the same advice sells still counts that night). A close already advised is never carried out twice.

The run writes to its own database (`cix-stocks`, containers `advice`, `state`, `trades`, `bars`, and `analysis` for the Claude analyst, all with partition key `/id`; it only reads `requests` and `control`): the trades and the dated advice (`advice-YYYY-MM-DD`) once each, never changed; the paper portfolio with an ETag, so of two overlapping runs only one saves; then `advice-latest`, which a later run republishes from the dated advice if a failure left it behind. The page shows money to the cent; the portfolio keeps every digit. The EODHD key is read from `/run/secrets/eodhd-api-token` (Key Vault) and is never logged.

### The owner's real portfolio

The owner sends his real portfolio from the Stocks page (ADR-0026): the capital the advisor may plan with, in USD, and each holding at Revolut's broker (US shares only: ticker, shares, average price paid, the stop resting at the broker, the day bought). Each save is the whole portfolio or one trade made at Revolut ("I bought", "I sold": shares, price per share, fees, day, and a stop for a buy), `portfolio-N` in the `requests` container, created by the dashboard's writer identity, which can do nothing else there.

1. At the start of the run, every submission after the last one seen (`seenSequence`) is checked and applied in order (`OwnerPortfolio.Apply`), or refused with a reason the page shows until a later one is applied (`capital`, `holdings`, `symbol`, `figures`, `opened`, `sequence`, `unreadable`; for a trade also `no-portfolio`, `not-held`, `too-many`, and `after-refused` for a trade after a refused whole portfolio, where the portfolio's refusal is the one shown; a refused trade carries its kind and ticker), and never read again. A whole portfolio replaces the state. A trade changes it in decimal: a buy adds the shares at its price with the fees in the average cost and leaves the capital, so the free cash falls (a new holding opens on the trade's day with its stop; an existing one keeps its stop unless a new one is given); a sell takes the shares off at the average cost and adds `quantity × (price − average) − fees`, to the cent, to the capital, closing the holding at zero (the average to 8 decimals; both round half to even). A split since the last run moves the saved holdings into the new terms before the submissions apply, so a trade made after it meets the new shares. Each applied trade is written once as `trades/owner-N` and kept, the latest 100, with the owner state. Without any, there is no owner section and the advice is the paper portfolio's alone. A run with no new close saves nothing, so a submission waits for the next close. Only a USD base currency advises the owner.
2. The held symbols (the owner's and the paper portfolio's) are fetched right after the benchmark, the longest unfetched first, so with more of them than calls they take turns; one outside the watchlist costs a call a night.
3. The advisor runs again for the owner: capital from the submission, free cash the capital less what the holdings cost, the owner's holdings checked first. A stop it sets or raises is saved as placed; a sell is advised again each night until the owner records the sale or sends the portfolio without it. A split moves the holding (note `adjusted`); a dividend changes nothing.
4. The owner state is saved with an ETag (`state/owner`) after the paper portfolio. The watchlist's `held` and the market-wide search (sized on the owner's capital, his holdings left out) follow the owner's portfolio once there is one.

### Stop and Start

The owner can stop the whole stocks advisory from the Stocks page and start it again (ADR-0032), to save Claude usage and EODHD requests or to take a break. Each press is `switch-N` in the `control` container (`{ sequence, action: "pause" | "resume", submittedAt }`), numbered from 1 with no gaps and created by the dashboard's writer identity, which can do nothing else there; the newest decides, found in a few reads (`StocksSwitch.CurrentAsync`: doubling, then halving). A newest press that can't be read counts as started.

- Stopped, the nightly run exits 0 before any EODHD request or write, so there is no advice, market search or brief; `--brief` exits 3, so neither of the analyst's passes runs Claude. Everything saved stays, and the page shows it frozen, with a banner instead of the stale-advice warning. Trades and portfolios the owner sends wait for the first run after Start.
- After Start, the next nightly run carries on from the last advice (the paper portfolio carries out that advice at the next session's open and checks its stops on every session since, as after a holiday), and the analyst waits for that run's brief: a brief written before the Start is never analysed, even by the check before the open.
- `--dry-run` ignores the switch.

### Market-wide search

After the advice, the same run searches the whole US market for candidates (ADR-0025, `MarketSearch` and `Cix.Stocks.Screener`), when `screener.enabled` is set in `config/stocks.json`. It needs EODHD's paid EOD All World plan: the free key can't download the whole market, and the page says the search is off or refused.

1. Its calls are reserved in its own state (`state/search`, `screener.maxCallsPerDay`, 300 by default) before any is made: the whole-market download (100 calls), the symbol list when older than `universeDays` (1 call, kept in `state/universe`), and each shortlisted stock's bars (1 call each at most, `shortlistSize` 40). Calls not made go back.
2. The shortlist: common stocks on `venues`, priced from `minPrice` to the advisor's `maxSharePrice`, with `minMarketCap` and `minDollarVolume` (50-day average volume times the close), the provider's 50-day EMA over its 200-day and the close over that, within `maxBelowHigh` of the 250-day high; nearest the high first. The watchlist, the holdings and the benchmark are left out.
3. Each shortlisted stock's own bars go through `Advisor.Entry`, the same rule as the watchlist: a breakout, or an uptrend within `nearHighFraction` of its prior high, is a candidate, sized by the advisor's rules on the owner's capital (the paper portfolio's equity before he sends one) (`Advisor.InitialStop`, `Advisor.Sized`). Entry signals first, then momentum, at most `maxCandidates`.
4. `candidates-YYYY-MM-DD` once, then `candidates-latest`, in the `advice` container. A session already searched isn't searched again (a page a failed run left behind is copied from the dated one); a download for an earlier session (`not-ready`) or a later one than the advice (`advice-behind`) is retried by the next run. Bars stop being fetched 12 minutes into the job, inside its 15-minute timeout, and what was found is listed.

### Claude analyst

When the analyst job is deployed (`STOCKS_ANALYST_BRIEF=1`), the nightly run ends by writing the brief for Claude (ADR-0027); the analyst job then asks Claude what it makes of the night's advice, using the host for everything that touches the database:

1. The nightly run saves the brief as `brief-latest` in the `analysis` container, also when the search failed (a brief it couldn't write leaves the advice as it is): the owner's holdings (the paper portfolio's before he sends his) with the rules' action on each, the rules' buys, the search's candidates of the same close, and EODHD news (5 items of the last 7 days) on up to 30 of those stocks (`newsMissing`: failed; `newsSkipped`: past the 30, or not fetched in time: news stops a minute after the search's deadline). Sizes are fractions of the capital (`weight`, `cashWeight`), never amounts: the brief sits beside untrusted news in a session that can search the web. Nothing new to analyse (no advice, or its close is already analysed): no brief. The analyst job's `--brief <file>` copies `brief-latest` to the file when it's for the latest advice and not yet analysed, and exits 3 otherwise.
2. Claude reads it and answers with one JSON object: `{ market, marketRu, holdings: [{ symbol, action: hold | sell-half | sell-all | add, reason, reasonRu, earnings }], picks: [{ symbol, rank, reason, reasonRu, earnings }], skips: [{ symbol, reason, reasonRu, earnings }] }`.
3. `--accept <brief> <answer>` checks the answer against the brief (`AnalystAnswer.Check`: a malformed line is dropped and counted; an answer that isn't the object is `refused` as `unreadable`, and one with no line for the holdings, or none at all, as `empty`) and saves it; `--not-run <brief> <reason>` saves that Claude didn't run (`usage-limit`, `credentials`, `timeout`, `failed`). These three need only `STOCKS_COSMOS_ENDPOINT` and `AZURE_CLIENT_ID`, never the EODHD key. A done analysis is kept as `analysis-YYYY-MM-DD` once and as `analysis-latest`, in the `analysis` container; a failed run never replaces a done one of the same close, and a brief for a close after the latest advice is refused. On a weekday from 08:35 New York time to the 09:30 open, `--brief` hands out the check before the open instead (`pass`: `pre-open`, with the close's latest done view as `night`): once per New York day, kept as `analysis-YYYY-MM-DD-pre-open-YYYY-MM-DD` (the close, then the day) and as `analysis-latest`. After a done night it can only take risk away: it keeps only the night's picks, a holding's action less cautious than the night's is dropped and the night's line stands, and holdings and skips it leaves out keep the night's lines. The night's view never replaces it.

In Azure this is the analyst job (`caj-cix-stocks-analyst`, `stocks/analyst/run.sh`), with its own identity: it reads the Claude token and no other secret, reads the `advice` container and writes its own `analysis` container alone, so it can't change the advice. `run.sh` takes the brief (`--brief`), runs Claude Code with the owner's Claude token (the host processes never get it): web search, and page fetches from listed sites only, no shell, files or MCP servers; then `--accept` or `--not-run`. `instructions.md` is Claude's brief on the brief. `provision-stocks.ps1` stores the token and deploys it (see `docs/azure.md`, "Stocks advisory").

Claude's view never changes an amount or a stop: it can only advise less risk.

Locally, `EODHD_API_TOKEN=... dotnet run --project stocks/src/Cix.Stocks.Host -- --dry-run` keeps everything in memory and prints the advice. It still spends the key's calls, which the nightly run doesn't know about: with the free key, a dry run the same UTC day leaves the job short.

## Dashboard

The dashboard's Stocks mode (the second button of its Crypto | Stocks switch, which hides the crypto tabs and reads nothing of crypto's) reads `GET /api/stocks` (`dashboard/api/src/stocks.js`), read only and owner only. The route reads the stocks host's own Cosmos DB account, never the crypto engines' databases, through these app settings: `STOCKS_COSMOS_ENDPOINT`, `STOCKS_COSMOS_READONLY_KEY` and `STOCKS_COSMOS_DATABASE` (default `cix-stocks`). Without them, the tab says the advisor isn't connected. `tools/azure/provision-stocks.ps1` sets them, and `provision.ps1` keeps them when it redeploys the dashboard.

The host writes one document after each close: id `advice-latest` in the `advice` container, with partition key `/id`. Its `value` looks like this:

```json
{
  "asOf": "2026-10-30", "generatedAt": "2026-10-30T21:30:00Z",
  "regime": "risk-on | risk-off | unknown", "benchmark": "CSPX.LSE",
  "baseCurrency": "EUR", "capital": 10000, "cash": 6200.5, "equity": 10150.25,
  "actions": [{ "kind": "sell | set-stop | raise-stop | buy", "symbol": "MSFT.US", "name": "Microsoft", "currency": "USD",
                "quantity": 4, "referencePrice": 512.3, "stop": 480.1, "reason": "breakout" }],
  "holdings": [{ "symbol": "SAP.XETRA", "name": "SAP", "currency": "EUR", "quantity": 10, "averageCost": 200,
                 "lastClose": 250, "stop": 225, "openedOn": "2026-08-01" }],
  "notes": [{ "symbol": "NVDA.US", "reason": "max-positions" }],
  "watchlist": [{ "symbol": "EPAM.US", "name": "EPAM Systems", "currency": "USD", "theme": "software", "lastClose": 168.2,
                  "lastDay": "2026-10-30", "breakoutHigh": 190.5, "trendAverage": 181.3, "atr": 4.1,
                  "moves": { "day": -0.012, "week": 0.01, "month": 0.03, "halfYear": 0.08 },
                  "status": "held | breakout | uptrend | overextended | too-volatile | below-trend | too-expensive | stale-data | not-enough-data | no-data" }],
  "owner": { "capital": 10000, "cash": 7120, "invested": 2880, "value": 3105.5, "appliedSequence": 1, "updatedAt": "2026-10-30T18:00:00Z",
             "actions": [ /* as above */ ], "holdings": [ /* as above */ ], "notes": [ /* as above */ ] },
  "refusal": { "sequence": 2, "reason": "capital | holdings | symbol | figures | opened | sequence | unreadable" }
}
```

`owner` is the advice for the owner's real portfolio (absent until he sends one; `value` is the holdings at the last close, null when one has no bars) and `refusal` the newest submission the run refused (absent otherwise). The page sends `POST /api/owner/stocks-portfolio` with `{ clientId, capital, holdings: [{ symbol, name, quantity, averageCost, stop, openedOn }] }` (numbers as decimal strings) and the route creates `{ id: "portfolio-N", value: { sequence: N, submittedAt, capital, holdings, clientId } }`; A trade goes to `POST /api/owner/stocks-trade` with `{ clientId, trade: { kind, symbol, name, quantity, price, fees, day, stop } }` and becomes `{ id: "portfolio-N", value: { sequence: N, submittedAt, trade, clientId } }`. `GET /api/stocks` adds `pending`, the newest whole portfolio the host hasn't seen yet, and `pendingTrades`, the trades after the last one seen, in order; `owner.trades` lists the applied ones, newest first, with each sale's profit.

The route also reads `candidates-latest` (null until the first search):

```json
{
  "asOf": "2026-10-30", "generatedAt": "2026-10-31T02:12:00Z", "status": "done | off | call-budget | data-error | not-ready | advice-behind",
  "regime": "risk-on", "baseCurrency": "USD", "capital": 10000, "listed": 5214, "shortlisted": 40,
  "candidates": [{ "symbol": "ANET.US", "name": "Arista Networks", "venue": "NYSE", "status": "breakout | near-high", "close": 142.2,
                   "breakoutHigh": 140.1, "trendAverage": 112.3, "stop": 133.5, "shares": 14, "momentum": 0.42, "marketCap": 180000000000,
                   "atr": 2.9, "moves": { "day": 0.031, "week": 0.058, "month": 0.112, "halfYear": 0.42 } }]
}
```

And `analysis-latest` in the `analysis` container (null until the analyst's first run):

```json
{
  "asOf": "2026-10-30", "generatedAt": "2026-10-31T02:45:00Z", "status": "done | refused | not-run", "pass": "night | pre-open",
  "reason": "usage-limit | credentials | timeout | failed | unreadable | empty", "market": "…", "marketRu": "…", "dropped": 1,
  "holdings": [{ "symbol": "EPAM.US", "action": "hold | sell-half | sell-all | add", "reason": "…", "reasonRu": "…", "earnings": "2026-11-06" }],
  "picks": [{ "symbol": "ANET.US", "rank": 1, "reason": "…", "reasonRu": "…", "earnings": null }],
  "skips": [{ "symbol": "APP.US", "reason": "…", "reasonRu": "…", "earnings": null }]
}
```

`pass` is the run that wrote it: the night's, or the check before the US open (`pre-open`, between 08:35 New York time and the open on weekdays: the night's view plus news since then); none, from before that check, is the night's. The route adds `current`: whether the analysis is of the advice's close. The page shows Claude's view only then; otherwise it says why the rules' advice stands alone.

Numbers are written as decimal strings (`"6200.5"`), so they reach the database exact; the tab reads both. The host also adds `trades` (the paper fills of that run) and `skipped`, which the tab doesn't show yet. `watchlist` has every watched stock: its last close, the closing high of the 50 sessions before it (`breakoutHigh`: a close above it in an uptrend is the entry signal) and the 200-day average (`trendAverage`); advice written before it existed has none, and the tab says the next run brings it. Watched stocks and candidates also carry the advisor's 20-day ATR (`atr`) and their moves to the last close (`moves`: over 1, 5, 21 and 126 sessions, as fractions), from the bars the run already has; documents from before them have neither, and the page shows gaps. The route adds `working`, the Today tab's table: Claude's picks with the figures of the buy or candidate each one is (or, without Claude's view of this close, the rules' buys and the search's entry signals while risk-on), and for each an entry plan: `buyUpTo` (the close plus half an ATR, the highest price worth paying), `failsUnder` (the 50-day high it broke, or the stop if higher: under it, no buy that day) and, for a stock only near its high, `waitAbove`. Reasons are the advisor's codes (`Cix.Stocks.Reasons`) and the host's (`call-budget`, `data-error`). The tab checks every field, drops what doesn't fit, and marks advice as stale once two weekday closes pass without new advice.
