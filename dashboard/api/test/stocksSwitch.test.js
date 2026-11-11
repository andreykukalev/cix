'use strict';
// The owner's Stop and Start for the stocks advisory (ADR-0032): the newest press found in a few reads, and checked.
const test = require('node:test');
const assert = require('node:assert/strict');
const { latestSwitch, advisorState, switchView, switchValue, switchDocument } = require('../src/stocksSwitch');

const AT = Date.parse('2026-11-11T16:00:00Z');

/** A reader over n presses, pause first, a minute apart; it counts its reads. */
function presses(n, extra = {}) {
  const docs = new Map();
  for (let i = 1; i <= n; i += 1) {
    const value = switchValue({ action: i % 2 ? 'pause' : 'resume' }, AT + i * 60_000);
    docs.set(`switch-${i}`, switchDocument(value, i));
  }
  for (const [id, doc] of Object.entries(extra)) docs.set(id, doc);
  const reader = {
    reads: 0,
    async readDocument(container, id) {
      assert.equal(container, 'control');
      reader.reads += 1;
      return docs.get(id) ?? null;
    },
  };
  return reader;
}

test('never pressed: started, and the page says nothing was pressed', async () => {
  const reader = presses(0);
  assert.equal(await latestSwitch(reader), null);
  assert.equal(reader.reads, 1);
  assert.deepEqual(advisorState(null), { paused: false, since: null, sequence: 0, unreadable: false });
});

test('the newest press decides, however many there are, in a few reads', async () => {
  for (const n of [1, 2, 3, 4, 5, 7, 8, 9, 16, 17, 31, 100]) {
    const reader = presses(n);
    const state = advisorState(await latestSwitch(reader));
    assert.deepEqual(state, { paused: n % 2 === 1, since: new Date(AT + n * 60_000).toISOString(), sequence: n, unreadable: false }, `${n}`);
    assert.ok(reader.reads <= 16, `${n} presses took ${reader.reads} reads`);
  }
});

test('a newest press that can\'t be read counts as started, and says so', async () => {
  for (const bad of [
    { id: 'switch-3', value: { sequence: 3, action: 'halt', submittedAt: '2026-11-11T16:00:00Z' } },
    { id: 'switch-3', value: { sequence: 2, action: 'pause', submittedAt: '2026-11-11T16:00:00Z' } },
    { id: 'switch-3', value: { sequence: 3, action: 'pause', submittedAt: 'yesterday' } },
    { id: 'switch-3', value: 'pause' },
    { id: 'switch-3' },
  ]) {
    const state = advisorState(await latestSwitch(presses(2, { 'switch-3': bad })));
    assert.deepEqual(state, { paused: false, since: null, sequence: 3, unreadable: true }, JSON.stringify(bad));
  }
});

test('a press is pause or resume, with the time it was sent', () => {
  assert.deepEqual(switchValue({ action: 'pause', note: 'ignored' }, AT), { action: 'pause', submittedAt: '2026-11-11T16:00:00.000Z' });
  for (const body of [{ action: 'stop' }, {}, null, 'pause']) assert.throws(() => switchValue(body, AT), RangeError);
  assert.deepEqual(switchView(switchDocument(switchValue({ action: 'resume' }, AT), 4), 4), { sequence: 4, action: 'resume', submittedAt: '2026-11-11T16:00:00.000Z' });
});
