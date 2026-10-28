'use strict';
// Trading pairs on the Coins tab: which pairs the engine trades now, which the owner can add (the exchange's active pairs in
// the quote currency config/risk-limits.json permits), and the owner's add and remove requests. The engine writes the list
// and the catalog (state container) and does every check again: an add needs an active pair in that currency within the
// cap, a remove needs nothing held or working in the coin. Whether a watched coin trades is the strategy's filters' call at
// every scan (ADR-0021). A change is a request document the engine takes, because the
// dashboard's writer can only create documents in the requests container (ADR-0015, ADR-0016). Live changes its own list,
// within its own limits file, only with confirmLive (ADR-0018). Pure functions: tested on their own.

const { engineTextRu } = require('./engineText');
const { cleanNote, RequestError } = require('./requests');

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const SYMBOL = /^[A-Z0-9]{1,20}-[A-Z]{3,5}$/;
const REQUEST_TYPE = 'tradable-pair';
const ACTIONS = new Set(['add', 'remove']);
const MODES = new Set(['paper', 'live']);

/**
 * The request document for adding or removing a pair, or a RequestError saying what is wrong.
 * @param {object} body id (a fresh UUID per dialog), action (add or remove), symbol, note, tradingMode, confirmLive.
 * @param {number} now Milliseconds since the epoch: the request's time is the server's.
 */
function pairRequestDocument(body, now) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new RequestError('The request must be a JSON object.');
  const { id, action } = body;
  if (typeof id !== 'string' || !UUID.test(id)) throw new RequestError('id must be a UUID.');
  if (typeof action !== 'string' || !ACTIONS.has(action)) throw new RequestError('action must be add or remove.');
  const symbol = typeof body.symbol === 'string' ? body.symbol.trim().toUpperCase().replace('/', '-') : null;
  if (!symbol || !SYMBOL.test(symbol)) throw new RequestError('symbol must look like SOL-EUR.');
  const tradingMode = body.tradingMode ?? 'paper';
  if (!MODES.has(tradingMode)) throw new RequestError('tradingMode must be paper or live.');
  if (tradingMode === 'live' && body.confirmLive !== true) throw new RequestError('Confirm that this changes real-money trading.');
  const at = new Date(now).toISOString();
  const doc = {
    id: id.toLowerCase(),
    day: at.slice(0, 10),
    createdAt: at,
    type: REQUEST_TYPE,
    action,
    symbol,
    requestedBy: 'owner', // a role, never the account's name or email
    tradingMode,
    status: 'new',
  };
  const note = cleanNote(body.note);
  if (note) doc.note = note;
  return doc;
}

/** A pair request as the tab's history shows it. */
function pairRequestItem(doc) {
  const events = Array.isArray(doc.events) ? doc.events : [];
  const last = events[events.length - 1] ?? {};
  return {
    id: doc.id,
    day: doc.day ?? null,
    createdAt: doc.createdAt ?? null,
    action: doc.action ?? null,
    symbol: doc.symbol ?? null,
    tradingMode: doc.tradingMode ?? 'paper',
    note: doc.note ?? null,
    status: doc.status ?? 'new',
    detail: last.detail ?? null,
    detailRu: engineTextRu(last.detail),
    updatedAt: doc.updatedAt ?? null,
  };
}

function symbols(list) {
  return (Array.isArray(list) ? list : []).filter((s) => typeof s === 'string' && SYMBOL.test(s));
}

/**
 * Everything the pairs panel shows for one mode.
 * @param {{mode: string, catalog: object|null, list: object|null, requests: object[]}} input
 *   catalog: the engine's pair-catalog value; list: its tradable-pairs value (absent until the owner first changes it).
 */
function pairsView({ mode, catalog, list, requests }, now) {
  const tradable = symbols(list?.symbols ?? catalog?.tradable);
  // Held coins the engine watches although the owner didn't add them: the strategy doesn't buy them until the owner does.
  const adopted = new Set(symbols(catalog?.adopted));
  const capValue = Number(catalog?.cap); // the engine writes numbers as strings
  const cap = Number.isInteger(capValue) && capValue > 0 ? capValue : null;
  const pending = (Array.isArray(requests) ? requests : []).filter((r) => (r.status ?? 'new') === 'new');
  const pendingFor = (symbol) => pending.find((r) => r.symbol === symbol) ?? null;
  const available = (Array.isArray(catalog?.pairs) ? catalog.pairs : [])
    .filter((p) => p && typeof p.symbol === 'string' && SYMBOL.test(p.symbol) && p.permitted && !tradable.includes(p.symbol))
    .map((p) => ({ symbol: p.symbol, base: typeof p.base === 'string' ? p.base : p.symbol.split('-')[0] }))
    .sort((a, b) => a.symbol.localeCompare(b.symbol));
  const editable = MODES.has(mode) && !!catalog;
  return {
    generatedAt: new Date(now).toISOString(),
    mode,
    editable,
    readOnlyReason: catalog ? null : 'The engine hasn\'t listed the exchange\'s pairs yet; it does when it starts.',
    quoteCurrency: typeof catalog?.quoteCurrency === 'string' ? catalog.quoteCurrency : null,
    cap,
    full: cap !== null && tradable.length >= cap,
    catalogAt: catalog?.updatedAt ?? null,
    changedAt: list?.updatedAt ?? null,
    tradable: tradable.map((symbol) => ({ symbol, adopted: adopted.has(symbol), pending: pendingFor(symbol)?.action ?? null })),
    available: available.map((p) => ({ ...p, pending: pendingFor(p.symbol)?.action ?? null })),
    requests: (Array.isArray(requests) ? requests : []).map(pairRequestItem)
      .sort((a, b) => (Date.parse(b.createdAt ?? '') || 0) - (Date.parse(a.createdAt ?? '') || 0)),
  };
}

module.exports = { pairRequestDocument, pairRequestItem, pairsView, REQUEST_TYPE };
