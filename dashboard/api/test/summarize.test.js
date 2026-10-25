'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { summarize, engineHealth, publicText } = require('../src/summarize');

const NOW = Date.parse('2026-10-25T13:00:00Z');

const status = {
  mode: 'paper', updatedAt: '2026-10-25T12:59:30+00:00', startedAt: '2026-10-25T12:00:00+00:00', strategy: 'risky@v1#abc',
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

test('the public view never shows stops, targets or kill-switch reasons', () => {
  const view = summarize({ mode: 'paper', status, engineState: null, paperAccount: null, journal, stateDocs: [] }, NOW);
  const text = JSON.stringify(view);

  for (const secret of ['1.1481', '1.1641', '1.1561', '1.1560', 'venue-9']) assert.ok(!text.includes(secret), `${secret} leaked`);
  assert.deepEqual(view.portfolio.holdings, [{ symbol: 'XRP-EUR', valueQuote: '5.00', planStatus: 'open', protected: true }]);
  assert.equal(view.decisions[1].rationale, 'signal entry XRP-EUR rank 1: test entry; stop …, target …, risk …');
  assert.ok(!('price' in view.decisions[0]) && !('averagePrice' in view.trades[0]), 'no price fields at all');
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
  assert.equal(view.problems[1].message, 'Kill switch engaged by reconciler.');
  assert.equal(view.agents.trader.lastRun.runId, 'run-2');
  assert.equal(view.agents.trader.runsToday, 2);
  assert.equal(view.agents.analyst.runsToday, 0);
  assert.equal(view.agents.analyst.lastRun.runId, 'analyst-1');
  assert.deepEqual(view.cycle.ordersInFlight, [{ clientOrderId: 'c-2', symbol: 'SOL-EUR', side: 'buy', size: '0.03', source: 'place_order', sentAt: '2026-10-25T12:59:00+00:00', confirmed: true, runId: null }]);
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
});

test('masks prices in every text format the engine writes', () => {
  // The exact formats of OrderTools, MarketScanService (protective exits, scan signals) and MarketScanner exit details.
  const engineTexts = [
    'Buy 0.0005 BTC-EUR @ 60010: Placed (accepted)',
    'protective stop sell 4.32488 XRP-EUR @ 1.1481: Placed (accepted)',
    'buy SOL-EUR rank 1 at 150.25: uptrend pullback 0.3 ATR above EMA20',
    'bid 59800 at or below stop 61000',
    'stopLoss=1.2 takeProfit: 1.3 target 2.5',
  ];
  for (const text of engineTexts) assert.doesNotMatch(publicText(text), /\d+\.\d+|\d{3,}/, text);
  assert.equal(publicText('rank 1, 24h +3%'), 'rank 1, 24h +3%');
  assert.equal(publicText(null), null);
});
