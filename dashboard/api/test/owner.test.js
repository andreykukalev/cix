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
  if (container === 'requests' && stored.has(id)) return stored.get(id).day === pk ? stored.get(id) : null;
  // The looked-up request has its own id, so the request created in these tests never finds it as yesterday's.
  return id === 'a1b2c3d4-0000-4000-8000-000000000001' && pk === '2026-10-26' ? { id, type: 'manual-request', status: 'placed' } : null;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://acct.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('k').toString('base64'), COSMOS_DATABASE: 'cix',
  COSMOS_LIVE_DATABASE: 'cix-live', DASHBOARD_OWNER_USER_ID: 'owner-user-id',
  COSMOS_WRITER_TENANT_ID: '11111111-2222-3333-4444-555555555555', COSMOS_WRITER_CLIENT_ID: 'app', COSMOS_WRITER_SECRET: ['x', 'y'].join(''),
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

const BODY = JSON.stringify({ id: '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f', side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.25' });

test('the owner endpoints are registered under /api/owner', () => {
  assert.deepEqual(Object.values(routes).map((r) => r.route).sort(),
    ['owner/control-requests', 'owner/control-requests/{day}/{id}', 'owner/pair-requests', 'owner/pair-requests/{day}/{id}', 'owner/pairs',
      'owner/requests', 'owner/requests/{day}/{id}', 'owner/strategies', 'owner/strategy-requests', 'owner/strategy-requests/{day}/{id}']);
});

test('the owner creates a request; the same click again is not a second request', async () => {
  writes.length = 0;
  stored.clear();
  const first = await routes['owner-requests'].handler(request({ method: 'POST', body: BODY }), context);
  assert.equal(first.status, 201);
  assert.equal(first.jsonBody.result, 'created');
  assert.equal(first.jsonBody.request.status, 'new');
  assert.equal(first.headers['cache-control'], 'no-store');
  const again = await routes['owner-requests'].handler(request({ method: 'POST', body: BODY }), context);
  assert.equal(again.status, 200);
  assert.equal(again.jsonBody.result, 'exists');
  assert.equal(writes[0].requestedBy, 'owner');
  assert.equal(writes[0].tradingMode, 'paper');
});

test('nobody else can write or read, whatever the route rules say', async () => {
  writes.length = 0;
  for (const principal of [VISITOR, OTHER_OWNER, null, 'garbage']) {
    for (const name of ['owner-requests', 'owner-request']) {
      const method = name === 'owner-requests' ? 'POST' : 'GET';
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
    request({ method: 'POST', body: JSON.stringify({ ...JSON.parse(BODY), tradingMode: 'live' }) }),
  ];
  for (const r of cases) assert.equal((await routes['owner-requests'].handler(r, context)).status, 400);
  assert.equal(writes.length, 0);
});

test('history and a single request are read with the read-only key, never cached', async () => {
  reads.length = 0;
  stored.clear();
  const list = await routes['owner-requests'].handler(request(), context);
  assert.equal(list.status, 200);
  assert.equal(list.jsonBody.requests[0].final, true);
  assert.equal(reads[0].container, 'requests');
  assert.equal(list.headers['cache-control'], 'no-store');

  const one = await routes['owner-request'].handler(request({ params: { day: '2026-10-26', id: 'a1b2c3d4-0000-4000-8000-000000000001' } }), context);
  assert.equal(one.status, 200);
  assert.equal(one.jsonBody.status, 'placed');
  assert.equal(reads[1].pk, '2026-10-26');
  const missing = await routes['owner-request'].handler(request({ params: { day: '2026-10-26', id: '00000000-0000-4000-8000-000000000000' } }), context);
  assert.equal(missing.status, 404);
  const bad = await routes['owner-request'].handler(request({ params: { day: '../x', id: 'y' } }), context);
  assert.equal(bad.status, 400);
});

test('a retry with other details is refused, and answered with the request as first sent', async () => {
  stored.clear();
  const id = '7a1b2c3d-1111-4222-8333-444455556666';
  const first = JSON.stringify({ id, side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.25' });
  assert.equal((await routes['owner-requests'].handler(request({ method: 'POST', body: first }), context)).status, 201);
  const changed = await routes['owner-requests'].handler(request({ method: 'POST', body: first.replace('0.25', '1') }), context);
  assert.equal(changed.status, 409);
  assert.equal(changed.jsonBody.request.portion, '0.25');
});

test('a retry just after midnight finds the request made the day before', async () => {
  stored.clear();
  const id = '8b2c3d4e-2222-4333-8444-555566667777';
  const yesterday = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
  stored.set(id, { id, day: yesterday, type: 'manual-request', side: 'sell', mode: 'agent', symbol: 'UNI-EUR', portion: '1', status: 'awaiting-agent' });
  writes.length = 0;
  const r = await routes['owner-requests'].handler(request({ method: 'POST', body: JSON.stringify({ id, side: 'sell', mode: 'agent', symbol: 'UNI-EUR', portion: '1' }) }), context);
  assert.equal(r.status, 200);
  assert.equal(r.jsonBody.result, 'exists');
  assert.equal(r.jsonBody.request.status, 'awaiting-agent');
  assert.equal(writes.length, 0, 'not written a second time');
});

test('without a confirmed owner, nobody is the owner', async () => {
  const saved = process.env.DASHBOARD_OWNER_USER_ID;
  delete process.env.DASHBOARD_OWNER_USER_ID;
  try {
    assert.equal((await routes['owner-requests'].handler(request(), context)).status, 403);
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
    const r = await fresh['owner-requests'].handler(request({ method: 'POST', body: JSON.stringify({ id: '9c3d4e5f-3333-4444-8555-666677778888', side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '1' }) }), context);
    assert.equal(r.status, 503);
    assert.match(r.jsonBody.error, /setup-dashboard-requests/);
  } finally {
    Module._load = originalLoad;
    process.env.COSMOS_WRITER_SECRET = saved;
  }
});

const STRATEGY_BODY = JSON.stringify({ id: '7c9e6679-7425-40de-944b-e07fc1f90ae7', name: 'balanced', version: 1, note: 'calmer' });

test('the owner asks to switch strategy: a request in the requests container, never the state', async () => {
  writes.length = 0;
  const first = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: STRATEGY_BODY }), context);
  const again = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: STRATEGY_BODY }), context);

  assert.equal(first.status, 201);
  assert.equal(again.status, 200); // the same dialog sent twice is one request
  assert.equal(writes.length, 2);
  assert.deepEqual({ type: writes[0].type, name: writes[0].name, version: writes[0].version, status: writes[0].status, requestedBy: writes[0].requestedBy },
    { type: 'strategy-selection', name: 'balanced', version: 1, status: 'new', requestedBy: 'owner' });
  assert.equal(first.jsonBody.request.name, 'balanced');
});

