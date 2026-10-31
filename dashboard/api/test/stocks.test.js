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
  return id === 'candidates-latest' ? null : latest;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://crypto.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('crypto').toString('base64'), COSMOS_DATABASE: 'cix',
  STOCKS_COSMOS_ENDPOINT: 'https://stocks.documents.azure.com', STOCKS_COSMOS_READONLY_KEY: Buffer.from('stocks').toString('base64'),
  DASHBOARD_OWNER_USER_ID: 'owner-user-id',
});
require('../src/functions/stocks.js');
Module._load = originalLoad;
const { stocksView, candidatesView, loadStocks, stocksReader } = require('../src/stocks');

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
  ]);
  assert.equal(answer.jsonBody.candidates, null);
  assert.equal(answer.jsonBody.configured, true);
  assert.equal(answer.jsonBody.advice.actions.length, 3);
});

test('without the stocks settings the tab is not set up, and nothing is read', async () => {
  assert.equal(stocksReader({ COSMOS_ENDPOINT: 'https://crypto.documents.azure.com', COSMOS_READONLY_KEY: 'a2V5' }), null);
  assert.deepEqual(await loadStocks(null, NOW), { configured: false, advice: null, candidates: null, generatedAt: '2026-10-30T22:00:00.000Z' });
});

test('no document yet is set up but without advice', async () => {
  const reader = { readDocument: async () => null };
  assert.deepEqual(await loadStocks(reader, NOW), { configured: true, advice: null, candidates: null, generatedAt: '2026-10-30T22:00:00.000Z' });
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
      w('GGG.US', 'sideways', '1', '1', '1'),
      { symbol: 'bad', status: 'uptrend' },
    ],
  }, NOW);
  assert.deepEqual(view.watchlist.map((x) => x.symbol), ['FFF.US', 'EEE.US', 'CCC.US', 'BBB.US', 'JJJ.US', 'AAA.US', 'III.US', 'HHH.US', 'DDD.US']);
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
    listed: 5200, shortlisted: 40,
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
  assert.equal(candidatesView(null), null);
});

test('the answer carries both pages: the advice and the candidates', async () => {
  const docs = { 'advice-latest': { value: ADVICE }, 'candidates-latest': { value: { status: 'off', candidates: [] } } };
  const answer = await loadStocks({ readDocument: async (container, id) => docs[id] ?? null }, NOW);
  assert.equal(answer.advice.asOf, '2026-10-30');
  assert.equal(answer.candidates.status, 'off');
});
