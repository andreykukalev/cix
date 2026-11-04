'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { engineTextRu } = require('../src/engineText');

// One sentence of each format the engine writes (as C# formats them, invariant culture), and what must survive in Russian.
const SENTENCES = [
  // ManualRequestService.cs
  ["The trader didn't answer within 15 minutes.", '15'],
  ['The request was 12 minutes old when the engine saw it (at most 5); not acted on. Send it again if it still applies.', '12'],
  ['At most 20 manual requests a day (ManualRequests:MaxPerDay).', '20'],
  ['Another sell request for SOL-EUR is still in progress.', 'SOL-EUR'],
  ["The active strategy doesn't let the trader place orders, so it can't act on requests.", null],
  ['Received from owner.', null],
  ['Sent to the trader to analyse.', null],
  ["Owner's direct sell.", null],
  ["Owner's direct sell: taking profit", 'taking profit'],
  ['No current price for SOL-EUR.', 'SOL-EUR'],
  ['No current price for SOL-EUR; try again shortly or decline.', 'SOL-EUR'],
  ["The portfolio can't be valued right now.", null],
  ['There is no sellable SOL-EUR position (an open sell order may hold it).', 'SOL-EUR'],
  ['New buys are paused by the market view (risk-off) until 2026-10-27 14:00 UTC.', '2026-10-27 14:00'],
  ['The resulting order size is zero.', null],
  ['portion must be more than 0 and at most what the owner asked (0.5).', '0.5'],
  ['Filled 0.001 at 60010.', '60010'],
  ['The order ended without a fill.', null],
  ['The engine restarted while handling this request; the order is being reconciled.', null],
  ['See the journal.', null],
  ["The order didn't fill at once; cancelled so it can't hold the position.", null],
  ["The engine has no record of how this order ended (it stopped while sending it, or lost track of it). Check the exchange's order history before sending the request again.", null],
  // TradingControlService.cs
  ['Trading halted; 2 open order(s) cancelled.', '2'],
  ["Trading halted; 2 open order(s) cancelled, 1 couldn't be: check the exchange.", '1'],
  ['Trading was already halted; the halt and its reason stay as they were.', null],
  ["Trading wasn't halted; nothing to resume.", null],
  ['Trading resumed. The risk limits still apply, and the automatic checks can halt it again.', null],
  ['The halt changed after the page showed it: A safety limit was reached (drawdown, daily loss or stale market data). Check why on the dashboard, then press Resume again if you still want to.', null],
  ["This halt has no start time, so the engine can't tell it is the one you saw. Resume it with the operator tool resume_trading.", 'resume_trading'],
  ["At most 20 Pause or Resume requests a day reach the engine; this one didn't. To stop live trading now, see docs/live-trading.md.", '20'],
  ['A Resume request needs haltSince: the start of the halt the page showed.', 'haltSince'],
  ['A newer Pause or Resume request replaced this one.', null],
  ['The request was more than 5 minutes old when the engine saw it; press the button again.', null],
  ['Done.', null],
  // AgentRunPlanner.cs, AgentRunScheduler.cs, AnalystRunRequestService.cs (the analyst's Run now)
  ['Analyst run analyst-20261029-220000-3fa2c1 queued; its market view usually arrives within 10 minutes. The next scheduled run is 6 hours after this one.', 'analyst-20261029-220000-3fa2c1'],
  ['The analyst is already queued to run; its view arrives a few minutes after it starts.', null],
  ['The analyst is running now; its view arrives when the run ends.', null],
  ['The analyst was queued 12 min ago; ask again in 18 min (30 min between runs, to spare the Claude limit).', '18'],
  ['You have asked for 8 analyst runs today, the most a day; the scheduled runs go on (AgentRuns:MaxAnalystNowPerDay).', '8'],
  ['The analyst is off on this engine (AgentRuns:Analyst).', 'AgentRuns:Analyst'],
  ["The analyst run couldn't be queued; press the button again in a minute.", null],
  ["This engine doesn't queue the agents' runs (AgentRuns:Mode Off): the analyst runs on its own loop in Docker Compose.", 'Docker Compose'],
  // TradablePairs.cs, TradableSymbols.cs
  ['VVV-EUR is already tradable.', 'VVV-EUR'],
  ['VVV-EUR is not tradable; nothing to remove.', 'VVV-EUR'],
  ['VVV-EUR added. Its prices load now; the scanner can signal it once it has a day of hourly candles, and every order still goes through the risk gate.', 'VVV-EUR'],
  ['VVV-EUR added: it was watched because the account holds it, and now the strategy may buy it too.', 'VVV-EUR'],
  ['VVV-EUR removed: no new orders in it.', 'VVV-EUR'],
  ['Only EUR pairs are permitted by config/risk-limits.json.', 'EUR'],
  ["config/risk-limits.json doesn't permit it; only the owner can widen that list.", null],
  ['Only EUR pairs are permitted by config/risk-limits.live.json.', 'config/risk-limits.live.json'],
  ["config/risk-limits.live.json doesn't permit it; only the owner can widen that list.", 'config/risk-limits.live.json'],
  ['At most 15 pairs can be tradable at once; remove one first.', '15'],
  ['At most 15 pairs can be tradable at once (config/risk-limits.json).', '15'],
  ['Not permitted by config/risk-limits.json: ETH-USD.', 'ETH-USD'],
  ["At most 15 pairs can be tradable at once (the owner's limits file).", '15'],
  ["Not permitted by the owner's limits file: ETH-USD.", 'ETH-USD'],
  ["The exchange couldn't be asked about this pair right now; send the request again later.", null],
  ["Revolut X doesn't list it as an active pair.", null],
  ['Every pair must be quoted in EUR.', 'EUR'],
  ['It traded 24 EUR in the last 24 hours; the running strategy needs at least 100000.', '100000'],
  ['At least one pair must stay tradable.', null],
  ["An order in it was just sent and isn't settled yet; send the request again in a minute.", null],
  ["The portfolio can't be valued right now, so the engine can't tell whether you hold it; send the request again later.", null],
  ['It has an open order; cancel it or let it fill first.', null],
  ['You still hold it (about 12.3 EUR); sell it first.', '12.3'],
  ["A residual of it is still on the account (about 0.06 EUR), below the exchange minimum, so it can't be sold on its own; a pair can be removed only when its balance is zero. To clear it, buy enough to reach the minimum, then sell all.", '0.06'],
  ["The sized order rounds down to zero at the exchange's size step.", null],
  ['A buy needs a stopLoss at least 0.5 % below the ask (60010).', '0.5 %'],
  ['A buy in it is still being settled; send the request again in a minute.', null],
  ['The request was more than 5 minutes old when the engine saw it; send it again.', null],
  ['symbol must be a pair such as SOL-EUR.', null],
  // MarketScanner.cs
  ['Passed every entry rule, but the strategy allows no buy signals.', null],
  ['Passed every entry rule, but the strategy allows 2 buy signal(s) per scan and higher-ranked BTC-EUR, ETH-EUR took them.', 'BTC-EUR, ETH-EUR'],
  ['The strategy buys only in an uptrend (EMA20 above EMA50); the trend is down.', 'EMA50'],
  ['The exchange shows no ask price right now.', null],
  ['Price is 1.2 ATR above EMA20; the strategy buys a pullback, at most 0.5 ATR above it.', '0.5'],
  ["Last hour -0.25%; the strategy waits for an hour that isn't falling.", '-0.25%'],
  ['A stop 1.5 ATR below the ask would be at or below zero.', '1.5'],
  ['The stop (1.5 ATR below the ask) or the target is within 0.5% of the ask; an order needs at least 0.5%.', '0.5%'],
  ['The stop leaves no distance to size the order by.', null],
  ['The sized order, 4.99 EUR (at most 5 per order and 1 at risk), is below the exchange minimum of 10 EUR.', '4.99'],
  ['The sized order, 0.00001 BTC (at most 5 EUR per order and 1 at risk), is below the exchange minimum of 0.0001 BTC.', '0.00001'],
  ['Flagged by the market analyst until 2026-10-27 18:00 UTC.', '2026-10-27 18:00'],
  ['Not among the tradable pairs (the owner adds them from the dashboard, within config/risk-limits.json).', null],
  ["Not among the tradable pairs (the owner adds them from the dashboard, within the owner's limits file).", null],
  ["The exchange doesn't list this pair.", null],
  ['The exchange has suspended trading in this pair.', null],
  ['Market data is stale.', null],
  ['Not enough candles yet for EMA, ATR or spread.', null],
  ['Only 9 hourly candles in the last 24h; 12 needed.', '12'],
  ['24h volume 45,210 is below the minimum 100,000.', '45,210'],
  ['Spread 0.31% is above the maximum 0.2%.', '0.31%'],
  ['Hourly ATR 6.12% is above the maximum 5%.', '6.12%'],
  ['Hourly ATR 0.4% is under 2× the round-trip cost 0.38%.', '0.38%'],
  // RiskGate.cs (through explain.js)
  ['Within all limits.', null],
];

