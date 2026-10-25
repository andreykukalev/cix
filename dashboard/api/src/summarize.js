'use strict';
// Turns the engine's documents (state container) and recent journal entries into what the dashboard shows. Pure: no I/O,
// so it is tested on its own. Amounts arrive as strings (the engine writes decimals as strings to keep them exact) and are
// passed through as strings for display; only the drawdown percentage is computed here, for display.

const STALE_AFTER_MS = 3 * 60 * 1000; // the engine writes its status every minute

// The dashboard is public, so it never shows where a position will be sold: no stops, targets or entry prices, no prices
// of resting orders, and levels named in an agent's rationale are blanked. The journal keeps them for the owner.
const HIDDEN = 'hidden';

function redactLevels(text) {
  return typeof text === 'string' ? text.replace(/\b(stop(?:loss)?|target|takeprofit|take-profit)(\s*[:=]?\s*)[0-9][0-9.,]*/gi, `$1$2${HIDDEN}`) : text;
}

const AGENTS = ['trader', 'analyst'];

const RECONCILE_PROBLEMS = {
  'exchange-unreadable': "The exchange couldn't be read at the last reconciliation.",
  'unknown-open-orders': 'The exchange has open orders the engine didn\'t send.',
  'unexplained-holdings-change': 'Holdings changed more than the engine\'s fills explain.',
  'holdings-not-compared': 'Holdings couldn\'t be compared (no fresh prices).',
};
const ERROR_ORDER_STATUSES = new Set(['rejected', 'failed', 'uncertain']);

/**
 * A journal summary fit for a public page: levels blanked, and a kill switch's free-text reason (which can name orders and
 * amounts) replaced by who acted. The journal itself keeps everything.
 */
function publicSummary(e) {
  if (e.kind === 'kill-switch') return `Kill switch ${e.data?.action === 'released' ? 'released' : 'engaged'} by ${e.actor}.`;
  return redactLevels(e.summary);
}

