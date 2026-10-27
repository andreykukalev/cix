'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { requestDocument, history, cleanNote, RequestError, MAX_NOTE } = require('../src/requests');
const { principal, isOwner } = require('../src/auth');

const NOW = Date.parse('2026-10-26T13:05:00Z');
const ID = '3f2b8c1e-5d4a-4b6c-9e8f-0a1b2c3d4e5f';

const sell = (extra = {}) => ({ id: ID, side: 'sell', mode: 'direct', symbol: 'LINK-EUR', portion: '0.5', ...extra });
const buy = (extra = {}) => ({ id: ID, side: 'buy', mode: 'agent', symbol: 'UNI-EUR', quoteAmount: '25', ...extra });

test('a sell request becomes a new paper request with the server time', () => {
  const doc = requestDocument(sell({ id: ID.toUpperCase(), note: 'take profit' }), NOW);
  assert.deepEqual(doc, {
    id: ID, day: '2026-10-26', createdAt: '2026-10-26T13:05:00.000Z', type: 'manual-request', side: 'sell', mode: 'direct',
    symbol: 'LINK-EUR', requestedBy: 'owner', tradingMode: 'paper', status: 'new', portion: '0.5', note: 'take profit',
  });
});

test('a buy request carries a quote amount and goes through the trader', () => {
  const doc = requestDocument(buy(), NOW);
  assert.equal(doc.quoteAmount, '25');
  assert.equal(doc.mode, 'agent');
  assert.ok(!('portion' in doc));
  assert.throws(() => requestDocument(buy({ mode: 'direct' }), NOW), /through the trader/);
});

test('whatever the page sends, status, time, author and mode are the server\'s', () => {
  const doc = requestDocument(sell({ status: 'filled', createdAt: '2020-01-01', day: '2020-01-01', requestedBy: 'someone', type: 'x', outcome: {} }), NOW);
  assert.equal(doc.status, 'new');
  assert.equal(doc.day, '2026-10-26');
  assert.equal(doc.requestedBy, 'owner');
  assert.equal(doc.type, 'manual-request');
  assert.ok(!('outcome' in doc));
});

test('refuses malformed requests', () => {
  const bad = [
    null, [], 'x',
    sell({ id: 'not-a-uuid' }), sell({ id: '../state/x' }),
    sell({ side: 'short' }), sell({ mode: 'now' }),
    sell({ symbol: 'link-eur' }), sell({ symbol: 'LINK EUR' }), sell({ symbol: 'A-EUR' }),
    sell({ portion: '0' }), sell({ portion: '1.5' }), sell({ portion: '-0.5' }), sell({ portion: '1e-3' }), sell({ portion: undefined }),
    sell({ quoteAmount: '10' }),
    buy({ quoteAmount: '0' }), buy({ quoteAmount: '1000.01' }), buy({ quoteAmount: 'NaN' }), buy({ portion: '0.5' }),
    sell({ tradingMode: 'live' }), buy({ tradingMode: 'live' }),
    sell({ note: 42 }),
  ];
  for (const body of bad) assert.throws(() => requestDocument(body, NOW), RequestError, JSON.stringify(body));
});

test('live requests are refused: the dashboard writes paper requests only', () => {
  assert.throws(() => requestDocument(sell({ tradingMode: 'live' }), NOW), /Only paper/);
});

test('notes are one line, without control or format characters, and bounded', () => {
  assert.equal(cleanNote('  sell\nnow‮please\u0007 '), 'sell now please');
  assert.equal(cleanNote(''), null);
  assert.equal(cleanNote('​​'), null);
  assert.equal(Array.from(cleanNote('é'.repeat(400))).length, MAX_NOTE);
});

test('history lists newest first with outcome and events, and marks final statuses', () => {
  const items = history([
    { id: 'a', createdAt: '2026-10-26T10:00:00Z', side: 'sell', status: 'filled', outcome: { averagePrice: '10.2', filledSize: '1' }, events: [{ at: 't', status: 'placed', by: 'engine', detail: 'd', extra: 'dropped' }] },
    { id: 'b', createdAt: '2026-10-26T12:00:00Z', side: 'buy', status: 'awaiting-agent' },
  ]);
  assert.deepEqual(items.map((i) => i.id), ['b', 'a']);
  assert.equal(items[0].final, false);
  assert.equal(items[1].final, true);
  assert.equal(items[1].outcome.averagePrice, '10.2');
  assert.deepEqual(items[1].events, [{ at: 't', status: 'placed', by: 'engine', detail: 'd', detailRu: null }]);
});

function withPrincipal(value) {
  const header = value === undefined ? null : Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64');
  return { headers: { get: (name) => (name === 'x-ms-client-principal' ? header : null) } };
}

test('only the confirmed account, signed in with Microsoft and holding the owner role, is the owner', () => {
  const me = { identityProvider: 'aad', userId: 'u1', userRoles: ['anonymous', 'authenticated', 'owner'] };
  assert.equal(isOwner(withPrincipal(me), 'u1'), true);
  assert.equal(isOwner(withPrincipal(me), 'someone-else'), false);
  assert.equal(isOwner(withPrincipal(me), undefined), false, 'no confirmed owner: nobody');
  assert.equal(isOwner(withPrincipal(me), ''), false);
  assert.equal(isOwner(withPrincipal({ ...me, userId: undefined }), 'u1'), false);
  assert.equal(isOwner(withPrincipal({ ...me, identityProvider: 'github' }), 'u1'), false);
  assert.equal(isOwner(withPrincipal({ ...me, userRoles: ['authenticated'] }), 'u1'), false);
  assert.equal(isOwner(withPrincipal({ ...me, userRoles: 'owner' }), 'u1'), false);
  assert.equal(isOwner(withPrincipal(undefined), 'u1'), false);
  assert.equal(isOwner(withPrincipal('not json'), 'u1'), false);
  assert.equal(isOwner({}, 'u1'), false);
  assert.equal(principal(withPrincipal('x'.repeat(9000))), null);
});
