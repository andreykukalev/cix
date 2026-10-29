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
