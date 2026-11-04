'use strict';
// Turns the engine's documents (state container) and recent journal entries into what the dashboard shows. Pure: no I/O,
// so it is tested on its own. Amounts arrive as strings (the engine writes decimals as strings to keep them exact) and are
// passed through as strings for display; only the drawdown percentage is computed here, for display.

const { briefing } = require('./briefing');
const { explain } = require('./explain');

const STALE_AFTER_MS = 3 * 60 * 1000; // the engine writes its status every minute

// The dashboard is the owner's only (ADR-0014), so levels, prices and the journal's own text are passed on as written.

const AGENTS = ['trader', 'analyst'];

const RECONCILE_PROBLEMS = {
  'exchange-unreadable': "The exchange couldn't be read at the last reconciliation.",
  'unknown-open-orders': 'The exchange has open orders the engine didn\'t send.',
  'unexplained-holdings-change': 'Holdings changed more than the engine\'s fills explain.',
  'holdings-not-compared': 'Holdings couldn\'t be compared (no fresh prices).',
};
const RECONCILE_PROBLEMS_RU = {
  'exchange-unreadable': 'При последней сверке не удалось прочитать данные биржи.',
  'unknown-open-orders': 'На бирже есть открытые ордера, которые движок не отправлял.',
  'unexplained-holdings-change': 'Монеты в портфеле изменились сильнее, чем объясняют сделки движка.',
  'holdings-not-compared': 'Монеты в портфеле не удалось сравнить (нет свежих цен).',
};
/** A table's text for a code, or null: only the table's own keys, never inherited ones. */
function ownText(table, code) {
  return typeof code === 'string' && Object.hasOwn(table, code) ? table[code] : null;
}

const ERROR_ORDER_STATUSES = new Set(['rejected', 'failed', 'uncertain']);

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

const NOT_RUN_REASONS = new Set(['usage-limit', 'credentials']);

/**
 * The engine's agent jobs (trader, trader-requests, analyst) from its agent-run journal entries, for the health monitors:
 * the latest run and how it ended, and today's failed and not-run counts. A run that didn't start because of Claude's usage
 * limit is its own state (`limit`) and count, apart from failures and other not-runs: limits run out now and then, and the
 * engine runs them again later by itself. Entries come newest first.
 */
function agentJobs(entries, now) {
  const dayStart = new Date(now);
  dayStart.setUTCHours(0, 0, 0, 0);
  const jobs = {};
  const empty = () => ({ last: null, failedToday: 0, notRunToday: 0, limitedToday: 0, cappedAt: null });
  for (const e of entries) {
    const d = e.data ?? {};
    if (e.kind !== 'agent-run' || !d.job || !e.runId) continue;
    const job = jobs[d.job] ??= empty();
    const reason = d.step === 'not-run' && NOT_RUN_REASONS.has(d.reason) ? d.reason : null;
    if (!job.last) job.last = { runId: e.runId, purpose: d.purpose ?? null, step: d.step ?? null, reason, at: e.at }; // its latest step
    const today = (time(e.at) ?? 0) >= dayStart.getTime();
    if (today && d.step === 'failed') job.failedToday += 1;
    if (today && reason === 'usage-limit') job.limitedToday += 1;
    else if (today && (d.step === 'not-run' || d.step === 'not-sent')) job.notRunToday += 1;
  }
  for (const e of entries) {
    if (e.kind === 'agent-run' && e.data?.step === 'capped' && (time(e.at) ?? 0) >= dayStart.getTime()) {
      (jobs.trader ??= empty()).cappedAt ??= e.at;
    }
  }
  for (const job of Object.values(jobs)) {
    if (job.last) {
      const { step, reason } = job.last;
      job.last.state = step === 'done' ? 'done' : step === 'failed' ? 'failed'
        : reason === 'usage-limit' ? 'limit' : step === 'not-run' || step === 'not-sent' ? 'not-run' : 'running';
      delete job.last.step;
      delete job.last.reason;
    }
  }
  return jobs;
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

/** Orders the agents and the engine decided, newest first, each with a plain-language explanation. */
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
      rationale: d.rationale ?? null,
      stopLoss: d.stopLoss ?? null,
      takeProfit: d.takeProfit ?? null,
      clientOrderId: d.clientOrderId ?? null,
      summary: e.summary ?? null,
      explanation: explain(e),
      explanationRu: explain(e, 'ru'),
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
      fee: d.fee ?? null,
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
    if (e.kind === 'tool-denied') list.push({ at: e.at, source: e.actor, kind: 'tool-denied', message: e.summary ?? null });
    else if (e.kind === 'kill-switch' && d.action !== 'released') list.push({ at: e.at, source: e.actor, kind: 'kill-switch', message: e.summary ?? null });
    else if (e.kind === 'order' && ERROR_ORDER_STATUSES.has(d.status)) {
      list.push({ at: e.at, source: e.actor, kind: `order-${d.status}`, message: [e.summary, d.reason].filter(Boolean).join(': ') || null });
    }
  }
  if (status?.lastReconcileProblem) {
    list.push({
      at: status.lastReconcileAttemptAt ?? status.updatedAt, source: 'reconciler', kind: 'reconcile',
      message: ownText(RECONCILE_PROBLEMS, status.lastReconcileProblem) ?? status.lastReconcileProblem,
      messageRu: ownText(RECONCILE_PROBLEMS_RU, status.lastReconcileProblem),
    });
  }
  for (const message of readErrors) list.push({ at: null, source: 'dashboard', kind: 'read', message });
  return list.sort(byTimeDesc);
}

