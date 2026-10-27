'use strict';
// The dashboard's reads, run through their real handlers with @azure/functions replaced by a stub that records the routes
// and the database by a fake, and the site's route rules: the whole dashboard is the owner's (ADR-0014).
const test = require('node:test');
const assert = require('node:assert/strict');
const Module = require('node:module');

const routes = {};
const originalLoad = Module._load;
Module._load = function load(request, ...rest) {
  if (request === '@azure/functions') return { app: { http: (name, options) => { routes[name] = options; } } };
  return originalLoad.call(this, request, ...rest);
};
const { CosmosReader } = require('../src/cosmos');
const reads = [];
CosmosReader.prototype.query = async function query(container, sql, options) {
  reads.push({ database: this.database, container, sql, options });
  return [];
};
CosmosReader.prototype.readDocument = async function readDocument(container, id) {
  reads.push({ database: this.database, container, id });
  return null;
};
Object.assign(process.env, {
  COSMOS_ENDPOINT: 'https://acct.documents.azure.com', COSMOS_READONLY_KEY: Buffer.from('k').toString('base64'), COSMOS_DATABASE: 'cix',
  COSMOS_LIVE_DATABASE: 'cix-live', DASHBOARD_OWNER_USER_ID: 'owner-user-id',
});
require('../src/functions/dashboard.js');
Module._load = originalLoad;

const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64');
const OWNER = b64({ identityProvider: 'aad', userId: 'owner-user-id', userRoles: ['authenticated', 'owner'] });
const VISITOR = b64({ identityProvider: 'aad', userId: 'visitor', userRoles: ['authenticated'] });
const OTHER_OWNER = b64({ identityProvider: 'aad', userId: 'redeemed-the-link', userRoles: ['authenticated', 'owner'] });
const GITHUB_OWNER = b64({ identityProvider: 'github', userId: 'owner-user-id', userRoles: ['authenticated', 'owner'] });
const context = { log() {}, error() {} };
const today = new Date().toISOString().slice(0, 10);
const QUERIES = { dashboard: 'mode=paper', journal: `day=${today}`, activity: 'period=week', 'balance-history': 'mode=live&days=30' };

function request(name, principal = OWNER, query = QUERIES[name]) {
  const headers = { 'x-ms-client-principal': principal };
  return { method: 'GET', params: {}, query: new URLSearchParams(query), headers: { get: (n) => headers[n.toLowerCase()] ?? null } };
}

test('the reads are registered under /api', () => {
  assert.deepEqual(Object.values(routes).map((r) => r.route).sort(), ['activity', 'balance-history', 'dashboard', 'journal']);
});

test('only the confirmed owner gets an answer; nobody else reaches the database or the cache', async () => {
  for (const name of Object.keys(QUERIES)) {
    const ok = await routes[name].handler(request(name), context);
    assert.equal(ok.status, 200, name);
    assert.equal(ok.headers['cache-control'], 'no-store', `${name}: a browser or proxy never keeps the owner's data`);
  }
  reads.length = 0;
  for (const principal of [null, 'garbage', VISITOR, OTHER_OWNER, GITHUB_OWNER]) {
    for (const name of Object.keys(QUERIES)) {
      const r = await routes[name].handler(request(name, principal), context);
      assert.equal(r.status, 403, `${name} ${principal}`);
      assert.equal(r.headers['cache-control'], 'no-store');
      assert.deepEqual(Object.keys(r.jsonBody), ['error'], 'nothing but the refusal, even with a warm cache');
    }
  }
  assert.equal(reads.length, 0);
});

test('without a confirmed owner, nobody gets the dashboard', async () => {
  const saved = process.env.DASHBOARD_OWNER_USER_ID;
  delete process.env.DASHBOARD_OWNER_USER_ID;
  try {
    assert.equal((await routes.dashboard.handler(request('dashboard'), context)).status, 403);
  } finally { process.env.DASHBOARD_OWNER_USER_ID = saved; }
});

