'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { summarize, engineHealth } = require('../src/summarize');

const NOW = Date.parse('2026-10-25T13:00:00Z');

const status = {
  mode: 'paper', updatedAt: '2026-10-25T12:59:30+00:00', startedAt: '2026-10-25T12:00:00+00:00', strategy: 'risky@v1#abc',
  strategySelection: { state: 'active', requested: 'risky@v1', requestedBy: 'owner', requestedAt: '2026-10-25T12:30:00+00:00', problems: [], checkedAt: '2026-10-25T12:59:00+00:00', activatedAt: '2026-10-25T12:31:00+00:00' },
  halted: false, lastReconciledAt: '2026-10-25T12:58:00+00:00', lastReconcileAttemptAt: '2026-10-25T12:58:00+00:00',
  lastReconcileProblem: null, unquotedSymbols: [],
  lastScan: { at: '2026-10-25T12:57:57+00:00', ranked: '10', excluded: '0', entrySignals: '1', exitSignals: '0', signalKey: 'buy:SOL-EUR' },
  portfolio: {
    at: '2026-10-25T12:59:00+00:00', currency: 'EUR', equity: '199.50', cash: '189.50', peakEquity: '200.00', pnlToday: '-0.50',
    exposureQuote: '10.00', holdings: [{ symbol: 'XRP-EUR', valueQuote: '5.00', planStatus: 'open', entryPrice: '1.156', stopLoss: '1.1481', takeProfit: '1.1641' }],
  },
};

const entry = (at, kind, actor, runId, summary, data = {}) => ({ id: `${at}-${kind}`, at, kind, actor, runId, summary, data });

const journal = [
  entry('2026-10-25T12:46:50+00:00', 'tool-call', 'trader', 'run-1', 'scan_market'),
  entry('2026-10-25T12:46:56+00:00', 'order', 'trader', 'run-1', 'Buy 4.3 XRP-EUR @ 1.1561: Placed (accepted)', {
    tool: 'place_order', symbol: 'XRP-EUR', side: 'buy', requestedSize: '4.3', requestedPrice: '1.1561', status: 'placed',
    stopLoss: '1.1481', takeProfit: '1.1641', rationale: 'signal entry XRP-EUR rank 1: test entry; stop 1.1481, target 1.1641, risk 0.05', clientOrderId: 'c-1',
  }),
  entry('2026-10-25T12:48:00+00:00', 'fill', 'engine', 'run-1', 'buy 4.3 of 4.3 XRP-EUR (Filled)', {
    clientOrderId: 'c-1', symbol: 'XRP-EUR', side: 'buy', filledSize: '4.3', averagePrice: '1.1560', fee: '0.0045', source: 'place_order', stopLoss: '1.1481',
  }),
  entry('2026-10-25T12:50:00+00:00', 'kill-switch', 'reconciler', null, 'Engaged: Open orders the engine didn\'t send: venue-9.', { action: 'engaged', reason: 'Open orders the engine didn\'t send: venue-9.' }),
  entry('2026-10-25T12:55:00+00:00', 'order', 'trader', 'run-2', 'Buy 1 SOL-EUR @ 150: Rejected (max-order)', { tool: 'place_order', status: 'rejected', reason: 'Order is above the limit.' }),
  entry('2026-10-24T18:00:00+00:00', 'tool-call', 'analyst', 'analyst-1', 'record_market_view'),
];

test('engine health follows the heartbeat', () => {
  assert.equal(engineHealth(status, NOW).state, 'running');
  assert.equal(engineHealth({ updatedAt: '2026-10-25T12:50:00Z' }, NOW).state, 'stale');
  assert.equal(engineHealth(null, NOW).state, 'unknown');
});

