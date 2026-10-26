'use strict';
// Reads what the dashboard shows from Cosmos DB: the state documents and the last two days of the journal. Each read can
// fail on its own; the dashboard then shows what it has and says what it couldn't read.

const { summarize, publicSummary, decisions } = require('./summarize');
const { activity, fromDay } = require('./activity');
const { history, historyItem } = require('./requests');

const MODES = new Set(['paper', 'live']);
const JOURNAL_PER_DAY = 300;
const STATE_LIST_MAX = 200;
const LATEST_VIEW_SQL = 'SELECT TOP 1 * FROM c WHERE c.entry.kind = @kind ORDER BY c.entry.at DESC';

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

  const latestView = (day) => reader.query('journal', LATEST_VIEW_SQL, { partitionKey: day, parameters: [{ name: '@kind', value: 'market-view' }], maxItems: 1 });
  const [statusDoc, stateDoc, accountDoc, todayEntries, yesterdayEntries, stateDocs, todayView, yesterdayView] = await Promise.all([
    attempt('the engine status', readErrors, () => reader.readDocument('state', `engine-status-${mode}`)),
    attempt('the engine state', readErrors, () => reader.readDocument('state', `engine-state-${mode}`)),
    mode === 'paper' ? attempt('the paper account', readErrors, () => reader.readDocument('state', 'paper-account')) : null,
    attempt("today's journal", readErrors, () => reader.query('journal', `SELECT TOP ${JOURNAL_PER_DAY} * FROM c ORDER BY c.entry.at DESC`, { partitionKey: today, maxItems: JOURNAL_PER_DAY })),
    attempt("yesterday's journal", readErrors, () => reader.query('journal', `SELECT TOP ${JOURNAL_PER_DAY} * FROM c ORDER BY c.entry.at DESC`, { partitionKey: yesterday, maxItems: JOURNAL_PER_DAY })),
    attempt('the list of state documents', readErrors, () => reader.query('state', 'SELECT c.id, c.type, c.about, c.updatedAt FROM c', { maxItems: STATE_LIST_MAX })),
    attempt("today's market view", readErrors, () => latestView(today)),
    attempt("yesterday's market view", readErrors, () => latestView(yesterday)),
  ]);

  return summarize({
    mode,
    status: statusDoc?.value ?? null,
    engineState: stateDoc?.value ?? null,
    paperAccount: accountDoc?.value ?? null,
    journal: [...(todayEntries ?? []), ...(yesterdayEntries ?? [])].map((d) => d.entry).filter(Boolean),
    marketViews: [...(todayView ?? []), ...(yesterdayView ?? [])].map((d) => d.entry).filter(Boolean),
    stateDocs: (stateDocs ?? []).map((d) => ({ id: d.id, type: d.type ?? null, about: d.about ?? null, updatedAt: d.updatedAt ?? null })),
    readErrors: (stateDocs ?? []).length >= STATE_LIST_MAX
      ? [...readErrors, `Showing the first ${STATE_LIST_MAX} state documents only.`]
      : readErrors,
  }, now);
}

const DAY = /^\d{4}-\d{2}-\d{2}$/;
const KINDS = new Set(['order', 'fill', 'order-closed', 'tool-call', 'tool-denied', 'kill-switch', 'scan-signal', 'market-view', 'manual-request', 'scanner-filters', 'scan-eligibility']);
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

const FILLS_MAX = 5000;

/**
 * Trading activity for the period switch (day, week or month): executed trades only (journal "fill" entries), read across
 * the day partitions the periods cover. Paper only for now: the journal has no mode tag yet (#65), and only paper runs.
 */
async function loadActivity(reader, period, now) {
  const from = fromDay(period, now); // throws RangeError for an unknown period
  const fills = await reader.query('journal', 'SELECT c.entry.at AS at, c.entry.data AS data FROM c WHERE c.day >= @from AND c.entry.kind = @kind', {
    parameters: [{ name: '@from', value: from }, { name: '@kind', value: 'fill' }],
    maxItems: FILLS_MAX,
  });
  return { ...activity(fills, period, now), truncated: fills.length >= FILLS_MAX };
}

/** The owner's view of recent decisions: the same list as the public page, with levels and prices kept. */
async function loadOwnerDecisions(reader, now) {
  const sql = `SELECT TOP ${JOURNAL_PER_DAY} * FROM c WHERE c.entry.kind = @kind ORDER BY c.entry.at DESC`;
  const parameters = [{ name: '@kind', value: 'order' }];
  const days = [utcDay(now), utcDay(now - 24 * 3600 * 1000)];
  const pages = await Promise.all(days.map((day) => reader.query('journal', sql, { partitionKey: day, parameters, maxItems: JOURNAL_PER_DAY })));
  const entries = pages.flat().map((d) => d.entry).filter(Boolean);
  return { generatedAt: new Date(now).toISOString(), decisions: decisions(entries, { reveal: true }).slice(0, 50) };
}

const REQUEST_DAYS = 7;
const REQUESTS_MAX = 200;

/** The owner's requests from the last week, newest first, with where each stands. */
async function loadRequests(reader, now) {
  const from = utcDay(now - (REQUEST_DAYS - 1) * 24 * 3600 * 1000);
  const docs = await reader.query('requests', 'SELECT * FROM c WHERE c.day >= @from AND c.type = @type', {
    parameters: [{ name: '@from', value: from }, { name: '@type', value: 'manual-request' }],
    maxItems: REQUESTS_MAX,
  });
  return { generatedAt: new Date(now).toISOString(), from, requests: history(docs) };
}

/** One request, for the page to follow it after a click; null if it isn't there (yet). */
async function loadRequest(reader, day, id) {
  if (!DAY.test(day ?? '') || !/^[0-9a-f-]{36}$/.test(id ?? '')) throw new RangeError('day and id must be a request\'s');
  const doc = await reader.readDocument('requests', id, day);
  return doc && doc.type === 'manual-request' ? historyItem(doc) : null;
}

module.exports = { loadDashboard, loadJournalDay, journalRequest, loadActivity, loadOwnerDecisions, loadRequests, loadRequest, utcDay, KINDS };
