'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadDashboard, loadJournalDay } = require('../src/data');

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

  assert.deepEqual(reader.queries.filter((q) => q.container === 'journal').map((q) => q.partitionKey), ['2026-10-25', '2026-10-24']);
  assert.ok(reader.queries.filter((q) => q.container === 'journal').every((q) => q.sql.includes('ORDER BY')));
  const stateList = reader.queries.find((q) => q.container === 'state');
  assert.equal(stateList.partitionKey, undefined);
  assert.ok(!stateList.sql.includes('ORDER BY'), 'a cross-partition query must stay simple for the REST gateway');
  assert.equal(view.activity.length, 1);
  assert.equal(view.engine.halted, false);
});

test('a failed read is reported, the rest still shows', async () => {
  const view = await loadDashboard(fakeReader({ failStatus: true }), 'paper', NOW);
  assert.equal(view.problems.find((p) => p.kind === 'read').message, "Couldn't read the engine status (503).");
  assert.equal(view.activity.length, 1);
});

test('rejects unknown modes and malformed journal filters', async () => {
  await assert.rejects(() => loadDashboard(fakeReader(), 'prod', NOW), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25; DROP', '', '10'), RangeError);
  await assert.rejects(() => loadJournalDay(fakeReader(), '2026-10-25', 'tool call', '10'), RangeError);
});

test('a journal day is one partition, parameterised, capped, and hides raw data', async () => {
  const reader = fakeReader();
  const entries = await loadJournalDay(reader, '2026-10-25', 'tool-call', '5000');
  const q = reader.queries[0];
  assert.equal(q.partitionKey, '2026-10-25');
  assert.match(q.sql, /^SELECT TOP 300 \* FROM c WHERE c\.entry\.kind = @kind/);
  assert.deepEqual(q.parameters, [{ name: '@kind', value: 'tool-call' }]);
  assert.deepEqual(Object.keys(entries[0]), ['id', 'at', 'kind', 'actor', 'runId', 'summary']);
});
