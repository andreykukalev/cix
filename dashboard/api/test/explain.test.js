'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { explain } = require('../src/explain');

const own = (e) => explain(e);

const ENTRY = {
  actor: 'trader',
  data: {
    tool: 'place_order', symbol: 'LINK-EUR', side: 'buy', status: 'placed', rule: 'ok', requestedPrice: '10.20', approvedPrice: '10.20',
    stopLoss: '9.90', takeProfit: '10.80',
    rationale: 'signal entry LINK-EUR rank 3: uptrend pullback 0.31 ATR above EMA20, 1h +0.22%, 24h +0.38%; stop 9.90, target 10.80, risk 1.47',
  },
};

test('a scanner entry leads with the reason and lists the evidence in words', () => {
  const x = own(ENTRY);
  assert.equal(x.headline, "Buy LINK-EUR: the scanner's #3 ranked coin had an entry signal");
  assert.deepEqual(x.points, [
    'Setup: uptrend pullback',
    'Price is 0.31× the hourly range (ATR) above its 20-hour average (EMA20)',
    'Moves: last hour +0.22%, last 24 hours +0.38%',
  ]);
  assert.equal(x.risk.text, 'Risk plan: stop 9.90 (-2.94%), target 10.80 (+5.88%), about 1.47 EUR lost if the stop is hit');
  assert.equal(x.outcome, 'Placed');
  assert.equal(x.raw, ENTRY.data.rationale, 'the technical text stays available');
});

test('test entries say what they are', () => {
  const x = own({ ...ENTRY, data: { ...ENTRY.data, rationale: 'signal entry XRP-EUR rank 1: test entry (trend down), -0.40 ATR above EMA20, 1h n/a, 24h -1.20%; stop 1.1, target 1.2, risk 0.05' } });
  assert.match(x.points[0], /^Test entry: .* \(now down\)$/);
  assert.equal(x.points[2], 'Moves: last hour n/a, last 24 hours -1.20%');
});

