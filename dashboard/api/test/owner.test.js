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
RequestWriter.prototype.create = async function create(doc) {
  writes.push(doc);
  return writes.filter((d) => d.id === doc.id).length > 1 ? 'exists' : 'created';
};
const { CosmosReader } = require('../src/cosmos');
const reads = [];
CosmosReader.prototype.query = async function query(container, sql, options) {
  reads.push({ container, sql, options });
  return container === 'requests' ? [{ id: 'r1', type: 'manual-request', createdAt: '2026-10-26T10:00:00Z', status: 'filled' }] : [];
};
CosmosReader.prototype.readDocument = async function readDocument(container, id, pk) {
  reads.push({ container, id, pk });
  return id === '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f' ? { id, type: 'manual-request', status: 'placed' } : null;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://acct.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('k').toString('base64'), COSMOS_DATABASE: 'cix',
  COSMOS_WRITER_TENANT_ID: '11111111-2222-3333-4444-555555555555', COSMOS_WRITER_CLIENT_ID: 'app', COSMOS_WRITER_SECRET: ['x', 'y'].join(''),
});
require('../src/functions/owner.js');
Module._load = originalLoad;

const OWNER = Buffer.from(JSON.stringify({ identityProvider: 'aad', userRoles: ['authenticated', 'owner'] })).toString('base64');
const VISITOR = Buffer.from(JSON.stringify({ identityProvider: 'aad', userRoles: ['authenticated'] })).toString('base64');
const context = { log() {}, error() {} };

function request({ method = 'GET', principal = OWNER, headers = {}, body = '', params = {} } = {}) {
  const all = { 'x-ms-client-principal': principal, 'content-type': 'application/json', 'x-cix-request': '1', ...headers };
  return { method, params, query: new URLSearchParams(), headers: { get: (n) => all[n.toLowerCase()] ?? null }, text: async () => body };
}

const BODY = JSON.stringify({ id: '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f', side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.25' });

test('the owner endpoints are registered under /api/owner', () => {
  assert.deepEqual(Object.values(routes).map((r) => r.route).sort(), ['owner/decisions', 'owner/requests', 'owner/requests/{day}/{id}']);
});

test('the owner creates a request; the same click again is not a second request', async () => {
  writes.length = 0;
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
  for (const principal of [VISITOR, null, 'garbage']) {
    for (const name of ['owner-requests', 'owner-decisions', 'owner-request']) {
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
  const list = await routes['owner-requests'].handler(request(), context);
  assert.equal(list.status, 200);
  assert.equal(list.jsonBody.requests[0].final, true);
  assert.equal(reads[0].container, 'requests');
  assert.equal(list.headers['cache-control'], 'no-store');

  const one = await routes['owner-request'].handler(request({ params: { day: '2026-10-26', id: '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f' } }), context);
  assert.equal(one.status, 200);
  assert.equal(one.jsonBody.status, 'placed');
  assert.equal(reads[1].pk, '2026-10-26');
  const missing = await routes['owner-request'].handler(request({ params: { day: '2026-10-26', id: '00000000-0000-4000-8000-000000000000' } }), context);
  assert.equal(missing.status, 404);
  const bad = await routes['owner-request'].handler(request({ params: { day: '../x', id: 'y' } }), context);
  assert.equal(bad.status, 400);
});

test('the site config lets only the owner role into /api/owner and only Microsoft accounts sign in', () => {
  const config = require('../../app/staticwebapp.config.json');
  const owner = config.routes.findIndex((r) => r.route === '/api/owner/*');
  const api = config.routes.findIndex((r) => r.route === '/api/*');
  assert.ok(owner >= 0 && owner < api, 'the owner rule comes first: the first matching route wins');
  assert.deepEqual(config.routes[owner].allowedRoles, ['owner']);
  assert.ok(!('methods' in config.routes[owner]), 'every method, so no request slips past the rule');
  for (const provider of ['github', 'twitter']) {
    assert.equal(config.routes.find((r) => r.route === `/.auth/login/${provider}`)?.statusCode, 404, provider);
  }
});
