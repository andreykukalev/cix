'use strict';
// The owner's requests, under /api/owner/: manual buy and sell requests (ADR-0011, ADR-0012) and their history. Static Web
// Apps lets only the owner role through (staticwebapp.config.json); each handler checks again (ADR-0014). Nothing here is
// cached, by the platform or by a browser.
//
// The one write is POST /api/owner/requests, with the writer identity whose Cosmos DB role can only create items in the
// requests container. A cross-site page can't send it: it needs a JSON body and a custom header, which a browser sends
// cross-origin only after a preflight this API never answers.

const { app } = require('@azure/functions');
const { CosmosReader } = require('../cosmos');
const { RequestWriter, WriterNotConfigured } = require('../writer');
const { isOwner, forbidden } = require('../auth');
const { requestDocument, historyItem, RequestError } = require('../requests');
const { loadRequests, loadRequest } = require('../data');

const MAX_BODY = 4096;
const HEADERS = { 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
let reader;
let writer;

function getReader() {
  reader ??= new CosmosReader({
    endpoint: process.env.COSMOS_ENDPOINT,
    key: process.env.COSMOS_READONLY_KEY,
    database: process.env.COSMOS_DATABASE ?? 'cix',
  });
  return reader;
}

function getWriter() {
  writer ??= new RequestWriter({
    endpoint: process.env.COSMOS_ENDPOINT,
    database: process.env.COSMOS_DATABASE ?? 'cix',
    tenantId: process.env.COSMOS_WRITER_TENANT_ID,
    clientId: process.env.COSMOS_WRITER_CLIENT_ID,
    clientSecret: process.env.COSMOS_WRITER_SECRET,
  });
  return writer;
}

function respond(status, body) {
  return { status, jsonBody: body, headers: HEADERS };
}

/** Runs handler for the owner only; a failure is logged by name and status, never with the request or a secret. */
async function ownerOnly(request, context, what, handler) {
  if (!isOwner(request)) return forbidden();
  try {
    return await handler();
  } catch (error) {
    if (error instanceof RangeError || error instanceof RequestError) return respond(400, { error: error.message });
    if (error instanceof WriterNotConfigured) return respond(503, { error: error.message });
    context.error(`Owner ${what} failed: ${error?.name ?? 'Error'} ${error?.status ?? ''}`);
    return respond(503, { error: `The ${what} could not be completed right now.` });
  }
}

/** The body of a request from the dashboard page, or a RequestError: JSON, small, with the page's own header. */
async function readBody(request) {
  if (request.headers.get('x-cix-request') !== '1') throw new RequestError('Send requests from the dashboard.');
  if (!/^application\/json(;|$)/i.test(request.headers.get('content-type') ?? '')) throw new RequestError('The request must be JSON.');
  const text = await request.text();
  if (text.length > MAX_BODY) throw new RequestError('The request is too large.');
  try {
    return JSON.parse(text);
  } catch {
    throw new RequestError('The request must be JSON.');
  }
}

async function createRequest(request, context) {
  return ownerOnly(request, context, 'request', async () => {
    const doc = requestDocument(await readBody(request), Date.now());
    // Ids are unique per day (the partition): a retry just after midnight UTC would be a second request, so look at the
    // day before first.
    const earlier = await getReader().readDocument('requests', doc.id, previousDay(doc.day));
    const result = earlier ? 'exists' : await getWriter().create(doc);
    context.log(`Owner request ${result}: ${doc.side} ${doc.mode} ${doc.symbol} (${doc.id})`);
    if (result === 'created') return respond(201, { result, request: historyItem(doc) });
    // 'exists': this id was sent before (a retry). The first one stands: answer with it, as stored, and say so if the retry
    // asked for something else.
    const stored = earlier ?? await getReader().readDocument('requests', doc.id, doc.day);
    if (!stored) return respond(200, { result, request: historyItem(doc) }); // can't read it yet: the page follows it by id
    const same = ['side', 'mode', 'symbol', 'portion', 'quoteAmount'].every((k) => (stored[k] ?? null) === (doc[k] ?? null));
    return same
      ? respond(200, { result, request: historyItem(stored) })
      : respond(409, { error: 'This request was already sent with different details; it stands as first sent. Close the dialog to make a new one.', request: historyItem(stored) });
  });
}

function previousDay(day) {
  return new Date(Date.parse(`${day}T00:00:00Z`) - 86_400_000).toISOString().slice(0, 10);
}

app.http('owner-requests', {
  methods: ['GET', 'POST'],
  authLevel: 'anonymous', // Static Web Apps checks the role before the function runs
  route: 'owner/requests',
  handler: (request, context) => (request.method === 'POST'
    ? createRequest(request, context)
    : ownerOnly(request, context, 'request history', async () => respond(200, await loadRequests(getReader(), Date.now())))),
});

app.http('owner-request', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/requests/{day}/{id}',
  handler: (request, context) => ownerOnly(request, context, 'request lookup', async () => {
    const item = await loadRequest(getReader(), request.params.day, request.params.id);
    return item ? respond(200, item) : respond(404, { error: 'Not there yet.' });
  }),
});

module.exports = { createRequest, readBody, ownerOnly };
