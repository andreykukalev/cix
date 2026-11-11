'use strict';
// The Stocks tab's read (ADR-0023): the advisor's document checked field by field, and the route owner only, reading the
// stocks database alone.
const test = require('node:test');
const assert = require('node:assert/strict');
const Module = require('node:module');

const routes = {};
const originalLoad = Module._load;
Module._load = function load(request, ...rest) {
  if (request === '@azure/functions') return { app: { http: (name, options) => { routes[name] = options; } } };
  return originalLoad.call(this, request, ...rest);
};
const { CosmosReader } = require('../src/cosmos');
const reads = [];
let latest = null;
CosmosReader.prototype.readDocument = async function readDocument(container, id) {
  reads.push({ endpoint: this.base, database: this.database, container, id });
  return id === 'advice-latest' ? latest : null;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://crypto.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('crypto').toString('base64'),
  STOCKS_COSMOS_ENDPOINT: 'https://stocks.documents.azure.com', STOCKS_COSMOS_READONLY_KEY: Buffer.from('stocks').toString('base64'),
  DASHBOARD_OWNER_USER_ID: 'owner-user-id',
});
require('../src/functions/stocks.js');
Module._load = originalLoad;
const { stocksView, candidatesView, analysisView, workingTable, entryPlan, loadStocks, stocksReader } = require('../src/stocks');

const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64');
const OWNER = b64({ identityProvider: 'aad', userId: 'owner-user-id', userRoles: ['authenticated', 'owner'] });
const VISITOR = b64({ identityProvider: 'aad', userId: 'visitor', userRoles: ['authenticated'] });
const context = { log() {}, error() {} };
const NOW = Date.parse('2026-10-30T22:00:00Z');

function request(principal) {
  const headers = { 'x-ms-client-principal': principal };
  return { method: 'GET', params: {}, query: new URLSearchParams(), headers: { get: (n) => headers[n.toLowerCase()] ?? null } };
}

const ADVICE = {
  asOf: '2026-10-30', generatedAt: '2026-10-30T21:30:00Z', regime: 'risk-on', benchmark: 'CSPX.LSE', baseCurrency: 'EUR',
  capital: 10000, cash: 6200.5, equity: 10150.25,
  actions: [
    { kind: 'buy', symbol: 'MSFT.US', name: 'Microsoft', currency: 'USD', quantity: 4, referencePrice: 512.3, stop: 480.1, reason: 'breakout' },
    { kind: 'raise-stop', symbol: 'SAP.XETRA', currency: 'EUR', quantity: 10, referencePrice: 250, stop: 231.5, reason: 'trail' },
    { kind: 'sell', symbol: 'ASML.AS', currency: 'EUR', quantity: 2, referencePrice: 610, reason: 'trend-broken' },
  ],
  holdings: [
    { symbol: 'SAP.XETRA', name: 'SAP', currency: 'EUR', quantity: 10, averageCost: 200, lastClose: 250, stop: 225, openedOn: '2026-08-01' },
  ],
  notes: [{ symbol: 'NVDA.US', reason: 'max-positions' }],
};

test('the route is registered under /api/stocks', () => {
  assert.equal(routes.stocks.route, 'stocks');
  assert.deepEqual(routes.stocks.methods, ['GET']);
});

test('only the confirmed owner gets an answer; nobody else reaches the database', async () => {
  reads.length = 0;
  const answer = await routes.stocks.handler(request(VISITOR), context);
  assert.equal(answer.status, 403);
  assert.deepEqual(reads, []);
});

test('the owner reads the stocks database alone, never the crypto one', async () => {
  reads.length = 0;
  latest = { id: 'advice-latest', value: ADVICE };
  const answer = await routes.stocks.handler(request(OWNER), context);
  assert.equal(answer.status, 200);
  assert.equal(answer.headers['cache-control'], 'no-store');
  assert.deepEqual(reads, [
    { endpoint: 'https://stocks.documents.azure.com', database: 'cix-stocks', container: 'advice', id: 'advice-latest' },
    { endpoint: 'https://stocks.documents.azure.com', database: 'cix-stocks', container: 'advice', id: 'candidates-latest' },
    { endpoint: 'https://stocks.documents.azure.com', database: 'cix-stocks', container: 'analysis', id: 'analysis-latest' },
    // The owner's Stop and Start: never pressed.
    { endpoint: 'https://stocks.documents.azure.com', database: 'cix-stocks', container: 'control', id: 'switch-1' },
    // The owner's portfolio submissions waiting for the next run: none.
    { endpoint: 'https://stocks.documents.azure.com', database: 'cix-stocks', container: 'requests', id: 'portfolio-1' },
  ]);
  assert.equal(answer.jsonBody.candidates, null);
  assert.equal(answer.jsonBody.configured, true);
  assert.equal(answer.jsonBody.advice.actions.length, 3);
});

