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

A symbol's currency comes from its exchange (`US` is USD, `XETRA`, `PA`, `AS` and the other euro exchanges are EUR, `SW` is CHF); give it after a colon otherwise, as LSE lines trade in USD, GBP or GBX. Options: `--step` (smallest quantity, 1 for whole shares), the advisor's `--initial-stop-atr`, `--trail-atr`, `--risk` (per trade, as a fraction), `--max-position` (fraction) and `--max-positions`, `--commission`, `--min-commission`, `--fx-fee`, `--slippage`, `--out` (default `backtest-out`), `--cache` (default `.cache/eodhd`). Bars are cached per symbol and range, so a rerun spends no API calls; a range ending within two days of today isn't cached, as its last bar may be partial. The report is `summary.json` (return, CAGR, max drawdown, round trips, win rate, time in the market, for the strategy and the benchmark), `equity.csv` and `trades.csv`.

## Daily run

`Cix.Stocks.Host` runs once a night after the US close (a Container Apps job in `rg-cix-stocks`, set up by `tools/azure/provision-stocks.ps1`, see `docs/azure.md`, "Stocks advisory"), and must start after `dataReadyUtc` (01:00 UTC the next day), when EODHD has the session's bars. It never places an order. The advice is for the paper portfolio, which carries every piece of advice out by itself: the owner, following the advice at the broker, holds the same, and the record shows what following it earns.

1. The night's EODHD calls are reserved in the saved portfolio before any is made (`maxCallsPerRun`, and `maxCallsPerDay` across the UTC day: 18, as the free key allows 20), so a run overlapping another fails instead of spending them twice.
2. Bars for the benchmark, then the held symbols, then the rest of `config/stocks.json`'s watchlist, the longest unfetched first. Bars fetched after the latest session's data was ready are reused from the database, so a repeated or manual run the same night spends no calls. A symbol over budget, or whose fetch fails or comes back empty, keeps its older bars with the note `call-budget` or `data-error`.
3. A split or dividend re-adjusts the history: the run compares the bars with closes saved with the portfolio, on several days (one revised bar isn't an adjustment), and moves the position and the pending advice into the new terms (a split changes the quantity, a dividend is paid in cash; note `adjusted`).
4. The previous close's advice is carried out in the paper portfolio at the next session's open (sells, then stops, then buys), and the stops are checked on every session since, including a held symbol's sessions whose bars came a night late.
5. The advisor runs on the latest close, sized on the portfolio's equity, within the owner's caps: `maxSharePrice` (dearer shares are noted `too-expensive`) and `maxThemeFraction` per watchlist theme (`theme-limit`; a holding the same advice sells still counts that night). A close already advised is never carried out twice.

The run writes to its own database (`cix-stocks`, containers `advice`, `state`, `trades`, `bars`, all with partition key `/id`): the trades and the dated advice (`advice-YYYY-MM-DD`) once each, never changed; the paper portfolio with an ETag, so of two overlapping runs only one saves; then `advice-latest`, which a later run republishes from the dated advice if a failure left it behind. The page shows money to the cent; the portfolio keeps every digit. The EODHD key is read from `/run/secrets/eodhd-api-token` (Key Vault) and is never logged.

Locally, `EODHD_API_TOKEN=... dotnet run --project stocks/src/Cix.Stocks.Host -- --dry-run` keeps everything in memory and prints the advice. It still spends the key's calls, which the nightly run doesn't know about: with the free key, a dry run the same UTC day leaves the job short.

## Dashboard

The dashboard's Stocks tab reads `GET /api/stocks` (`dashboard/api/src/stocks.js`), read only and owner only. The route reads the stocks host's own Cosmos DB account, never the crypto engines' databases, through these app settings: `STOCKS_COSMOS_ENDPOINT`, `STOCKS_COSMOS_READONLY_KEY` and `STOCKS_COSMOS_DATABASE` (default `cix-stocks`). Without them, the tab says the advisor isn't connected. `tools/azure/provision-stocks.ps1` sets them, and `provision.ps1` keeps them when it redeploys the dashboard.

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
  "notes": [{ "symbol": "NVDA.US", "reason": "max-positions" }]
}
```

Numbers are written as decimal strings (`"6200.5"`), so they reach the database exact; the tab reads both. The host also adds `trades` (the paper fills of that run) and `skipped`, which the tab doesn't show yet. Reasons are the advisor's codes (`Cix.Stocks.Reasons`) and the host's (`call-budget`, `data-error`). The tab checks every field, drops what doesn't fit, and marks advice as stale once two weekday closes pass without new advice.
