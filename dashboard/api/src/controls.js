'use strict';
// The owner's Pause and Resume buttons (ADR-0018), for paper or live: a trading-control request document the engine of
// that mode takes (src/Cix.Engine/Manual/TradingControlService.cs). Pause engages its kill switch; Resume releases a halt
// that was already in place when the owner pressed it. A live request is taken only with confirmLive, which the page sends
// once the owner has ticked the box that names real money. The engine checks everything again. Pure functions: tested on
// their own.

const { cleanNote, RequestError } = require('./requests');
const { engineTextRu } = require('./engineText');

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const REQUEST_TYPE = 'trading-control';
const ACTIONS = new Set(['pause', 'resume']);
const MODES = new Set(['paper', 'live']);

/**
 * The request document for Pause or Resume, or a RequestError saying what is wrong.
 * @param {object} body id (a fresh UUID per dialog), action, note, tradingMode, confirmLive.
 * @param {number} now Milliseconds since the epoch: the request's time is the server's.
 */
function controlRequestDocument(body, now) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new RequestError('The request must be a JSON object.');
  const { id, action } = body;
  if (typeof id !== 'string' || !UUID.test(id)) throw new RequestError('id must be a UUID.');
  if (!ACTIONS.has(action)) throw new RequestError('action must be pause or resume.');
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
    requestedBy: 'owner', // a role, never the account's name or email
    tradingMode,
    status: 'new',
  };
  const note = cleanNote(body.note);
  if (note) doc.note = note;
  return doc;
}

/** A Pause or Resume request as the page follows it. */
function controlRequestItem(doc) {
  const events = Array.isArray(doc.events) ? doc.events : [];
  const last = events[events.length - 1] ?? {};
  return {
    id: doc.id,
    day: doc.day ?? null,
    createdAt: doc.createdAt ?? null,
    action: doc.action ?? null,
    tradingMode: doc.tradingMode ?? 'paper',
    note: doc.note ?? null,
    status: doc.status ?? 'new',
    detail: last.detail ?? null,
    detailRu: engineTextRu(last.detail),
    updatedAt: doc.updatedAt ?? null,
  };
}

module.exports = { controlRequestDocument, controlRequestItem, REQUEST_TYPE };