test('strategy requests: owner only, live only when confirmed and to the live engine, well formed', async () => {
  const liveBody = { ...JSON.parse(STRATEGY_BODY), id: '9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d', name: 'safe', version: 2, tradingMode: 'live' };
  const visitor = await routes['owner-strategy-requests'].handler(request({ method: 'POST', principal: VISITOR, body: STRATEGY_BODY }), context);
  writeDatabases.length = 0;
  const unconfirmed = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify(liveBody) }), context);
  assert.deepEqual(writeDatabases, [], 'nothing written without the confirmation');
  const live = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, confirmLive: true }) }), context);
  const bad = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...JSON.parse(STRATEGY_BODY), version: '1' }) }), context);
  const noHeader = await routes['owner-strategy-requests'].handler(request({ method: 'POST', body: STRATEGY_BODY, headers: { 'x-cix-request': null } }), context);

  assert.equal(visitor.status, 403);
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.equal(live.status, 201);
  assert.deepEqual(writeDatabases, ['cix-live']);
  assert.deepEqual({ name: writes.at(-1).name, version: writes.at(-1).version, tradingMode: writes.at(-1).tradingMode }, { name: 'safe', version: 2, tradingMode: 'live' });
  assert.equal('confirmLive' in writes.at(-1), false);
  assert.equal(bad.status, 400);
  assert.equal(noHeader.status, 400);

  // The page follows it in the live engine's database, and a paper lookup never finds it.
  const day = writes.at(-1).day;
  const lookup = (query) => routes['owner-strategy-request'].handler(request({ params: { day, id: liveBody.id }, query }), context);
  assert.equal((await lookup('mode=live')).status, 200);
  assert.equal((await lookup('')).status, 404);
});

