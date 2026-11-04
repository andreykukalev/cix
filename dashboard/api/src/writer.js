'use strict';
// The dashboard's only write: creating a request document in a requests container. It signs in to Microsoft Entra ID as
// the dashboard's writer identity (a service principal, client credentials) and uses Cosmos DB role-based access, where
// that identity's role allows exactly one thing: creating items in CIX's requests container (ADR-0012,
// tools/azure/setup-dashboard-requests.ps1), and in the stocks database's, the owner's portfolio submissions (ADR-0026,
// provision-stocks.ps1). It can't read, change or delete anything, there or anywhere else. The secret stays in the app's
// settings on the server; nothing here ever logs it or a token.

const API_VERSION = '2018-12-31';

class WriterError extends Error {
  constructor(status, what) {
    super(`${what} answered ${status}`); // never a response body: it can echo the request
    this.status = status;
  }
}

/** The writer identity isn't set up (or its settings are incomplete): the owner runs the setup script. */
class WriterNotConfigured extends Error {}

class RequestWriter {
  /**
   * @param {{endpoint: string, database: string, tenantId: string, clientId: string, clientSecret: string,
   *   partitionKeyOf?: (doc: object) => string, fetchImpl?: typeof fetch, now?: () => Date, timeoutMs?: number}} options
   *   partitionKeyOf: the document's partition key; CIX's requests are partitioned by day, the stocks database's by id.
   */
  constructor({
    endpoint, database, tenantId, clientId, clientSecret, partitionKeyOf = (doc) => doc.day, fetchImpl = fetch, now = () => new Date(), timeoutMs = 8000,
  }) {
    if (!endpoint || !database || !tenantId || !clientId || !clientSecret) {
      throw new WriterNotConfigured('Requests are not set up on this dashboard yet: run tools/azure/setup-dashboard-requests.ps1.');
    }
    const url = new URL(endpoint);
    if (url.protocol !== 'https:') throw new Error('COSMOS_ENDPOINT must be https.');
    if (!/^[0-9a-f-]{36}$/i.test(tenantId)) throw new Error('COSMOS_WRITER_TENANT_ID must be a tenant ID.');
    this.base = url.origin;
    this.scope = `${url.origin}/.default`;
    this.database = database;
    this.partitionKeyOf = partitionKeyOf;
    this.tenantId = tenantId;
    this.clientId = clientId;
    this.clientSecret = clientSecret;
    this.fetch = fetchImpl;
    this.now = now;
    this.timeoutMs = timeoutMs;
    this.token = null;
  }

  /** An access token for Cosmos DB, reused until five minutes before it expires. */
  async accessToken() {
    if (this.token && this.token.expiresAt - 5 * 60_000 > this.now().getTime()) return this.token.value;
    const response = await this.fetch(`https://login.microsoftonline.com/${encodeURIComponent(this.tenantId)}/oauth2/v2.0/token`, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded', accept: 'application/json' },
      body: new URLSearchParams({ grant_type: 'client_credentials', client_id: this.clientId, client_secret: this.clientSecret, scope: this.scope }).toString(),
      signal: AbortSignal.timeout(this.timeoutMs),
    });
    if (!response.ok) throw new WriterError(response.status, 'Microsoft Entra ID');
    const body = await response.json();
    if (typeof body.access_token !== 'string') throw new WriterError(response.status, 'Microsoft Entra ID (no token)');
    this.token = { value: body.access_token, expiresAt: this.now().getTime() + (Number(body.expires_in) || 3600) * 1000 };
    return this.token.value;
  }

  /**
   * Creates the document. Returns 'created', or 'exists' when a document with this id is already there (the same click
   * sent twice): a request is never written twice.
   */
  async create(doc) {
    const token = await this.accessToken();
    const url = `${this.base}/dbs/${encodeURIComponent(this.database)}/colls/requests/docs`;
    const response = await this.fetch(url, {
      method: 'POST',
      headers: {
        authorization: encodeURIComponent(`type=aad&ver=1.0&sig=${token}`),
        'x-ms-date': this.now().toUTCString(),
        'x-ms-version': API_VERSION,
        'x-ms-documentdb-partitionkey': JSON.stringify([this.partitionKeyOf(doc)]),
        'content-type': 'application/json',
        accept: 'application/json',
      },
      body: JSON.stringify(doc),
      signal: AbortSignal.timeout(this.timeoutMs),
    });
    if (response.status === 409) return 'exists';
    if (response.status === 401 || response.status === 403) this.token = null; // a role change or revoked secret: sign in again next time
    if (!response.ok) throw new WriterError(response.status, 'Cosmos DB');
    return 'created';
  }
}

module.exports = { RequestWriter, WriterError, WriterNotConfigured };