test('without the stocks settings the tab is not set up, and nothing is read', async () => {
  assert.equal(stocksReader({ COSMOS_ENDPOINT: 'https://crypto.documents.azure.com', COSMOS_READONLY_KEY: 'a2V5' }), null);
  assert.deepEqual(await loadStocks(null, NOW), { configured: false, advice: null, candidates: null, analysis: null, working: { source: null, rows: [] }, pending: null, pendingTrades: [], control: { paused: false, since: null, sequence: 0, unreadable: false }, generatedAt: '2026-10-30T22:00:00.000Z' });
});

test('no document yet is set up but without advice', async () => {
  const reader = { readDocument: async () => null };
  assert.deepEqual(await loadStocks(reader, NOW), { configured: true, advice: null, candidates: null, analysis: null, working: { source: null, rows: [] }, pending: null, pendingTrades: [], control: { paused: false, since: null, sequence: 0, unreadable: false }, generatedAt: '2026-10-30T22:00:00.000Z' });
});

test('actions come protective first, in the advisor\'s order within a kind', () => {
  const view = stocksView({ ...ADVICE, actions: [...ADVICE.actions, { kind: 'set-stop', symbol: 'AAPL.US', quantity: 3, stop: 200, reason: 'no-stop' }] }, NOW);
  assert.deepEqual(view.actions.map((a) => `${a.kind} ${a.symbol}`), ['sell ASML.AS', 'set-stop AAPL.US', 'raise-stop SAP.XETRA', 'buy MSFT.US']);
});

test('holdings carry the change since entry and the room to the stop', () => {
  const [h] = stocksView(ADVICE, NOW).holdings;
  assert.equal(h.change, 0.25); // 250 / 200 − 1
  assert.equal(h.toStop, 0.1); // (250 − 225) / 250
  assert.equal(h.openedOn, '2026-08-01');
});

test('advice goes stale once two weekday closes pass without new advice', () => {
  assert.deepEqual([stocksView(ADVICE, NOW).ageDays, stocksView(ADVICE, NOW).stale], [0, false]);
  const at = (asOf, now) => stocksView({ ...ADVICE, asOf }, Date.parse(now)).stale;
  // Friday's advice: fresh over the weekend and on Tuesday (Monday may be a holiday), stale on Wednesday.
  assert.equal(at('2026-10-30', '2026-11-02T22:00:00Z'), false);
  assert.equal(at('2026-10-30', '2026-11-03T22:00:00Z'), false);
  assert.equal(at('2026-10-30', '2026-11-04T08:00:00Z'), true);
  // Tuesday's advice: Thursday is one missed close, Friday two.
  assert.equal(at('2026-10-27', '2026-10-29T12:00:00Z'), false);
  assert.equal(at('2026-10-27', '2026-10-30T12:00:00Z'), true);
  assert.equal(stocksView({ ...ADVICE, asOf: 'yesterday' }, NOW).stale, true);
});

test('a date that isn\'t real, or is in the future, is not fresh advice', () => {
  assert.equal(stocksView({ ...ADVICE, asOf: '2026-02-30' }, NOW).asOf, null);
  assert.equal(stocksView({ ...ADVICE, asOf: '2026-11-02' }, NOW).stale, true);
});

test('a name loses control and bidi-override characters', () => {
  const [a] = stocksView({ ...ADVICE, actions: [{ ...ADVICE.actions[0], name: 'Micro\u202Esoft\u0007 ' }] }, NOW).actions;
  assert.equal(a.name, 'Microsoft');
});

