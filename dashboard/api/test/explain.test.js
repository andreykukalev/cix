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

test('every explanation also comes in Russian, with the terms, levels and the records\' own text kept', () => {
  const { explain: x, GATE_RULES, GATE_RULES_RU } = require('../src/explain');
  const ru = x(ENTRY, 'ru');
  assert.equal(ru.headline, 'Покупка LINK-EUR: у монеты №3 в рейтинге сканера появился сигнал входа');
  assert.deepEqual(ru.points, [
    'Сетап: uptrend pullback',
    'Цена выше своей 20-часовой средней (EMA20) на 0.31× часового диапазона (ATR)',
    'Движение: за последний час +0.22%, за 24 часа +0.38%',
  ]);
  assert.equal(ru.risk.text, 'План риска: stop 9.90 (-2.94%), target 10.80 (+5.88%), около 1.47 EUR убытка, если сработает stop');
  assert.equal(ru.outcome, 'Размещён');
  assert.equal(ru.raw, ENTRY.data.rationale, 'the technical text stays as the engine wrote it');

  const stop = { actor: 'engine', data: { tool: 'protective-exit', symbol: 'UNI-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: 'protective exit UNI-EUR stop: bid 6.10 at or below stop 6.12' } };
  assert.equal(x(stop, 'ru').headline, 'Продажа UNI-EUR: цена опустилась до stop-loss позиции');
  assert.ok(x(stop, 'ru').points.includes('Подробности: bid 6.10 at or below stop 6.12'));

  const refused = { actor: 'trader', data: { tool: 'place_order', symbol: 'SOL-EUR', side: 'buy', status: 'rejected', rule: 'daily-loss', reason: 'Daily loss 5.20 reached.', rationale: 'my own words' } };
  assert.equal(x(refused, 'ru').outcome, 'Отклонён до отправки: дневной лимит убытка остановил новые покупки (Daily loss 5.20 reached.)');
  assert.deepEqual(x(refused, 'ru').points, ['my own words'], "an agent's words are never paraphrased");

  const odd = { actor: 'trader', data: { tool: 'place_order', symbol: 'SOL-EUR', side: 'buy', status: 'rejected', rule: 'constructor', rationale: 'signal exit SOL-EUR constructor: x' } };
  assert.equal(x(odd, 'ru').outcome, 'Отклонён до отправки: constructor', 'only the table\'s own codes are translated');
  assert.equal(x(odd, 'ru').headline, 'Продажа SOL-EUR: сигнал выхода: constructor');
  assert.equal(x(ENTRY, 'xx').headline, x(ENTRY).headline, 'an unknown language is English');
  assert.deepEqual(Object.keys(GATE_RULES_RU).sort(), Object.keys(GATE_RULES).sort(), 'every rule has its Russian');
});
