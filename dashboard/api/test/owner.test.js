'use strict';
// The owner endpoints, run through their real handlers with @azure/functions replaced by a stub that records the routes,
// and the database replaced by fakes: who may call, what is written, and what comes back.
const test = require('node:test');
const assert = require('node:assert/strict');
const Module = require('node:module');

const routes = {};
const originalLoad = Module._load;
Module._load = function load(request, ...rest) {
  if (request === '@azure/functions') return { app: { http: (name, options) => { routes[name] = options; } } };
  return originalLoad.call(this, request, ...rest);
};
const writes = [];
const { RequestWriter } = require('../src/writer');
const writeDatabases = [];
let writerAnswer = null; // a status the fake Cosmos DB answers instead of creating, e.g. 403 without the role
RequestWriter.prototype.create = async function create(doc) {
  if (writerAnswer) { const { WriterError } = require('../src/writer'); throw new WriterError(writerAnswer, 'Cosmos DB'); }
  writes.push(doc);
  writeDatabases.push(this.database);
  if (stored.has(doc.id) && stored.get(doc.id).day === doc.day) return 'exists'; // ids are unique within a day's partition
  stored.set(doc.id, doc);
  return 'created';
};
const { CosmosReader } = require('../src/cosmos');
const reads = [];
CosmosReader.prototype.query = async function query(container, sql, options) {
  reads.push({ database: this.database, container, sql, options });
  return container === 'requests' ? [{ id: 'r1', type: 'manual-request', createdAt: '2026-10-26T10:00:00Z', status: 'filled' }] : [];
};
const stored = new Map(); // what the fake writer created, by id, for reads after a retry
CosmosReader.prototype.readDocument = async function readDocument(container, id, pk) {
  reads.push({ database: this.database, container, id, pk });
  // CIX's requests are partitioned by day; the stocks database's (the owner's portfolio) by id.
  if (container === 'requests' && stored.has(id)) return (stored.get(id).day ?? id) === (pk ?? id) ? stored.get(id) : null;
  // The looked-up request has its own id, so the request created in these tests never finds it as yesterday's.
  return id === 'a1b2c3d4-0000-4000-8000-000000000001' && pk === '2026-10-26' ? { id, type: 'manual-request', status: 'placed' } : null;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://acct.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('k').toString('base64'), COSMOS_DATABASE: 'cix',
  COSMOS_LIVE_DATABASE: 'cix-live', DASHBOARD_OWNER_USER_ID: 'owner-user-id',
  COSMOS_WRITER_TENANT_ID: '11111111-2222-3333-4444-555555555555', COSMOS_WRITER_CLIENT_ID: 'app', COSMOS_WRITER_SECRET: ['x', 'y'].join(''),
  STOCKS_COSMOS_ENDPOINT: 'https://stocks.documents.azure.com', STOCKS_COSMOS_READONLY_KEY: Buffer.from('s').toString('base64'),
});
require('../src/functions/owner.js');
Module._load = originalLoad;

const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64');
const OWNER = b64({ identityProvider: 'aad', userId: 'owner-user-id', userRoles: ['authenticated', 'owner'] });
const VISITOR = b64({ identityProvider: 'aad', userId: 'visitor', userRoles: ['authenticated'] });
const OTHER_OWNER = b64({ identityProvider: 'aad', userId: 'redeemed-the-link', userRoles: ['authenticated', 'owner'] }); // has the role, isn't the owner
const context = { log() {}, error() {} };

function request({ method = 'GET', principal = OWNER, headers = {}, body = '', params = {}, query = '' } = {}) {
  const all = { 'x-ms-client-principal': principal, 'content-type': 'application/json', 'x-cix-request': '1', ...headers };
  return { method, params, query: new URLSearchParams(query), headers: { get: (n) => all[n.toLowerCase()] ?? null }, text: async () => body };
}

// Every crypto request is the live engine's (ADR-0028): it says so, and the owner has ticked the real-money box.
const live = (body) => ({ ...body, tradingMode: 'live', confirmLive: true });
const STRATEGY_BODY = JSON.stringify(live({ id: '7c9e6679-7425-40de-944b-e07fc1f90ae7', name: 'balanced', version: 1, note: 'calmer' }));
const BODY = STRATEGY_BODY;