test('a malformed document gives gaps, not errors or markup', () => {
  const view = stocksView({
    regime: 'moon', asOf: 42, capital: -5, cash: 'lots', benchmark: '../etc',
    actions: [
      { kind: 'short', symbol: 'MSFT.US' }, { kind: 'buy', symbol: 'msft' }, { kind: 'buy', symbol: 'MSFT.US', quantity: 'four', reason: '<b>x</b>' }, null,
    ],
    holdings: [{ symbol: 'SAP.XETRA', quantity: 0 }, { symbol: 'SAP.XETRA', quantity: 1, lastClose: 10, stop: 12 }],
    notes: [{ symbol: 'NVDA.US', reason: 'Max Positions' }, { symbol: 'NVDA.US', reason: 'max-positions' }],
  }, NOW);
  assert.equal(view.regime, 'unknown');
  assert.equal(view.asOf, null);
  assert.equal(view.stale, true);
  assert.equal(view.capital, null);
  assert.equal(view.cash, null);
  assert.equal(view.benchmark, null);
  assert.deepEqual(view.actions, [{ kind: 'buy', symbol: 'MSFT.US', name: null, currency: null, quantity: null, referencePrice: null, stop: null, reason: null }]);
  assert.equal(view.holdings.length, 1);
  assert.equal(view.holdings[0].toStop, -0.2); // already under its stop
  assert.deepEqual(view.notes, [{ symbol: 'NVDA.US', reason: 'max-positions' }]);
  assert.deepEqual(stocksView(null, NOW).actions, []);
});

test('the host\'s numbers come as decimal strings and read like numbers', () => {
  const view = stocksView({
    ...ADVICE, capital: '10000', cash: '6200.50', equity: '10150.25',
    actions: [{ ...ADVICE.actions[0], quantity: '4', referencePrice: '512.3', stop: '480.1' }],
    holdings: [{ ...ADVICE.holdings[0], quantity: '10', averageCost: '200', lastClose: '250', stop: '225' }],
  }, NOW);
  assert.deepEqual([view.capital, view.cash, view.equity], [10000, 6200.5, 10150.25]);
  assert.equal(stocksView({ ...ADVICE, cash: '8043.1701250000000000000000001' }, NOW).cash, 8043.170125);
  assert.deepEqual([view.actions[0].quantity, view.actions[0].referencePrice, view.actions[0].stop], [4, 512.3, 480.1]);
  assert.equal(view.holdings[0].change, 0.25);
  assert.equal(view.holdings[0].toStop, 0.1);
  for (const bad of ['1e5', '12abc', ' 5', '', '0x10', 'Infinity']) assert.equal(stocksView({ ...ADVICE, capital: bad }, NOW).capital, null, bad);
});

test('the watchlist lists held, then entry signals, then uptrends nearest their high, then the rest', () => {
  const w = (symbol, status, lastClose, breakoutHigh, trendAverage) => ({ symbol, status, lastClose, breakoutHigh, trendAverage, currency: 'USD', theme: 'semis' });
  const view = stocksView({
    ...ADVICE,
    watchlist: [
      w('AAA.US', 'below-trend', '90', '120', '100'),
      w('BBB.US', 'uptrend', '100', '110', '80'),
      w('CCC.US', 'uptrend', '100', '102', '80'),
      w('DDD.US', 'no-data', null, null, null),
      w('EEE.US', 'breakout', '125', '120', '100'),
      w('FFF.US', 'held', '50', '55', '40'),
      w('HHH.US', 'stale-data', '10', '9', '8'),
      w('III.US', 'too-expensive', '300', '290', '250'),
      w('JJJ.US', 'uptrend', '100', null, '80'),
      w('KKK.US', 'too-volatile', '20', '19', '15'),
      w('LLL.US', 'overextended', '150', '120', '100'),
      w('GGG.US', 'sideways', '1', '1', '1'),
      { symbol: 'bad', status: 'uptrend' },
    ],
  }, NOW);
  assert.deepEqual(view.watchlist.map((x) => x.symbol), ['FFF.US', 'EEE.US', 'CCC.US', 'BBB.US', 'JJJ.US', 'LLL.US', 'KKK.US', 'AAA.US', 'III.US', 'HHH.US', 'DDD.US']);
  const ccc = view.watchlist.find((x) => x.symbol === 'CCC.US');
  assert.ok(Math.abs(ccc.toBreakout - 0.02) < 1e-12); // 102 / 100 − 1
  assert.equal(ccc.vsTrend, 0.25); // 100 / 80 − 1
  assert.ok(Math.abs(view.watchlist.find((x) => x.symbol === 'EEE.US').toBreakout + 0.04) < 1e-12); // already above: 120 / 125 − 1
  const none = view.watchlist.find((x) => x.symbol === 'DDD.US');
  assert.deepEqual([none.lastClose, none.toBreakout, none.vsTrend], [null, null, null]);
  assert.deepEqual(stocksView(ADVICE, NOW).watchlist, []); // advice written before the watchlist existed
});

