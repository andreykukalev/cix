'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { cleanNote, RequestError, MAX_NOTE } = require('../src/requests');
const { principal, isOwner } = require('../src/auth');

test('notes are one line, without control or format characters, and bounded', () => {
  assert.equal(cleanNote('  sell\nnow‮please\u0007 '), 'sell now please');
  assert.equal(cleanNote(''), null);
  assert.equal(cleanNote('​​'), null);
  assert.equal(Array.from(cleanNote('é'.repeat(400))).length, MAX_NOTE);
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
