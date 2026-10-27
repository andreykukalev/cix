'use strict';
// Reads what the dashboard shows from Cosmos DB: the state documents and the last two days of the journal. Each read can
// fail on its own; the dashboard then shows what it has and says what it couldn't read.

const { summarize, journalItem } = require('./summarize');
const { activity, fromDay } = require('./activity');
const { history, historyItem } = require('./requests');
const { strategiesView, requestItem, REQUEST_TYPE } = require('./strategies');
const { pairsView, pairRequestItem, REQUEST_TYPE: PAIR_REQUEST_TYPE } = require('./pairs');

const MODES = new Set(['paper', 'live']);
const JOURNAL_PER_DAY = 300;
const STATE_LIST_MAX = 200;

/**
 * Journal entries of one mode only (#65): each engine tags what it writes with its mode, and entries from before the tag
 * are paper's. Takes the `@mode` parameter from modeParameter.
 */
function modeFilter(mode) {
  if (!MODES.has(mode)) throw new RangeError('mode must be paper or live');
  return mode === 'paper' ? '(NOT IS_DEFINED(c.mode) OR c.mode = @mode)' : 'c.mode = @mode';
}

const modeParameter = (mode) => ({ name: '@mode', value: mode });

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

  const inMode = modeFilter(mode);
  const recentSql = `SELECT TOP ${JOURNAL_PER_DAY} * FROM c WHERE ${inMode} ORDER BY c.entry.at DESC`;
  const recent = (day) => reader.query('journal', recentSql, { partitionKey: day, parameters: [modeParameter(mode)], maxItems: JOURNAL_PER_DAY });
  const latestView = (day) => reader.query('journal', `SELECT TOP 1 * FROM c WHERE c.entry.kind = @kind AND ${inMode} ORDER BY c.entry.at DESC`, {
    partitionKey: day, parameters: [{ name: '@kind', value: 'market-view' }, modeParameter(mode)], maxItems: 1,
  });
  const [statusDoc, stateDoc, accountDoc, todayEntries, yesterdayEntries, stateDocs, todayView, yesterdayView] = await Promise.all([
    attempt('the engine status', readErrors, () => reader.readDocument('state', `engine-status-${mode}`)),
    attempt('the engine state', readErrors, () => reader.readDocument('state', `engine-state-${mode}`)),
    mode === 'paper' ? attempt('the paper account', readErrors, () => reader.readDocument('state', 'paper-account')) : null,
    attempt("today's journal", readErrors, () => recent(today)),
    attempt("yesterday's journal", readErrors, () => recent(yesterday)),
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
const KINDS = new Set(['order', 'fill', 'order-closed', 'tool-call', 'tool-denied', 'kill-switch', 'scan-signal', 'market-view', 'manual-request', 'scanner-filters', 'scan-eligibility', 'strategy', 'pairs']);
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

/** One UTC day of one mode's journal, newest first, optionally one kind only. Paper until the dashboard switches modes (#71). */
async function loadJournalDay(reader, day, kind, limit, now = Date.now(), mode = 'paper') {
  ({ day, kind, top: limit } = journalRequest(day, kind, limit, now));
  const top = limit;
  const inMode = modeFilter(mode);
  const sql = kind
    ? `SELECT TOP ${top} * FROM c WHERE c.entry.kind = @kind AND ${inMode} ORDER BY c.entry.at DESC`
    : `SELECT TOP ${top} * FROM c WHERE ${inMode} ORDER BY c.entry.at DESC`;
  const parameters = kind ? [{ name: '@kind', value: kind }, modeParameter(mode)] : [modeParameter(mode)];
  const docs = await reader.query('journal', sql, { partitionKey: day, parameters, maxItems: top });
  return docs.map((d) => d.entry).filter(Boolean).map(journalItem);
}

const FILLS_MAX = 5000;

/**
 * Trading activity for the period switch (day, week or month): one mode's executed trades only (journal "fill" entries),
 * read across the day partitions the periods cover. Paper until the dashboard switches modes (#71).
 */
async function loadActivity(reader, period, now, mode = 'paper') {
  const from = fromDay(period, now); // throws RangeError for an unknown period
  const fills = await reader.query('journal', `SELECT c.entry.at AS at, c.entry.data AS data FROM c WHERE c.day >= @from AND c.entry.kind = @kind AND ${modeFilter(mode)}`, {
    parameters: [{ name: '@from', value: from }, { name: '@kind', value: 'fill' }, modeParameter(mode)],
    maxItems: FILLS_MAX,
  });
  return { ...activity(fills, period, now), truncated: fills.length >= FILLS_MAX };
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

const VERSIONS_MAX = 100;

/**
 * The Strategy tab for one mode: the engine's parameter schema, every strategy version (without its instructions), the
 * running strategy and what became of the latest selection, and the owner's switch requests of the last week. The schema
 * and selection exist for paper only (#105); live shows the versions read-only.
 */
async function loadStrategies(reader, mode, now) {
  if (!MODES.has(mode)) throw new RangeError('mode must be paper or live');
  const from = utcDay(now - (REQUEST_DAYS - 1) * 24 * 3600 * 1000);
  const [schemaDoc, versionDocs, statusDoc, requestDocs] = await Promise.all([
    reader.readDocument('state', `strategy-schema-${mode === 'live' ? 'paper' : mode}`), // live shares paper's explanations until #71
    reader.query('state', 'SELECT c["value"].name, c["value"].version, c["value"].status, c["value"].purpose, c["value"].riskProfile, c["value"].parameters, c["value"].allowedTools FROM c WHERE c.type = @type', {
      parameters: [{ name: '@type', value: 'strategy-version' }],
      maxItems: VERSIONS_MAX,
    }),
    reader.readDocument('state', `engine-status-${mode}`),
    mode === 'paper'
      ? reader.query('requests', 'SELECT * FROM c WHERE c.day >= @from AND c.type = @type', {
        parameters: [{ name: '@from', value: from }, { name: '@type', value: REQUEST_TYPE }],
        maxItems: REQUESTS_MAX,
      })
      : [],
  ]);
  return strategiesView({ mode, schema: schemaDoc?.value ?? null, versions: versionDocs, status: statusDoc?.value ?? null, requests: requestDocs }, now);
}

/** One switch request, for the page to follow it after a click; null if it isn't there (yet). */
async function loadStrategyRequest(reader, day, id) {
  if (!DAY.test(day ?? '') || !/^[0-9a-f-]{36}$/.test(id ?? '')) throw new RangeError('day and id must be a request\'s');
  const doc = await reader.readDocument('requests', id, day);
  return doc && doc.type === REQUEST_TYPE ? requestItem(doc) : null;
}

/**
 * The pairs panel for one mode: the engine's catalog of pairs the owner can add, the list it trades now, and the owner's add
 * and remove requests of the last week. Paper only (#71 brings live).
 */
async function loadPairs(reader, mode, now) {
  if (!MODES.has(mode)) throw new RangeError('mode must be paper or live');
  if (mode !== 'paper') return pairsView({ mode, catalog: null, list: null, requests: [] }, now);
  const from = utcDay(now - (REQUEST_DAYS - 1) * 24 * 3600 * 1000);
  const [catalogDoc, listDoc, requestDocs] = await Promise.all([
    reader.readDocument('state', `pair-catalog-${mode}`),
    reader.readDocument('state', `tradable-pairs-${mode}`),
    reader.query('requests', 'SELECT * FROM c WHERE c.day >= @from AND c.type = @type', {
      parameters: [{ name: '@from', value: from }, { name: '@type', value: PAIR_REQUEST_TYPE }],
      maxItems: REQUESTS_MAX,
    }),
  ]);
  return pairsView({ mode, catalog: catalogDoc?.value ?? null, list: listDoc?.value ?? null, requests: requestDocs }, now);
}

/** One pair request, for the page to follow it after a click; null if it isn't there (yet). */
async function loadPairRequest(reader, day, id) {
  if (!DAY.test(day ?? '') || !/^[0-9a-f-]{36}$/.test(id ?? '')) throw new RangeError('day and id must be a request\'s');
  const doc = await reader.readDocument('requests', id, day);
  return doc && doc.type === PAIR_REQUEST_TYPE ? pairRequestItem(doc) : null;
}

module.exports = { loadDashboard, loadJournalDay, journalRequest, loadActivity, loadRequests, loadRequest, loadStrategies, loadStrategyRequest, loadPairs, loadPairRequest, utcDay, KINDS };