test('the market-wide search\'s candidates are checked field by field and keep the host\'s order', () => {
  const view = candidatesView({
    asOf: '2026-10-30', generatedAt: '2026-10-31T02:12:00Z', status: 'done', regime: 'risk-on', baseCurrency: 'USD', capital: '10000',
    listed: '5200', shortlisted: '40',
    candidates: [
      { symbol: 'NEW.US', name: 'New Co', venue: 'NASDAQ', status: 'breakout', close: '142.2', breakoutHigh: '142.1', trendAverage: '132.25', stop: '139.2', shares: '14', momentum: '0.31', marketCap: '5000000000' },
      { symbol: 'NEAR.US', status: 'near-high', close: '100', breakoutHigh: '102', stop: '94', shares: '3' },
      { symbol: 'BAD.US', status: 'moon', close: '1' },
      { symbol: 'nope', status: 'breakout', close: '1' },
      { symbol: 'NOPRICE.US', status: 'breakout', close: 'x' },
    ],
  });
  assert.equal(view.status, 'done');
  assert.deepEqual([view.listed, view.shortlisted, view.capital], [5200, 40, 10000]);
  assert.deepEqual(view.candidates.map((c) => c.symbol), ['NEW.US', 'NEAR.US']);
  const [c, near] = view.candidates;
  assert.ok(Math.abs(c.amount - 1990.8) < 1e-9); // 14 × 142.2
  assert.ok(Math.abs(c.toStop - 3 / 142.2) < 1e-12);
  assert.deepEqual([near.name, near.momentum, near.marketCap], [null, null, null]);
  assert.ok(Math.abs(near.toBreakout - 0.02) < 1e-12);
  assert.equal(candidatesView({ status: 'unknown-thing' }).status, null);
  assert.deepEqual([candidatesView({ listed: '1.5' }).listed, candidatesView({ listed: '-1' }).listed, candidatesView({ listed: 7 }).listed], [null, null, 7]);
  assert.equal(candidatesView({ status: 'advice-behind' }).status, 'advice-behind');
  assert.equal(candidatesView(null), null);
});

test('the answer carries both pages: the advice and the candidates', async () => {
  const docs = { 'advice-latest': { value: ADVICE }, 'candidates-latest': { value: { status: 'off', candidates: [] } } };
  const answer = await loadStocks({ readDocument: async (container, id) => docs[id] ?? null }, NOW);
  assert.equal(answer.advice.asOf, '2026-10-30');
  assert.equal(answer.candidates.status, 'off');
});

const OWNER_SECTION = {
  capital: '5000', cash: '3300', invested: '1700', value: '2133', appliedSequence: '2', updatedAt: '2026-10-28T18:00:00Z',
  actions: [
    { kind: 'buy', symbol: 'XYZ.US', currency: 'USD', quantity: '7', referencePrice: '142.2', stop: '139.2', reason: 'breakout' },
    { kind: 'set-stop', symbol: 'OWN.US', name: 'Own Inc', currency: 'USD', quantity: '5', referencePrice: '142.2', stop: '139.2', reason: 'no-stop' },
  ],
  holdings: [{ symbol: 'OWN.US', name: 'Own Inc', currency: 'USD', quantity: '5', averageCost: '100', lastClose: '142.2', openedOn: '2026-09-01' }],
  notes: [{ symbol: 'OWN.US', reason: 'data-error' }],
};

