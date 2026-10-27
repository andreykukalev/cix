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

test('without a catalog the panel is read-only; live is editable like paper once its engine has written one', () => {
  assert.equal(pairsView({ mode: 'paper', catalog: null, list: null, requests: [] }, NOW).editable, false);
  assert.equal(pairsView({ mode: 'live', catalog: null, list: null, requests: [] }, NOW).editable, false);
  const live = pairsView({ mode: 'live', catalog, list: null, requests: [{ id: 'a', createdAt: '2026-10-27T09:00:00Z', action: 'add', symbol: 'SOL-EUR', tradingMode: 'live' }] }, NOW);
  assert.equal(live.editable, true);
  assert.equal(live.readOnlyReason, null);
  assert.equal(live.requests[0].tradingMode, 'live');
});

// The catalog as the engine writes it when config/risk-limits.json has no AllowedQuoteCurrency: the list's own quote
// currency, a cap of the list's length, and only the listed pairs permitted.
const configuredOnly = {
  tradable: ['BTC-EUR', 'ETH-EUR'], cap: '2', quoteCurrency: 'EUR', alwaysPermitted: ['BTC-EUR', 'ETH-EUR'],
  pairs: [
    { symbol: 'BTC-EUR', base: 'BTC', tradable: true, permitted: true },
    { symbol: 'ETH-EUR', base: 'ETH', tradable: true, permitted: true },
  ],
};

test('the pairs note: what the panel says in each case, and what the empty add list shows', () => {
  const V = require('../../app/view.js');
  const note = (catalogue, symbols) => V.pairsNote(pairsView({ mode: 'paper', catalog: catalogue, list: { symbols }, requests: [] }, NOW));

  const widen = note(configuredOnly, ['BTC-EUR', 'ETH-EUR']);
  assert.match(widen.note, /the owner adds "AllowedQuoteCurrency": "EUR".*"MaxTradableSymbols"/, 'only the configured pairs, all tradable: what to add');
  assert.equal(widen.empty, 'no other pairs permitted');

  const back = note(configuredOnly, ['BTC-EUR']);
  assert.match(back.note, /turn the pairs in config\/risk-limits.json off and on again/, 'a removed one can be added back');
  assert.deepEqual(back.options.map((p) => p.symbol), ['ETH-EUR']);

  assert.match(note(catalog, ['BTC-EUR', 'ETH-EUR']).note, /add any EUR pair the exchange lists/);
  const full = note(catalog, ['BTC-EUR', 'ETH-EUR', 'SOL-EUR', 'XRP-EUR']);
  assert.match(full.note, /remove one to add another/);
  assert.equal(full.empty, 'the limit is reached');

  // Live names its own limits file.
  const liveNote = V.pairsNote(pairsView({ mode: 'live', catalog: configuredOnly, list: { symbols: ['BTC-EUR'] }, requests: [] }, NOW)).note;
  assert.match(liveNote, /turn the pairs in config\/risk-limits\.live\.json off and on again/);
  assert.equal(pairsView({ mode: 'paper', catalog: configuredOnly, list: null, requests: [] }, NOW).configuredOnly, true);
  assert.equal(pairsView({ mode: 'paper', catalog, list: null, requests: [] }, NOW).configuredOnly, false);
  assert.equal(pairsView({ mode: 'paper', catalog: null, list: null, requests: [] }, NOW).configuredOnly, false);
});
