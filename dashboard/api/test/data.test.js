'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadDashboard, loadJournalDay, journalRequest } = require('../src/data');

const NOW = Date.parse('2026-10-25T13:00:00Z');

function fakeReader({ failStatus = false } = {}) {
  const queries = [];
  return {
    queries,
    async readDocument(container, id) {
      if (failStatus && id.startsWith('engine-status')) throw Object.assign(new Error('x'), { status: 503 });
      if (id === 'engine-state-paper') return { id, value: { killSwitch: { engaged: false }, orders: [], positions: {} } };
      return null;
    },
    async query(container, sql, options = {}) {
      queries.push({ container, sql, ...options });
      if (container === 'journal' && options.maxItems === 1 && options.partitionKey === '2026-10-24') {
        return [{ id: 'v1', day: '2026-10-24', entry: { id: 'v1', at: '2026-10-24T20:00:00+00:00', kind: 'market-view', actor: 'analyst', runId: 'a', summary: 'Quiet overnight.', data: { regime: 'neutral', validUntil: '2026-10-25T02:00:00+00:00' } } }];
      }
      if (container === 'journal' && options.maxItems === 1) return [];
      if (container === 'journal' && options.partitionKey === '2026-10-25') {
        return [{ id: 'j1', day: '2026-10-25', entry: { id: 'j1', at: '2026-10-25T12:00:00+00:00', kind: 'tool-call', actor: 'trader', runId: 'r', summary: 'scan_market' } }];
      }
      return [];
    },
  };
}

test('reads today and yesterday as single-partition queries and the state list', async () => {
  const reader = fakeReader();
  const view = await loadDashboard(reader, 'paper', NOW);

  assert.deepEqual(reader.queries.filter((q) => q.container === 'journal').map((q) => q.partitionKey), ['2026-10-25', '2026-10-24', '2026-10-25', '2026-10-24']);
  const views = reader.queries.filter((q) => q.container === 'journal' && q.maxItems === 1);
  assert.equal(views.length, 2);
  assert.ok(views.every((q) => q.sql.startsWith('SELECT TOP 1 * FROM c WHERE c.entry.kind = @kind') && q.parameters[0].value === 'market-view'));
  assert.ok(reader.queries.filter((q) => q.container === 'journal').every((q) => q.sql.includes('ORDER BY')));
  const stateList = reader.queries.find((q) => q.container === 'state');
  assert.equal(stateList.partitionKey, undefined);
  assert.ok(!stateList.sql.includes('ORDER BY'), 'a cross-partition query must stay simple for the REST gateway');
  assert.equal(view.activity.length, 1);
  assert.equal(view.engine.halted, false);
  assert.equal(view.briefing.analyst.summary, 'Quiet overnight.', "yesterday's view comes from its own query, not the recent entries");
});

test("each mode reads only its own journal entries, and untagged ones are paper's", async () => {
  for (const mode of ['paper', 'live']) {
    const reader = fakeReader();
    await loadDashboard(reader, mode, NOW);
    await loadJournalDay(reader, '2026-10-25', '', '50', NOW, mode);
    await loadJournalDay(reader, '2026-10-25', 'fill', '50', NOW, mode);
    await loadActivity(reader, 'day', NOW, mode);
    const journal = reader.queries.filter((q) => q.container === 'journal');
    assert.equal(journal.length, 7);
    for (const q of journal) {
      assert.deepEqual(q.parameters.find((p) => p.name === '@mode'), { name: '@mode', value: mode });
      if (mode === 'paper') assert.ok(q.sql.includes('(NOT IS_DEFINED(c.mode) OR c.mode = @mode)'), q.sql);
      else assert.ok(q.sql.includes('c.mode = @mode') && !q.sql.includes('IS_DEFINED'), q.sql);
    }
  }
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25', '', '50', NOW, 'both'), RangeError);
});

test('a failed read is reported, the rest still shows', async () => {
  const view = await loadDashboard(fakeReader({ failStatus: true }), 'paper', NOW);
  assert.equal(view.problems.find((p) => p.kind === 'read').message, "Couldn't read the engine status (503).");
  assert.equal(view.activity.length, 1);
});

test('rejects unknown modes and malformed journal filters', async () => {
  await assert.rejects(() => loadDashboard(fakeReader(), 'prod', NOW), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25; DROP', '', '10', NOW), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25', 'tool call', '10', NOW), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25', 'made-up-kind', '10', NOW), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-09-01', '', '10', NOW), RangeError); // older than 14 days
});