test('the owner endpoints are registered under /api/owner, and the paper buy and sell requests are gone', () => {
  assert.deepEqual(Object.values(routes).map((r) => r.route).sort(),
    ['owner/analyst-requests', 'owner/analyst-requests/{day}/{id}', 'owner/control-requests', 'owner/control-requests/{day}/{id}', 'owner/pair-requests', 'owner/pair-requests/{day}/{id}', 'owner/pairs',
      'owner/stocks-portfolio', 'owner/stocks-trade', 'owner/strategies', 'owner/strategy-requests', 'owner/strategy-requests/{day}/{id}']);
});

test('nobody else can write or read, whatever the route rules say', async () => {
  writes.length = 0;
  for (const principal of [VISITOR, OTHER_OWNER, null, 'garbage']) {
    for (const name of ['owner-strategy-requests', 'owner-strategy-request', 'owner-strategies', 'owner-pairs']) {
      const method = name === 'owner-strategy-requests' ? 'POST' : 'GET';
      const r = await routes[name].handler(request({ method, principal, body: BODY }), context);
      assert.equal(r.status, 403, `${name} ${principal}`);
    }
  }
  assert.equal(writes.length, 0);
});

test('a write needs the page\'s header, a JSON body and a small one', async () => {
  writes.length = 0;
  const cases = [
    request({ method: 'POST', body: BODY, headers: { 'x-cix-request': null } }),
    request({ method: 'POST', body: BODY, headers: { 'content-type': 'text/plain' } }),
    request({ method: 'POST', body: BODY, headers: { 'content-type': 'application/x-www-form-urlencoded' } }),
    request({ method: 'POST', body: 'not json' }),
    request({ method: 'POST', body: JSON.stringify({ note: 'x'.repeat(5000) }) }),
  ];
  for (const r of cases) assert.equal((await routes['owner-strategy-requests'].handler(r, context)).status, 400);
  assert.equal(writes.length, 0);
});

test('paper is refused before anything is read or written: there is no paper engine (ADR-0028)', async () => {
  writes.length = 0;
  reads.length = 0;
  const id = () => crypto.randomUUID();
  const posts = [
    ['owner-strategy-requests', { id: id(), name: 'safe', version: 1 }],
    ['owner-strategy-requests', { id: id(), name: 'safe', version: 1, tradingMode: 'paper' }],
    ['owner-pair-requests', { id: id(), action: 'add', symbol: 'SOL-EUR' }],
    ['owner-control-requests', { id: id(), action: 'pause' }],
    ['owner-analyst-requests', { id: id() }],
  ];
  for (const [name, body] of posts) {
    const r = await routes[name].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);
    assert.equal(r.status, 400, `${name} ${JSON.stringify(body)}`);
    assert.match(r.jsonBody.error, /Paper trading was removed/);
  }
  for (const name of ['owner-strategies', 'owner-pairs']) {
    const r = await routes[name].handler(request({ query: 'mode=paper' }), context);
    assert.equal(r.status, 400, name);
  }
  const lookup = await routes['owner-control-request'].handler(request({ params: { day: '2026-10-27', id: id() }, query: 'mode=paper' }), context);
  assert.equal(lookup.status, 400);
  assert.equal(writes.length, 0);
  assert.equal(reads.length, 0);
});

test('a retry with other details is refused; the request stands as first sent', async () => {
  stored.clear();
  const id = '7a1b2c3d-1111-4222-8333-444455556666';
  const first = JSON.stringify(live({ id, name: 'safe', version: 3 }));
  assert.equal((await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: first }), context)).status, 201);
  const changed = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: first.replace('"version":3', '"version":1') }), context);
  assert.equal(changed.status, 409);
  assert.equal(stored.get(id).version, 3);
});

test('a retry just after midnight finds the request made the day before', async () => {
  stored.clear();
  const id = '8b2c3d4e-2222-4333-8444-555566667777';
  const yesterday = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
  stored.set(id, { id, day: yesterday, type: 'strategy-selection', name: 'risky', version: 4, tradingMode: 'live', status: 'accepted' });
  writes.length = 0;
  const r = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify(live({ id, name: 'risky', version: 4 })) }), context);
  assert.equal(r.status, 200);
  assert.equal(r.jsonBody.result, 'exists');
  assert.equal(r.jsonBody.request.status, 'accepted');
  assert.equal(writes.length, 0, 'not written a second time');
});