test('every sentence the engine writes for the owner has its Russian, figures kept', () => {
  for (const [en, kept] of SENTENCES) {
    const ru = engineTextRu(en);
    assert.ok(ru, `no Russian for: ${en}`);
    assert.ok(/[а-яё]/i.test(ru), `not Russian: ${ru}`);
    // No English words left, apart from the names, codes and abbreviations kept on purpose.
    const rest = ru.replace(/config\/risk-limits(?:\.live)?\.json|docs\/live-trading\.md|resume_trading|haltSince|ManualRequests:MaxPerDay|AgentRuns:\w+(?: Off)?|analyst-[\d-]+[0-9a-f]+|Docker Compose|Claude|Revolut X|risk-off|taking profit|ask|UTC|ATR|EMA\d*|[A-Z]{2,}-[A-Z]{3}/g, '');
    assert.ok(!/[a-z]{3,}/.test(rest), `English left in: ${ru}`);
    if (kept) assert.ok(ru.includes(kept), `${kept} lost in: ${ru}`);
  }
});

test('the trend and side are said in Russian, and an unknown sentence gets no guess', () => {
  assert.equal(engineTextRu('The strategy buys only in an uptrend (EMA10 above EMA30); the trend is flat.'),
    'Стратегия покупает только в восходящем тренде (EMA10 выше EMA30); сейчас тренд боковой.');
  assert.equal(engineTextRu('Another buy request for SOL-EUR is still in progress.'), 'Другой запрос на покупку SOL-EUR ещё выполняется.');
  assert.equal(engineTextRu('Something the engine says one day.'), null);
  assert.equal(engineTextRu(null), null);
  assert.equal(engineTextRu('   '), null);
});
