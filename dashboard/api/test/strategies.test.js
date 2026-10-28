'use strict';
// The Strategy tab's data (dashboard/api/src/strategies.js): the switch request the dashboard writes, and the view it shows.
const test = require('node:test');
const assert = require('node:assert/strict');
const { strategyRequestDocument, strategiesView, valueAt, parseLabel } = require('../src/strategies');
const { RequestError } = require('../src/requests');

const NOW = Date.parse('2026-10-27T09:30:00Z');
const ID = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

test('a switch request: the server\'s time, the owner as a role, a clean note', () => {
  const doc = strategyRequestDocument({ id: ID.toUpperCase(), name: 'safe', version: 1, note: 'calm‮down\n now' }, NOW);
  assert.deepEqual(doc, {
    id: ID, day: '2026-10-27', createdAt: '2026-10-27T09:30:00.000Z', type: 'strategy-selection', name: 'safe', version: 1,
    requestedBy: 'owner', tradingMode: 'paper', status: 'new', note: 'calm down now',
  });
});

test('a switch request is refused unless well formed, and for live unless the owner confirmed it', () => {
  const ok = { id: ID, name: 'safe', version: 1 };
  for (const bad of [null, [], { ...ok, id: 'x' }, { ...ok, name: 'Safe' }, { ...ok, name: '../x' }, { ...ok, version: '1' }, { ...ok, version: 1.5 },
    { ...ok, version: 0 }, { ...ok, version: 10000 }, { ...ok, tradingMode: 'live' }, { ...ok, tradingMode: 'live', confirmLive: 1 },
    { ...ok, tradingMode: 'demo' }, { ...ok, note: 5 }]) {
    assert.throws(() => strategyRequestDocument(bad, NOW), RequestError, JSON.stringify(bad));
  }
  const live = strategyRequestDocument({ ...ok, version: 2, tradingMode: 'live', confirmLive: true }, NOW);
  assert.deepEqual([live.tradingMode, live.name, live.version, 'confirmLive' in live], ['live', 'safe', 2, false]);
});

test('values are read at schema keys, top level or under scanner', () => {
  const p = { orderQuote: 10, scanner: { stopAtr: 1.5 } };
  assert.equal(valueAt(p, 'orderQuote'), 10);
  assert.equal(valueAt(p, 'scanner.stopAtr'), 1.5);
  assert.equal(valueAt(p, 'scanner.missing'), null);
  assert.equal(valueAt(null, 'orderQuote'), null);
  assert.deepEqual(parseLabel('balanced@v1#3f2a9c1b04de'), { label: 'balanced@v1#3f2a9c1b04de', name: 'balanced', version: 1, hash: '3f2a9c1b04de' });
  assert.equal(parseLabel('<b>@v1'), null);
});

const SCHEMA = {
  writtenAt: '2026-10-27T08:00:00Z',
  parameters: [
    { key: 'scanner.fastEmaPeriod', group: 'trend', label: 'Fast EMA period', explanation: 'e', kind: 'integer', min: '3', max: '54', unit: 'candles', applies: 'open-positions-too' },
    { key: 'orderQuote', group: 'size', label: 'Largest order', explanation: 'e', kind: 'number', min: '1', max: '40', unit: 'EUR', applies: 'new-trades', boundBy: 'maxOrderQuote' },
  ],
  mandatory: [{ key: 'maxOrderQuote', label: 'Largest order', explanation: 'e', value: '40', source: 'risk-limits' }],
};
const VERSIONS = [
  { name: 'good', version: '1', status: 'Paper', parameters: { orderQuote: 5, scanner: { fastEmaPeriod: 20 } }, allowedTools: ['place_order'] },
  { name: 'risky', version: '2', status: 'Live', purpose: 'p', riskProfile: 'r', parameters: { orderQuote: 20, scanner: { fastEmaPeriod: 12 } }, allowedTools: ['place_order'] },
  { name: 'risky', version: '1', status: 'Live', parameters: { orderQuote: 20, scanner: { fastEmaPeriod: 12 } }, allowedTools: ['place_order'] },
  { name: 'safe', version: '1', status: 'Live', parameters: { orderQuote: 5, scanner: { fastEmaPeriod: 20 } }, allowedTools: ['place_order'] },
  { name: 'safe', version: '2', status: 'Draft', parameters: { orderQuote: 5, scanner: { fastEmaPeriod: 20 } }, allowedTools: ['place_order'] },
  { name: 'balanced', version: '1', status: 'Live', parameters: { orderQuote: 10, scanner: { fastEmaPeriod: 20 } }, allowedTools: ['place_order'] },
  { name: 'observer', version: '1', status: 'Paper', parameters: {}, allowedTools: ['get_portfolio'] },
  { name: '<script>', version: '1', status: 'Live' },
];