test('without a confirmed owner, nobody is the owner', async () => {
  const saved = process.env.DASHBOARD_OWNER_USER_ID;
  delete process.env.DASHBOARD_OWNER_USER_ID;
  try {
    assert.equal((await routes['owner-strategies'].handler(request(), context)).status, 403);
  } finally { process.env.DASHBOARD_OWNER_USER_ID = saved; }
});

test('with no writer settings, the owner is told requests aren\'t set up', async () => {
  // A fresh copy of the module, so its writer is built from these (missing) settings.
  const saved = process.env.COSMOS_WRITER_SECRET;
  delete process.env.COSMOS_WRITER_SECRET;
  const fresh = {};
  Module._load = function load(request, ...rest) {
    if (request === '@azure/functions') return { app: { http: (name, options) => { fresh[name] = options; } } };
    return originalLoad.call(this, request, ...rest);
  };
  try {
    delete require.cache[require.resolve('../src/functions/owner.js')];
    require('../src/functions/owner.js');
    const r = await fresh['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify(live({ id: '9c3d4e5f-3333-4444-8555-666677778888', name: 'safe', version: 3 })) }), context);
    assert.equal(r.status, 503);
    assert.match(r.jsonBody.error, /setup-dashboard-requests/);
  } finally {
    Module._load = originalLoad;
    process.env.COSMOS_WRITER_SECRET = saved;
  }
});

test('the owner asks to switch strategy: a request in the live requests container, never the state', async () => {
  writes.length = 0;
  writeDatabases.length = 0;
  stored.clear();
  const first = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: STRATEGY_BODY }), context);
  const again = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: STRATEGY_BODY }), context);

  assert.equal(first.status, 201);
  assert.equal(first.headers['cache-control'], 'no-store');
  assert.equal(again.status, 200); // the same dialog sent twice is one request
  assert.equal(again.jsonBody.result, 'exists');
  assert.equal(writes.length, 2, 'the database, not the API, finds the id taken');
  assert.deepEqual(writeDatabases, ['cix-live', 'cix-live']);
  assert.deepEqual({ type: writes[0].type, name: writes[0].name, version: writes[0].version, status: writes[0].status, requestedBy: writes[0].requestedBy, tradingMode: writes[0].tradingMode },
    { type: 'strategy-selection', name: 'balanced', version: 1, status: 'new', requestedBy: 'owner', tradingMode: 'live' });
  assert.equal('confirmLive' in writes[0], false);
  assert.equal(first.jsonBody.request.name, 'balanced');
});

test('strategy requests: owner only, only when confirmed, well formed, followed in the live engine\'s database', async () => {
  const liveBody = { ...JSON.parse(STRATEGY_BODY), id: '9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d', name: 'safe', version: 2 };
  const visitor = await routes['owner-strategy-requests'].handler(request({ method: 'POST', principal: VISITOR, body: JSON.stringify(liveBody) }), context);
  writeDatabases.length = 0;
  const unconfirmed = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, confirmLive: false }) }), context);
  assert.deepEqual(writeDatabases, [], 'nothing written without the confirmation');
  const sent = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify(liveBody) }), context);
  const bad = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, id: crypto.randomUUID(), version: '1' }) }), context);

  assert.equal(visitor.status, 403);
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.equal(sent.status, 201);
  assert.deepEqual(writeDatabases, ['cix-live']);
  assert.deepEqual({ name: writes.at(-1).name, version: writes.at(-1).version, tradingMode: writes.at(-1).tradingMode }, { name: 'safe', version: 2, tradingMode: 'live' });
  assert.equal(bad.status, 400);

  const day = writes.at(-1).day;
  const lookup = (query) => routes['owner-strategy-request'].handler(request({ params: { day, id: liveBody.id }, query }), context);
  assert.equal((await lookup('mode=live')).status, 200);
  assert.equal((await lookup('')).status, 200, 'no mode is live');
});

