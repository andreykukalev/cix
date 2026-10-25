'use strict';
// The dashboard's API (Static Web Apps managed functions, under /api). Read-only: GET only, and the Cosmos DB key it uses
// is the read-only one, kept in the app's settings on the server. Responses are cached for a few seconds per instance, so
// a busy (public) page can't use up the database throughput the engine needs.

const { app } = require('@azure/functions');
const { CosmosReader } = require('../cosmos');
const { loadDashboard, loadJournalDay, journalRequest } = require('../data');

const CACHE_MS = 20_000;
const cache = new Map();
let reader;

function getReader() {
  reader ??= new CosmosReader({
    endpoint: process.env.COSMOS_ENDPOINT,
    key: process.env.COSMOS_READONLY_KEY,
    database: process.env.COSMOS_DATABASE ?? 'cix',
  });
  return reader;
}

// Concurrent requests for the same thing share one database read (the promise is cached, not just the result).
async function cached(key, load) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < CACHE_MS) return hit.body;
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
  const cacheControl = status === 200 ? `public, max-age=${CACHE_MS / 1000}` : 'no-store'; // never keep an error around
  return { status, jsonBody: body, headers: { 'cache-control': cacheControl, 'x-content-type-options': 'nosniff' } };
}

async function handle(context, load) {
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
  handler: (request, context) => handle(context, () => {
    const mode = request.query.get('mode') === 'live' ? 'live' : 'paper';
    return cached(`dashboard:${mode}`, () => loadDashboard(getReader(), mode, Date.now()));
  }),
});

app.http('journal', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'journal',
  handler: (request, context) => handle(context, () => {
    const now = Date.now();
    const r = journalRequest(request.query.get('day'), request.query.get('kind') ?? '', request.query.get('limit') ?? '100', now);
    return cached(r.key, () => loadJournalDay(getReader(), r.day, r.kind, String(r.top), now));
  }),
});