test('the owner sees levels, prices and the journal\'s own words (ADR-0014)', () => {
  const view = summarize({ mode: 'paper', status, engineState: null, paperAccount: null, journal, stateDocs: [] }, NOW);
  assert.deepEqual(view.portfolio.holdings, [{ symbol: 'XRP-EUR', valueQuote: '5.00', planStatus: 'open', protected: true, entryPrice: '1.156', stopLoss: '1.1481', takeProfit: '1.1641' }]);
  const placed = view.decisions.find((d) => d.status === 'placed');
  assert.equal(placed.rationale, 'signal entry XRP-EUR rank 1: test entry; stop 1.1481, target 1.1641, risk 0.05');
  assert.equal(placed.price, '1.1561');
  assert.equal(placed.stopLoss, '1.1481');
  assert.equal(placed.summary, 'Buy 4.3 XRP-EUR @ 1.1561: Placed (accepted)');
  assert.match(placed.explanation.risk.text, /stop 1\.1481/);
  assert.equal(view.trades[0].averagePrice, '1.1560');
  assert.equal(view.trades[0].fee, '0.0045');
  assert.equal(view.problems.find((p) => p.kind === 'kill-switch').message, "Engaged: Open orders the engine didn't send: venue-9.");
});

test('summarises portfolio, trades, decisions, problems, agents and sync', () => {
  const view = summarize({
    mode: 'paper', status, journal,
    engineState: { killSwitch: { engaged: false }, orders: [{ clientOrderId: 'c-2', symbol: 'SOL-EUR', side: 'buy', size: '0.03', limitPrice: '150', source: 'place_order', sentAt: '2026-10-25T12:59:00+00:00', venueOrderId: 'v' }], positions: {} },
    paperAccount: { wallets: { EUR: { available: '189.50', reserved: '0' }, XRP: { available: '4.3', reserved: '0' } } },
    stateDocs: [{ id: 'paper-account', type: 'paper-account', about: 'x', updatedAt: null }, { id: 'engine-state-paper', type: 'engine-state', about: 'y', updatedAt: null }],
  }, NOW);

  assert.equal(view.engine.state, 'running');
  assert.equal(view.portfolio.drawdownPct, 0.25);
  assert.deepEqual(view.portfolio.balances.map((b) => b.currency), ['EUR', 'XRP']);
  assert.equal(view.trades.length, 1);
  assert.equal(view.trades[0].filledSize, '4.3');
  assert.deepEqual(view.decisions.map((d) => d.status), ['rejected', 'placed']);
  assert.deepEqual(view.problems.map((p) => p.kind), ['order-rejected', 'kill-switch']);
  assert.equal(view.problems[0].message, 'Buy 1 SOL-EUR @ 150: Rejected (max-order): Order is above the limit.');
  assert.equal(view.agents.trader.lastRun.runId, 'run-2');
  assert.equal(view.agents.trader.runsToday, 2);
  assert.equal(view.agents.analyst.runsToday, 0);
  assert.equal(view.agents.analyst.lastRun.runId, 'analyst-1');
  assert.deepEqual(view.cycle.ordersInFlight, [{ clientOrderId: 'c-2', symbol: 'SOL-EUR', side: 'buy', size: '0.03', limitPrice: '150', source: 'place_order', sentAt: '2026-10-25T12:59:00+00:00', confirmed: true, runId: null }]);
  assert.equal(view.sync.exchange, '2026-10-25T12:58:00+00:00');
  assert.equal(view.activity[0].kind, 'order');
  assert.deepEqual(view.stateDocuments.map((d) => d.id), ['engine-state-paper', 'paper-account']);
});

test('works with nothing but read errors', () => {
  const view = summarize({ mode: 'live', status: null, engineState: null, paperAccount: null, journal: [], stateDocs: [], readErrors: ["Couldn't read the engine status (503)."] }, NOW);
  assert.equal(view.engine.state, 'unknown');
  assert.equal(view.portfolio.valuedAt, null);
  assert.equal(view.problems[0].kind, 'read');
});

