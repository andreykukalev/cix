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

test('a pair request is refused unless well formed and for paper', () => {
  const ok = { id: ID, action: 'remove', symbol: 'SOL-EUR' };
  for (const bad of [null, [], 'x', { ...ok, id: 'x' }, { ...ok, action: 'delete' }, { ...ok, action: undefined }, { ...ok, symbol: 'SOL' },
    { ...ok, symbol: 'SOL-EUR-X' }, { ...ok, symbol: '../x' }, { ...ok, symbol: 5 }, { ...ok, symbol: 'A'.repeat(21) + '-EUR' },
    { ...ok, tradingMode: 'live' }, { ...ok, note: 5 }]) {
    assert.throws(() => pairRequestDocument(bad, NOW), RequestError, JSON.stringify(bad));
  }
});

const catalog = {
  tradable: ['BTC-EUR', 'ETH-EUR'],
  cap: '4', // the engine writes numbers as strings
  quoteCurrency: 'EUR',
  alwaysPermitted: ['BTC-EUR', 'ETH-EUR'],
  pairs: [
    { symbol: 'BTC-EUR', base: 'BTC', tradable: true, permitted: true },
    { symbol: 'ETH-EUR', base: 'ETH', tradable: true, permitted: true },
    { symbol: 'SOL-EUR', base: 'SOL', tradable: false, permitted: true },
    { symbol: 'XRP-EUR', base: 'XRP', tradable: false, permitted: true },
    { symbol: 'BAD', base: 'BAD', tradable: false, permitted: true },
  ],
  updatedAt: '2026-10-27T09:00:00Z',
};

test('the view: tradable pairs, the ones to add, pending changes and the history, newest first', () => {
  const requests = [
    { id: 'a', createdAt: '2026-10-27T09:00:00Z', action: 'add', symbol: 'XRP-EUR', status: 'rejected', events: [{ detail: 'too thin' }] },
    { id: 'b', createdAt: '2026-10-27T09:29:00Z', action: 'add', symbol: 'SOL-EUR', status: 'new' },
  ];
  const view = pairsView({ mode: 'paper', catalog, list: { symbols: ['BTC-EUR', 'ETH-EUR'], updatedAt: '2026-10-27T08:00:00Z' }, requests }, NOW);
  assert.equal(view.editable, true);
  assert.equal(view.cap, 4);
  assert.equal(view.full, false);
  assert.equal(view.openToAnyPair, true);
  assert.deepEqual(view.tradable, [{ symbol: 'BTC-EUR', configured: true, pending: null }, { symbol: 'ETH-EUR', configured: true, pending: null }]);
  assert.deepEqual(view.available, [{ symbol: 'SOL-EUR', base: 'SOL', pending: 'add' }, { symbol: 'XRP-EUR', base: 'XRP', pending: null }]);
  assert.deepEqual(view.requests.map((r) => [r.id, r.status, r.detail]), [['b', 'new', null], ['a', 'rejected', 'too thin']]);
});

test('the saved list wins over the catalog\'s copy, and a full list says so', () => {
  const view = pairsView({ mode: 'paper', catalog, list: { symbols: ['BTC-EUR', 'ETH-EUR', 'SOL-EUR', 'XRP-EUR'] }, requests: [] }, NOW);
  assert.equal(view.full, true);
  assert.deepEqual(view.available, []);
});

test('without a catalog, or in live, the panel is read-only', () => {
  assert.equal(pairsView({ mode: 'paper', catalog: null, list: null, requests: [] }, NOW).editable, false);
  const live = pairsView({ mode: 'live', catalog, list: null, requests: [] }, NOW);
  assert.equal(live.editable, false);
  assert.match(live.readOnlyReason, /live/i);
});