test('the owner\'s real portfolio is checked like the paper one, protective actions first', () => {
  const view = stocksView({ ...ADVICE, owner: OWNER_SECTION, refusal: { sequence: '3', reason: 'capital' } }, NOW);
  assert.equal(view.owner.capital, 5000);
  assert.equal(view.owner.appliedSequence, 2);
  assert.deepEqual(view.owner.actions.map((a) => `${a.kind} ${a.symbol}`), ['set-stop OWN.US', 'buy XYZ.US']);
  assert.equal(view.owner.holdings[0].change, 0.42199999999999993); // 142.2 / 100 − 1
  assert.equal(view.owner.holdings[0].stop, null);
  assert.deepEqual(view.owner.notes, [{ symbol: 'OWN.US', reason: 'data-error' }]);
  assert.deepEqual(view.refusal, { sequence: 3, reason: 'capital', kind: null, symbol: null });
  // A refused trade says which one; a kind or ticker that isn't one is left out.
  assert.deepEqual(stocksView({ ...ADVICE, refusal: { sequence: 4, reason: 'not-held', kind: 'sell', symbol: 'ABC.US' } }, NOW).refusal,
    { sequence: 4, reason: 'not-held', kind: 'sell', symbol: 'ABC.US' });
  assert.deepEqual(stocksView({ ...ADVICE, refusal: { sequence: 4, reason: 'not-held', kind: 'short', symbol: '<b>' } }, NOW).refusal,
    { sequence: 4, reason: 'not-held', kind: null, symbol: null });
  // None, or one without a capital: no section.
  assert.equal(stocksView(ADVICE, NOW).owner, null);
  assert.equal(stocksView({ ...ADVICE, owner: { ...OWNER_SECTION, capital: '0' } }, NOW).owner, null);
  assert.equal(stocksView({ ...ADVICE, refusal: { sequence: '0', reason: 'capital' } }, NOW).refusal, null);
});

test('a portfolio sent since the last run is shown as waiting, from the first number the host has not seen', async () => {
  const docs = {
    'advice-latest': { value: { ...ADVICE, owner: OWNER_SECTION, refusal: { sequence: 3, reason: 'capital' } } },
    'portfolio-4': { value: { sequence: 4, submittedAt: '2026-10-30T19:00:00Z', capital: '4000', holdings: [] } },
    'portfolio-5': { value: { sequence: 5, submittedAt: '2026-10-30T20:00:00Z', capital: '3000', holdings: [{ symbol: 'OWN.US', quantity: '5', averageCost: '100' }] } },
  };
  const asked = [];
  const reader = { readDocument: async (container, id) => { asked.push(`${container}/${id}`); return docs[id] ?? null; } };
  const answer = await loadStocks(reader, NOW);
  assert.deepEqual(asked.filter((a) => a.startsWith('requests/')), ['requests/portfolio-4', 'requests/portfolio-5', 'requests/portfolio-6']);
  assert.equal(answer.pending.sequence, 5);
  assert.equal(answer.pending.capital, '3000');
  assert.deepEqual(answer.pending.holdings, [{ symbol: 'OWN.US', name: null, quantity: '5', averageCost: '100', stop: null, openedOn: null }]);
  assert.deepEqual(answer.pendingTrades, []);
});

test('trades sent since the last run wait in order, beside the newest whole portfolio', async () => {
  const trade = (n, kind) => ({ value: { sequence: n, submittedAt: '2026-10-30T19:00:00Z', trade: { kind, symbol: 'OWN.US', quantity: '2', price: '150', day: '2026-10-30' } } });
  const docs = {
    'advice-latest': { value: { ...ADVICE, owner: OWNER_SECTION } },
    'portfolio-3': trade(3, 'buy'),
    'portfolio-4': { value: { sequence: 4, submittedAt: '2026-10-30T19:30:00Z', capital: '4000', holdings: [] } },
    'portfolio-5': trade(5, 'sell'),
    'portfolio-6': { value: { sequence: 6, trade: { kind: 'sell' } } }, // not a trade the page could send
  };
  const reader = { readDocument: async (_container, id) => docs[id] ?? null };
  const answer = await loadStocks(reader, NOW);
  assert.equal(answer.pending.sequence, 4);
  assert.deepEqual(answer.pendingTrades.map((x) => [x.sequence, x.trade?.kind ?? null, x.unreadable ?? false]), [[3, 'buy', false], [5, 'sell', false], [6, null, true]]);
  assert.deepEqual(answer.pendingTrades[0].trade, { kind: 'buy', symbol: 'OWN.US', name: null, quantity: '2', price: '150', fees: null, day: '2026-10-30', stop: null });
});

