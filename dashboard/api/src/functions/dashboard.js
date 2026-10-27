'use strict';
// The dashboard's reads (Static Web Apps managed functions, under /api): GET only, with the Cosmos DB read-only key kept in
// the app's settings on the server. Only the confirmed owner gets an answer (ADR-0014): the routes require the owner role,
// and each handler checks again before anything is read or taken from the cache. Answers are kept a few seconds per
// instance, so refreshes and several open tabs share one database read; a browser never keeps them (no-store).

const { app } = require('@azure/functions');
const { readerCache } = require('../cosmos');
const { loadDashboard, loadJournalDay, journalRequest, loadActivity, loadBalanceHistory } = require('../data');
const { isOwner, forbidden } = require('../auth');

const CACHE_MS = 20_000;
const ACTIVITY_CACHE_MS = 60_000; // a query across up to a year of day partitions: at most once a minute
const cache = new Map();
const getReader = readerCache(); // each mode's database: live's engine writes to its own (ADR-0017)

// Concurrent requests for the same thing share one database read (the promise is cached, not just the result).
async function cached(key, load, ttl = CACHE_MS) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttl) return hit.body;
  const body = load();
  cache.set(key, { at: Date.now(), body });
  if (cache.size > 200) cache.delete(cache.keys().next().value);
  try {
    return await body;
  } catch (error) {
    cache.delete(key); // don't keep a failure
    throw error;
  }
}

function respond(body, status = 200) {
  return { status, jsonBody: body, headers: { 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' } };
}

/** Runs load for the owner only: anyone else gets 403 before the database or the cache is touched. */
async function handle(request, context, load) {
  if (!isOwner(request)) return forbidden();
  try {
    return respond(await load());
  } catch (error) {
    if (error instanceof RangeError) return respond({ error: error.message }, 400);
    context.error(`Dashboard read failed: ${error?.name ?? 'Error'} ${error?.status ?? ''}`); // never the key or the request
    return respond({ error: 'The database could not be read right now.' }, 503);
  }
}

app.http('dashboard', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'dashboard',
  handler: (request, context) => handle(request, context, () => {
    const mode = request.query.get('mode') === 'live' ? 'live' : 'paper';
    return cached(`dashboard:${mode}`, () => loadDashboard(getReader(mode), mode, Date.now()));
  }),
});

app.http('journal', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'journal',
  handler: (request, context) => handle(request, context, () => {
    const now = Date.now();
    const mode = request.query.get('mode') === 'live' ? 'live' : 'paper';
    const r = journalRequest(request.query.get('day'), request.query.get('kind') ?? '', request.query.get('limit') ?? '100', now);
    return cached(`${mode}:${r.key}`, () => loadJournalDay(getReader(mode), r.day, r.kind, String(r.top), now, mode));
  }),
});

app.http('activity', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'activity',
  handler: (request, context) => handle(request, context, () => {
    const mode = request.query.get('mode') === 'live' ? 'live' : 'paper';
    const period = request.query.get('period') ?? 'day';
    if (!['day', 'week', 'month'].includes(period)) throw new RangeError('period must be day, week or month');
    return cached(`activity:${mode}:${period}`, () => loadActivity(getReader(mode), period, Date.now(), mode), ACTIVITY_CACHE_MS);
  }),
});

app.http('balance-history', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'balance-history',
  handler: (request, context) => handle(request, context, () => {
    const mode = request.query.get('mode') === 'live' ? 'live' : 'paper';
    const days = request.query.get('days') ?? '7';
    if (!['7', '30', '90'].includes(days)) throw new RangeError('days must be 7, 30 or 90');
    return cached(`balance-history:${mode}:${days}`, () => loadBalanceHistory(getReader(mode), days, Date.now(), mode), ACTIVITY_CACHE_MS);
  }),
});

module.exports = { handle };
