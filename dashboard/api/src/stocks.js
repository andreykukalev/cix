'use strict';
// The Stocks tab's read (ADR-0023): the stocks advisor's latest advice, from the stocks host's own Cosmos DB account in its
// own resource group, never the crypto engines' databases. The host writes one document, `advice-latest` in the `advice`
// container, after each close (stocks/README.md has its shape); this module checks every field and keeps only what the tab
// shows, so a malformed document gives gaps, not a broken page. Without the stocks settings the tab says it isn't set up.

const { CosmosReader } = require('./cosmos');
const { pendingPortfolio, firstUnseen } = require('./portfolio');

const CONTAINER = 'advice';
const LATEST = 'advice-latest';
const CANDIDATES = 'candidates-latest'; // the market-wide search's page (ADR-0025), same container
const SEARCH_STATUSES = new Set(['done', 'off', 'call-budget', 'data-error', 'not-ready', 'advice-behind']);
const CANDIDATE_STATUSES = new Set(['breakout', 'near-high']);
const MAX_CANDIDATES = 100;
const DAY_MS = 24 * 3600 * 1000;
/**
 * Advice is stale once this many weekday closes have passed without new advice (today's close not counted, as it may not
 * have happened yet): one missing close can be a holiday, two mean the host has stopped.
 */
const STALE_MISSED_CLOSES = 2;
const MAX_ROWS = 200;

const ACTION_ORDER = ['sell', 'set-stop', 'raise-stop', 'buy']; // protecting what is held comes before buying
const REGIMES = new Set(['risk-on', 'risk-off', 'unknown']);
// The watchlist's statuses (Cix.Stocks.Host.WatchStatus), in the order the page lists them: what is held, what signals an
// entry tonight, what is in an uptrend under its high, then the rest.
const WATCH_ORDER = ['held', 'breakout', 'uptrend', 'below-trend', 'too-expensive', 'stale-data', 'not-enough-data', 'no-data'];
// The same rule as Cix.Stocks.StockSymbol: TICKER.EXCHANGE in capitals.
const SYMBOL = /^[A-Z0-9][A-Z0-9_-]{0,19}\.[A-Z]{1,10}$/;
const CODE = /^[a-z][a-z-]{0,39}$/;
const CURRENCY = /^[A-Z]{3}$/;
const DAY = /^\d{4}-\d{2}-\d{2}$/;

/** The stocks database's reader, or null while the stocks settings aren't there. */
function stocksReader(env = process.env) {
  if (!env.STOCKS_COSMOS_ENDPOINT || !env.STOCKS_COSMOS_READONLY_KEY) return null;
  return new CosmosReader({
    endpoint: env.STOCKS_COSMOS_ENDPOINT, key: env.STOCKS_COSMOS_READONLY_KEY, database: env.STOCKS_COSMOS_DATABASE || 'cix-stocks',
  });
}

// The host writes every number as a string, so decimals reach the database exactly (CLAUDE.md rule 7): a plain decimal
// string is read like a number, anything else is a gap.
const NUMBER = /^-?\d{1,15}(\.\d{1,28})?$/; // C# decimals keep up to 28 digits after the point
const num = (v) => {
  const n = typeof v === 'number' ? v : typeof v === 'string' && NUMBER.test(v) ? Number(v) : NaN;
  return Number.isFinite(n) ? n : null;
};
const positive = (v) => {
  const n = num(v);
  return n !== null && n > 0 ? n : null;
};
const symbol = (v) => (typeof v === 'string' && SYMBOL.test(v) ? v : null);
const code = (v) => (typeof v === 'string' && CODE.test(v) ? v : null);
const currency = (v) => (typeof v === 'string' && CURRENCY.test(v) ? v : null);
// A real calendar day: Date.parse rolls 2026-02-30 over to March, so the day must come back unchanged.
const day = (v) => (typeof v === 'string' && DAY.test(v) && Number.isFinite(Date.parse(v)) && new Date(v).toISOString().slice(0, 10) === v ? v : null);
const iso = (v) => (typeof v === 'string' && v.length <= 40 && Number.isFinite(Date.parse(v)) ? new Date(v).toISOString() : null);
// Control, format (bidi overrides, zero-width) and line-separator characters out, so a name can't disguise the ticker beside it.
const name = (v) => {
  const s = typeof v === 'string' ? v.replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, '').trim() : '';
  return s ? s.slice(0, 80) : null;
};
const list = (v) => (Array.isArray(v) ? v.slice(0, MAX_ROWS) : []);
const ratio = (a, b) => (a !== null && b !== null && b !== 0 ? a / b - 1 : null);