test('the strategy view is the owner\'s only, and live\'s', async () => {
  const visitor = await routes['owner-strategies'].handler(request({ principal: VISITOR }), context);
  const owner = await routes['owner-strategies'].handler(request(), context);

  assert.equal(visitor.status, 403);
  assert.equal(owner.status, 200);
  assert.equal(owner.jsonBody.mode, 'live');
  assert.equal(owner.headers['cache-control'], 'no-store');
});

const PAIR_BODY = JSON.stringify(live({ id: 'c56a4180-65aa-42ec-a945-5fd21dec0538', action: 'add', symbol: 'SOL-EUR', note: 'more coins' }));

test('the owner asks to add a pair: a request in the live requests container, never the state', async () => {
  writes.length = 0;
  const first = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: PAIR_BODY }), context);
  const again = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: PAIR_BODY }), context);

  assert.equal(first.status, 201);
  assert.equal(again.status, 200); // the same dialog sent twice is one request
  assert.deepEqual({ type: writes[0].type, action: writes[0].action, symbol: writes[0].symbol, status: writes[0].status, requestedBy: writes[0].requestedBy, tradingMode: writes[0].tradingMode },
    { type: 'tradable-pair', action: 'add', symbol: 'SOL-EUR', status: 'new', requestedBy: 'owner', tradingMode: 'live' });
  assert.equal(first.jsonBody.request.symbol, 'SOL-EUR');
});

test('pair requests: owner only, only when confirmed and to the live engine, well formed', async () => {
  const liveBody = { ...JSON.parse(PAIR_BODY), id: '2c1f7c3e-1b7a-4c1d-8a7e-3f0a9b1c2d3e' };
  const visitor = await routes['owner-pair-requests'].handler(request({ method: 'POST', principal: VISITOR, body: JSON.stringify(liveBody) }), context);
  writeDatabases.length = 0;
  const unconfirmed = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, confirmLive: undefined }) }), context);
  assert.deepEqual(writeDatabases, [], 'nothing written without the confirmation');
  const sent = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify(liveBody) }), context);
  const bad = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, id: crypto.randomUUID(), action: 'swap' }) }), context);
  const noHeader = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify(liveBody), headers: { 'x-cix-request': null } }), context);

  assert.equal(visitor.status, 403);
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.equal(sent.status, 201);
  assert.deepEqual(writeDatabases, ['cix-live']);
  assert.equal(writes.at(-1).tradingMode, 'live');
  assert.equal(bad.status, 400);
  assert.equal(noHeader.status, 400);

  const day = writes.at(-1).day;
  const lookup = (query) => routes['owner-pair-request'].handler(request({ params: { day, id: liveBody.id }, query }), context);
  assert.equal((await lookup('mode=live')).status, 200);

  const saved = process.env.COSMOS_LIVE_DATABASE;
  delete process.env.COSMOS_LIVE_DATABASE;
  try {
    reads.length = 0;
    const off = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, id: crypto.randomUUID() }) }), context);
    assert.equal(off.status, 503, 'never written to another database instead');
    assert.equal((await lookup('mode=live')).status, 503, 'a lookup never reads another database instead');
    assert.equal(reads.length, 0);
  } finally { process.env.COSMOS_LIVE_DATABASE = saved; }
  assert.deepEqual(writeDatabases, ['cix-live']);
});

test('the pairs view is the owner\'s only, and live\'s', async () => {
  const visitor = await routes['owner-pairs'].handler(request({ principal: VISITOR }), context);
  const owner = await routes['owner-pairs'].handler(request(), context);

  assert.equal(visitor.status, 403);
  assert.equal(owner.status, 200);
  assert.equal(owner.jsonBody.mode, 'live');
});

test("the strategy view reads the live engine's database only, its requests too", async () => {
  reads.length = 0;
  const view = await routes['owner-strategies'].handler(request({ query: 'mode=live' }), context);
  assert.equal(view.status, 200);
  assert.deepEqual([...new Set(reads.map((r) => r.database))], ['cix-live']);
  assert.ok(reads.some((r) => r.id === 'strategy-schema-live'));
  assert.ok(reads.some((r) => r.container === 'requests'));
});

