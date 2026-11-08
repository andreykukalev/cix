# 0031. Plays beside the swing: bursts and BTC events, judged on Binance's candles

- Status: proposed
- Date: 2026-11-08

## Context

swing.v1 has bought nothing since it went live: its daily breakout needs BTC above its 100-day EMA and a coin breaking a 20-day high, which may not happen for weeks. The owner asked (2026-11-08, thread "Add a more active luck strategy") for more activity with an element of luck, "не казино", from chart patterns that raise the odds, and then for one consolidated strategy: the swing as the base, plus modes that switch on by indicators for coin volume bursts and for sharp BTC rallies and crashes. Binance is fine as a data source; orders stay on Revolut X.

The research (`/mnt/project-files/luck-strategy/results.md`, `market-events.txt`):

- Buying dips (RSI oversold, a 24-hour drop, daily RSI2) loses in every variant, and random entries lose the costs.
- A **volume burst** works: an hourly close above the highest high of the 96 hours before it, on at least four times the average hourly volume of the 168 hours before it, while BTC's daily close is above its EMA100; a stop 4 hourly ATR below, sold after 72 hours, no target. All 81 neighbouring settings were positive in 2023-24 and 2025-26. The volume must be Binance's: Revolut X's EUR volume is too thin to read a burst from. The Binance signal at Revolut X's EUR prices made +2.5 to 3.3% a trade over 12 months, about 2-3 trades a week, only while BTC is above its EMA100. Without the BTC filter it loses.
- A **BTC rally** (BTC +13% over three days, buy a basket of six large coins, hold 30 days, stop 12%): 19 events, +18.5% on average. A **BTC crash** (BTC -15% over three days, the same basket, hold 14 days, stop 25%): 9 events, +5.8% on average; a 15% stop kills it.
- Binance's hourly moves don't lead Revolut X's (correlation about 0), so the signal is the volume and the BTC trend, not a price lag.

The owner fixed the sizes on 2026-11-08: bursts 4 × 6% of the capital, the basket 6 × 6%, and a loss stop for the new modes at 18% of the capital. The capital and its shares are ADR-0030's.

## Decision

**Plays.** A spec may give `parameters.plays` beside its own rules: `burst`, `rally` and `crash`, each optional, plus `lossStopFraction` and `mainExposureFraction`. A play is not a separate strategy or budget: its buys are signals in the same scan, placed by the same trader through `place_order`, the strategy gate and the risk gate, in the same account.

**Binance's candles, for signals only.** `BinanceFeed` reads the public `https://data-api.binance.vision/api/v3/klines` (no key, no account, GET only): closed hourly candles of each watched coin's `BASE`USDT pair (300 kept) and BTCUSDT's daily candles (200 kept). A series is read once a newer candle has closed, at most five requests every 15 seconds; a coin Binance doesn't list is asked again a day later, a failure two minutes later; nothing is read unless the running strategy has plays. The candle still forming is never kept. Binance's prices never price an order: every buy is at Revolut X's ask, every sell at its bid, and a coin without a fresh Revolut X quote, or with a spread wider than the strategy's `scanner.maxSpreadFraction`, is not bought.

**Burst.** The burst rule above, on the hour that closed within the last hour; at most `maxPositions` burst positions, the largest volume multiple first. **Rally and crash.** On the first daily close where BTC's move over `moveDays` crosses `btcMoveFraction`, buy each coin of `basket` within the following day; one BTC event at a time, so a rally's positions block a crash's buys and the other way round; a coin sold since that close isn't bought again for it. Each play buys `orderFraction` of the capital per coin. A burst isn't bought when Binance's USDT close is not 0.8 to 1.5 times Revolut X's EUR mid: the ticker may name another coin on Binance, or a price is wrong. Candles with prices or volumes no market has are refused whole. A play's buy still on the book counts toward its play's limits. Holds are at most 30 days.

**Exits.** A play's buy carries its play and a time to sell by (`exitBy`): the burst's `holdHours`, the event's `holdDays`. The trader buys with the signal's stop; `place_order` tags a buy of a coin with a play signal in a scan from the last hour, and refuses one larger than the signal's size or without its stop (or a higher one), so a tagged position always has a real stop. The position plan keeps the tag and the time, and so does the journal, which the plans fall back to. The engine sells a play's position at its stop (protection, on every quote) or when its time is up (the scan's protective exits, reason `time`), never on the strategy's trend exit, and its stop is never trailed. Plays have no take-profit.

**Room for each.** The strategy's own entries stop once the coins it bought itself (not the plays') and its open buys reach `mainExposureFraction` of the capital: the scan offers none, and the strategy filter refuses any other buy that isn't a play's. The spec's `maxExposureFraction` caps everything together in the strategy gate. A play's signal passes the strategy's coin filters (liquidity, volatility, costs on Revolut X), not the spread limit, the avoid list or the gates. The strategy's own signal for a coin wins over a play's.

**Pauses.** No play buys while trading is halted, a risk-off view or another strategy limit blocks entries, the capital isn't valued, the engine has no Binance feed, or the **loss stop** holds: the plays bought in the last 60 days have lost `lossStopFraction` of the capital: their buys and fees against their sells, matched first in, first out per coin (a sale of a coin bought before the window counts for nothing), plus the value of what is still held of them. A journal that can't be read stops the plays too. An analyst's avoid flag holds for plays as for the strategy. `scan_market` shows each play's state (`waiting`, `signal`, `full`, `paused`) and why.

**swing.v3** (draft, for the owner to mark live) is swing.v2 with the plays at the owner's sizes: bursts 6% × 4 (96 h high, 4× the 168 h volume, BTC above EMA100, stop 4 hourly ATR, 72 h), rally and crash 6% per coin of BTC, ETH, SOL, XRP, DOGE and ADA (+13% / 30 days / stop 12%, -15% / 14 days / stop 25%, both over three days), the loss stop at 18%, the swing's own coins at most 40% and all coins at most 100% of the capital, and up to five new positions per run. It fits the live limits file unchanged (20% per order and coin, 8% daily loss).

## Alternatives considered

| Option | Why not |
| --- | --- |
| A separate strategy or budget for the plays | One spec runs at a time (ADR-0015), and the owner asked for one consolidated strategy; shares of one capital do the budgeting. |
| Revolut X's own volume for the burst | Too thin: as a signal it is noise in the backtest. |
| Trade on Binance | Out of scope: the owner's money is on Revolut X; Binance is a data source only. |
| Buy dips or oversold coins | Lost in every variant tested. |
| Let the engine place the play buys itself | Every buy so far goes through the trader and `place_order`; the plays keep that path, and the engine only sells (stops and times), as it does for the swing. |
| A loss stop per play | One stop for all plays is what the owner chose; the journal's fills give it without new state. |

## Consequences

- More trades only while BTC is above its EMA100 for bursts (about 5 of the last 12 months); BTC events come a few times a year, none in 2025. Months without a play are still normal.
- A burst signal lasts the hour after its candle closes; a trader run that comes later misses it. The trader runs on new signals with a 15-minute gap, so this is rare.
- With every play full the account can be all in coins (swing 40%, bursts 24%, a basket 36%); the risk gate's per-order, per-coin and daily-loss shares still bound each buy, and the loss stop the plays together.
- A crash basket's 25% stops can lose more than the 8% daily loss limit in one day across six coins; the limit then stops new buys, it never forces a sale.
- The loss stop counts play fills from their `play` tag in the journal; fills from before this change have none.
- Binance's availability only affects the plays: without it they wait, and the swing and every exit go on.
