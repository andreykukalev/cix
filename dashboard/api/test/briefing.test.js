'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { briefing, highlights, link } = require('../src/briefing');
const { summarize } = require('../src/summarize');

const NOW = Date.parse('2026-10-25T13:00:00Z');

// As the engine writes it: numbers as strings, nulls left out.
const status = {
  updatedAt: '2026-10-25T12:59:30+00:00',
  lastScan: {
    at: '2026-10-25T12:57:57+00:00', ranked: '3', excluded: '1', entrySignals: '1', exitSignals: '1', signalKey: 'k',
    market: [
      { symbol: 'ETH-EUR', rank: '1', trend: 'up', return1h: '0.003', return24h: '0.021', volatility: '0.008', volumeRatio: '1.1', signal: 'buy' },
      { symbol: 'SOL-EUR', rank: '2', trend: 'down', return1h: '-0.025', return24h: '-0.12', volatility: '0.024', volumeRatio: '3.1', note: 'trend down' },
      { symbol: 'XRP-EUR', rank: '3', trend: 'flat', return1h: '0.001', return24h: '0.01', volatility: '0.006', volumeRatio: '0.9' },
      { symbol: 'BTC-EUR', signal: 'sell (stop)', note: 'costs-exceed-volatility' },
    ],
  },
  portfolio: { holdings: [{ symbol: 'BTC-EUR', valueQuote: '5.00' }] },
};

const view = (at, data, summary = 'Calm ahead of the Fed; nothing major for the coins.') => ({
  id: `v-${at}`, at, kind: 'market-view', actor: 'analyst', runId: 'a-1', specVersion: 'analyst@v2#x', summary, data,
});

const goodHighlights = JSON.stringify([
  { title: 'FOMC decision at 18:00 UTC today', impact: 'high', direction: 'risk', source: 'https://www.example.com/fed', symbols: ['BTC-EUR'], detail: 'A surprise could move every coin.' },
  { title: 'Bad link is dropped here', impact: 'high', direction: 'risk', source: 'javascript:alert(1)' },
  { title: 'Unknown impact is dropped', impact: 'huge', direction: 'risk', source: 'https://example.com/x' },
  { title: 'Large unlock for SOL this week', impact: 'medium', direction: 'watch', source: 'https://example.com/unlock', symbols: ['SOL-EUR', '<b>x</b>'] },
]);

test('the latest market view in the last two days is shown with its checked highlights', () => {
  const b = briefing(status, [
    view('2026-10-25T06:00:00+00:00', { regime: 'neutral', validUntil: '2026-10-25T12:00:00+00:00', avoid: '', sources: '' }),
    view('2026-10-25T10:00:00+00:00', { regime: 'risk-off', validUntil: '2026-10-25T16:00:00+00:00', avoid: 'SOL-EUR', sources: 'https://example.com/fed http://insecure.example.com', highlights: goodHighlights }),
  ], NOW);

  assert.equal(b.analyst.regime, 'risk-off');
  assert.equal(b.analyst.active, true);
  assert.deepEqual(b.analyst.avoid, ['SOL-EUR']);
  assert.deepEqual(b.analyst.sources, [{ url: 'https://example.com/fed', host: 'example.com' }]);
  assert.deepEqual(b.analyst.highlights.map((h) => h.title), ['FOMC decision at 18:00 UTC today', 'Large unlock for SOL this week']);
  assert.equal(b.analyst.highlights[0].sourceHost, 'example.com');
  assert.deepEqual(b.analyst.highlights[1].symbols, ['SOL-EUR']);
});

test('an old or missing market view shows no analyst section', () => {
  assert.equal(briefing(status, [view('2026-10-23T06:00:00+00:00', { regime: 'neutral', validUntil: '2026-10-23T12:00:00+00:00' })], NOW).analyst, null);
  assert.equal(briefing(status, [], NOW).analyst, null);
  const expired = briefing(status, [view('2026-10-25T01:00:00+00:00', { regime: 'risk-off', validUntil: '2026-10-25T07:00:00+00:00', avoid: 'SOL-EUR' })], NOW);
  assert.equal(expired.analyst.active, false);
  assert.ok(!expired.attention.find((a) => a.symbol === 'SOL-EUR').reasons.includes('Analyst: avoid new entries'), 'an expired avoid is history, not a warning');
});

test('needs attention: signals, big moves, volume and volatility, most pressing first, quiet coins left out', () => {
  const b = briefing(status, [], NOW);

  assert.deepEqual(b.attention.map((a) => [a.symbol, a.severity, a.direction]), [
    ['SOL-EUR', 3, 'risk'], // -12% in 24h, -2.5% in an hour, volume, volatility
    ['BTC-EUR', 3, 'risk'], // sell signal on a held coin
    ['ETH-EUR', 2, 'opportunity'], // buy signal
  ]);
  assert.deepEqual(b.attention[0].reasons, ['-12.0% in 24h', '-2.5% in the last hour', 'Volume 3.1× usual', 'Volatile: 2.4% hourly range']);
  assert.deepEqual(b.attention[1].reasons, ['Sell signal: stop']);
  assert.equal(b.attention[1].held, true);
  assert.equal(b.market.length, 4);
  assert.equal(b.market[0].return24h, 0.021);
  assert.equal(b.scanStale, false);
});

test('a stale or missing scan is flagged', () => {
  assert.equal(briefing({ lastScan: { at: '2026-10-25T11:00:00Z', market: [] } }, [], NOW).scanStale, true);
  const none = briefing(null, [], NOW);
  assert.equal(none.scanStale, true);
  assert.deepEqual([none.attention, none.market], [[], []]);
});

test('highlights survive bad input: not JSON, not a list, too many, too long, control characters', () => {
  assert.deepEqual(highlights('not json'), []);
  assert.deepEqual(highlights('{"title":"x"}'), []);
  const many = JSON.stringify(Array.from({ length: 9 }, (_, i) => ({ title: `Item number ${i}‮`, impact: 'medium', direction: 'watch', source: `https://example.com/${i}`, detail: 'x'.repeat(500) })));
  const out = highlights(many);
  assert.equal(out.length, 5);
  assert.equal(out[0].title, 'Item number 0');
  assert.equal(out[0].detail.length, 300);
  assert.equal(link('https://user:pw@example.com/'), null);
  assert.equal(link('data:text/html,hi'), null);
});

test('the dashboard carries the briefing, and the scan rows only there', () => {
  const v = summarize({ mode: 'paper', status, engineState: null, paperAccount: null, journal: [], marketViews: [view('2026-10-25T10:00:00+00:00', { regime: 'neutral', validUntil: '2026-10-25T16:00:00+00:00' })], stateDocs: [] }, NOW);

  assert.equal(v.briefing.analyst.regime, 'neutral');
  assert.equal(v.briefing.market.length, 4);
  assert.equal(v.cycle.lastScan.market, undefined);
  assert.equal(v.cycle.lastScan.signalKey, 'k');
});