test("Pause and Resume need the owner's confirmation and go to the live engine's database", async () => {
  const post = (body) => routes['owner-control-requests'].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);
  const id = () => crypto.randomUUID();

  writeDatabases.length = 0;
  const paused = await post(live({ id: id(), action: 'pause', note: 'lunch' }));
  assert.equal(paused.status, 201);
  assert.equal(paused.jsonBody.request.action, 'pause');
  assert.equal(writes.at(-1).type, 'trading-control');
  assert.equal(writes.at(-1).tradingMode, 'live');
  assert.deepEqual(writeDatabases, ['cix-live']);

  const since = '2026-10-27T16:45:00.1234567+00:00'; // as the engine's status writes it
  const unconfirmed = await post({ id: id(), action: 'resume', tradingMode: 'live', haltSince: since });
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.deepEqual(writeDatabases, ['cix-live'], 'nothing written without the confirmation');

  const resumed = await post(live({ id: id(), action: 'resume', haltSince: since, haltAlsoAt: null }));
  assert.equal(resumed.status, 201);
  assert.deepEqual(writeDatabases, ['cix-live', 'cix-live']);
  assert.equal(writes.at(-1).haltSince, since, 'the halt the page showed, passed back untouched');
  assert.equal('haltAlsoAt' in writes.at(-1), false);
  const seenAlso = await post(live({ id: id(), action: 'resume', haltSince: since, haltAlsoAt: '2026-10-27T17:00:00Z' }));
  assert.equal(seenAlso.status, 201);
  assert.equal(writes.at(-1).haltAlsoAt, '2026-10-27T17:00:00Z');

  for (const bad of [{ id: id(), action: 'sell-all' }, { id: 'nope', action: 'pause' }, { id: id(), action: 'pause', tradingMode: 'demo' },
    { id: id(), action: 'resume' }, { id: id(), action: 'resume', haltSince: 'yesterday' }, { id: id(), action: 'resume', haltSince: since, haltAlsoAt: 5 }]) {
    assert.equal((await post({ ...live(bad), ...bad })).status, 400, JSON.stringify(bad));
  }

  writerAnswer = 403; // the live role isn't granted yet
  try {
    const refused = await post(live({ id: id(), action: 'pause' }));
    assert.equal(refused.status, 503);
    assert.match(refused.jsonBody.error, /-LiveRequests/);
  } finally { writerAnswer = null; }

  const saved = process.env.COSMOS_LIVE_DATABASE;
  delete process.env.COSMOS_LIVE_DATABASE;
  try {
    const off = await post(live({ id: id(), action: 'pause' }));
    assert.equal(off.status, 503, 'never written to another database instead');
    assert.match(off.jsonBody.error, /-LiveRequests/);
    reads.length = 0;
    const lookup = await routes['owner-control-request'].handler(request({ params: { day: '2026-10-27', id: id() }, query: 'mode=live' }), context);
    assert.equal(lookup.status, 503, 'a lookup never reads another database instead');
    assert.equal(reads.length, 0);
    for (const view of ['owner-strategies', 'owner-pairs']) {
      assert.equal((await routes[view].handler(request({ query: 'mode=live' }), context)).status, 503, `${view} never shows another database as live's`);
    }
    assert.equal(reads.length, 0);
  } finally { process.env.COSMOS_LIVE_DATABASE = saved; }
  assert.deepEqual(writeDatabases, ['cix-live', 'cix-live', 'cix-live']);
});

