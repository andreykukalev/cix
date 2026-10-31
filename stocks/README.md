# CIX stocks advisory

Recommendations for medium-term trading on the owner's Revolut brokerage account, which has no API: CIX says what to buy, where to put the stop and when to raise or sell, and the owner places the orders by hand (ADR-0023).

| Project | What it holds |
| --- | --- |
| `src/Cix.Stocks` | The advisor: market regime, trend entries, stops that only rise, sizing with gap risk and FX. No I/O, no packages; reuses `Cix.Domain`'s candles and ATR. |
| `src/Cix.Stocks.MarketData` | The EODHD client for adjusted daily bars. |
| `src/Cix.Stocks.Backtest` | A console backtest: the advisor replayed day by day through a paper portfolio. |
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

## Dashboard

The dashboard's Stocks tab reads `GET /api/stocks` (`dashboard/api/src/stocks.js`), read only and owner only. The route reads the stocks host's own Cosmos DB account, never the crypto engines' databases, through these app settings: `STOCKS_COSMOS_ENDPOINT`, `STOCKS_COSMOS_READONLY_KEY` and `STOCKS_COSMOS_DATABASE` (default `cix-stocks`). Without them, the tab says the advisor isn't connected. The infrastructure that sets them comes with the host.

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

Reasons are the advisor's codes (`Cix.Stocks.Reasons`). The tab checks every field, drops what doesn't fit, and marks advice older than four days as stale.