function action(a) {
  const kind = typeof a?.kind === 'string' ? a.kind : null;
  const s = symbol(a?.symbol);
  if (!ACTION_ORDER.includes(kind) || !s) return null;
  return {
    kind, symbol: s, name: name(a.name), currency: currency(a.currency),
    quantity: positive(a.quantity), referencePrice: positive(a.referencePrice), stop: positive(a.stop), reason: code(a.reason),
  };
}

function holding(h) {
  const s = symbol(h?.symbol);
  const quantity = positive(h?.quantity);
  if (!s || quantity === null) return null;
  const averageCost = positive(h.averageCost);
  const lastClose = positive(h.lastClose);
  const stop = positive(h.stop);
  return {
    symbol: s, name: name(h.name), currency: currency(h.currency), quantity, averageCost, lastClose, stop, openedOn: day(h.openedOn),
    change: ratio(lastClose, averageCost),
    // How far the price can fall before the stop sells, as a fraction of the last close: negative when it's already under.
    toStop: lastClose !== null && stop !== null ? (lastClose - stop) / lastClose : null,
  };
}

function note(n) {
  const s = symbol(n?.symbol);
  const reason = code(n?.reason);
  return s && reason ? { symbol: s, reason } : null;
}

function watch(w) {
  const s = symbol(w?.symbol);
  const status = WATCH_ORDER.includes(w?.status) ? w.status : null;
  if (!s || !status) return null;
  const lastClose = positive(w.lastClose);
  const breakoutHigh = positive(w.breakoutHigh);
  const trendAverage = positive(w.trendAverage);
  return {
    symbol: s, name: name(w.name), currency: currency(w.currency), theme: code(w.theme), lastClose, lastDay: day(w.lastDay),
    breakoutHigh, trendAverage,
    // How far the close must rise to beat its prior high (negative once above it), and how far it is over its trend average.
    toBreakout: ratio(breakoutHigh, lastClose),
    vsTrend: ratio(lastClose, trendAverage),
    status,
  };
}

/** Held first, then breakouts, then uptrends nearest their high, then the rest, each in the watchlist's order. */
function watchOrder(x, y) {
  const byStatus = WATCH_ORDER.indexOf(x.w.status) - WATCH_ORDER.indexOf(y.w.status);
  if (byStatus !== 0) return byStatus;
  if (x.w.status === 'uptrend') {
    const a = x.w.toBreakout ?? Infinity;
    const b = y.w.toBreakout ?? Infinity;
    if (a !== b) return a < b ? -1 : 1;
  }
  return x.i - y.i;
}

function candidate(c) {
  const s = symbol(c?.symbol);
  const status = CANDIDATE_STATUSES.has(c?.status) ? c.status : null;
  const close = positive(c?.close);
  if (!s || !status || close === null) return null;
  const breakoutHigh = positive(c.breakoutHigh);
  const stop = positive(c.stop);
  const shares = positive(c.shares);
  return {
    symbol: s, name: name(c.name), venue: name(c.venue), status, close, breakoutHigh, trendAverage: positive(c.trendAverage), stop, shares,
    amount: shares !== null ? shares * close : null,
    momentum: num(c.momentum), marketCap: positive(c.marketCap),
    toStop: stop !== null ? (close - stop) / close : null,
    toBreakout: ratio(breakoutHigh, close),
  };
}

// A count the host wrote as a decimal string (or a number): a whole number from 0, else a gap.
const count = (v) => {
  const n = num(v);
  return Number.isInteger(n) && n >= 0 ? n : null;
};

/** The market-wide search's page, every field checked; null when there is none. */
function candidatesView(value) {
  if (!value || typeof value !== 'object') return null;
  return {
    status: SEARCH_STATUSES.has(value.status) ? value.status : null,
    asOf: day(value.asOf),
    generatedAt: iso(value.generatedAt),
    regime: REGIMES.has(value.regime) ? value.regime : 'unknown',
    baseCurrency: currency(value.baseCurrency),
    capital: positive(value.capital),
    listed: count(value.listed),
    shortlisted: count(value.shortlisted),
    // The host ranks them (entry signals first, then momentum): the order is kept.
    candidates: (Array.isArray(value.candidates) ? value.candidates.slice(0, MAX_CANDIDATES) : []).map(candidate).filter(Boolean),
  };
}