test("Run now needs the owner's confirmation and goes to the live engine; the lookup reads only its own type", async () => {
  const post = (body) => routes['owner-analyst-requests'].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);
  const lookup = (day, id, query = '') => routes['owner-analyst-request'].handler(request({ params: { day, id }, query }), context);

  writeDatabases.length = 0;
  const id = crypto.randomUUID();
  const unconfirmed = await post({ id, tradingMode: 'live' });
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  const sent = await post(live({ id, note: 'ignored' }));
  assert.equal(sent.status, 201);
  assert.equal(sent.jsonBody.request.status, 'new');
  const doc = writes.at(-1);
  assert.deepEqual(Object.keys(doc).sort(), ['createdAt', 'day', 'id', 'requestedBy', 'status', 'tradingMode', 'type'], 'nothing the analyst reads');
  assert.equal(doc.type, 'analyst-run');
  assert.deepEqual(writeDatabases, ['cix-live']);
  const again = await post(live({ id }));
  assert.equal(again.status, 200, 'the same press again is the same request');
  assert.equal(again.jsonBody.result, 'exists');

  assert.equal((await lookup(doc.day, id)).status, 200);
  assert.equal((await lookup(doc.day, id, 'mode=live')).status, 200);
  stored.set('0f0f0f0f-0000-4000-8000-000000000001', { id: '0f0f0f0f-0000-4000-8000-000000000001', day: doc.day, type: 'trading-control', tradingMode: 'live' });
  assert.equal((await lookup(doc.day, '0f0f0f0f-0000-4000-8000-000000000001')).status, 404, 'another type of request is not a Run now');

  for (const bad of [{ id: 'nope' }, { id: crypto.randomUUID(), tradingMode: 'demo' }, []]) {
    assert.equal((await post(Array.isArray(bad) ? bad : { ...live(bad), ...bad })).status, 400, JSON.stringify(bad));
  }
  assert.equal((await lookup('yesterday', id)).status, 400);
  const visitor = await routes['owner-analyst-requests'].handler(request({ method: 'POST', principal: VISITOR, body: JSON.stringify(live({ id: crypto.randomUUID() })) }), context);
  assert.equal(visitor.status, 403);
});

const PORTFOLIO = {
  clientId: '0d9c2f4e-1a2b-4c3d-8e9f-0a1b2c3d4e5f', capital: '10000',
  holdings: [{ symbol: 'epam.us', name: 'EPAM Systems', quantity: '10', averageCost: '170.50', stop: '', openedOn: '2026-10-20' }],
};

test('the owner sends the stocks portfolio to the stocks database; the same dialog again is not sent twice', async () => {
  writes.length = 0;
  writeDatabases.length = 0;
  stored.clear();
  const sent = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', body: JSON.stringify(PORTFOLIO) }), context);
  assert.equal(sent.status, 201);
  assert.deepEqual(writeDatabases, ['cix-stocks']);
  const [doc] = writes;
  assert.equal(doc.id, 'portfolio-1');
  assert.equal(doc.value.sequence, 1);
  assert.equal(doc.value.capital, '10000');
  assert.deepEqual(doc.value.holdings, [{ symbol: 'EPAM.US', name: 'EPAM Systems', quantity: '10', averageCost: '170.50', stop: null, openedOn: '2026-10-20' }]);
  assert.equal(sent.jsonBody.pending.sequence, 1);

  const again = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', body: JSON.stringify(PORTFOLIO) }), context);
  assert.equal(again.status, 200);
  assert.equal(again.jsonBody.result, 'exists');
  assert.equal(writes.length, 1);

  // A new dialog: the next number.
  const next = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', body: JSON.stringify({ ...PORTFOLIO, clientId: '1d9c2f4e-1a2b-4c3d-8e9f-0a1b2c3d4e5f', capital: '5000' }) }), context);
  assert.equal(next.status, 201);
  assert.equal(writes[1].id, 'portfolio-2');
});

test('a portfolio the host would refuse is refused before anything is written', async () => {
  writes.length = 0;
  for (const [body, says] of [
    [{ ...PORTFOLIO, holdings: [{ symbol: 'SAP.XETRA', quantity: '1', averageCost: '1' }] }, 'US share'],
    [{ ...PORTFOLIO, capital: '0' }, 'capital'],
    [{ ...PORTFOLIO, holdings: [{ symbol: 'EPAM.US', quantity: '1', averageCost: '1' }, { symbol: 'EPAM.US', quantity: '2', averageCost: '1' }] }, 'twice'],
    [{ ...PORTFOLIO, holdings: [{ symbol: 'EPAM.US', quantity: '1e3', averageCost: '1' }] }, 'number'],
    [{ ...PORTFOLIO, holdings: [{ symbol: 'EPAM.US', quantity: '1', averageCost: '1', openedOn: '2999-01-01' }] }, 'after today'],
  ]) {
    const answer = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);
    assert.equal(answer.status, 400, says);
    assert.match(answer.jsonBody.error, new RegExp(says, 'i'));
  }
  const visitor = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', principal: VISITOR, body: JSON.stringify(PORTFOLIO) }), context);
  assert.equal(visitor.status, 403);
  const crossSite = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', headers: { 'x-cix-request': null }, body: JSON.stringify(PORTFOLIO) }), context);
  assert.equal(crossSite.status, 400);
  assert.equal(writes.length, 0);
});

