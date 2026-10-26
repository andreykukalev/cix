'use strict';
// Who is calling, from what Static Web Apps tells the API. The platform signs the user in (Microsoft accounts only, see
// staticwebapp.config.json) and passes the identity in the x-ms-client-principal header; a browser can't set it, because
// the platform replaces it on every request to the managed API. The routes already require the owner role; this checks
// again, so a route rule edited by mistake can't open the write path.

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

/** True only for a Microsoft sign-in that holds the owner role (given by invitation, docs/azure.md). */
function isOwner(request) {
  const p = principal(request);
  return Boolean(p && p.identityProvider === PROVIDER && Array.isArray(p.userRoles) && p.userRoles.includes(OWNER_ROLE));
}

module.exports = { principal, isOwner, OWNER_ROLE };
