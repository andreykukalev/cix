'use strict';
// Who is calling, from what Static Web Apps tells the API. The platform signs the user in (Microsoft accounts only, see
// staticwebapp.config.json) and passes the identity in the x-ms-client-principal header; a browser can't set it, because
// the platform replaces it on every request to the managed API. The whole dashboard is the owner's (ADR-0014): the routes
// already require the owner role, and every API handler checks again with isOwner, so a route rule edited by mistake
// can't open any read or the write path.

const OWNER_ROLE = 'owner';
const PROVIDER = 'aad';

/** The signed-in user's principal, or null when nobody is signed in or the header can't be read. */
function principal(request) {
  const header = request?.headers?.get?.('x-ms-client-principal');
  if (!header || header.length > 8192) return null;
  try {
    const value = JSON.parse(Buffer.from(header, 'base64').toString('utf8'));
    return value && typeof value === 'object' ? value : null;
  } catch {
    return null;
  }
}

/**
 * True only for the one owner: a Microsoft sign-in that holds the owner role (given by invitation) and is the account the
 * setup script confirmed (DASHBOARD_OWNER_USER_ID, docs/azure.md). The role alone isn't enough: whoever redeems an
 * invitation link gets it. With no confirmed owner, nobody is.
 */
function isOwner(request, ownerId = process.env.DASHBOARD_OWNER_USER_ID) {
  const p = principal(request);
  return Boolean(ownerId && p && p.identityProvider === PROVIDER && Array.isArray(p.userRoles) && p.userRoles.includes(OWNER_ROLE)
    && typeof p.userId === 'string' && p.userId === ownerId);
}

const NOT_OWNER = "Only the dashboard's confirmed owner can see this (setup-dashboard-requests.ps1 confirms you).";

/** The answer to anyone but the owner: nothing about the account, never cached. */
function forbidden() {
  return { status: 403, jsonBody: { error: NOT_OWNER }, headers: { 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' } };
}

module.exports = { principal, isOwner, forbidden, OWNER_ROLE };