test('without the writer\'s role on the stocks database the page is told to run the setup', async () => {
  stored.clear();
  writerAnswer = 403;
  try {
    const answer = await routes['owner-stocks-portfolio'].handler(request({ method: 'POST', body: JSON.stringify(PORTFOLIO) }), context);
    assert.equal(answer.status, 503);
    assert.match(answer.jsonBody.error, /provision-stocks\.ps1/);
  } finally {
    writerAnswer = null;
  }
});


const postPortfolio = (body, extra = {}) => routes['owner-stocks-portfolio'].handler(
  request({ method: 'POST', body: typeof body === 'string' ? body : JSON.stringify(body), ...extra }), context);

test('the same dialog sent again with other figures is refused, not dropped', async () => {
  writes.length = 0;
  stored.clear();
  assert.equal((await postPortfolio(PORTFOLIO)).status, 201);
  const edited = await postPortfolio({ ...PORTFOLIO, capital: '9000' });
  assert.equal(edited.status, 409);
  assert.match(edited.jsonBody.error, /other figures/);
  assert.equal(edited.jsonBody.pending.capital, '10000');
  assert.equal(writes.length, 1);
});

test('a portfolio body too large, or not JSON, is refused', async () => {
  writes.length = 0;
  stored.clear();
  const large = await postPortfolio({ ...PORTFOLIO, pad: 'x'.repeat(16_384) });
  assert.equal(large.status, 400);
  assert.match(large.jsonBody.error, /too large/);
  const form = await postPortfolio(JSON.stringify(PORTFOLIO), { headers: { 'content-type': 'text/plain' } });
  assert.equal(form.status, 400);
  assert.match(form.jsonBody.error, /JSON/);
  assert.equal((await postPortfolio('{not json')).status, 400);
  assert.equal(writes.length, 0);
});

test('a name loses what could disguise the ticker, and a day bought may be the owner\'s tomorrow-in-UTC', async () => {
  writes.length = 0;
  stored.clear();
  const day = (n) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10);
  const holdings = [
    { symbol: 'EPAM.US', name: 'EP‮AM​ Systems ', quantity: '1', averageCost: '1', openedOn: day(1) },
    { symbol: 'NIO.US', name: '\u{1F600}'.repeat(85), quantity: '1', averageCost: '1' },
  ];
  assert.equal((await postPortfolio({ ...PORTFOLIO, holdings })).status, 201);
  assert.deepEqual(writes[0].value.holdings.map((h) => h.name), ['EPAM Systems', '\u{1F600}'.repeat(80)]);
  assert.equal(writes[0].value.holdings[0].openedOn, day(1));
  const later = await postPortfolio({ ...PORTFOLIO, clientId: crypto.randomUUID(), holdings: [{ ...holdings[0], openedOn: day(2) }] });
  assert.equal(later.status, 400);
  assert.match(later.jsonBody.error, /after today/);
});

test('an unreadable waiting submission is passed over; the next number is used', async () => {
  writes.length = 0;
  stored.clear();
  stored.set('portfolio-1', { id: 'portfolio-1', value: { sequence: 1, capital: 'oops' } });
  const sent = await postPortfolio(PORTFOLIO);
  assert.equal(sent.status, 201);
  assert.equal(writes[0].id, 'portfolio-2');
});

test('with too many waiting, or the numbers taken as they are tried, the owner is told to wait or reload', async () => {
  writes.length = 0;
  stored.clear();
  const waiting = (n) => ({ id: `portfolio-${n}`, value: { sequence: n, submittedAt: new Date().toISOString(), capital: '1000', holdings: [] } });
  for (let n = 1; n <= 50; n += 1) stored.set(`portfolio-${n}`, waiting(n));
  const full = await postPortfolio(PORTFOLIO);
  assert.equal(full.status, 400);
  assert.match(full.jsonBody.error, /Too many/);
  assert.equal(writes.length, 0);

  // Another tab takes each number just before this one tries it.
  stored.clear();
  const create = RequestWriter.prototype.create;
  RequestWriter.prototype.create = async function taken(doc) { writes.push(doc); return 'exists'; };
  try {
    const raced = await postPortfolio(PORTFOLIO);
    assert.equal(raced.status, 400);
    assert.match(raced.jsonBody.error, /same time/);
    assert.deepEqual(writes.map((d) => d.id), ['portfolio-1', 'portfolio-2', 'portfolio-3']);
  } finally {
    RequestWriter.prototype.create = create;
  }
});

