'use strict';
// The owner's requests, under /api/owner/: manual buy and sell requests (ADR-0011, ADR-0012) and their history. Static Web
// Apps lets only the owner role through (staticwebapp.config.json); each handler checks again (ADR-0014). Nothing here is
// cached, by the platform or by a browser.
//
// The owner's strategy switches (#108) are requests too: POST /api/owner/strategy-requests creates one in the same container,
// and the engine checks it and switches (ADR-0015). GET /api/owner/strategies shows every strategy, explained.
//
// Adding and removing trading pairs (ADR-0016) works the same way: POST /api/owner/pair-requests creates a request, the engine
// checks it against config/risk-limits.json, the exchange and the portfolio, and GET /api/owner/pairs shows where things stand.
//
// Strategy switches, pair changes, Pause and Resume work for live too (ADR-0018): each goes to the live engine's own requests
// container, only with confirmLive, and each lookup and view takes ?mode=live.
//
// The analyst's Run now (POST /api/owner/analyst-requests) is a request too, for either mode: the engine queues an analyst
// run outside its schedule, or says why not.
//
// The owner's real stocks portfolio (ADR-0026) is a request too: POST /api/owner/stocks-portfolio creates the next
// submission, portfolio-N, in the stocks database's own requests container, where the same writer identity holds a separate
// create-only role (provision-stocks.ps1). The stocks host applies the newest on its next nightly run.
//
// The writes are POST /api/owner/requests and /api/owner/strategy-requests, with the writer identity whose Cosmos DB role can only create items in the
// requests container. A cross-site page can't send it: it needs a JSON body and a custom header, which a browser sends
// cross-origin only after a preflight this API never answers.

const { app } = require('@azure/functions');
const { readerCache, databaseFor } = require('../cosmos');
const { RequestWriter, WriterError, WriterNotConfigured } = require('../writer');
const { isOwner, forbidden } = require('../auth');
const { requestDocument, historyItem, RequestError } = require('../requests');
const { loadRequests, loadRequest, loadStrategies, loadStrategyRequest, loadPairs, loadPairRequest } = require('../data');
const { strategyRequestDocument, requestItem } = require('../strategies');
const { pairRequestDocument, pairRequestItem } = require('../pairs');
const {
  controlRequestDocument, controlRequestItem, analystRequestDocument, analystRequestItem, ANALYST_REQUEST_TYPE,
} = require('../controls');

const { stocksReader, stocksView } = require('../stocks');
const { portfolioValue, portfolioDocument, firstUnseen, pendingPortfolio, samePortfolio, MAX_PENDING } = require('../portfolio');