test('the owner\'s trades as the host recorded them, every field checked', () => {
  const trades = [
    { day: '2026-10-29', symbol: 'OWN.US', kind: 'sell', quantity: '4', price: '140', feesBase: '1', profitBase: '79', reason: 'owner' },
    { day: '2026-10-28', symbol: 'OWN.US', kind: 'buy', quantity: '10', price: '120', feesBase: '0', profitBase: null, reason: 'owner' },
    { day: '2026-10-28', symbol: 'OWN.US', kind: 'stop-out', quantity: '1', price: '1' },
    { day: '2026-10-28', symbol: 'bad', kind: 'buy', quantity: '1', price: '1' },
  ];
  const view = stocksView({ ...ADVICE, owner: { ...OWNER_SECTION, trades } }, NOW);
  assert.deepEqual(view.owner.trades, [
    { day: '2026-10-29', symbol: 'OWN.US', kind: 'sell', quantity: 4, price: 140, fees: 1, profit: 79 },
    { day: '2026-10-28', symbol: 'OWN.US', kind: 'buy', quantity: 10, price: 120, fees: 0, profit: null },
  ]);
  assert.deepEqual(stocksView({ ...ADVICE, owner: OWNER_SECTION }, NOW).owner.trades, []);
});


const ANALYSIS = {
  asOf: '2026-10-30', generatedAt: '2026-10-31T02:40:00Z', status: 'done', market: 'Calm\nbefore the Fed.', dropped: '2',
  holdings: [
    { symbol: 'SAP.XETRA', action: 'sell-half', reason: 'Guidance cut.', reasonRu: 'Прогноз снижен.', earnings: '2026-11-05' },
    { symbol: 'SAP.XETRA', action: 'hold', reason: 'A second line for it.' },
    { symbol: 'ASML.AS', action: 'buy-more', reason: 'Not an action.' },
    { symbol: 'msft', action: 'hold', reason: 'Not a symbol.' },
  ],
  picks: [
    { symbol: 'MSFT.US', rank: '1', reason: 'Clean \u202Ebreakout.', earnings: '2026-02-30' },
    { symbol: 'NVDA.US', rank: '2', reason: '   ' },
    { symbol: 'NEW.US', rank: '3', reason: 'x'.repeat(500) },
  ],
  skips: [{ symbol: 'NEAR.US', reason: 'Lawsuit pending.' }],
};

test('the analyst\'s view is checked line by line: allowed actions, real symbols and days, cleaned and cut text', () => {
  const view = analysisView(ANALYSIS);
  assert.equal(view.status, 'done');
  assert.equal(view.market, 'Calm before the Fed.');
  assert.equal(view.dropped, 2);
  assert.deepEqual(view.holdings, [{ symbol: 'SAP.XETRA', action: 'sell-half', reason: 'Guidance cut.', reasonRu: 'Прогноз снижен.', earnings: '2026-11-05' }]);
  assert.deepEqual(view.picks.map((p) => [p.symbol, p.rank, p.earnings]), [['MSFT.US', 1, null], ['NEW.US', 2, null]]);
  assert.equal(view.picks[0].reason, 'Clean breakout.');
  assert.equal(view.picks[1].reason.length, 400);
  assert.deepEqual(view.skips, [{ symbol: 'NEAR.US', reason: 'Lawsuit pending.', reasonRu: null, earnings: null }]);
  assert.deepEqual(analysisView({ status: 'not-run', reason: 'usage-limit', asOf: '2026-10-30' }),
    { asOf: '2026-10-30', generatedAt: null, status: 'not-run', pass: 'night', reason: 'usage-limit', market: null, marketRu: null, holdings: [], picks: [], skips: [], dropped: null });
  assert.equal(analysisView({ status: 'done', pass: 'pre-open' }).pass, 'pre-open');
  assert.equal(analysisView({ status: 'done', pass: 'midday' }).pass, 'night');
  assert.equal(analysisView({ status: 'thinking' }), null);
  assert.equal(analysisView('done'), null);
  assert.equal(analysisView({ status: 'done', picks: Array.from({ length: 9 }, (_, i) => ({ symbol: `P${i}.US`, reason: 'r' })) }).picks.length, 5);
});