const TRADE = { clientId: '2d9c2f4e-1a2b-4c3d-8e9f-0a1b2c3d4e5f', trade: { kind: 'sell', symbol: 'epam.us', quantity: '4', price: '182.25', fees: '0', day: '2026-10-29', stop: '170' } };
const postTrade = (body) => routes['owner-stocks-trade'].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);

test('the owner sends a trade as the next submission, after a portfolio waiting before it; the same dialog again is not sent twice', async () => {
  writes.length = 0;
  stored.clear();
  assert.equal((await postPortfolio(PORTFOLIO)).status, 201);
  const sold = await postTrade({ ...TRADE, trade: { ...TRADE.trade, symbol: 'EPAM.US' } });
  assert.equal(sold.status, 201);
  assert.equal(writes[1].id, 'portfolio-2');
  // A sell has no stop; fees of 0 are kept as given.
  assert.deepEqual(writes[1].value.trade, { kind: 'sell', symbol: 'EPAM.US', name: null, quantity: '4', price: '182.25', fees: '0', day: '2026-10-29', stop: null });
  assert.equal(writes[1].value.capital, undefined);
  assert.equal(writes[1].value.holdings, undefined);

  const again = await postTrade({ ...TRADE, trade: { ...TRADE.trade, symbol: 'EPAM.US' } });
  assert.equal(again.status, 200);
  assert.equal(again.jsonBody.result, 'exists');
  const edited = await postTrade({ ...TRADE, trade: { ...TRADE.trade, symbol: 'EPAM.US', price: '183' } });
  assert.equal(edited.status, 409);
  assert.match(edited.jsonBody.error, /trade was already saved/);
  assert.equal(writes.length, 2);

  // Another change landed between a lost answer and its retry: the retried trade is still found, not saved again.
  assert.equal((await postPortfolio({ ...PORTFOLIO, clientId: crypto.randomUUID() })).status, 201);
  const retried = await postTrade({ ...TRADE, trade: { ...TRADE.trade, symbol: 'EPAM.US' } });
  assert.equal(retried.status, 200);
  assert.equal(retried.jsonBody.pending.sequence, 2);
  assert.equal(writes.length, 3);

  // A buy keeps its stop.
  const bought = await postTrade({ clientId: crypto.randomUUID(), trade: { kind: 'buy', symbol: 'NIO.US', quantity: '10', price: '5.5', day: '2026-10-29', stop: '4.9' } });
  assert.equal(bought.status, 201);
  assert.deepEqual(writes[3].value.trade, { kind: 'buy', symbol: 'NIO.US', name: null, quantity: '10', price: '5.5', fees: null, day: '2026-10-29', stop: '4.9' });
});

test('a trade the host would refuse is refused before anything is written', async () => {
  writes.length = 0;
  stored.clear();
  for (const [trade, says] of [
    [{ ...TRADE.trade, kind: 'short' }, 'buy or a sell'],
    [{ ...TRADE.trade, symbol: 'SAP.XETRA' }, 'US share'],
    [{ ...TRADE.trade, quantity: '0' }, 'shares'],
    [{ ...TRADE.trade, price: '-1' }, 'price'],
    [{ ...TRADE.trade, fees: '-1' }, 'fees'],
    [{ ...TRADE.trade, day: '' }, 'its day'],
    [{ ...TRADE.trade, day: '2999-01-01' }, 'after today'],
  ]) {
    const answer = await postTrade({ ...TRADE, trade });
    assert.equal(answer.status, 400, says);
    assert.match(answer.jsonBody.error, new RegExp(says, 'i'));
  }
  assert.equal((await postTrade({ clientId: TRADE.clientId })).status, 400);
  assert.equal(writes.length, 0);
});
