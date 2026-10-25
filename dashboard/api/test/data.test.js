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

test('a journal day is one partition, parameterised, capped, and hides raw data', async () => {
  const reader = fakeReader();
  const entries = await loadJournalDay(reader, '2026-10-25', 'tool-call', '5000', NOW);
  const q = reader.queries[0];
  assert.equal(q.partitionKey, '2026-10-25');
  assert.match(q.sql, /^SELECT TOP 200 \* FROM c WHERE c\.entry\.kind = @kind/);
  assert.deepEqual(q.parameters, [{ name: '@kind', value: 'tool-call' }]);
  assert.deepEqual(Object.keys(entries[0]), ['id', 'at', 'kind', 'actor', 'runId', 'summary']);
});

test('journal requests are normalised so equal requests share one cache entry', () => {
  const keys = ['1', '01', '001', '50', 'abc'].map((limit) => journalRequest('2026-10-25', 'fill', limit, NOW).key);
  assert.deepEqual([...new Set(keys)], ['journal:2026-10-25:fill:50', 'journal:2026-10-25:fill:100']);
  assert.equal(journalRequest('2026-10-25', '', '150', NOW).top, 200);
});