function num(value) {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function time(value) {
  const t = Date.parse(value ?? '');
  return Number.isFinite(t) ? t : null;
}

function byTimeDesc(a, b) {
  return (time(b.at) ?? 0) - (time(a.at) ?? 0);
}

/** Engine health from its heartbeat (the status document's updatedAt). */
function engineHealth(status, now) {
  const heartbeat = time(status?.updatedAt);
  if (heartbeat === null) return { state: 'unknown', heartbeatAt: null, ageSeconds: null };
  const age = now - heartbeat;
  return {
    state: age <= STALE_AFTER_MS ? 'running' : 'stale',
    heartbeatAt: status.updatedAt,
    ageSeconds: Math.max(0, Math.round(age / 1000)),
  };
}

/** Agent runs from the journal: each runId, when it started and ended, and what it did. */
function agentRuns(entries) {
  const runs = new Map();
  for (const e of entries) {
    if (!AGENTS.includes(e.actor) || !e.runId) continue;
    const run = runs.get(e.runId) ?? { runId: e.runId, agent: e.actor, firstAt: e.at, lastAt: e.at, calls: 0, orders: 0, denied: 0, specVersion: e.specVersion ?? null };
    run.calls += 1;
    if (e.kind === 'order') run.orders += 1;
    if (e.kind === 'tool-denied') run.denied += 1;
    if ((time(e.at) ?? 0) < (time(run.firstAt) ?? 0)) run.firstAt = e.at;
    if ((time(e.at) ?? 0) > (time(run.lastAt) ?? 0)) run.lastAt = e.at;
    runs.set(e.runId, run);
  }
  return [...runs.values()].sort((a, b) => (time(b.lastAt) ?? 0) - (time(a.lastAt) ?? 0));
}

function agentsSummary(entries, now) {
  const runs = agentRuns(entries);
  const dayStart = new Date(now);
  dayStart.setUTCHours(0, 0, 0, 0);
  return Object.fromEntries(AGENTS.map((agent) => {
    const mine = runs.filter((r) => r.agent === agent);
    return [agent, {
      lastRun: mine[0] ?? null,
      runsToday: mine.filter((r) => (time(r.firstAt) ?? 0) >= dayStart.getTime()).length,
      recentRuns: mine.slice(0, 10),
    }];
  }));
}

function decisions(entries) {
  return entries.filter((e) => e.kind === 'order').map((e) => {
    const d = e.data ?? {};
    return {
      at: e.at,
      actor: e.actor,
      runId: e.runId ?? null,
      action: d.tool ?? null,
      symbol: d.symbol ?? null,
      side: d.side ?? null,
      size: d.approvedSize ?? d.requestedSize ?? null,
      price: d.approvedPrice ?? d.requestedPrice ?? null,
      status: d.status ?? null,
      rule: d.rule ?? null,
      rationale: redactLevels(d.rationale ?? null),
      hasStop: d.stopLoss !== undefined,
      clientOrderId: d.clientOrderId ?? null,
      summary: redactLevels(e.summary),
    };
  }).sort(byTimeDesc);
}

function trades(entries) {
  return entries.filter((e) => e.kind === 'fill').map((e) => {
    const d = e.data ?? {};
    return {
      at: e.at,
      symbol: d.symbol ?? null,
      side: d.side ?? null,
      filledSize: d.filledSize ?? null,
      averagePrice: d.averagePrice ?? null,
      filledQuote: d.filledQuote ?? null,
      fee: d.fee ?? null,
      feeCurrency: d.feeCurrency ?? null,
      source: d.source ?? null,
      status: d.status ?? null,
      clientOrderId: d.clientOrderId ?? null,
      runId: e.runId ?? null,
    };
  }).sort(byTimeDesc);
}

function problems(entries, status, readErrors) {
  const list = [];
  for (const e of entries) {
    const d = e.data ?? {};
    if (e.kind === 'tool-denied') list.push({ at: e.at, source: e.actor, kind: 'tool-denied', message: e.summary });
    else if (e.kind === 'kill-switch' && d.action !== 'released') list.push({ at: e.at, source: e.actor, kind: 'kill-switch', message: publicSummary(e) });
    else if (e.kind === 'order' && ERROR_ORDER_STATUSES.has(d.status)) {
      list.push({ at: e.at, source: e.actor, kind: `order-${d.status}`, message: d.reason ? `${publicSummary(e)}: ${redactLevels(d.reason)}` : publicSummary(e) });
    }
  }
  if (status?.lastReconcileProblem) {
    list.push({ at: status.lastReconcileAttemptAt ?? status.updatedAt, source: 'reconciler', kind: 'reconcile', message: RECONCILE_PROBLEMS[status.lastReconcileProblem] ?? status.lastReconcileProblem });
  }
  for (const message of readErrors) list.push({ at: null, source: 'dashboard', kind: 'read', message });
  return list.sort(byTimeDesc);
}

function portfolio(status, paperAccount) {
  const p = status?.portfolio;
  const balances = Object.entries(paperAccount?.wallets ?? {})
    .map(([currency, w]) => ({ currency, available: w.available ?? '0', reserved: w.reserved ?? '0' }))
    .sort((a, b) => a.currency.localeCompare(b.currency));
  if (!p) return { valuedAt: null, balances, holdings: [] };
  const equity = num(p.equity);
  const peak = num(p.peakEquity);
  return {
    valuedAt: p.at ?? null,
    currency: p.currency ?? null,
    equity: p.equity ?? null,
    cash: p.cash ?? null,
    peakEquity: p.peakEquity ?? null,
    pnlToday: p.pnlToday ?? null,
    exposure: p.exposureQuote ?? null,
    drawdownPct: equity !== null && peak ? Math.round(((peak - equity) / peak) * 10000) / 100 : null,
    holdings: (p.holdings ?? []).map((h) => ({
      symbol: h.symbol, valueQuote: h.valueQuote ?? null, planStatus: h.planStatus ?? null, protected: h.stopLoss !== undefined && h.stopLoss !== null,
    })),
    balances,
  };
}

/**
 * @param {{mode: string, status: object|null, engineState: object|null, paperAccount: object|null, journal: object[], stateDocs: object[], readErrors?: string[]}} input
 *   Document values (the `value` of state documents, the `entry` of journal documents).
 * @param {number} now Milliseconds since the epoch.
 */
function summarize(input, now) {
  const entries = [...(input.journal ?? [])].sort(byTimeDesc);
  const status = input.status ?? null;
  const engineState = input.engineState ?? null;
  const kill = engineState?.killSwitch ?? null;
  const lastTraderRun = agentRuns(entries).find((r) => r.agent === 'trader') ?? null;

  return {
    generatedAt: new Date(now).toISOString(),
    mode: input.mode,
    engine: {
      ...engineHealth(status, now),
      startedAt: status?.startedAt ?? null,
      strategy: status?.strategy ?? null,
      halted: status?.halted ?? kill?.engaged ?? null,
      haltedBy: status?.haltedBy ?? (kill?.engaged ? kill.by ?? null : null),
      haltCategory: status?.haltCategory ?? (kill?.engaged ? 'Halted (the reason is in the journal).' : null),
      haltedSince: status?.haltedSince ?? (kill?.engaged ? kill.since : null) ?? null,
      oldestQuoteAt: status?.oldestQuoteAt ?? null,
      unquotedSymbols: status?.unquotedSymbols ?? [],
      marketView: status?.marketView ?? null,
    },
    sync: {
      exchange: status?.lastReconciledAt ?? null,
      exchangeAttempt: status?.lastReconcileAttemptAt ?? null,
      database: status?.updatedAt ?? null,
      reconcileProblem: status?.lastReconcileProblem ? RECONCILE_PROBLEMS[status.lastReconcileProblem] ?? status.lastReconcileProblem : null,
    },
    cycle: {
      lastScan: status?.lastScan ?? null,
      lastTraderRun,
      ordersInFlight: (engineState?.orders ?? []).map((o) => ({
        clientOrderId: o.clientOrderId, symbol: o.symbol, side: o.side, size: o.size, source: o.source, sentAt: o.sentAt,
        confirmed: Boolean(o.venueOrderId), runId: o.runId ?? null,
      })),
      positionPlans: Object.values(engineState?.positions ?? {}).map((p) => ({
        symbol: p.symbol, status: p.status, since: p.since, filledAt: p.filledAt ?? null, strategy: p.strategy ?? null,
        protected: p.stopLoss !== undefined && p.stopLoss !== null,
      })),
    },
    agents: agentsSummary(entries, now),
    portfolio: portfolio(status, input.paperAccount),
    decisions: decisions(entries).slice(0, 50),
    trades: trades(entries).slice(0, 50),
    problems: problems(entries, status, input.readErrors ?? []).slice(0, 50),
    activity: entries.slice(0, 80).map((e) => ({ id: e.id, at: e.at, kind: e.kind, actor: e.actor, runId: e.runId ?? null, summary: publicSummary(e) })),
    stateDocuments: [...(input.stateDocs ?? [])].sort((a, b) => a.id.localeCompare(b.id)),
  };
}

module.exports = { summarize, engineHealth, agentRuns, redactLevels, publicSummary, STALE_AFTER_MS };