test('activity comes with amounts; an unknown period is refused', async () => {
  const r = await routes.activity.handler(request('activity'), context);
  assert.equal(r.jsonBody.current.turnover, '0.00');
  assert.equal(r.jsonBody.current.fees, '0.00');
  const bad = await routes.activity.handler({ ...request('activity'), query: new URLSearchParams('period=year') }, context);
  assert.equal(bad.status, 400);
});

const config = require('../../app/staticwebapp.config.json');

/** The first rule that matches a path, as Static Web Apps picks it (a trailing * matches any rest). */
function ruleFor(path, method = 'GET') {
  return config.routes.find((r) => (r.route.endsWith('*') ? path.startsWith(r.route.slice(0, -1)) : path === r.route)
    && (!r.methods || r.methods.includes(method)));
}

test('the site lets only the owner role into every page and API route', () => {
  for (const path of ['/', '/index.html', '/app.js', '/view.js', '/anything', '/api/dashboard', '/api/journal', '/api/activity', '/api/owner/requests']) {
    for (const method of ['GET', 'POST', 'DELETE']) {
      assert.deepEqual(ruleFor(path, method)?.allowedRoles, ['owner'], `${method} ${path}`);
    }
  }
  assert.deepEqual(config.routes.at(-1), { route: '/*', allowedRoles: ['owner'], headers: { 'cache-control': 'no-store' } }, 'the catch-all comes last');
  assert.ok(config.routes.every((r) => !r.methods), 'no rule is limited to some methods, so no method slips past one');
});

test('only the signed-out page, its stylesheet and the icon are open, and they hold nothing about the account', () => {
  const open = config.routes.filter((r) => !r.allowedRoles && !r.statusCode).map((r) => r.route);
  assert.deepEqual(open.sort(), ['/favicon.svg', '/signed-out.html', '/styles.css']);
  const fs = require('node:fs');
  const page = fs.readFileSync(require.resolve('../../app/signed-out.html'), 'utf8');
  assert.doesNotMatch(page, /<script|\/api\//, 'no script and no API call on the open page');
  const icon = fs.readFileSync(require.resolve('../../app/favicon.svg'), 'utf8');
  assert.doesNotMatch(icon, /<script|<foreignObject|href=|\/api\//i, 'the icon is a plain drawing: no script, link or embedded page');
});

test('anyone else is sent to the Microsoft sign-in, or to the signed-out page; GitHub and X sign-in are off', () => {
  assert.deepEqual(config.responseOverrides['401'], { statusCode: 302, redirect: '/.auth/login/aad?post_login_redirect_uri=/' });
  assert.deepEqual(config.responseOverrides['403'], { rewrite: '/signed-out.html' });
  for (const provider of ['github', 'twitter']) assert.equal(ruleFor(`/.auth/login/${provider}`)?.statusCode, 404, provider);
});

test("each mode reads its own engine's database: live's is its own (ADR-0017)", async () => {
  const cases = [
    ['dashboard', 'mode=live', 'cix-live'], // paper's dashboard is in the cache from the first test
    ['journal', `day=${today}&kind=fill&mode=live`, 'cix-live'], ['journal', `day=${today}&kind=fill`, 'cix'],
    ['activity', 'period=month&mode=live', 'cix-live'], ['activity', 'period=month', 'cix'],
    ['balance-history', 'mode=live&days=90', 'cix-live'], ['balance-history', 'mode=paper&days=90', 'cix'],
  ];
  for (const [name, query, database] of cases) {
    reads.length = 0;
    const r = await routes[name].handler(request(name, OWNER, query), context);
    assert.equal(r.status, 200, `${name} ${query}`);
    assert.ok(reads.length > 0, `${name} ${query}: read the database, not another mode's cached answer`);
    assert.deepEqual([...new Set(reads.map((x) => x.database))], [database], `${name} ${query}`);
    const mode = database === 'cix-live' ? 'live' : 'paper';
    for (const q of reads.filter((x) => x.options?.parameters)) {
      assert.deepEqual(q.options.parameters.filter((p) => p.name === '@mode'), [{ name: '@mode', value: mode }], `${name} ${query}: ${mode}'s entries only`);
    }
  }
});