test('the strategy view is the owner\'s only', async () => {
  const visitor = await routes['owner-strategies'].handler(request({ principal: VISITOR }), context);
  const owner = await routes['owner-strategies'].handler(request(), context);

  assert.equal(visitor.status, 403);
  assert.equal(owner.status, 200);
  assert.equal(owner.jsonBody.mode, 'paper');
  assert.equal(owner.headers['cache-control'], 'no-store');
});

const PAIR_BODY = JSON.stringify({ id: 'c56a4180-65aa-42ec-a945-5fd21dec0538', action: 'add', symbol: 'SOL-EUR', note: 'more coins' });

test('the owner asks to add a pair: a request in the requests container, never the state', async () => {
  writes.length = 0;
  const first = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: PAIR_BODY }), context);
  const again = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: PAIR_BODY }), context);

  assert.equal(first.status, 201);
  assert.equal(again.status, 200); // the same dialog sent twice is one request
  assert.deepEqual({ type: writes[0].type, action: writes[0].action, symbol: writes[0].symbol, status: writes[0].status, requestedBy: writes[0].requestedBy },
    { type: 'tradable-pair', action: 'add', symbol: 'SOL-EUR', status: 'new', requestedBy: 'owner' });
  assert.equal(first.jsonBody.request.symbol, 'SOL-EUR');
});

test('pair requests: owner only, live only when confirmed and to the live engine, well formed', async () => {
  const liveBody = { ...JSON.parse(PAIR_BODY), id: '2c1f7c3e-1b7a-4c1d-8a7e-3f0a9b1c2d3e', tradingMode: 'live' };
  const visitor = await routes['owner-pair-requests'].handler(request({ method: 'POST', principal: VISITOR, body: PAIR_BODY }), context);
  writeDatabases.length = 0;
  const unconfirmed = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify(liveBody) }), context);
  assert.deepEqual(writeDatabases, [], 'nothing written without the confirmation');
  const live = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, confirmLive: true }) }), context);
  const bad = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...JSON.parse(PAIR_BODY), action: 'swap' }) }), context);
  const noHeader = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: PAIR_BODY, headers: { 'x-cix-request': null } }), context);

  assert.equal(visitor.status, 403);
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.equal(live.status, 201);
  assert.deepEqual(writeDatabases, ['cix-live']);
  assert.equal(writes.at(-1).tradingMode, 'live');
  assert.equal(bad.status, 400);
  assert.equal(noHeader.status, 400);

  const day = writes.at(-1).day;
  const lookup = (query) => routes['owner-pair-request'].handler(request({ params: { day, id: liveBody.id }, query }), context);
  assert.equal((await lookup('mode=live')).status, 200);
  assert.equal((await lookup('')).status, 404);

  const saved = process.env.COSMOS_LIVE_DATABASE;
  delete process.env.COSMOS_LIVE_DATABASE;
  try {
    reads.length = 0;
    const off = await routes['owner-pair-requests'].handler(request({ method: 'POST', body: JSON.stringify({ ...liveBody, id: crypto.randomUUID(), confirmLive: true }) }), context);
    assert.equal(off.status, 503, 'never written to paper\'s database instead');
    assert.equal((await lookup('mode=live')).status, 503, 'a live lookup never reads paper\'s database instead');
    assert.equal(reads.length, 0);
  } finally { process.env.COSMOS_LIVE_DATABASE = saved; }
  assert.deepEqual(writeDatabases, ['cix-live']);
});

test('the pairs view is the owner\'s only', async () => {
  const visitor = await routes['owner-pairs'].handler(request({ principal: VISITOR }), context);
  const owner = await routes['owner-pairs'].handler(request(), context);

  assert.equal(visitor.status, 403);
  assert.equal(owner.status, 200);
  assert.equal(owner.jsonBody.mode, 'paper');
});