const MAX_BODY = 4096;
const MAX_PORTFOLIO_BODY = 16_384; // 50 holdings
const SENT_DIFFERENT = 'This portfolio was already saved with other figures; it stands as first saved. Close the dialog and open it again to save the new ones.';
const STOCKS_NOT_SET_UP = "The Stocks page can't send your portfolio yet: run tools/azure/provision-stocks.ps1 again, after setup-dashboard-requests.ps1.";
const HEADERS = { 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
const getReader = readerCache(); // each mode's database: live's engine writes to its own (ADR-0017); requests are paper's
const writers = new Map();
const LIVE_NOT_SET_UP = 'Live requests are not set up on this dashboard yet: run tools/azure/setup-dashboard-requests.ps1 -LiveRequests.';

/**
 * The writer for one mode's requests container: paper's database, or the live engine's own (ADR-0018), where the same
 * identity holds a separate create-only role. Live without its database setting is refused, never written to paper's.
 */
function requireLiveDatabase(mode) {
  if (mode === 'live' && !process.env.COSMOS_LIVE_DATABASE) throw new WriterNotConfigured(LIVE_NOT_SET_UP);
}

function getWriter(mode = 'paper') {
  requireLiveDatabase(mode);
  const database = databaseFor(mode);
  if (!writers.has(database)) {
    writers.set(database, new RequestWriter({
      endpoint: process.env.COSMOS_ENDPOINT,
      database,
      tenantId: process.env.COSMOS_WRITER_TENANT_ID,
      clientId: process.env.COSMOS_WRITER_CLIENT_ID,
      clientSecret: process.env.COSMOS_WRITER_SECRET,
    }));
  }
  return writers.get(database);
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
async function readBody(request, max = MAX_BODY) {
  if (request.headers.get('x-cix-request') !== '1') throw new RequestError('Send requests from the dashboard.');
  if (!/^application\/json(;|$)/i.test(request.headers.get('content-type') ?? '')) throw new RequestError('The request must be JSON.');
  const text = await request.text();
  if (text.length > max) throw new RequestError('The request is too large.');
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

/**
 * Creates doc in its mode's requests container (paper's, or the live engine's own: ADR-0018), or finds it there from an
 * earlier send of the same dialog. Returns 'created' or 'exists', with the stored document for 'exists' (null when it can't
 * be read back yet). Live is refused before any read, since live's reads would otherwise fall back to paper's database.
 */
async function createInMode(doc) {
  const mode = doc.tradingMode;
  requireLiveDatabase(mode);
  const earlier = await getReader(mode).readDocument('requests', doc.id, previousDay(doc.day));
  let result;
  try {
    result = earlier ? 'exists' : await getWriter(mode).create(doc);
  } catch (error) {
    // The live role is the owner's to grant (setup-dashboard-requests.ps1 -LiveRequests): without it, say so.
    if (mode === 'live' && error instanceof WriterError && error.status === 403) throw new WriterNotConfigured(LIVE_NOT_SET_UP);
    throw error;
  }
  const stored = result === 'created' ? null : earlier ?? await getReader(mode).readDocument('requests', doc.id, doc.day);
  return { result, stored };
}

/** Whether a stored request is the one doc describes, in the same mode: the same dialog sent again. */
function sameRequest(stored, doc, fields) {
  return stored.type === doc.type && (stored.tradingMode ?? 'paper') === doc.tradingMode && fields.every((f) => stored[f] === doc[f]);
}

const DIFFERENT = 'This request was already sent with different details; it stands as first sent. Close the dialog to make a new one.';

async function createStrategyRequest(request, context) {
  return ownerOnly(request, context, 'strategy request', async () => {
    const doc = strategyRequestDocument(await readBody(request), Date.now());
    const { result, stored } = await createInMode(doc);
    context.log(`Owner ${doc.tradingMode} strategy request ${result}: ${doc.name}@v${doc.version} (${doc.id})`);
    if (result === 'created') return respond(201, { result, request: requestItem(doc) });
    if (!stored) return respond(200, { result, request: requestItem(doc) });
    return sameRequest(stored, doc, ['name', 'version'])
      ? respond(200, { result, request: requestItem(stored) })
      : respond(409, { error: DIFFERENT });
  });
}

async function createPairRequest(request, context) {
  return ownerOnly(request, context, 'pair request', async () => {
    const doc = pairRequestDocument(await readBody(request), Date.now());
    const { result, stored } = await createInMode(doc);
    context.log(`Owner ${doc.tradingMode} pair request ${result}: ${doc.action} ${doc.symbol} (${doc.id})`);
    if (result === 'created') return respond(201, { result, request: pairRequestItem(doc) });
    if (!stored) return respond(200, { result, request: pairRequestItem(doc) });
    return sameRequest(stored, doc, ['action', 'symbol'])
      ? respond(200, { result, request: pairRequestItem(stored) })
      : respond(409, { error: DIFFERENT });
  });
}

async function createControlRequest(request, context) {
  return ownerOnly(request, context, 'pause or resume request', async () => {
    const doc = controlRequestDocument(await readBody(request), Date.now());
    const { result, stored } = await createInMode(doc);
    context.log(`Owner ${doc.tradingMode} ${doc.action} request ${result} (${doc.id})`);
    if (result === 'created') return respond(201, { result, request: controlRequestItem(doc) });
    if (!stored) return respond(200, { result, request: controlRequestItem(doc) });
    return sameRequest(stored, doc, ['action', 'haltSince'])
      ? respond(200, { result, request: controlRequestItem(stored) })
      : respond(409, { error: DIFFERENT });
  });
}

async function createAnalystRequest(request, context) {
  return ownerOnly(request, context, 'analyst run request', async () => {
    const doc = analystRequestDocument(await readBody(request), Date.now());
    const { result, stored } = await createInMode(doc);
    context.log(`Owner ${doc.tradingMode} analyst run request ${result} (${doc.id})`);
    if (result === 'created') return respond(201, { result, request: analystRequestItem(doc) });
    if (!stored) return respond(200, { result, request: analystRequestItem(doc) });
    return sameRequest(stored, doc, [])
      ? respond(200, { result, request: analystRequestItem(stored) })
      : respond(409, { error: DIFFERENT });
  });
}

let stocksRead;
let stocksWriter;

/** The stocks database's writer: the same identity, its create-only role there (provision-stocks.ps1); partitioned by id. */
function getStocksWriter() {
  if (!process.env.STOCKS_COSMOS_ENDPOINT) throw new WriterNotConfigured(STOCKS_NOT_SET_UP);
  stocksWriter ??= new RequestWriter({
    endpoint: process.env.STOCKS_COSMOS_ENDPOINT,
    database: process.env.STOCKS_COSMOS_DATABASE || 'cix-stocks',
    tenantId: process.env.COSMOS_WRITER_TENANT_ID,
    clientId: process.env.COSMOS_WRITER_CLIENT_ID,
    clientSecret: process.env.COSMOS_WRITER_SECRET,
    partitionKeyOf: (doc) => doc.id,
  });
  return stocksWriter;
}

/**
 * Sends the owner's whole portfolio as the next submission. The number is the first free one after what the host has seen;
 * a send that loses the number to another takes the next. The same dialog sent again (its clientId) is answered with the
 * submission already there, not sent twice.
 */
async function createStocksPortfolio(request, context) {
  return ownerOnly(request, context, 'portfolio', async () => {
    const now = Date.now();
    const value = portfolioValue(await readBody(request, MAX_PORTFOLIO_BODY), now);
    stocksRead ??= stocksReader();
    if (!stocksRead) throw new WriterNotConfigured(STOCKS_NOT_SET_UP);
    const latest = await stocksRead.readDocument('advice', 'advice-latest');
    const advice = latest ? stocksView(latest.value ?? null, now) : null;
    const { newest, next } = await pendingPortfolio(stocksRead, firstUnseen(advice));
    if (value.clientId && newest?.clientId === value.clientId) {
      // The same dialog again: the first send stands. Edited figures since would be dropped silently, so say so.
      return samePortfolio(newest, value)
        ? respond(200, { result: 'exists', pending: newest })
        : respond(409, { error: SENT_DIFFERENT, pending: newest });
    }
    let n = next;
    const last = next === null ? null : Math.min(next + 2, firstUnseen(advice) + MAX_PENDING - 1); // the host looks no further
    for (; n !== null && n <= last; n += 1) {
      let result;
      try {
        result = await getStocksWriter().create(portfolioDocument(value, n));
      } catch (error) {
        // The role is granted by provision-stocks.ps1: without it, say so.
        if (error instanceof WriterError && error.status === 403) throw new WriterNotConfigured(STOCKS_NOT_SET_UP);
        throw error;
      }
      if (result === 'created') {
        context.log(`Owner stocks portfolio created: portfolio-${n}, ${value.holdings.length} holdings`);
        return respond(201, { result, pending: { sequence: n, ...value } });
      }
    }
    if (next === null || n > firstUnseen(advice) + MAX_PENDING - 1) throw new RequestError('Too many portfolio changes are waiting for the next run: wait for it, then send again.');
    throw new RequestError('Another change was sent at the same time: reload the page and send again.');
  });
}

/** The mode a lookup or view asks for: live only when it says so. */
function modeOf(request) {
  return request.query.get('mode') === 'live' ? 'live' : 'paper';
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

app.http('owner-strategies', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/strategies',
  handler: (request, context) => ownerOnly(request, context, 'strategy view', async () => {
    const mode = modeOf(request);
    requireLiveDatabase(mode); // never paper's database shown as live's
    return respond(200, await loadStrategies(getReader(mode), mode, Date.now(), getReader('paper')));
  }),
});

app.http('owner-strategy-requests', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'owner/strategy-requests',
  handler: createStrategyRequest,
});

app.http('owner-strategy-request', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/strategy-requests/{day}/{id}',
  handler: (request, context) => ownerOnly(request, context, 'strategy request lookup', async () => {
    const mode = modeOf(request);
    requireLiveDatabase(mode);
    const item = await loadStrategyRequest(getReader(mode), request.params.day, request.params.id, mode);
    return item ? respond(200, item) : respond(404, { error: 'Not there yet.' });
  }),
});

app.http('owner-pairs', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/pairs',
  handler: (request, context) => ownerOnly(request, context, 'pairs view', async () => {
    const mode = modeOf(request);
    requireLiveDatabase(mode); // never paper's database shown as live's
    return respond(200, await loadPairs(getReader(mode), mode, Date.now()));
  }),
});