test('the answer says whether Claude\'s view is of the advice\'s close', async () => {
  const docs = { 'advice-latest': { value: ADVICE }, 'analysis-latest': { value: ANALYSIS } };
  const reader = { readDocument: async (container, id) => docs[id] ?? null };
  assert.equal((await loadStocks(reader, NOW)).analysis.current, true);
  docs['analysis-latest'] = { value: { ...ANALYSIS, asOf: '2026-10-29' } };
  assert.equal((await loadStocks(reader, NOW)).analysis.current, false);
  delete docs['advice-latest'];
  assert.equal((await loadStocks(reader, NOW)).analysis.current, false);
});

test('the entry plan: a limit half an ATR over the close, no buy back under the broken high, and only a wait when near it', () => {
  // 100 + 0.5 × 2; under 98, the high it broke, the breakout failed.
  assert.deepEqual(entryPlan({ status: 'breakout', close: 100, breakoutHigh: 98, atr: 2, stop: 94 }), { buyUpTo: 101, failsUnder: 98, waitAbove: null });
  // Whole cents, down: 142.2 + 0.685 is 142.885.
  assert.equal(entryPlan({ status: 'breakout', close: 142.2, breakoutHigh: 142.1, atr: 1.37, stop: 138.1 }).buyUpTo, 142.88);
  // A stop above the broken high is the floor; without an ATR (a page from before it) there is no limit.
  assert.deepEqual(entryPlan({ status: 'breakout', close: 100, breakoutHigh: 90, atr: null, stop: 94 }), { buyUpTo: null, failsUnder: 94, waitAbove: null });
  assert.deepEqual(entryPlan({ status: 'near-high', close: 100, breakoutHigh: 102, atr: 2, stop: 94 }), { buyUpTo: null, failsUnder: null, waitAbove: 102 });
  assert.deepEqual(entryPlan({ status: 'breakout', close: null, breakoutHigh: 98, atr: 2, stop: 94 }), { buyUpTo: null, failsUnder: null, waitAbove: null });
});

const WORKING_ADVICE = {
  ...ADVICE,
  owner: { ...OWNER_SECTION },
  watchlist: [{ symbol: 'XYZ.US', name: 'XYZ Corp', currency: 'USD', lastClose: '142.2', breakoutHigh: '142.1', trendAverage: '132.25', status: 'breakout',
    atr: '1', moves: { day: '0.0007', week: '0.0035', month: '0.015', halfYear: '0.0972' } }],
};
const WORKING_CANDIDATES = {
  asOf: '2026-10-30', status: 'done', regime: 'risk-on', candidates: [
    { symbol: 'NEW.US', name: 'New Co', status: 'breakout', close: '50', breakoutHigh: '49', stop: '47', shares: '20', momentum: '0.31', atr: '1', moves: { day: '0.02', week: '-0.01', month: '0.1', halfYear: '0.3' } },
    { symbol: 'NEAR.US', status: 'near-high', close: '100', breakoutHigh: '102', stop: '94', shares: '3', momentum: '0.2' },
  ],
};