test("live's strategy view reads the live engine's database, its requests too, with paper's explanations as a stand-in", async () => {
  reads.length = 0;
  const live = await routes['owner-strategies'].handler(request({ query: 'mode=live' }), context);
  assert.equal(live.status, 200);
  const where = Object.fromEntries(reads.map((r) => [r.id ?? r.container, r.database]));
  assert.deepEqual(where, { 'strategy-schema-live': 'cix-live', state: 'cix-live', 'engine-status-live': 'cix-live', requests: 'cix-live', 'strategy-schema-paper': 'cix' },
    "live's own schema first; paper's explanations only while the live engine hasn't written its own (the fake has none)");
  reads.length = 0;
  await routes['owner-requests'].handler(request(), context);
  assert.deepEqual([...new Set(reads.map((r) => r.database))], ['cix']);
});

test("Pause and Resume: paper's go to paper's database; live's need the owner's confirmation and go to the live engine's", async () => {
  const post = (body) => routes['owner-control-requests'].handler(request({ method: 'POST', body: JSON.stringify(body) }), context);
  const id = () => crypto.randomUUID();

  writeDatabases.length = 0;
  const paper = await post({ id: id(), action: 'pause', note: 'lunch' });
  assert.equal(paper.status, 201);
  assert.equal(paper.jsonBody.request.action, 'pause');
  assert.equal(writes.at(-1).type, 'trading-control');
  assert.equal(writes.at(-1).tradingMode, 'paper');
  assert.deepEqual(writeDatabases, ['cix']);

  const since = '2026-10-27T16:45:00.1234567+00:00'; // as the engine's status writes it
  const unconfirmed = await post({ id: id(), action: 'resume', tradingMode: 'live', haltSince: since });
  assert.equal(unconfirmed.status, 400);
  assert.match(unconfirmed.jsonBody.error, /real-money/);
  assert.deepEqual(writeDatabases, ['cix'], 'nothing written without the confirmation');

  const live = await post({ id: id(), action: 'resume', tradingMode: 'live', confirmLive: true, haltSince: since, haltAlsoAt: null });
  assert.equal(live.status, 201);
  assert.deepEqual(writeDatabases, ['cix', 'cix-live']);
  assert.equal(writes.at(-1).haltSince, since, 'the halt the page showed, passed back untouched');
  assert.equal('haltAlsoAt' in writes.at(-1), false);
  const seenAlso = await post({ id: id(), action: 'resume', haltSince: since, haltAlsoAt: '2026-10-27T17:00:00Z' });
  assert.equal(seenAlso.status, 201);
  assert.equal(writes.at(-1).haltAlsoAt, '2026-10-27T17:00:00Z');

  for (const bad of [{ id: id(), action: 'sell-all' }, { id: 'nope', action: 'pause' }, { id: id(), action: 'pause', tradingMode: 'demo' },
    { id: id(), action: 'resume' }, { id: id(), action: 'resume', haltSince: 'yesterday' }, { id: id(), action: 'resume', haltSince: since, haltAlsoAt: 5 }]) {
    assert.equal((await post(bad)).status, 400, JSON.stringify(bad));
  }

  writerAnswer = 403; // the live role isn't granted yet
  try {
    const refused = await post({ id: id(), action: 'pause', tradingMode: 'live', confirmLive: true });
    assert.equal(refused.status, 503);
    assert.match(refused.jsonBody.error, /-LiveRequests/);
  } finally { writerAnswer = null; }

  const saved = process.env.COSMOS_LIVE_DATABASE;
  delete process.env.COSMOS_LIVE_DATABASE;
  try {
    const off = await post({ id: id(), action: 'pause', tradingMode: 'live', confirmLive: true });
    assert.equal(off.status, 503, 'never written to paper\'s database instead');
    assert.match(off.jsonBody.error, /-LiveRequests/);
    reads.length = 0;
    const lookup = await routes['owner-control-request'].handler(request({ params: { day: '2026-10-27', id: id() }, query: 'mode=live' }), context);
    assert.equal(lookup.status, 503, 'a live lookup never reads paper\'s database instead');
    assert.equal(reads.length, 0);
  } finally { process.env.COSMOS_LIVE_DATABASE = saved; }
  assert.deepEqual(writeDatabases, ['cix', 'cix-live', 'cix']);
});
