'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { RequestWriter, WriterError } = require('../src/writer');

const TENANT = '11111111-2222-3333-4444-555555555555';
// Built at run time: nothing here should look like a credential to the secret scan.
const SECRET = ['not', 'a', 'real', 'secret'].join('-');

function fakeFetch(answers) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init });
    const next = answers.shift();
    return { ok: next.status >= 200 && next.status < 300, status: next.status, json: async () => next.body ?? {} };
  };
  fn.calls = calls;
  return fn;
}

function writer(fetchImpl, now = () => new Date('2026-10-26T13:05:00Z')) {
  return new RequestWriter({ endpoint: 'https://acct.documents.azure.com:443/', database: 'cix', tenantId: TENANT, clientId: 'app-id', clientSecret: SECRET, fetchImpl, now });
}

const DOC = { id: 'abc', day: '2026-10-26', type: 'manual-request' };

test('signs in with client credentials for the account and creates the item with an AAD token', async () => {
  const f = fakeFetch([{ status: 200, body: { access_token: 'tok', expires_in: 3600 } }, { status: 201 }]);
  assert.equal(await writer(f).create(DOC), 'created');

  const [login, create] = f.calls;
  assert.equal(login.url, `https://login.microsoftonline.com/${TENANT}/oauth2/v2.0/token`);
  const form = new URLSearchParams(login.init.body);
  assert.equal(form.get('grant_type'), 'client_credentials');
  assert.equal(form.get('scope'), 'https://acct.documents.azure.com/.default');
  assert.equal(create.url, 'https://acct.documents.azure.com/dbs/cix/colls/requests/docs');
  assert.equal(create.init.method, 'POST');
  assert.equal(decodeURIComponent(create.init.headers.authorization), 'type=aad&ver=1.0&sig=tok');
  assert.equal(create.init.headers['x-ms-documentdb-partitionkey'], '["2026-10-26"]');
  assert.deepEqual(JSON.parse(create.init.body), DOC);
  assert.ok(!('x-ms-documentdb-is-upsert' in create.init.headers), 'create only, never upsert');
});

test('reuses the token until five minutes before it expires', async () => {
  let t = Date.parse('2026-10-26T13:00:00Z');
  const f = fakeFetch([
    { status: 200, body: { access_token: 'one', expires_in: 3600 } }, { status: 201 }, { status: 201 },
    { status: 200, body: { access_token: 'two', expires_in: 3600 } }, { status: 201 },
  ]);
  const w = writer(f, () => new Date(t));
  await w.create(DOC);
  t += 50 * 60_000;
  await w.create(DOC);
  t += 6 * 60_000;
  await w.create(DOC);
  assert.equal(f.calls.filter((c) => c.url.includes('login.microsoftonline.com')).length, 2);
  assert.equal(decodeURIComponent(f.calls[4].init.headers.authorization), 'type=aad&ver=1.0&sig=two');
});

test('the same request sent twice is not written twice', async () => {
  const f = fakeFetch([{ status: 200, body: { access_token: 'tok', expires_in: 3600 } }, { status: 409 }]);
  assert.equal(await writer(f).create(DOC), 'exists');
});

test('errors carry the status only, never a body, and a refused token is dropped', async () => {
  const f = fakeFetch([{ status: 200, body: { access_token: 'tok', expires_in: 3600 } }, { status: 403, body: { message: 'echo of the request' } }]);
  const w = writer(f);
  await assert.rejects(w.create(DOC), (e) => e instanceof WriterError && e.status === 403 && !e.message.includes('echo'));
  assert.equal(w.token, null);

  const g = fakeFetch([{ status: 401, body: { error_description: 'bad secret' } }]);
  await assert.rejects(writer(g).create(DOC), (e) => e instanceof WriterError && !e.message.includes('secret'));
});

test('refuses to start without its settings, and never over plain http', () => {
  assert.throws(() => new RequestWriter({ endpoint: 'https://a.documents.azure.com', database: 'cix', tenantId: TENANT, clientId: 'x' }), /setup-dashboard-requests/);
  assert.throws(() => new RequestWriter({ endpoint: 'http://a.documents.azure.com', database: 'cix', tenantId: TENANT, clientId: 'x', clientSecret: SECRET }), /https/);
  assert.throws(() => new RequestWriter({ endpoint: 'https://a.documents.azure.com', database: 'cix', tenantId: 'evil.com/x', clientId: 'x', clientSecret: SECRET }), /tenant/);
});