function withoutMarket({ market, previews, ...scan }) { // the switch previews are the Strategy tab's
  return scan;
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
      entryPrice: h.entryPrice ?? null, stopLoss: h.stopLoss ?? null, takeProfit: h.takeProfit ?? null,
    })),
    balances,
  };
}

/** The engine's strategy selection, as the page needs it: no more than strategies.js passes on. */
function strategySelection(selection) {
  if (!selection || typeof selection !== 'object') return null;
  return {
    state: selection.state ?? null,
    requested: selection.requested ?? null,
    requestedAt: selection.requestedAt ?? null,
    activatedAt: selection.activatedAt ?? null,
    checkedAt: selection.checkedAt ?? null,
  };
}

const PAIR = /^[A-Z0-9]{1,20}-[A-Z]{3,5}$/;
const SPEC = /^[a-z0-9-]{1,40}@v\d{1,4}(?:#[0-9a-f]{1,64})?$/;
const pairList = (value) => (Array.isArray(value) ? [...new Set(value.filter((s) => typeof s === 'string' && PAIR.test(s)))].sort().slice(0, 100) : []);

/**
 * The last change of the coin filters (a strategy switch, ADR-0021): which watched coins entered and left trading, and which
 * of those the account holds. The Coins tab shows it until the owner dismisses it. Checked again: the page trusts no one.
 */
function filterChange(change) {
  if (!change || typeof change !== 'object' || !Number.isFinite(Date.parse(change.at ?? ''))) return null;
  return {
    at: change.at,
    fromSpec: SPEC.test(change.fromSpec ?? '') ? change.fromSpec : null,
    toSpec: SPEC.test(change.toSpec ?? '') ? change.toSpec : null,
    added: pairList(change.added),
    removed: pairList(change.removed),
    heldOutside: pairList(change.heldOutside),
  };
}

/**
 * @param {{mode: string, status: object|null, engineState: object|null, paperAccount: object|null, journal: object[], marketViews?: object[], stateDocs: object[], readErrors?: string[]}} input
 *   Document values (the `value` of state documents, the `entry` of journal documents). `marketViews` are the latest
 *   market-view entries, read on their own so a busy journal can't push them out of the recent entries.
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
      // Where the engine stands on the owner's latest strategy switch: the header and the Active strategy tile show it.
      strategySelection: strategySelection(status?.strategySelection),
      filterChange: filterChange(status?.filters?.lastChange),
      halted: status?.halted ?? kill?.engaged ?? null,
      haltedBy: status?.haltedBy ?? (kill?.engaged ? kill.by ?? null : null),
      haltCategory: status?.haltCategory ?? (kill?.engaged ? 'Halted (the reason is in the journal).' : null),
      haltedSince: status?.haltedSince ?? (kill?.engaged ? kill.since : null) ?? null,
      // A later cause found while already halted (ADR-0018): the Trading tile shows it, and Resume sends both times back.
      haltAlsoCategory: status?.haltAlsoCategory ?? null,
      haltAlsoAt: status?.haltAlsoAt ?? (kill?.engaged && !status ? kill.alsoAt ?? null : null) ?? null,
      oldestQuoteAt: status?.oldestQuoteAt ?? null,
      unquotedSymbols: status?.unquotedSymbols ?? [],
      marketView: status?.marketView ?? null,
      // Whether the engine takes owner requests now (ok, off, unavailable): the page disables the buttons otherwise.
      // The engine's stop-loss exits: coins being sold, and exits blocked after 5 tries (retried every minute).
      protection: status?.protection
        ? {
          armed: status.protection.armed ?? 0,
          exiting: status.protection.exiting ?? [],
          blocked: (status.protection.blocked ?? []).map((b) => ({ symbol: b.symbol, since: b.since ?? null, problem: b.problem ?? null })),
        }
        : null,
      manualRequests: status?.manualRequests
        ? { state: status.manualRequests.state ?? null, awaitingAgent: status.manualRequests.awaitingAgent ?? null, lastPollAt: status.manualRequests.lastPollAt ?? null }
        : null,
    },
    sync: {
      exchange: status?.lastReconciledAt ?? null,
      exchangeAttempt: status?.lastReconcileAttemptAt ?? null,
      database: status?.updatedAt ?? null,
      reconcileProblem: status?.lastReconcileProblem ? ownText(RECONCILE_PROBLEMS, status.lastReconcileProblem) ?? status.lastReconcileProblem : null,
      reconcileProblemRu: ownText(RECONCILE_PROBLEMS_RU, status?.lastReconcileProblem),
    },
    cycle: {
      lastScan: status?.lastScan ? withoutMarket(status.lastScan) : null, // the coins are in the briefing
      lastTraderRun,
      ordersInFlight: (engineState?.orders ?? []).map((o) => ({
        clientOrderId: o.clientOrderId, symbol: o.symbol, side: o.side, size: o.size, limitPrice: o.limitPrice ?? null, source: o.source, sentAt: o.sentAt,
        confirmed: Boolean(o.venueOrderId), runId: o.runId ?? null,
      })),
      positionPlans: Object.values(engineState?.positions ?? {}).map((p) => ({
        symbol: p.symbol, status: p.status, since: p.since, filledAt: p.filledAt ?? null, strategy: p.strategy ?? null,
        protected: p.stopLoss !== undefined && p.stopLoss !== null,
      })),
    },
    briefing: briefing(status, [...entries, ...(input.marketViews ?? [])], now),
    agents: agentsSummary(entries, now),
    jobs: agentJobs(entries, now),
    portfolio: portfolio(status, input.paperAccount),
    decisions: decisions(entries).slice(0, 50),
    trades: trades(entries).slice(0, 50),
    problems: problems(entries, status, input.readErrors ?? []).slice(0, 50),
    activity: entries.slice(0, 80).map(journalItem),
    stateDocuments: [...(input.stateDocs ?? [])].sort((a, b) => a.id.localeCompare(b.id)),
  };
}

/** A journal entry as the activity list shows it: the summary as written; the data stays in the journal. */
function journalItem(e) {
  return { id: e.id, at: e.at, kind: e.kind, actor: e.actor, runId: e.runId ?? null, summary: e.summary ?? null };
}

module.exports = { summarize, decisions, journalItem, engineHealth, agentRuns, agentJobs, STALE_AFTER_MS, RECONCILE_PROBLEMS, RECONCILE_PROBLEMS_RU };
