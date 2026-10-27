'use strict';
// The owner's manual buy and sell requests (ADR-0011): checked here before anything is written, then created in the
// requests container, where the engine picks them up. The engine checks everything again and is the only thing that acts
// on a request; this side only makes sure what it writes is well formed. Pure functions: tested on their own.

const { engineTextRu } = require('./engineText');

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const SYMBOL = /^[A-Z0-9]{2,12}-[A-Z]{3,4}$/;
const MAX_NOTE = 280;
const MAX_QUOTE = 1000; // well above any order the risk gate allows; the engine and the gate decide the real size

class RequestError extends Error {}

/** One line, no control or format characters (they could disguise what the owner or the trader reads), bounded. */
function cleanNote(value) {
  if (value === undefined || value === null || value === '') return null;
  if (typeof value !== 'string') throw new RequestError('note must be text.');
  const text = value.replace(/[\p{Cc}\p{Cf}]/gu, ' ').replace(/\s+/g, ' ').trim();
  return text ? Array.from(text).slice(0, MAX_NOTE).join('') : null;
}

function decimal(value, name) {
  const text = typeof value === 'number' ? String(value) : value;
  if (typeof text !== 'string' || !/^\d+(\.\d{1,8})?$/.test(text.trim())) throw new RequestError(`${name} must be a positive number.`);
  return text.trim();
}

/**
 * The document to create for a request from the dashboard, or a RequestError saying what is wrong.
 * @param {object} body What the page sent: id (a fresh UUID per click: a second click with the same id is the same request),
 *   side, mode, symbol, portion (sells) or quoteAmount (buys), note, tradingMode.
 * @param {number} now Milliseconds since the epoch: the request's time is the server's, never the browser's.
 */
function requestDocument(body, now) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new RequestError('The request must be a JSON object.');
  const { id, side, mode, symbol, portion, quoteAmount } = body;
  if (typeof id !== 'string' || !UUID.test(id)) throw new RequestError('id must be a UUID.');
  if (side !== 'buy' && side !== 'sell') throw new RequestError('side must be buy or sell.');
  if (mode !== 'direct' && mode !== 'agent') throw new RequestError('mode must be direct or agent.');
  if (side === 'buy' && mode === 'direct') throw new RequestError('Buys go through the trader: choose agent.');
  if (typeof symbol !== 'string' || !SYMBOL.test(symbol)) throw new RequestError('symbol must look like BTC-EUR.');
  const tradingMode = body.tradingMode ?? 'paper';
  if (tradingMode !== 'paper') throw new RequestError('Only paper trading takes requests from the dashboard.');

  const at = new Date(now).toISOString();
  const doc = {
    id: id.toLowerCase(),
    day: at.slice(0, 10),
    createdAt: at,
    type: 'manual-request',
    side,
    mode,
    symbol,
    requestedBy: 'owner', // a role, never the account's name or email
    tradingMode,
    status: 'new',
  };
  if (side === 'sell') {
    if (quoteAmount !== undefined && quoteAmount !== null) throw new RequestError('A sell takes portion, not quoteAmount.');
    const p = decimal(portion, 'portion');
    if (!(Number(p) > 0 && Number(p) <= 1)) throw new RequestError('portion must be more than 0 and at most 1.');
    doc.portion = p;
  } else {
    if (portion !== undefined && portion !== null) throw new RequestError('A buy takes quoteAmount, not portion.');
    const q = decimal(quoteAmount, 'quoteAmount');
    if (!(Number(q) > 0 && Number(q) <= MAX_QUOTE)) throw new RequestError(`quoteAmount must be more than 0 and at most ${MAX_QUOTE}.`);
    doc.quoteAmount = q;
  }
  const note = cleanNote(body.note);
  if (note) doc.note = note;
  return doc;
}

const FINAL = new Set(['filled', 'closed', 'declined', 'rejected', 'failed', 'invalid', 'duplicate', 'expired']);

/** A request document as the owner's history shows it: what was asked, where it stands, and each step. */
function historyItem(doc) {
  const o = doc.outcome ?? {};
  return {
    id: doc.id,
    createdAt: doc.createdAt ?? null,
    updatedAt: doc.updatedAt ?? null,
    side: doc.side ?? null,
    mode: doc.mode ?? null,
    symbol: doc.symbol ?? null,
    portion: doc.portion ?? null,
    quoteAmount: doc.quoteAmount ?? null,
    note: doc.note ?? null,
    tradingMode: doc.tradingMode ?? 'paper',
    status: doc.status ?? 'new',
    final: FINAL.has(doc.status),
    outcome: {
      rule: o.rule ?? null,
      reason: o.reason ?? null,
      reasonRu: engineTextRu(o.reason),
      decidedBy: o.decidedBy ?? null,
      rationale: o.rationale ?? null,
      rationaleRu: o.rationaleRu ?? null,
      requestedSize: o.requestedSize ?? null,
      requestedPrice: o.requestedPrice ?? null,
      approvedSize: o.approvedSize ?? null,
      approvedPrice: o.approvedPrice ?? null,
      filledSize: o.filledSize ?? null,
      averagePrice: o.averagePrice ?? null,
    },
    events: (Array.isArray(doc.events) ? doc.events : []).map((e) => ({
      at: e.at ?? null, status: e.status ?? null, by: e.by ?? null, detail: e.detail ?? null, detailRu: engineTextRu(e.detail),
    })),
  };
}

function history(docs) {
  return docs.map(historyItem).sort((a, b) => (Date.parse(b.createdAt ?? '') || 0) - (Date.parse(a.createdAt ?? '') || 0));
}

module.exports = { requestDocument, history, historyItem, cleanNote, RequestError, MAX_NOTE, MAX_QUOTE };