app.http('owner-pair-requests', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'owner/pair-requests',
  handler: createPairRequest,
});

app.http('owner-pair-request', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/pair-requests/{day}/{id}',
  handler: (request, context) => ownerOnly(request, context, 'pair request lookup', async () => {
    const mode = modeOf(request);
    requireLiveDatabase(mode);
    const item = await loadPairRequest(getReader(mode), request.params.day, request.params.id, mode);
    return item ? respond(200, item) : respond(404, { error: 'Not there yet.' });
  }),
});

app.http('owner-control-requests', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'owner/control-requests',
  handler: createControlRequest,
});

app.http('owner-control-request', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/control-requests/{day}/{id}',
  handler: (request, context) => ownerOnly(request, context, 'pause or resume lookup', async () => {
    const mode = modeOf(request);
    const { day, id } = request.params;
    if (!/^\d{4}-\d{2}-\d{2}$/.test(day ?? '') || !/^[0-9a-f-]{36}$/.test(id ?? '')) throw new RangeError('day and id must be a request\'s');
    requireLiveDatabase(mode);
    const doc = await getReader(mode).readDocument('requests', id, day);
    return doc && doc.type === 'trading-control' && (doc.tradingMode ?? 'paper') === mode ? respond(200, controlRequestItem(doc)) : respond(404, { error: 'Not there yet.' });
  }),
});

app.http('owner-analyst-requests', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'owner/analyst-requests',
  handler: createAnalystRequest,
});

app.http('owner-analyst-request', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'owner/analyst-requests/{day}/{id}',
  handler: (request, context) => ownerOnly(request, context, 'analyst run lookup', async () => {
    const mode = modeOf(request);
    const { day, id } = request.params;
    if (!/^\d{4}-\d{2}-\d{2}$/.test(day ?? '') || !/^[0-9a-f-]{36}$/.test(id ?? '')) throw new RangeError('day and id must be a request\'s');
    requireLiveDatabase(mode);
    const doc = await getReader(mode).readDocument('requests', id, day);
    return doc && doc.type === ANALYST_REQUEST_TYPE && (doc.tradingMode ?? 'paper') === mode
      ? respond(200, analystRequestItem(doc)) : respond(404, { error: 'Not there yet.' });
  }),
});

app.http('owner-stocks-portfolio', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'owner/stocks-portfolio',
  handler: createStocksPortfolio,
});

module.exports = {
  createRequest, createStrategyRequest, createPairRequest, createControlRequest, createAnalystRequest, createStocksPortfolio, readBody, ownerOnly,
};
