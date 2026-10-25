'use strict';
// Reads what the dashboard shows from Cosmos DB: the state documents and the last two days of the journal. Each read can
// fail on its own; the dashboard then shows what it has and says what it couldn't read.

const { summarize, publicSummary } = require('./summarize');

const MODES = new Set(['paper', 'live']);
const JOURNAL_PER_DAY = 300;

function utcDay(ms) {
  return new Date(ms).toISOString().slice(0, 10);
}

async function attempt(what, readErrors, read) {
  try {
    return await read();
  } catch (error) {
    readErrors.push(`Couldn't read ${what}${error?.status ? ` (${error.status})` : ''}.`);
    return null;
  }
}

/** Loads and summarises everything for one mode. */
async function loadDashboard(reader, mode, now) {
  if (!MODES.has(mode)) throw new RangeError('mode must be paper or live');
  const readErrors = [];
  const today = utcDay(now);
  const yesterday = utcDay(now - 24 * 3600 * 1000);

  const [statusDoc, stateDoc, accountDoc, todayEntries, yesterdayEntries, stateDocs] = await Promise.all([
    attempt('the engine status', readErrors, () => reader.readDocument('state', `engine-status-${mode}`)),
    attempt('the engine state', readErrors, () => reader.readDocument('state', `engine-state-${mode}`)),
    mode === 'paper' ? attempt('the paper account', readErrors, () => reader.readDocument('state', 'paper-account')) : null,
    attempt("today's journal", readErrors, () => reader.query('journal', `SELECT TOP ${JOURNAL_PER_DAY} * FROM c ORDER BY c.entry.at DESC`, { partitionKey: today, maxItems: JOURNAL_PER_DAY })),
    attempt("yesterday's journal", readErrors, () => reader.query('journal', `SELECT TOP ${JOURNAL_PER_DAY} * FROM c ORDER BY c.entry.at DESC`, { partitionKey: yesterday, maxItems: JOURNAL_PER_DAY })),
    attempt('the list of state documents', readErrors, () => reader.query('state', 'SELECT c.id, c.type, c.about, c.updatedAt FROM c', { maxItems: 200 })),
  ]);

  return summarize({
    mode,
    status: statusDoc?.value ?? null,
    engineState: stateDoc?.value ?? null,
    paperAccount: accountDoc?.value ?? null,
    journal: [...(todayEntries ?? []), ...(yesterdayEntries ?? [])].map((d) => d.entry).filter(Boolean),
    stateDocs: (stateDocs ?? []).map((d) => ({ id: d.id, type: d.type ?? null, about: d.about ?? null, updatedAt: d.updatedAt ?? null })),
    readErrors,
  }, now);
}

const DAY = /^\d{4}-\d{2}-\d{2}$/;
const KINDS = new Set(['order', 'fill', 'order-closed', 'tool-call', 'tool-denied', 'kill-switch', 'scan-signal', 'market-view']);
const LIMITS = [50, 100, 200];
const JOURNAL_DAYS = 14;

/**
 * Checks and normalises a journal request, so equal requests share one cache entry and the set of distinct requests (each
 * a database query on the engine's throughput) stays small: the last 14 days, known kinds, three page sizes.
 */
function journalRequest(day, kind, limit, now) {
  if (!DAY.test(day ?? '')) throw new RangeError('day must be YYYY-MM-DD');
  const age = (now - Date.parse(`${day}T00:00:00Z`)) / (24 * 3600 * 1000);
  if (!(age > -1 && age < JOURNAL_DAYS)) throw new RangeError(`day must be within the last ${JOURNAL_DAYS} days`);
  if (kind && !KINDS.has(kind)) throw new RangeError('kind is not valid');
  const wanted = Number.parseInt(limit ?? '100', 10) || 100;
  const top = LIMITS.find((l) => wanted <= l) ?? LIMITS[LIMITS.length - 1];
  return { day, kind: kind || '', top, key: `journal:${day}:${kind || ''}:${top}` };
}

/** One UTC day of the journal, newest first, optionally one kind only. */
async function loadJournalDay(reader, day, kind, limit, now = Date.now()) {
  ({ day, kind, top: limit } = journalRequest(day, kind, limit, now));
  const top = limit;
  const sql = kind
    ? `SELECT TOP ${top} * FROM c WHERE c.entry.kind = @kind ORDER BY c.entry.at DESC`
    : `SELECT TOP ${top} * FROM c ORDER BY c.entry.at DESC`;
  const docs = await reader.query('journal', sql, { partitionKey: day, parameters: kind ? [{ name: '@kind', value: kind }] : [], maxItems: top });
  // Public page: the same fields as the activity list, levels blanked; no raw data (it holds stops and targets).
  return docs.map((d) => d.entry).filter(Boolean).map((e) => ({ id: e.id, at: e.at, kind: e.kind, actor: e.actor, runId: e.runId ?? null, summary: publicSummary(e) }));
}

module.exports = { loadDashboard, loadJournalDay, journalRequest, utcDay };