test('the working table: Claude\'s picks in its order, each with the figures of the buy or candidate it picked', () => {
  const advice = stocksView(WORKING_ADVICE, NOW);
  const found = candidatesView(WORKING_CANDIDATES);
  const analysis = { ...analysisView({ ...ANALYSIS, picks: [{ symbol: 'NEW.US', reason: 'Strong.' }, { symbol: 'XYZ.US', reason: 'Clean.', earnings: '2026-11-05' }, { symbol: 'GONE.US', reason: 'Gone.' }] }), current: true };
  const table = workingTable(advice, found, analysis);
  assert.equal(table.source, 'claude');
  assert.deepEqual(table.rows.map((r) => [r.rank, r.symbol, r.origin, r.reason, r.earnings]),
    [[1, 'NEW.US', 'candidate', 'Strong.', null], [2, 'XYZ.US', 'buy', 'Clean.', '2026-11-05'], [3, 'GONE.US', null, 'Gone.', null]]);
  const [candidate, buy, gone] = table.rows;
  // The candidate: 20 shares of 50, 3 / 50 to the stop, a limit at 50.5, none under its broken high 49.
  assert.deepEqual([candidate.amount, candidate.toStop, candidate.buyUpTo, candidate.failsUnder], [1000, 0.06, 50.5, 49]);
  assert.deepEqual(candidate.moves, { day: 0.02, week: -0.01, month: 0.1, halfYear: 0.3 });
  // The owner's buy of a watched stock (7 at 142.2, stop 139.2): its high, ATR and moves from the watchlist.
  assert.deepEqual([buy.name, buy.shares, buy.close, buy.stop, buy.buyUpTo, buy.failsUnder], ['XYZ Corp', 7, 142.2, 139.2, 142.7, 142.1]);
  assert.deepEqual(buy.moves, { day: 0.0007, week: 0.0035, month: 0.015, halfYear: 0.0972 });
  assert.deepEqual([gone.close, gone.buyUpTo, gone.moves.day], [null, null, null]);
});

test('without Claude\'s view of this close the rules\' buys and the search\'s entry signals stand, and none while risk-off', () => {
  const advice = stocksView(WORKING_ADVICE, NOW);
  const found = candidatesView(WORKING_CANDIDATES);
  const behind = { ...analysisView(ANALYSIS), current: false };
  const table = workingTable(advice, found, behind);
  assert.equal(table.source, 'rules');
  // NEAR is only near its high: not an entry.
  assert.deepEqual(table.rows.map((r) => [r.symbol, r.origin, r.rank]), [['XYZ.US', 'buy', null], ['NEW.US', 'candidate', null]]);
  assert.deepEqual(workingTable(advice, found, null).rows.map((r) => r.symbol), ['XYZ.US', 'NEW.US']);
  // The paper portfolio's buys before the owner sends a portfolio.
  assert.deepEqual(workingTable(stocksView({ ...WORKING_ADVICE, owner: undefined }, NOW), found, null).rows.map((r) => [r.symbol, r.shares]), [['MSFT.US', 4], ['NEW.US', 20]]);
  // Risk-off: the search's signals are for watching only.
  assert.deepEqual(workingTable(stocksView({ ...WORKING_ADVICE, regime: 'risk-off' }, NOW), found, null).rows.map((r) => r.symbol), ['XYZ.US']);
  // Candidates of an older close aren't tonight's.
  assert.deepEqual(workingTable(advice, candidatesView({ ...WORKING_CANDIDATES, asOf: '2026-10-29' }), null).rows.map((r) => r.symbol), ['XYZ.US']);
  // The half year falls back on the momentum on a page from before the moves.
  assert.equal(workingTable(advice, candidatesView({ ...WORKING_CANDIDATES, candidates: [{ ...WORKING_CANDIDATES.candidates[0], moves: undefined }] }), null).rows[1].moves.halfYear, 0.31);
  assert.deepEqual(workingTable(null, found, null), { source: null, rows: [] });
  // A candidate's shares are capped by the free cash: 300 buys 6 at 50, not the 20 sized on the capital.
  const poor = stocksView({ ...WORKING_ADVICE, owner: { ...OWNER_SECTION, cash: '300' } }, NOW);
  assert.deepEqual(workingTable(poor, found, null).rows.map((r) => [r.symbol, r.shares, r.amount]), [['XYZ.US', 7, 7 * 142.2], ['NEW.US', 6, 300]]);
});

test('moves and the ATR are checked like every other figure', () => {
  const view = candidatesView({ status: 'done', candidates: [{ symbol: 'A.US', status: 'breakout', close: '10', atr: '-1', moves: { day: '11', week: 'x', month: 0.5, halfYear: null } }] });
  assert.deepEqual([view.candidates[0].atr, view.candidates[0].moves], [null, { day: null, week: null, month: 0.5, halfYear: null }]);
});