test('reconcile problems are shown as plain sentences', () => {
  const view = summarize({ mode: 'paper', status: { ...status, lastReconcileProblem: 'unknown-open-orders' }, journal: [], stateDocs: [] }, NOW);
  assert.match(view.sync.reconcileProblem, /open orders the engine didn't send/);
  assert.equal(view.sync.reconcileProblemRu, 'На бирже есть открытые ордера, которые движок не отправлял.');
  assert.equal(view.problems.find((p) => p.kind === 'reconcile').messageRu, view.sync.reconcileProblemRu);
  const odd = summarize({ mode: 'paper', status: { ...status, lastReconcileProblem: 'toString' }, journal: [], stateDocs: [] }, NOW);
  assert.equal(odd.sync.reconcileProblemRu, null, 'an unknown code has no Russian; the page shows the code');
  assert.equal(odd.sync.reconcileProblem, 'toString');
});

test('the engine section says whether owner requests are taken; a direct sell shows the owner\'s note', () => {
  const view = summarize({ mode: 'paper', status: { updatedAt: new Date(NOW).toISOString(), manualRequests: { state: 'off', awaitingAgent: 0, lastPollAt: null } }, engineState: null, journal: [
    { id: 'm', at: new Date(NOW).toISOString(), kind: 'order', actor: 'owner', summary: 'manual sell 1 LINK-EUR @ 10: placed (ok)', data: { tool: 'manual-direct', symbol: 'LINK-EUR', side: 'sell', status: 'placed', rule: 'ok', rationale: "Owner's direct sell: my secret plan" } },
  ], stateDocs: [] }, NOW);
  assert.deepEqual(view.engine.manualRequests, { state: 'off', awaitingAgent: 0, lastPollAt: null });
  assert.equal(view.decisions[0].explanation.points[0], 'Your note: my secret plan');
  assert.equal(summarize({ mode: 'paper', status: null, journal: [], stateDocs: [] }, NOW).engine.manualRequests, null);
});

test('the journal list shows a manual request\'s summary as the engine wrote it', () => {
  const e = { id: 'm', at: '2026-10-25T12:00:00Z', kind: 'manual-request', actor: 'engine', summary: 'Owner request x: agent-assisted sell LINK-EUR, 0.5 of the position (worried)', data: { mode: 'agent' } };
  const view = summarize({ mode: 'paper', status: null, journal: [e], stateDocs: [] }, NOW);
  assert.deepEqual(view.activity, [{ id: 'm', at: e.at, kind: 'manual-request', actor: 'engine', runId: null, summary: e.summary }]);
});

test('the engine section says which strategy runs and how it was chosen, for the header and the Active strategy tile', () => {
  const view = summarize({ mode: 'paper', status, engineState: null, paperAccount: null, journal: [], stateDocs: [] }, NOW);
  assert.equal(view.engine.strategy, 'risky@v1#abc');
  assert.deepEqual(view.engine.strategySelection, {
    state: 'active', requested: 'risky@v1', requestedAt: '2026-10-25T12:30:00+00:00', activatedAt: '2026-10-25T12:31:00+00:00', checkedAt: '2026-10-25T12:59:00+00:00',
  }, 'no more than the page needs: not who asked, nor the problems');
  assert.equal(summarize({ mode: 'paper', status: { ...status, strategySelection: undefined }, journal: [], stateDocs: [] }, NOW).engine.strategySelection, null);
  assert.equal(summarize({ mode: 'paper', status: null, journal: [], stateDocs: [] }, NOW).engine.strategySelection, null);
});

test('agent jobs: the latest run of each job and how it ended, with today\'s failures', () => {
  const { agentJobs } = require('../src/summarize');
  const now = Date.parse('2026-10-27T12:30:00Z');
  const run = (at, id, job, step, purpose) => ({ at, kind: 'agent-run', actor: 'engine', runId: id, data: { step, job, purpose } });
  const entries = [
    run('2026-10-27T12:19:40Z', 'run-2', 'trader', 'done', 'signals'),
    run('2026-10-27T12:18:40Z', 'run-2', 'trader', 'claimed', 'signals'),
    run('2026-10-27T12:18:36Z', 'run-2', 'trader', 'queued', 'signals'),
    run('2026-10-27T11:20:00Z', 'run-1', 'trader', 'failed', 'signals'),
    run('2026-10-27T10:50:00Z', 'analyst-1', 'analyst', 'claimed', 'analyst'),
    run('2026-10-26T23:00:00Z', 'run-0', 'trader', 'failed', 'heartbeat'),
    { at: '2026-10-27T12:00:00Z', kind: 'tool-call', actor: 'trader', runId: 'run-2', data: { tool: 'scan_market' } },
  ];
  const jobs = agentJobs(entries, now);
  assert.deepEqual(jobs.trader, { last: { runId: 'run-2', purpose: 'signals', at: '2026-10-27T12:19:40Z', state: 'done' }, failedToday: 1, notRunToday: 0, limitedToday: 0, cappedAt: null });
  assert.equal(jobs.analyst.last.state, 'running');
});

test('agent jobs: a run stopped by the Claude usage limit is its own state and count, not a failure', () => {
  const { agentJobs } = require('../src/summarize');
  const now = Date.parse('2026-10-28T19:30:00Z');
  const run = (at, id, step, reason) => ({ at, kind: 'agent-run', actor: 'engine', runId: id, data: { step, job: 'analyst', purpose: 'analyst', ...(reason ? { reason } : {}) } });
  const jobs = agentJobs([
    run('2026-10-28T17:15:00Z', 'analyst-3', 'not-run', 'usage-limit'),
    run('2026-10-28T16:45:00Z', 'analyst-2', 'not-run', 'usage-limit'),
    run('2026-10-28T10:45:00Z', 'analyst-1', 'not-run', 'credentials'),
    run('2026-10-28T04:45:00Z', 'analyst-0', 'not-run', '<b>made up</b>'),
  ], now);
  assert.deepEqual(jobs.analyst, { last: { runId: 'analyst-3', purpose: 'analyst', at: '2026-10-28T17:15:00Z', state: 'limit' }, failedToday: 0, notRunToday: 2, limitedToday: 2, cappedAt: null });
  // A reason on any step but not-run (the engine never writes one) doesn't make it a limit.
  const odd = agentJobs([run('2026-10-28T17:15:00Z', 'analyst-4', 'failed', 'usage-limit')], now);
  assert.deepEqual([odd.analyst.last.state, odd.analyst.failedToday, odd.analyst.limitedToday], ['failed', 1, 0]);
});

test('the last coin filter change reaches the page checked again, and the switch previews stay with the Strategy tab', () => {
  const lastChange = {
    at: '2026-10-25T12:31:00+00:00', fromSpec: 'balanced@v1#ab12', toSpec: '<b>@v1',
    added: ['DOGE-EUR', 'DOGE-EUR', 'bad'], removed: ['ETH-EUR'], heldOutside: ['ETH-EUR', 5],
  };
  const withChange = { ...status, filters: { lastChange }, lastScan: { ...status.lastScan, previews: [{ name: 'safe', version: 1, passing: [], excluded: {} }] } };
  const view = summarize({ mode: 'paper', status: withChange, journal: [], stateDocs: [] }, NOW);
  assert.deepEqual(view.engine.filterChange, {
    at: lastChange.at, fromSpec: 'balanced@v1#ab12', toSpec: null, added: ['DOGE-EUR'], removed: ['ETH-EUR'], heldOutside: ['ETH-EUR'],
  });
  assert.equal('previews' in view.cycle.lastScan, false);
  assert.equal(summarize({ mode: 'paper', status, journal: [], stateDocs: [] }, NOW).engine.filterChange, null);
  assert.equal(summarize({ mode: 'paper', status: { ...status, filters: { lastChange: { at: 'soon' } } }, journal: [], stateDocs: [] }, NOW).engine.filterChange, null);
});
