'use strict';
// The page's view logic (dashboard/app/view.js), which runs in the browser, tested here in Node.
const test = require('node:test');
const assert = require('node:assert/strict');
const V = require('../../app/view.js');

const ID = '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f';

test('moves read as an arrow, a signed percentage and words', () => {
  assert.deepEqual(V.change(0.0123, '24 hours'), { text: '▲ +1.23%', dir: 'up', label: 'up 1.23% over 24 hours' });
  assert.deepEqual(V.change(-0.152, '24 hours'), { text: '▼ -15.2%', dir: 'down', label: 'down 15.20% over 24 hours' });
  assert.equal(V.change(0.0004, '1 hour').dir, 'flat');
  assert.equal(V.change(0.0004, '1 hour').text, '■ 0.0%');
  assert.equal(V.change(null, '1 hour').dir, 'none');
  assert.equal(V.change('x', '1 hour').text, '–');
});

test('coins: the scan\'s coins plus anything held, held first, then by rank', () => {
  const rows = V.coins({
    briefing: { market: [
      { symbol: 'ADA-EUR', rank: 2, return1h: 0.01, return24h: -0.02 },
      { symbol: 'BTC-EUR', rank: 1, return1h: 0, return24h: 0.05, signal: 'buy' },
      { symbol: 'LINK-EUR', rank: 5, return1h: -0.003, return24h: 0.0003 },
      { symbol: 'bad', rank: 0 },
    ] },
    portfolio: { holdings: [{ symbol: 'LINK-EUR', valueQuote: '12.5' }, { symbol: 'UNI-EUR', valueQuote: '6' }] },
  });
  assert.deepEqual(rows.map((r) => r.symbol), ['LINK-EUR', 'UNI-EUR', 'BTC-EUR', 'ADA-EUR']);
  assert.equal(rows[0].held, true);
  assert.equal(rows[0].day.dir, 'flat');
  assert.equal(rows[1].inScan, false);
  assert.equal(rows[2].held, false);
  assert.equal(rows[2].quote, 'EUR');
  assert.deepEqual(V.coins({}), []);
});