test('a journal day is one partition, parameterised, capped, and lists entries without their raw data', async () => {
  const reader = fakeReader();
  const entries = await loadJournalDay(reader, '2026-10-25', 'tool-call', '5000', NOW);
  const q = reader.queries[0];
  assert.equal(q.partitionKey, '2026-10-25');
  assert.match(q.sql, /^SELECT TOP 200 \* FROM c WHERE c\.entry\.kind = @kind AND \(NOT IS_DEFINED\(c\.mode\) OR c\.mode = @mode\) ORDER BY/);
  assert.deepEqual(q.parameters, [{ name: '@kind', value: 'tool-call' }, { name: '@mode', value: 'paper' }]);
  assert.deepEqual(Object.keys(entries[0]), ['id', 'at', 'kind', 'actor', 'runId', 'summary']);
});

test('journal requests are normalised so equal requests share one cache entry', () => {
  const keys = ['1', '01', '001', '50', 'abc'].map((limit) => journalRequest('2026-10-25', 'fill', limit, NOW).key);
  assert.deepEqual([...new Set(keys)], ['journal:2026-10-25:fill:50', 'journal:2026-10-25:fill:100']);
  assert.equal(journalRequest('2026-10-25', '', '150', NOW).top, 200);
});

const { loadActivity, loadRequests, KINDS } = require('../src/data');

test('activity reads only fills, across the day partitions the periods cover', async () => {
  const queries = [];
  const reader = { async query(container, sql, options) { queries.push({ container, sql, ...options }); return [{ at: '2026-10-25T10:00:00Z', data: { symbol: 'LINK-EUR', side: 'buy', filledQuote: '12.5', clientOrderId: 'c' } }]; } };
  const view = await loadActivity(reader, 'week', NOW);
  assert.equal(queries.length, 1);
  assert.equal(queries[0].partitionKey, undefined, 'cross-partition');
  assert.match(queries[0].sql, /c\.day >= @from AND c\.entry\.kind = @kind AND \(NOT IS_DEFINED\(c\.mode\) OR c\.mode = @mode\)$/);
  assert.deepEqual(queries[0].parameters, [{ name: '@from', value: '2026-08-03' }, { name: '@kind', value: 'fill' }, { name: '@mode', value: 'paper' }]);
  assert.equal(view.current.turnover, '12.50');
  assert.equal(view.current.trades, 1);
  assert.equal(view.truncated, false);
  await assert.rejects(loadActivity(reader, 'year', NOW), RangeError);
});

test('request history covers the last week, newest first', async () => {
  let seen;
  const reader = { async query(container, sql, options) { seen = { container, sql, ...options }; return [{ id: 'a', createdAt: '2026-10-24T00:00:00Z' }, { id: 'b', createdAt: '2026-10-25T00:00:00Z' }]; } };
  const view = await loadRequests(reader, NOW);
  assert.equal(seen.container, 'requests');
  assert.deepEqual(seen.parameters, [{ name: '@from', value: '2026-10-19' }, { name: '@type', value: 'manual-request' }]);
  assert.deepEqual(view.requests.map((r) => r.id), ['b', 'a']);
});

test('the journal filter knows the manual and scanner-filter kinds', () => {
  for (const kind of ['manual-request', 'scanner-filters', 'scan-eligibility']) assert.ok(KINDS.has(kind), kind);
});

const { loadBalanceHistory } = require('../src/data');

test('balance history reads one mode\'s hourly snapshots across days, oldest first, with each holding\'s value', async () => {
  const queries = [];
  const reader = {
    async query(container, sql, options) {
      queries.push({ container, sql, ...options });
      return [
        { at: '2026-10-25T12:00:00+00:00', data: { source: 'exchange', currency: 'EUR', equity: '101.5', cash: '81.5', exposure: '20', pnlToday: '1.5', 'value:BTC-EUR': '20', 'base:BTC-EUR': '0.0003' } },
        { at: '2026-10-25T11:00:00+00:00', data: { source: 'exchange', currency: 'EUR', equity: '100', cash: '100', exposure: '0', pnlToday: '0' } },
      ];
    },
  };

  const view = await loadBalanceHistory(reader, '7', NOW, 'live');

  assert.equal(queries.length, 1);
  assert.equal(queries[0].container, 'journal');
  assert.equal(queries[0].partitionKey, undefined, 'cross-partition');
  assert.match(queries[0].sql, /c\.day >= @from AND c\.entry\.kind = @kind AND c\.mode = @mode$/);
  assert.deepEqual(queries[0].parameters, [{ name: '@from', value: '2026-10-19' }, { name: '@kind', value: 'balance-snapshot' }, { name: '@mode', value: 'live' }]);
  assert.deepEqual(view.points.map((p) => p.equity), [100, 101.5]);
  assert.deepEqual(view.points[1].holdings, { 'BTC-EUR': 20 });
  assert.equal(view.points[1].source, 'exchange');
  assert.equal(view.truncated, false);
  assert.ok(KINDS.has('balance-snapshot'));
  await assert.rejects(loadBalanceHistory(reader, '365', NOW), RangeError);
  await assert.rejects(loadBalanceHistory(reader, '7', NOW, 'both'), RangeError);
});