test('the view: Safe, Balanced, Risky first, the running one marked, every parameter grouped and explained', () => {
  const view = strategiesView({
    mode: 'paper', schema: SCHEMA, versions: VERSIONS,
    status: { strategy: 'balanced@v1#abc', startedAt: '2026-10-27T07:00:00Z', strategySelection: { state: 'active', requested: 'balanced@v1', problems: [], activatedAt: '2026-10-27T09:00:00Z' } },
    requests: [{ id: 'a', createdAt: '2026-10-27T08:59:00Z', name: 'balanced', version: 1, status: 'accepted', events: [{ detail: 'Selected balanced@v1.' }] }],
  }, NOW);

  // The newest version marked live of each, the same list as live's (ADR-0022): no draft, older or paper-only version.
  assert.deepEqual(view.strategies.map((s) => s.key), ['safe@v1', 'balanced@v1', 'risky@v2']);
  assert.deepEqual(view.strategies.filter((s) => s.active).map((s) => s.key), ['balanced@v1']);
  assert.match(view.note, /same strategies as live/);
  const running = strategiesView({ mode: 'paper', schema: SCHEMA, versions: VERSIONS, status: { strategy: 'observer@v1#abc' }, requests: [] }, NOW);
  assert.deepEqual(running.strategies.map((s) => s.key), ['safe@v1', 'balanced@v1', 'risky@v2', 'observer@v1'], 'the running one is listed, whatever it is');
  assert.equal(running.strategies.find((s) => s.name === 'observer').canTrade, false);
  assert.deepEqual(view.strategies.find((s) => s.name === 'risky').values, { 'scanner.fastEmaPeriod': 12, orderQuote: 20 });
  assert.deepEqual(view.groups.map((g) => g.title), ['Trend indicators', 'Position size']);
  assert.equal(view.groups[0].parameters[0].applies, 'New trades and open positions');
  assert.equal(view.groups[1].parameters[0].boundBy, 'maxOrderQuote');
  assert.equal(view.active.since, '2026-10-27T09:00:00Z');
  assert.equal(view.editable, true);
  assert.equal(view.requests[0].detail, 'Selected balanced@v1.');
});

test('live switches too, saying only specs marked live are listed, and the view copes with nothing written yet', () => {
  const view = strategiesView({ mode: 'live', schema: null, versions: [], status: null, requests: [] }, NOW);
  assert.equal(view.editable, true);
  assert.equal(view.readOnlyReason, null);
  assert.match(view.note, /marked live/);
  const liveVersions = [...VERSIONS, { name: 'safe', version: '3', status: 'Live', parameters: {}, allowedTools: ['place_order'] }];
  const both = ['paper', 'live'].map((mode) => strategiesView({ mode, schema: SCHEMA, versions: liveVersions, status: null, requests: [] }, NOW)
    .strategies.map((x) => x.key));
  assert.deepEqual(both, [['safe@v3', 'balanced@v1', 'risky@v2'], ['safe@v3', 'balanced@v1', 'risky@v2']], 'both modes offer the same presets');
  assert.equal(view.active, null);
  assert.deepEqual([view.groups, view.strategies, view.mandatory], [[], [], []]);
});

test('the switch preview: which watched coins would enter and leave trading, with the reason, and which are held (ADR-0021)', () => {
  const status = {
    strategy: 'balanced@v1#abc',
    lastScan: {
      at: '2026-10-27T09:25:00Z',
      market: [{ symbol: 'BTC-EUR', rank: 1 }, { symbol: 'ETH-EUR', rank: 2 }, { symbol: 'DOGE-EUR', rank: null }, { symbol: 'SOL-EUR', rank: null }],
      previews: [
        { name: 'safe', version: 1, passing: ['BTC-EUR'], excluded: { 'ETH-EUR': 'low-volume', 'DOGE-EUR': 'too-volatile', 'SOL-EUR': '<b>' } },
        { name: 'risky', version: 2, passing: ['BTC-EUR', 'ETH-EUR', 'DOGE-EUR', 'SOL-EUR', '../x'], excluded: {} },
      ],
    },
    portfolio: { holdings: [{ symbol: 'ETH-EUR', valueQuote: '12.5' }, { symbol: 'SOL-EUR', valueQuote: '3' }] },
  };
  const view = strategiesView({ mode: 'paper', schema: SCHEMA, versions: VERSIONS, status, requests: [] }, NOW);
  const preview = (name) => view.strategies.find((s) => s.name === name).preview;
  assert.deepEqual(preview('safe'), {
    at: '2026-10-27T09:25:00Z',
    enter: [],
    leave: [{ symbol: 'ETH-EUR', held: true, reason: 'low-volume' }],
  });
  assert.deepEqual(preview('risky').enter, [{ symbol: 'DOGE-EUR', held: false }, { symbol: 'SOL-EUR', held: true }], 'a malformed symbol is left out');
  assert.deepEqual(preview('risky').leave, []);
  assert.equal(preview('balanced'), null, 'the running strategy');
  const unpreviewed = strategiesView({ mode: 'paper', schema: SCHEMA, versions: VERSIONS, status: { ...status, lastScan: { ...status.lastScan, previews: [] } }, requests: [] }, NOW);
  assert.equal(unpreviewed.strategies.find((s) => s.name === 'safe').preview, null, 'not previewed yet');

  const V = require('../../app/view.js');
  assert.deepEqual(V.strategyCoins(preview('safe')), [
    'Leave trading: ETH-EUR (held): low-volume.',
    'You hold ETH-EUR: they keep their stop and target, and the new strategy buys no more.',
  ]);
  assert.deepEqual(V.strategyCoins(preview('risky')), ['Enter trading: DOGE-EUR, SOL-EUR (held).']);
  assert.deepEqual(V.strategyCoins({ enter: [], leave: [] }), ['No coin enters or leaves trading.']);
  assert.match(V.strategyCoins(null)[0], /^No preview for this strategy yet/);
});