test('a sell request: portion and mode as chosen, paper, a note cleaned', () => {
  const r = V.requestBody({ side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.5', note: ' take\nprofit ' }, ID);
  assert.deepEqual(r.body, { id: ID, side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.5', note: 'take profit', tradingMode: 'paper' });
  assert.match(V.describe(r.body), /^Sell half of your LINK-EUR position \(paper\)\. The engine sells at the bid now/);
  assert.match(V.describe({ ...r.body, mode: 'agent', portion: '1' }), /^Sell all .* The trader analyses it first/);
  assert.ok(V.requestBody({ side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.3' }, ID).error);
  assert.ok(V.requestBody({ side: 'sell', mode: 'now', symbol: 'LINK-EUR', portion: '1' }, ID).error);
});

test('a buy request always goes to the trader, for an amount in the quote currency', () => {
  const r = V.requestBody({ side: 'buy', mode: 'direct', symbol: 'UNI-EUR', quoteAmount: '25,5' }, ID);
  assert.deepEqual(r.body, { id: ID, side: 'buy', mode: 'agent', symbol: 'UNI-EUR', quoteAmount: '25.5', note: undefined, tradingMode: 'paper' });
  assert.match(V.describe(r.body), /^Ask the trader to buy UNI-EUR for 25.5 EUR \(paper\)/);
  for (const amount of ['', '0', '-5', 'ten', '1001', '1.234']) {
    assert.ok(V.requestBody({ side: 'buy', symbol: 'UNI-EUR', quoteAmount: amount }, ID).error, amount);
  }
  assert.ok(V.requestBody({ side: 'hold', symbol: 'UNI-EUR' }, ID).error);
  assert.ok(V.requestBody({ side: 'buy', symbol: '<b>', quoteAmount: '5' }, ID).error);
});

test('every engine status has words, and only final ones end the wait', () => {
  for (const status of ['new', 'awaiting-agent', 'executing', 'placed', 'unconfirmed', 'filled', 'closed', 'declined', 'rejected', 'failed', 'invalid', 'duplicate', 'expired']) {
    assert.ok(V.STATUS[status], status);
  }
  const done = V.requestState({ status: 'filled', final: true, outcome: { filledSize: '0.5', averagePrice: '10.2' } });
  assert.deepEqual(done, { label: 'Filled', tone: 'ok', final: true, detail: null, filled: '0.5 at 10.2' });
  const declined = V.requestState({ status: 'declined', final: true, outcome: { rationale: 'Trend still up.' } });
  assert.equal(declined.detail, 'Trend still up.');
  assert.equal(V.requestState({ status: 'awaiting-agent', final: false }).tone, 'pending');
  assert.equal(V.what({ side: 'sell', portion: '0.25', symbol: 'LINK-EUR' }), 'Sell 25% LINK-EUR');
  assert.equal(V.what({ side: 'buy', quoteAmount: '20', symbol: 'UNI-EUR' }), 'Buy UNI-EUR for 20 EUR');
});

test('the chart stacks sells on buys, scaled to the busiest period', () => {
  const g = V.chart([
    { label: 'a', from: '2026-10-13', trades: 2, buyVolume: '30.00', sellVolume: '10.00', turnover: '40.00' },
    { label: 'b', trades: 0, buyVolume: '0.00', sellVolume: '0.00', turnover: '0.00' },
    { label: 'c', trades: 1, buyVolume: '0.00', sellVolume: '20.00', turnover: '20.00' },
  ], { width: 300, height: 100, gap: 0 });
  assert.equal(g.max, 40);
  assert.deepEqual(g.bars[0].buy, { y: 25, height: 75 });
  assert.deepEqual(g.bars[0].sell, { y: 0, height: 25 });
  assert.equal(g.bars[1].buy.height, 0);
  assert.deepEqual(g.bars[2].sell, { y: 50, height: 50 });
  assert.equal(g.bars[2].x, 200);
  assert.equal(g.bars[0].title, 'a: 2 trades, bought 30.00, sold 10.00');
  assert.equal(V.chart([]).bars.length, 0);
  assert.equal(V.tick('2026-10-19', 'week'), '19 Oct');
  assert.equal(V.tick('2026-10-01', 'month'), 'Oct');
  assert.equal(V.tick('2026-01-01', 'month'), 'Jan 2026');
  assert.equal(V.tick('bad', 'day'), '');
});

test('this period against the previous one', () => {
  assert.deepEqual(V.versus('15', '10'), { text: '▲ +50% vs previous', dir: 'up' });
  assert.deepEqual(V.versus('5', '10'), { text: '▼ -50% vs previous', dir: 'down' });
  assert.equal(V.versus('0', '0').text, 'same as before');
  assert.equal(V.versus('3', '0').text, 'none before');
});

test('the header names whoever is signed in', () => {
  assert.deepEqual(V.viewer({ clientPrincipal: { identityProvider: 'aad', userDetails: 'me@example.com', userRoles: ['anonymous', 'authenticated', 'owner'] } }), { signedIn: true, name: 'me@example.com' });
  assert.deepEqual(V.viewer({ clientPrincipal: null }), { signedIn: false, name: null });
  assert.equal(V.viewer(null).signedIn, false);
});

test('requests pause, with a reason, when the engine can\'t take them', () => {
  const ok = { state: 'running', manualRequests: { state: 'ok' } };
  assert.equal(V.requestsBlocked(ok), null);
  assert.match(V.requestsBlocked({ ...ok, state: 'stale' }), /isn't running/);
  assert.match(V.requestsBlocked({ ...ok, manualRequests: { state: 'off' } }), /switched off/);
  assert.match(V.requestsBlocked({ ...ok, manualRequests: { state: 'unavailable' } }), /can't read requests/);
  assert.match(V.requestsBlocked({ state: 'running', manualRequests: null }), /doesn't report/);
  assert.ok(V.requestsBlocked(undefined));
});

test('a refusal names its rule: the engine refuses some, the risk gate others', () => {
  assert.equal(V.requestState({ status: 'rejected', final: true, outcome: { rule: 'daily-cap', reason: 'At most 20 a day.' } }).label, 'Refused (daily-cap)');
  assert.equal(V.requestState({ status: 'rejected', final: true, outcome: {} }).label, 'Refused');
});

test('strategy parameters read in words, with their range and whose limit they are', () => {
  assert.equal(V.paramValue(0.25, { unit: 'fraction' }), '25%');
  assert.equal(V.paramValue(0.003, { unit: 'fraction' }), '0.3%');
  assert.equal(V.paramValue(240, { unit: 'minutes' }), '4 h');
  assert.equal(V.paramValue(15, { unit: 'minutes' }), '15 min');
  assert.equal(V.paramValue(0, { unit: 'minutes' }), 'none');
  assert.equal(V.paramValue(true, { kind: 'boolean' }), 'on');
  assert.equal(V.paramValue('10', { unit: 'EUR' }), '10 EUR');
  assert.equal(V.paramValue(null, {}), '–');
  assert.equal(V.paramRange({ kind: 'number', min: '1', max: '40', unit: 'EUR' }), '1 EUR to 40 EUR');
  assert.equal(V.paramRange({ kind: 'boolean', min: null, max: null }), 'on or off');
  const mandatory = [{ key: 'maxOrderQuote', label: 'Largest order', value: '40' }];
  assert.equal(V.paramLimit({ boundBy: 'maxOrderQuote' }, mandatory), 'Strategy-specific, within the mandatory largest order (40)');
  assert.equal(V.paramLimit({}, mandatory), 'Strategy-specific');
});

test('a switch lists what changes, and which changes reach open positions', () => {
  const groups = [{ parameters: [
    { key: 'orderQuote', label: 'Largest order', unit: 'EUR', appliesToOpenPositions: false },
    { key: 'scanner.trendExits', label: 'Trend exits', kind: 'boolean', appliesToOpenPositions: true },
    { key: 'scanner.stopAtr', label: 'Stop', unit: 'ATR', appliesToOpenPositions: false },
  ] }];
  const from = { values: { orderQuote: 10, 'scanner.trendExits': true, 'scanner.stopAtr': 1.5 } };
  const to = { values: { orderQuote: 20, 'scanner.trendExits': false, 'scanner.stopAtr': 1.5 } };
  assert.deepEqual(V.strategyChanges(from, to, groups), [
    { key: 'orderQuote', label: 'Largest order', from: '10 EUR', to: '20 EUR', openPositions: false },
    { key: 'scanner.trendExits', label: 'Trend exits', from: 'on', to: 'off', openPositions: true },
  ]);
  assert.equal(V.strategyName({ name: 'safe', version: 1 }), 'Safe v1');
  assert.deepEqual(V.strategyRequestBody({ name: 'safe', version: 1, note: ' a\nb ' }, ID), { body: { id: ID, name: 'safe', version: 1, note: 'a b', tradingMode: 'paper' } });
  assert.ok(V.strategyRequestBody({ name: 'safe', version: '1' }, ID).error);
  assert.equal(V.selectionState({ state: 'refused' }).tone, 'bad');
});

test('content language: Russian by default, the English when there is no Russian', () => {
  assert.equal(V.contentLang(null), 'ru');
  assert.equal(V.contentLang('en'), 'en');
  assert.equal(V.contentLang('de'), 'ru');
  assert.deepEqual(V.pick('Calm market.', 'Спокойный рынок.', 'ru'), { text: 'Спокойный рынок.', lang: 'ru' });
  assert.deepEqual(V.pick('Calm market.', 'Спокойный рынок.', 'en'), { text: 'Calm market.', lang: 'en' });
  assert.deepEqual(V.pick('Calm market.', null, 'ru'), { text: 'Calm market.', lang: 'en' }, 'analyst v2 views have no Russian');
  assert.deepEqual(V.pick('Calm market.', '  ', 'ru'), { text: 'Calm market.', lang: 'en' });
  assert.deepEqual(V.pick(undefined, undefined, 'ru'), { text: null, lang: 'en' });
});
