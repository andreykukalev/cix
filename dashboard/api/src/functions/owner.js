'use strict';
// The owner's part of the API, under /api/owner/: manual buy and sell requests (ADR-0011) and the decisions with prices
// kept. Static Web Apps lets only the owner role through (staticwebapp.config.json); each handler checks again. Nothing
// here is cached, by the platform or by a browser.
//
// The one write is POST /api/owner/requests, with the writer identity whose Cosmos DB role can only create items in the
// requests container. A cross-site page can't send it: it needs a JSON body and a custom header, which a browser sends
// cross-origin only after a preflight this API never answers.

const { app } = require('@azure/functions');
const { CosmosReader } = require('../cosmos');
const { RequestWriter } = require('../writer');
const { isOwner } = require('../auth');
const { requestDocument, historyItem, RequestError } = require('../requests');
const { loadRequests, loadRequest, loadOwnerDecisions } = require('../data');

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
  if (!isOwner(request)) return respond(403, { error: 'Sign in as the owner.' });
  try {
    return await handler();
  } catch (error) {
    if (error instanceof RangeError || error instanceof RequestError) return respond(400, { error: error.message });
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
    const result = await getWriter().create(doc);
    // 'exists': the same click arrived twice (a retry); the first one stands and is what the page follows.
    context.log(`Owner request ${result}: ${doc.side} ${doc.mode} ${doc.symbol} (${doc.id})`);
    return respond(result === 'created' ? 201 : 200, { result, request: historyItem(doc) });
  });
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

app.http('owner-decisions', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/decisions',
  handler: (request, context) => ownerOnly(request, context, 'decisions read', async () => respond(200, await loadOwnerDecisions(getReader(), Date.now()))),
});

module.exports = { createRequest, readBody, ownerOnly };