/** Weekdays strictly between two UTC days: the closes there have been since asOf, not counting today's. */
function weekdaysBetween(fromMs, toMs) {
  let n = 0;
  for (let d = fromMs + DAY_MS; d < toMs; d += DAY_MS) {
    const w = new Date(d).getUTCDay();
    if (w !== 0 && w !== 6) n += 1;
  }
  return n;
}

const sortActions = (items) => list(items).map(action).filter(Boolean)
  .map((a, i) => ({ a, i })).sort((x, y) => ACTION_ORDER.indexOf(x.a.kind) - ACTION_ORDER.indexOf(y.a.kind) || x.i - y.i).map((x) => x.a);

// A submission number the host wrote: a whole number from 1, else a gap.
const sequence = (v) => {
  const n = num(v);
  return Number.isInteger(n) && n >= 1 ? n : null;
};

/** The owner's real portfolio as the host advised it (ADR-0026); null when there is none yet. */
function ownerView(o) {
  if (!o || typeof o !== 'object') return null;
  const capital = positive(o.capital);
  if (capital === null) return null;
  return {
    capital,
    cash: num(o.cash),
    invested: num(o.invested),
    value: num(o.value),
    appliedSequence: sequence(o.appliedSequence),
    updatedAt: iso(o.updatedAt),
    actions: sortActions(o.actions),
    holdings: list(o.holdings).map(holding).filter(Boolean),
    notes: list(o.notes).map(note).filter(Boolean),
  };
}

function refusalView(r) {
  const n = sequence(r?.sequence);
  const reason = code(r?.reason);
  return n && reason ? { sequence: n, reason } : null;
}

/** What the Stocks tab shows from the advisor's document, every field checked. */
function stocksView(value, now) {
  const v = value && typeof value === 'object' ? value : {};
  const asOf = day(v.asOf);
  const today = Date.parse(new Date(now).toISOString().slice(0, 10));
  const ageDays = asOf ? Math.round((today - Date.parse(asOf)) / DAY_MS) : null;
  return {
    asOf,
    ageDays,
    // A day in the future is as wrong as a missing one (a clock or host bug).
    stale: ageDays === null || ageDays < 0 || weekdaysBetween(Date.parse(asOf), today) >= STALE_MISSED_CLOSES,
    generatedAt: iso(v.generatedAt),
    regime: REGIMES.has(v.regime) ? v.regime : 'unknown',
    benchmark: symbol(v.benchmark),
    baseCurrency: currency(v.baseCurrency),
    capital: positive(v.capital),
    cash: num(v.cash),
    equity: positive(v.equity),
    actions: sortActions(v.actions),
    holdings: list(v.holdings).map(holding).filter(Boolean),
    notes: list(v.notes).map(note).filter(Boolean),
    watchlist: list(v.watchlist).map(watch).filter(Boolean).map((w, i) => ({ w, i })).sort(watchOrder).map((x) => x.w),
    owner: ownerView(v.owner),
    refusal: refusalView(v.refusal),
  };
}

/** The Stocks tab's answer: whether the stocks database is set up, and the latest advice when there is one. */
async function loadStocks(reader, now) {
  const generatedAt = new Date(now).toISOString();
  if (!reader) return { configured: false, advice: null, candidates: null, pending: null, generatedAt };
  const [doc, found] = await Promise.all([reader.readDocument(CONTAINER, LATEST), reader.readDocument(CONTAINER, CANDIDATES)]);
  const advice = doc ? stocksView(doc.value ?? null, now) : null;
  const { newest } = await pendingPortfolio(reader, firstUnseen(advice));
  return {
    configured: true, advice, candidates: found ? candidatesView(found.value ?? null) : null, pending: newest, generatedAt,
  };
}

module.exports = { stocksReader, stocksView, candidatesView, loadStocks, CONTAINER, LATEST, CANDIDATES, STALE_MISSED_CLOSES };
