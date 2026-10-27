'use strict';
// A small read-only Cosmos DB client over the REST API, so the dashboard API needs no SDK. It signs requests with the
// account's read-only key (master-key auth: https://learn.microsoft.com/rest/api/cosmos-db/access-control-on-cosmosdb-resources)
// and only ever reads: GET a document, or POST a query. The key never leaves the server.

const crypto = require('node:crypto');

const API_VERSION = '2018-12-31';

/** The authorization value for one request (URL-encoded, as the header expects). */
function authToken(verb, resourceType, resourceLink, date, key) {
  const payload = `${verb.toLowerCase()}\n${resourceType.toLowerCase()}\n${resourceLink}\n${date.toLowerCase()}\n\n`;
  const signature = crypto.createHmac('sha256', Buffer.from(key, 'base64')).update(payload, 'utf8').digest('base64');
  return encodeURIComponent(`type=master&ver=1.0&sig=${signature}`);
}

class CosmosError extends Error {
  constructor(status, what) {
    super(`Cosmos DB answered ${status} for ${what}`); // no response body: it can echo request details
    this.status = status;
  }
}

class CosmosReader {
  /**
   * @param {{endpoint: string, key: string, database: string, fetchImpl?: typeof fetch, now?: () => Date, timeoutMs?: number}} options
   */
  constructor({ endpoint, key, database, fetchImpl = fetch, now = () => new Date(), timeoutMs = 8000 }) {
    if (!endpoint || !key || !database) throw new Error('COSMOS_ENDPOINT, COSMOS_READONLY_KEY and COSMOS_DATABASE must be set.');
    const url = new URL(endpoint);
    if (url.protocol !== 'https:') throw new Error('COSMOS_ENDPOINT must be https.');
    this.base = url.origin;
    this.key = key;
    this.database = database;
    this.fetch = fetchImpl;
    this.now = now;
    this.timeoutMs = timeoutMs;
  }

  headers(verb, resourceType, resourceLink) {
    const date = this.now().toUTCString();
    return {
      authorization: authToken(verb, resourceType, resourceLink, date, this.key),
      'x-ms-date': date,
      'x-ms-version': API_VERSION,
      accept: 'application/json',
    };
  }

  async send(url, init, what) {
    const response = await this.fetch(url, { ...init, signal: AbortSignal.timeout(this.timeoutMs) });
    return { response, what };
  }

  /** One document by id, or null when it doesn't exist. */
  async readDocument(container, id, partitionKey = id) {
    const link = `dbs/${this.database}/colls/${container}/docs/${id}`;
    const url = `${this.base}/dbs/${encodeURIComponent(this.database)}/colls/${encodeURIComponent(container)}/docs/${encodeURIComponent(id)}`;
    const { response } = await this.send(url, {
      method: 'GET',
      headers: { ...this.headers('GET', 'docs', link), 'x-ms-documentdb-partitionkey': JSON.stringify([partitionKey]) },
    });
    if (response.status === 404) return null;
    if (!response.ok) throw new CosmosError(response.status, `${container}/${id}`);
    return response.json();
  }

  /**
   * Runs a query and follows continuations up to maxItems. With partitionKey it stays in one partition (ORDER BY and TOP
   * work); without it the query fans out across partitions, which the REST gateway allows only for simple queries.
   */
  async query(container, sql, { parameters = [], partitionKey, maxItems = 500 } = {}) {
    const link = `dbs/${this.database}/colls/${container}`;
    const url = `${this.base}/dbs/${encodeURIComponent(this.database)}/colls/${encodeURIComponent(container)}/docs`;
    const items = [];
    let continuation;
    do {
      const headers = {
        ...this.headers('POST', 'docs', link),
        'content-type': 'application/query+json',
        'x-ms-documentdb-isquery': 'True',
        'x-ms-max-item-count': String(Math.min(maxItems, 1000)),
      };
      if (partitionKey !== undefined) headers['x-ms-documentdb-partitionkey'] = JSON.stringify([partitionKey]);
      else headers['x-ms-documentdb-query-enablecrosspartition'] = 'True';
      if (continuation) headers['x-ms-continuation'] = continuation;
      const { response } = await this.send(url, { method: 'POST', headers, body: JSON.stringify({ query: sql, parameters }) });
      if (!response.ok) throw new CosmosError(response.status, `a query on ${container}`);
      const page = await response.json();
      items.push(...(page.Documents ?? []));
      continuation = response.headers.get('x-ms-continuation');
    } while (continuation && items.length < maxItems);
    return items.slice(0, maxItems);
  }
}

/**
 * The database one mode's engine writes to. Paper's is COSMOS_DATABASE. The live engine has its own (ADR-0017), named by
 * COSMOS_LIVE_DATABASE; without that setting, live reads paper's database, where it finds nothing of its own.
 */
function databaseFor(mode, env = process.env) {
  const paper = env.COSMOS_DATABASE ?? 'cix';
  return mode === 'live' && env.COSMOS_LIVE_DATABASE ? env.COSMOS_LIVE_DATABASE : paper;
}

/** One reader per database, made on first use, so each function instance signs in to each database once. */
function readerCache(env = process.env) {
  const readers = new Map();
  return (mode = 'paper') => {
    const database = databaseFor(mode, env);
    if (!readers.has(database)) {
      readers.set(database, new CosmosReader({ endpoint: env.COSMOS_ENDPOINT, key: env.COSMOS_READONLY_KEY, database }));
    }
    return readers.get(database);
  };
}

module.exports = { authToken, CosmosReader, CosmosError, databaseFor, readerCache };
