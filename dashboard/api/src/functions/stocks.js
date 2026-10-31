'use strict';
// The Stocks tab's read (ADR-0023): GET /api/stocks, the stocks advisor's latest advice. Owner only, like every read
// (dashboard.js's handle checks before anything is read). The stocks database is the stocks host's own, read with its own
// read-only key; this route never touches the crypto engines' databases or requests.

const { app } = require('@azure/functions');
const { handle } = require('./dashboard');
const { stocksReader, loadStocks } = require('../stocks');

const CACHE_MS = 60_000; // the advice changes once a day
let reader;
let hit = null;

app.http('stocks', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'stocks',
  handler: (request, context) => handle(request, context, async () => {
    if (hit && Date.now() - hit.at < CACHE_MS) return hit.body;
    reader ??= stocksReader();
    const body = await loadStocks(reader, Date.now());
    hit = { at: Date.now(), body };
    return body;
  }),
});
