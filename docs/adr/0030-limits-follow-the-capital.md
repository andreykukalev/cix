# 0030. Limits and strategy sizes follow the capital

- Status: proposed
- Date: 2026-11-08

## Context

The live account holds about 250 EUR, all the owner's money on Revolut X, while the live limits file and swing.v1 were written for 1,000 EUR in fixed amounts: 50 EUR per order and per coin, a 20 EUR daily loss, 40% of the 1,000 EUR allocation in coins. With 250 EUR those amounts are a fifth of the account per order and 8% of it in one bad day, and 40% of 1,000 is more than the account holds.

The owner decided (2026-11-08, thread "Add a more active luck strategy") that no limit or strategy amount should be fixed: they all derive from the money on the account and follow it when he deposits or withdraws; 1,000 EUR stays the ceiling ("макс-макс") even if the account holds more; the scaling is proportional for now (he may choose another law later); and the drawdown limit is 20%. A withdrawal must not count as a drawdown or a loss.

## Decision

**Capital.** The capital is the account's value in cash and coins (the portfolio's equity), never below 0 and never above `AllocationQuote`. It is measured at each check, from the same valuation the check uses.

**Risk limits.** `config/risk-limits*.json` may give three limits as shares of the capital beside their amounts: `MaxOrderFraction`, `MaxPositionFractionPerSymbol` and `MaxDailyLossFraction`. Where both are set the smaller applies, so the amount is the ceiling at 1,000 EUR and the share rules below it. The daily loss share is of the capital at the start of the UTC day, so today's losses don't shrink the limit as they grow. Each share stays within the hard ceilings (`MaxOrderFractionOfAllocation` 25%, `MaxDailyLossFractionOfAllocation` 10%, per coin at most 100%). The drawdown limit was already a share of the peak. With shares set, an account valued at 0 gets no buy (`no-capital`). The risk gate stays final and sells are never limited by the capital.

The live file becomes, on the owner's request: shares 20% per order, 20% per coin, 8% daily loss, drawdown 20%; amounts 200, 200 and 80 (the shares at 1,000 EUR); allocation 1,000 unchanged. At 250 EUR that is at most 50 EUR per order and per coin, 20 EUR daily loss and a 50 EUR drawdown.

**Strategy limits.** A spec may give `orderFraction`, `maxPositionFractionPerSymbol` and `dailyLossStopFraction` beside, or instead of, `orderQuote`, `maxPositionQuotePerSymbol` and `dailyLossStopQuote`, and `scanner.riskPerTradeFraction` beside `scanner.riskPerTradeQuote`; again the smaller applies. `maxExposureFraction` is now a share of the capital, not of the allocation: this changes the running swing.v1 too (40% of 250 is 100 EUR in coins, where it was 400). The strategy gate turns the shares into money from the portfolio it checks; the scanner sizes its signals from the safety monitor's latest valuation (the scan values the account before it ranks); the trim after a strategy switch from the valuation it trims against. A spec that can buy needs `orderQuote` or `orderFraction`.

A falling capital never forces a sale. Caps that shrink with it only limit new buys; positions leave by their stops, targets and trend exits as before. The trim still runs once per switch.

**Deposits and withdrawals.** The reconciliation already halts trading when the holdings change by more than the engine's fills explain ("deposit, withdrawal or outside trading?"), for the owner to look at. It now also moves the drawdown peak and the day's starting equity by that change, erring toward the tighter limit where the timing is unclear: a withdrawal lowers the current peak; a deposit raises the peak recorded with the previous holdings baseline (the minute's valuation may already have counted it), or keeps the current one if higher; on a new UTC day a deposit raises the day's start and a withdrawal leaves it; neither goes to 0. The move is journaled (kind `capital`, with the values before and after) and the halt reason says so, so the owner resumes knowing it. If the change was outside trading or a loss, the owner keeps trading paused. So after the owner resumes, a withdrawal is not a drawdown nor a daily loss, a deposit not a gain, and the limits follow the new capital. A withdrawal larger than 30% of the account may still trip the critical drawdown halt in the minute before the reconciliation sees it; the owner resumes it the same way.

**swing.v2** (draft, for the owner to mark live) is swing.v1 with every amount a share: 0.4% of the capital at risk per trade, at most 10% per order and per coin, 40% in coins, an 8% daily loss stop, and the amounts beside them at their 1,000 EUR value (100 per order, 4 at risk, 100 per coin), which never bind below 1,000 and keep the trader's order-size hook (the largest `orderQuote` of any spec) meaningful.

`get_portfolio` shows the limits' shares and, under `now`, the capital and the limits in money. The dashboard shows the new parameters and the shares in the mandatory limits.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Lower the fixed amounts to fit 250 EUR | The owner asked for none: every deposit or withdrawal would need a new limits file and a new spec version. |
| Shares only, no amounts | The amounts are the owner's explicit ceiling at 1,000 EUR, and the trader's order-size hook reads `orderQuote`. |
| Capital from the allocation only | It is the ceiling, not the money there is; 40% of 1,000 is more than 250. |
| Measure the drawdown on equity net of transfers | Takes a running total of deposits and withdrawals; moving the peak by each transfer does the same with the state the engine already keeps. |
| Move the baselines only when the owner confirms a transfer on resume | Safer against a loss read as a withdrawal, but a resume control and state for it; the halt, the journal entry and the reason text cover it for now. |
| Recognise deposits without halting | The engine can't tell a deposit from outside trading or a mistake; the halt and the owner's look stay. |

## Consequences

- Limits move with the market as well as with deposits: after a loss the next order is a little smaller, after a gain a little larger. Rounded down to the cent.
- swing.v1, still live, gets the capital-based exposure cap; its fixed amounts (50 per order and coin, 20 daily) stay and now sit under the 20% shares at 250 EUR.
- A later scaling law (progressive, stepped) replaces `Scaled` in `RiskLimits` and `StrategyLimits.At`; the specs and files stay as they are.
- An unexplained loss (outside trading, a stolen key) is also moved out of the drawdown and daily loss once the owner resumes; the halt, its reason and the `capital` journal entry are what tell it apart.
- The trader's order-size hook bounds buys by the largest `orderQuote` of any spec: swing.v2's 100 raises it from 55 to 110 EUR. The engine's limits (20% of the capital) stay the real bound.
- The rally, crash and burst plays (the next pull request) size from the same capital.