test('exits name their reason, with the detail', () => {
  const stop = { actor: 'engine', data: { tool: 'protective-exit', symbol: 'UNI-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'protective exit UNI-EUR stop: bid 6.10 at or below stop 6.12' } };
  assert.equal(own(stop).headline, "Sell UNI-EUR: the price fell to the position's stop-loss");
  assert.equal(own(stop).source, 'protective exit');
  assert.ok(own(stop).points.includes('Detail: bid 6.10 at or below stop 6.12'));

  const trend = { actor: 'trader', data: { tool: 'place_order', symbol: 'SOL-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'signal exit SOL-EUR trend-down: EMA20 crossed below EMA50' } };
  assert.match(own(trend).headline, /trend turned down/);
  assert.equal(own(trend).source, 'scanner signal');
});

test('owner requests are told apart from the strategy\'s decisions', () => {
  const sell = { actor: 'owner', data: { tool: 'manual-direct', symbol: 'LINK-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: "Owner's direct sell: lock in gains" } };
  assert.equal(own(sell).source, 'owner request');
  assert.equal(own(sell).headline, 'Sell LINK-EUR: your direct sell request');
  assert.equal(own(sell).points[0], 'Your note: lock in gains');
  assert.equal(own({ ...sell, data: { ...sell.data, rationale: "Owner's direct sell." } }).points[0], 'Requested from the dashboard; the engine sells at the bid, without an agent run');

  const agent = own({ actor: 'trader-requests', data: { tool: 'manual-agent', symbol: 'UNI-EUR', side: 'buy', status: 'placed', rule: 'ok', stopLoss: '6', takeProfit: '6.6', approvedPrice: '6.24', rationale: 'Uptrend intact; buying half.' } });
  assert.equal(agent.headline, 'Buy UNI-EUR: the trader agreed to your request');
  assert.deepEqual(agent.points, ["Trader's reasoning: Uptrend intact; buying half."]);
  assert.match(agent.risk.text, /^Risk plan: stop 6 \(-3\.85%\), target 6\.6 \(\+5\.77%\)$/);
});

test('the risk gate\'s verdict is in words, with its own reason', () => {
  const rejected = { actor: 'trader', data: { tool: 'place_order', symbol: 'LINK-EUR', side: 'buy', status: 'rejected', rule: 'price-band', reason: 'Limit price is 2.10% from the reference price; the maximum is 1.00%.', rationale: 'whatever' } };
  assert.equal(own(rejected).outcome, 'Refused before sending: the limit price was too far from the market (Limit price is 2.10% from the reference price; the maximum is 1.00%.)');
  const resized = own({ actor: 'trader', data: { side: 'buy', status: 'placed', rule: 'max-order', reason: 'r' } });
  assert.equal(resized.outcome, 'Placed, capped at the per-order limit (r)');
  assert.equal(own({ data: { status: 'rejected', rule: 'new-rule' } }).outcome, 'Refused before sending: new-rule');
});

test('words it doesn\'t recognise are shown as written, never paraphrased', () => {
  const x = own({ actor: 'trader', data: { tool: 'place_order', symbol: 'ADA-EUR', side: 'buy', status: 'placed', rule: 'ok', rationale: 'Breakout above the range.' } });
  assert.equal(x.headline, 'Buy ADA-EUR');
  assert.deepEqual(x.points, ['Breakout above the range.']);
  assert.equal(x.risk, null);
  const cancel = own({ actor: 'trader', data: { tool: 'cancel_order', status: 'notfound', rationale: 'stale at 1.234' } });
  assert.equal(cancel.headline, 'Cancel an open order');
  assert.equal(cancel.outcome, 'Nothing to cancel: it had already filled or been cancelled');
  assert.deepEqual(cancel.points, ['stale at 1.234']);
});

test('every explanation also comes in Russian, in the dashboard\'s terms, with the levels and the records\' own text kept', () => {
  const { explain: x, GATE_RULES, GATE_RULES_RU } = require('../src/explain');
  const ru = x(ENTRY, 'ru');
  assert.equal(ru.headline, 'Покупка LINK-EUR: у монеты №3 в рейтинге сканера появился сигнал на вход');
  assert.deepEqual(ru.points, [
    'Сетап: откат в восходящем тренде',
    'Цена выше своей 20-часовой средней (EMA20) на 0.31× часового диапазона (ATR)',
    'Движение: за последний час +0.22%, за 24 часа +0.38%',
  ]);
  assert.equal(ru.risk.text, 'План риска: стоп-лосс 9.90 (-2.94%), тейк-профит 10.80 (+5.88%), около 1.47 EUR убытка, если сработает стоп-лосс');
  assert.equal(ru.outcome, 'Размещён');
  assert.equal(ru.raw, ENTRY.data.rationale, 'the technical text stays as the engine wrote it');

  const stop = { actor: 'engine', data: { tool: 'protective-exit', symbol: 'UNI-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'protective exit UNI-EUR stop: bid 6.10 at or below stop 6.12' } };
  assert.equal(x(stop, 'ru').headline, 'Продажа UNI-EUR: цена опустилась до стоп-лосса позиции');
  assert.ok(x(stop, 'ru').points.includes('Подробности: bid 6.10 на уровне стоп-лосса 6.12 или ниже'));
  const trend = { actor: 'engine', data: { tool: 'protective-exit', symbol: 'UNI-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'signal exit UNI-EUR trend-down: the fast EMA crossed below the slow EMA' } };
  assert.ok(x(trend, 'ru').points.includes('Подробности: быстрая EMA пересекла медленную сверху вниз'));

  // The gate's reason in Russian when its format is known; else the rule alone, never English inside the Russian.
  const refused = { actor: 'trader', data: { tool: 'place_order', symbol: 'SOL-EUR', side: 'buy', status: 'rejected', rule: 'daily-loss', reason: 'The daily loss limit is reached; new buys are halted until tomorrow (UTC).', rationale: 'my own words' } };
  assert.equal(x(refused, 'ru').outcome, 'Отклонён до отправки: дневной лимит убытка остановил новые покупки (Достигнут дневной лимит убытка: новые покупки остановлены до завтра (UTC).)');
  assert.equal(x(refused).outcome, 'Refused before sending: the daily loss limit halts new buys (The daily loss limit is reached; new buys are halted until tomorrow (UTC).)');
  const unknown = { ...refused, data: { ...refused.data, reason: 'Daily loss 5.20 reached.' } };
  assert.equal(x(unknown, 'ru').outcome, 'Отклонён до отправки: дневной лимит убытка остановил новые покупки');
  assert.deepEqual(x(refused, 'ru').points, ['my own words'], "an agent's words are never paraphrased");
  assert.equal(x(refused, 'ru').raw, null, 'shown once: no second copy under Technical');

  const odd = { actor: 'trader', data: { tool: 'place_order', symbol: 'SOL-EUR', side: 'buy', status: 'rejected', rule: 'constructor', rationale: 'signal exit SOL-EUR constructor: x' } };
  assert.equal(x(odd, 'ru').outcome, 'Отклонён до отправки: constructor', 'only the table\'s own codes are translated');
  assert.equal(x(odd, 'ru').headline, 'Продажа SOL-EUR: сигнал на выход: constructor');
  assert.equal(x(ENTRY, 'xx').headline, x(ENTRY).headline, 'an unknown language is English');
  assert.deepEqual(Object.keys(GATE_RULES_RU).sort(), Object.keys(GATE_RULES).sort(), 'every rule has its Russian');
});

test("an agent's Russian rationale is shown in Russian, its English in English, each once", () => {
  const { explain: x } = require('../src/explain');
  const agreed = { actor: 'trader-requests', data: { tool: 'manual-agent', symbol: 'ETH-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'Trend turned down; selling half.', rationaleRu: 'Тренд развернулся вниз; продаю половину.' } };
  assert.deepEqual(x(agreed, 'ru').points, ['Обоснование трейдера: Тренд развернулся вниз; продаю половину.']);
  assert.deepEqual(x(agreed).points, ["Trader's reasoning: Trend turned down; selling half."]);
  assert.equal(x(agreed, 'ru').raw, 'Trend turned down; selling half.', 'the English behind the Russian stays one click away');
  assert.equal(x(agreed).raw, null, 'in English it is shown once');
  const free = { actor: 'trader', data: { tool: 'place_order', symbol: 'ETH-EUR', side: 'buy', status: 'placed', rule: 'ok', rationale: 'Breakout with volume.', rationaleRu: '  ' } };
  assert.deepEqual(x(free, 'ru').points, ['Breakout with volume.'], 'no Russian: the English as written');
});

test('every reason the gate, the strategy and the execution service write has its Russian', () => {
  const { reasonRu } = require('../src/explain');
  const reasons = [ // one of each format in RiskGate.cs, StrategyGate.cs and ExecutionService.cs
    'The kill switch is engaged; no orders are allowed.',
    'SOL-EUR is not in the allowed symbols.', // before ADR-0021, still in the journal
    'SOL-EUR is not among the watched pairs.',
    "SOL-EUR hasn't been scanned yet; the strategy's filters decide once it has.",
    'SOL-EUR is filtered out by the strategy (low-volume): 24h volume 1,200 is below the minimum 100,000.',
    "The latest scan is from 2026-10-28 17:00:00Z, too old for the strategy's filters; buys wait for a fresh one.",
    "The market scanner isn't running, so no coin passes the strategy's filters.",
    "SOL-EUR is watched because the account holds it, but the owner hasn't added it; the strategy buys it once the owner adds it on the Coins tab.",
    'Size and limit price must be positive and the client order ID set.',
    'SOL-EUR is not tradable on the exchange.',
    'Size must be a multiple of 0.0001 and price of 0.01.',
    'No valid market data for this symbol.',
    'Market data is too old (or from the future) to price this order.',
    'Limit price is 2.50 % from the reference price; the maximum is 2.00 %.', // .NET's P2 in the invariant culture: a space
    'Limit price is 2.50% from the reference price; the maximum is 2.00%.',
    'At most 4 orders per minute.',
    'Order reduced to the exchange maximum of 200.',
    'After position-cap, the order is below the exchange minimum (0.0001 base, 1 quote).',
    'The order is below the exchange minimum (0.0001 base, 1 quote).',
    'Spot only: there is no position to sell.',
    'The position is too small to sell at this price.',
    'Sell reduced to the current position size.',
    'Drawdown is 15.20 %; new buys are halted until the owner resumes trading.',
    'Drawdown is 15.20 %; new buys are refused while it stays at or above the limit.',
    "An owner's sell must be a sell.",
    'The engine is comparing the account with the exchange after its start; orders wait for that, about a minute.',
    'The daily loss limit is reached; new buys are halted until tomorrow (UTC).',
    'No room left under allocation-cap.',
    'Remaining room under max-order is too small for one unit.',
    'Order reduced to 12.5 quote to stay within position-cap.',
    'Within all limits.',
    "Within the strategy's limits.",
    "Sells aren't limited by the strategy.",
    "Today's loss has reached the strategy's daily stop of 6; no new buys until tomorrow (UTC).",
    'This run already placed 1 buy(s); the strategy allows 1 per run.',
    'The strategy waits 60 minutes between buys; the last was 12 minutes ago.',
    "No room left under the strategy's total exposure cap of 100.",
    "The room left under the strategy's cap of 20 per coin is too small for one unit.",
    "Order reduced to 7.5 to stay within the strategy's order size of 10.",
    'An earlier order with this client order ID has an unknown outcome; it must be reconciled first.',
    'This client order ID was already sent; use a new one for a new order.',
    "The exchange's trading rules could not be loaded.",
    "The exchange doesn't list SOL-EUR.",
    'The engine could not record the order before sending it; nothing was sent.',
    "This engine doesn't hold the engine lease for its trading mode (another engine may be trading); nothing was sent.",
    'The order would have filled at once, so the post-only order was rejected.',
    'The exchange may or may not have the order; it counts as open until reconciliation finds out.',
    'The exchange refused the order.',
  ];
  for (const r of reasons) {
    const ru = reasonRu(r);
    assert.ok(ru && !/[a-z]{4,}/.test(ru.replace(/post-only|SOL-EUR|UTC/g, '')), `${r} → ${ru}`);
  }
  assert.equal(reasonRu('Something new.'), null);
});
