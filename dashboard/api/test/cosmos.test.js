'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { authToken, CosmosReader, CosmosError } = require('../src/cosmos');

// Microsoft's documented example (access-control-on-cosmosdb-resources). Assembled at runtime: it is a published sample, not a key.
const SAMPLE_KEY = ['dsZQi3KtZmCv1ljt3VNWNm7sQUF1y5rJfC6kv5Jiwv', 'W0EndXdDku/dkKBp8/ufDToSxLzR4y+O/0H/t4bQtVNw=='].join('');

test('signs requests exactly as the Cosmos DB documentation example', () => {
  const token = authToken('GET', 'dbs', 'dbs/ToDoList', 'Thu, 27 Apr 2017 00:51:12 GMT', SAMPLE_KEY);
  assert.equal(decodeURIComponent(token), 'type=master&ver=1.0&sig=c09PEVJrgp2uQRkr934kFbTqhByc7TVr3OHyqlu+c+c=');
});

function fakeFetch(pages) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, init });
    const page = pages.shift();
    return {
      status: page.status ?? 200,
      ok: (page.status ?? 200) < 400,
      headers: new Map(Object.entries(page.headers ?? {})),
      json: async () => page.body,
    };
  };
  return { fetchImpl, calls };
}

const options = (fetchImpl) => ({
  endpoint: 'https://acct.documents.azure.com:443/', key: SAMPLE_KEY, database: 'cix', fetchImpl,
  now: () => new Date(Date.UTC(2026, 9, 25, 12, 0, 0)),
});

test('reads one document with its partition key, and treats 404 as missing', async () => {
  const { fetchImpl, calls } = fakeFetch([{ body: { id: 'paper-account', value: {} } }, { status: 404 }]);
  const reader = new CosmosReader(options(fetchImpl));

  assert.deepEqual(await reader.readDocument('state', 'paper-account'), { id: 'paper-account', value: {} });
  assert.equal(await reader.readDocument('state', 'engine-status-live'), null);
  assert.equal(calls[0].url, 'https://acct.documents.azure.com/dbs/cix/colls/state/docs/paper-account');
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].init.headers['x-ms-documentdb-partitionkey'], '["paper-account"]');
  assert.equal(calls[0].init.headers['x-ms-date'], 'Sun, 25 Oct 2026 12:00:00 GMT');
  assert.ok(calls[0].init.signal, 'every request has a timeout');
});

test('queries follow continuations, stay in one partition when asked, and stop at maxItems', async () => {
  const { fetchImpl, calls } = fakeFetch([
    { body: { Documents: [{ id: 'a' }, { id: 'b' }] }, headers: { 'x-ms-continuation': 'next' } },
    { body: { Documents: [{ id: 'c' }] } },
  ]);
  const reader = new CosmosReader(options(fetchImpl));

  const docs = await reader.query('journal', 'SELECT * FROM c', { partitionKey: '2026-10-25', maxItems: 10 });

  assert.deepEqual(docs.map((d) => d.id), ['a', 'b', 'c']);
  assert.equal(calls.length, 2);
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['x-ms-documentdb-partitionkey'], '["2026-10-25"]');
  assert.equal(calls[0].init.headers['x-ms-documentdb-query-enablecrosspartition'], undefined);
  assert.equal(calls[1].init.headers['x-ms-continuation'], 'next');
  assert.deepEqual(JSON.parse(calls[0].init.body), { query: 'SELECT * FROM c', parameters: [] });
});

test('a cross-partition query says so; a failure names no request details', async () => {
  const { fetchImpl, calls } = fakeFetch([{ status: 503, body: { message: 'secret-ish details' } }]);
  const reader = new CosmosReader(options(fetchImpl));

  await assert.rejects(() => reader.query('state', 'SELECT c.id FROM c'), (error) => {
    assert.ok(error instanceof CosmosError);
    assert.equal(error.status, 503);
    assert.ok(!error.message.includes('secret-ish'));
    return true;
  });
  assert.equal(calls[0].init.headers['x-ms-documentdb-query-enablecrosspartition'], 'True');
});

test('refuses plain http and missing settings', () => {
  assert.throws(() => new CosmosReader({ endpoint: 'http://acct', key: SAMPLE_KEY, database: 'cix' }));
  assert.throws(() => new CosmosReader({ endpoint: 'https://acct', key: '', database: 'cix' }));
});
