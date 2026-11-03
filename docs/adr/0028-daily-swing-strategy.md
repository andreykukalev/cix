# 0028. A daily swing strategy, and the only one offered

- Status: proposed
- Date: 2026-11-03

## Context

In the first week of live and paper trading (from 2026-10-28) the engine made no buy. The hourly presets (Safe v3, Balanced v3, Risky v4) require a coin's hourly ATR to be at least 7 times its round-trip cost; BTC-EUR moved 0.5% an hour against the 2% that asks for. Backtests on Revolut X data (thread "Find why CIX never buys", results in the project files under `no-buys-2026-11-03/`) found no hourly setting with an edge after costs: loosening the filter, maker entries and USD pairs all lost or stayed at noise, and even with zero spread and slippage the hourly entries lost. Hourly moves are too close to the costs; switching exchange would not change that.

The same research found that daily candles do have an edge. A breakout strategy on daily candles was positive out of sample in 81% of 1,152 configurations. Its central configuration, swing.v1, made +5.6% a year from 2024 to November 2026 on 1,000 EUR (worst drawdown 8.3%), +38 EUR in the last 11 months while BTC fell 17%, about three trades a month, held for about three weeks. Every neighbouring configuration was positive in both periods. Caveats: a modest return, today's listed coins only (survivorship), USDT prices for the long history, and months without a trade when BTC is weak.

The owner decided (2026-11-03) to build swing.v1 for live with a 1,000 EUR allocation, to offer only that strategy on the dashboard while keeping the switch, and to drop paper trading (a separate change).

## Decision

The scanner gets a daily mode, chosen by a spec's top-level `timeframe` (`1h`, the default, or `1d`). In daily mode the same filters, ranking, sizing and signals work on daily candles, with these rules:

- **Entry:** the last daily close is above the highest high of the `breakoutCandles` (20) days before it, in a daily uptrend (EMA 10 above EMA 50, close above EMA 50), and the ask is at most `entryMaxAboveBreakoutAtr` (0.5) daily ATRs above that close, so a late buy doesn't chase, and still above the high it broke.
- **Regime:** new buys only while BTC (in the coin's quote currency) closed above its `regimeEmaPeriod` (100) day EMA, whatever `requireUptrendPullback` says. Without fresh BTC daily candles nothing is bought, so BTC must be watched.
- **No buy-back:** a coin the engine sold (a fill in the journal) after its last daily candle began isn't bought again until the next daily close, so a stop-out isn't bought back on the same signal. If the journal can't be read, the scan offers no buys.
- **Stop and target:** the stop is `stopAtr` (3) daily ATRs below the ask; `rewardRisk` 0 sets no take-profit, which the signals, `place_order` and the plans already allowed to be absent.
- **Trailing stop:** each scan, a held position's stop rises to the highest daily close since its buy filled, less `trailAtr` (3) daily ATRs, but never above 0.5% under the bid, so a raise alone never sells (when the bid is already under the trail, the stop follows it 0.5% below, and the next small fall sells, close to the backtest's sale at the next open). The engine writes the raise to the position plan in the engine state (only upward, only for a filled plan with a stop) and journals it (`stop-raised`). A further buy into the coin can't lower it: a plan keeps the higher of its stop and the new buy's. No raise while trading is halted, so a Pause also freezes a trail the owner doesn't trust; an automatic halt (drawdown, stale data) freezes it too, which keeps the last stop armed but locks in no more of a gain until trading resumes. It places no order: `ProtectionService` arms the new stop at its next plan refresh and sells when the bid reaches it, through the risk gate, as with any stop.
- **Trend exit:** a daily close below the slow EMA (50).
- **Ranking:** the return over the breakout window in daily ATRs.

The market data poller fetches daily candles for every watched coin every 20 minutes (330 days on first load; the interval is capped at 2 hours, since a daily candle over 27 hours old is stale). The daily EMAs are capped at 150 days, so the 320 candles the scan reads let even the longest settle. Revolut X starts its daily candles at London midnight, so the day of a clock change lasts 23 or 25 hours: the next candle is looked for an hour early, and a candle counts as closed once a later one has begun or 25 hours have passed.

That daily-candle behaviour is an assumption from the backtest's data, not yet confirmed by the spike: that `interval=1440` is accepted, that days start at London midnight, that a `since` off the day boundary returns candles from it, and how a 330-day first load pages. If a day started elsewhere the rules still work (a candle closes when the next begins); if a first load came back short, coins would show "insufficient history" and no buy would happen, which the Coins tab shows.

`strategies/swing.v1.json` is the strategy, as a draft the owner marks live. The dashboard's Strategy tab offers only `swing` (the newest version marked live, plus the strategy running); the other specs, the switch and its checks stay as they are, and adding a name to the offered list brings one back.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Loosen the hourly filters | The backtest lost money with a looser cost filter (Balanced −11%, Risky −28% over 11 months at ratio 3). |
| Another exchange with more volume | The costs on Revolut X (0% maker, 0.09% taker) are among the lowest; the hourly moves are the same everywhere, and even zero spread and slippage lost on hourly entries. |
| 4-hour candles | Weaker than daily in the same grid: about zero over the last 11 months. |
| Hold BTC long term, with the bot on a small part | Larger returns over 2020-2026 but drawdowns of 50-77%; the owner declined money locked in a fall. |
| A separate scanner class for daily | Two copies of the filters, ranking, signals and the strategy-switch preview; the daily rules share them instead and differ only where the timeframe does. |
| Trailing stops as exchange-side orders | Not confirmed on Revolut X (ADR-0020); the engine's protection already sells stops on every quote. |

## Consequences

- A daily strategy reacts to a day's close, not an hour's: entries come a few times a month and may be days apart, and a breakout signal stands for the whole day after the close. Months without trades are expected while BTC is below its EMA 100.
- The trailing stop moves money decisions into the engine. A raise stays 0.5% under the bid, so where the backtest sold at the open after a close below the new stop, the engine sells once the price falls to it: a little later, and a single bad daily print can tighten a stop but not force a sale by itself. Raises are journaled, so each exit can be traced to the level that caused it. A position whose plan the engine state lost (the journal fallback) keeps its buy's stop and isn't trailed.
- The live limits already fit (`config/risk-limits.live.json`, 2026-11-03: 1,000 EUR allocation, 50 EUR per order and per coin, 20 EUR daily loss). With 4 EUR at risk per trade and stops 3 ATRs away, most positions are 25-50 EUR, and the 10% drawdown halt (100 EUR) sits above the backtest's worst drawdown (83 EUR).
- To run it, the owner marks swing.v1 live, approves the deploy, switches the live strategy on the dashboard, and watches BTC-EUR, ETH-EUR, XRP-EUR, SOL-EUR, PEPE-EUR, NEAR-EUR, ADA-EUR, DOGE-EUR, SUI-EUR and HBAR-EUR on the Coins tab. If the prod `Strategy__Name` and `Strategy__Version` still name balanced v3, they keep working while it stays marked live.
- Hourly strategies keep their behaviour: `timeframe` defaults to `1h`, and `regimeEmaPeriod` and `trailAtr` are refused outside daily mode (`breakoutCandles` and `entryMaxAboveBreakoutAtr` are read only by daily mode). Two changes reach them too: `rewardRisk` 0 (no target) is now allowed, and a further buy into a held coin keeps the higher of the two stops, so it can no longer lower a stop.
- Not yet explained on the dashboard: the `stop-raised` journal entries appear in the Journal tab's full list as the engine wrote them, without a filter or Russian text of their own.
