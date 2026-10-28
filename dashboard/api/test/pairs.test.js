'use strict';
// The pairs panel's data (dashboard/api/src/pairs.js): the add and remove requests the dashboard writes, and the view it shows.
const test = require('node:test');
const assert = require('node:assert/strict');
const { pairRequestDocument, pairsView } = require('../src/pairs');
const { RequestError } = require('../src/requests');

const NOW = Date.parse('2026-10-27T09:30:00Z');
const ID = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

test('a pair request: the server\'s time, the owner as a role, a normalised symbol, a clean note', () => {
  const doc = pairRequestDocument({ id: ID.toUpperCase(), action: 'add', symbol: ' sol/eur ', note: 'more\nchoice' }, NOW);
  assert.deepEqual(doc, {
    id: ID, day: '2026-10-27', createdAt: '2026-10-27T09:30:00.000Z', type: 'tradable-pair', action: 'add', symbol: 'SOL-EUR',
    requestedBy: 'owner', tradingMode: 'paper', status: 'new', note: 'more choice',
  });
});

test('a pair request is refused unless well formed, and for live unless the owner confirmed it', () => {
  const ok = { id: ID, action: 'remove', symbol: 'SOL-EUR' };
  for (const bad of [null, [], 'x', { ...ok, id: 'x' }, { ...ok, action: 'delete' }, { ...ok, action: undefined }, { ...ok, symbol: 'SOL' },
    { ...ok, symbol: 'SOL-EUR-X' }, { ...ok, symbol: '../x' }, { ...ok, symbol: 5 }, { ...ok, symbol: 'A'.repeat(21) + '-EUR' },
    { ...ok, tradingMode: 'live' }, { ...ok, tradingMode: 'live', confirmLive: 'yes' }, { ...ok, tradingMode: 'demo' }, { ...ok, note: 5 }]) {
    assert.throws(() => pairRequestDocument(bad, NOW), RequestError, JSON.stringify(bad));
  }
  const live = pairRequestDocument({ ...ok, tradingMode: 'live', confirmLive: true }, NOW);
  assert.equal(live.tradingMode, 'live');
  assert.equal('confirmLive' in live, false, 'the confirmation is the dashboard\'s check, not part of the request');
});

const catalog = {
  tradable: ['BTC-EUR', 'ETH-EUR'],
  cap: '4', // the engine writes numbers as strings
  quoteCurrency: 'EUR',
  pairs: [
    { symbol: 'BTC-EUR', base: 'BTC', tradable: true, permitted: true },
    { symbol: 'XRP-EUR', base: 'XRP', tradable: false, permitted: true },
    { symbol: 'ETH-EUR', base: 'ETH', tradable: true, permitted: true },
    { symbol: 'SOL-EUR', base: 'SOL', tradable: false, permitted: true },
    { symbol: 'SOL-USD', base: 'SOL', tradable: false, permitted: false },
    { symbol: 'BAD', base: 'BAD', tradable: false, permitted: true },
  ],
  updatedAt: '2026-10-27T09:00:00Z',
};

test('the view: watched pairs, every permitted pair to add in order, pending changes and the history, newest first', () => {
  const requests = [
    { id: 'a', createdAt: '2026-10-27T09:00:00Z', action: 'add', symbol: 'XRP-EUR', status: 'rejected', events: [{ detail: 'too thin' }] },
    { id: 'b', createdAt: '2026-10-27T09:29:00Z', action: 'add', symbol: 'SOL-EUR', status: 'new' },
  ];
  const view = pairsView({ mode: 'paper', catalog, list: { symbols: ['BTC-EUR', 'ETH-EUR'], updatedAt: '2026-10-27T08:00:00Z' }, requests }, NOW);
  assert.equal(view.editable, true);
  assert.equal(view.cap, 4);
  assert.equal(view.full, false);
  assert.deepEqual(view.tradable, [{ symbol: 'BTC-EUR', adopted: false, pending: null }, { symbol: 'ETH-EUR', adopted: false, pending: null }]);
  assert.deepEqual(view.available, [{ symbol: 'SOL-EUR', base: 'SOL', pending: 'add' }, { symbol: 'XRP-EUR', base: 'XRP', pending: null }],
    'every permitted pair not yet watched, sorted; another currency and a malformed symbol are left out');
  assert.deepEqual(view.requests.map((r) => [r.id, r.status, r.detail]), [['b', 'new', null], ['a', 'rejected', 'too thin']]);
  assert.equal('configuredOnly' in view || 'openToAnyPair' in view, false, 'no pair lists in configuration since ADR-0021');
});

test('an empty list, and the held coins the owner hasn\'t added', () => {
  const current = { tradable: ['SOL-EUR'], cap: '50', quoteCurrency: 'EUR', adopted: ['SOL-EUR'], pairs: catalog.pairs, updatedAt: catalog.updatedAt };
  const view = pairsView({ mode: 'paper', catalog: current, list: null, requests: [] }, NOW);
  assert.deepEqual(view.tradable, [{ symbol: 'SOL-EUR', adopted: true, pending: null }]);
  const empty = pairsView({ mode: 'paper', catalog: { ...current, tradable: [], adopted: [] }, list: { symbols: [] }, requests: [] }, NOW);
  assert.deepEqual(empty.tradable, []);
  assert.equal(empty.available.length, 4);
});

test('the saved list wins over the catalog\'s copy, and a full list says so', () => {
  const view = pairsView({ mode: 'paper', catalog, list: { symbols: ['BTC-EUR', 'ETH-EUR', 'SOL-EUR', 'XRP-EUR'] }, requests: [] }, NOW);
  assert.equal(view.full, true);
  assert.deepEqual(view.available, []);
});

test('without a catalog the panel is read-only; live is editable like paper once its engine has written one', () => {
  assert.equal(pairsView({ mode: 'paper', catalog: null, list: null, requests: [] }, NOW).editable, false);
  assert.equal(pairsView({ mode: 'live', catalog: null, list: null, requests: [] }, NOW).editable, false);
  const live = pairsView({ mode: 'live', catalog, list: null, requests: [{ id: 'a', createdAt: '2026-10-27T09:00:00Z', action: 'add', symbol: 'SOL-EUR', tradingMode: 'live' }] }, NOW);
  assert.equal(live.editable, true);
  assert.equal(live.readOnlyReason, null);
  assert.equal(live.requests[0].tradingMode, 'live');
});

test('the pairs note: the currency, the cap, how many are watched, and that trading is the strategy\'s call', () => {
  const V = require('../../app/view.js');
  const note = (catalogue, symbols) => V.pairsNote(pairsView({ mode: 'paper', catalog: catalogue, list: { symbols }, requests: [] }, NOW));

  const open = note(catalog, ['BTC-EUR', 'ETH-EUR']);
  assert.equal(open.note, 'You can watch any EUR pair the exchange lists, up to 4 at once (2 watched now). The strategy\'s filters decide at every scan which of them trade.');
  assert.deepEqual(open.options.map((p) => p.symbol), ['XRP-EUR', 'SOL-EUR'].sort());
  const full = note(catalog, ['BTC-EUR', 'ETH-EUR', 'SOL-EUR', 'XRP-EUR']);
  assert.match(full.note, /remove one to add another/);
  assert.equal(full.empty, 'the limit is reached');
  assert.equal(note(null, []).note, 'The engine hasn\'t listed the exchange\'s pairs yet; it does when it starts.');
  assert.equal(note({ ...catalog, cap: undefined }, []).note, 'You can watch any EUR pair the exchange lists (0 watched now). The strategy\'s filters decide at every scan which of them trade.');
});
