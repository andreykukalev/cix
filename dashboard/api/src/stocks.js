'use strict';
// The Stocks tab's read (ADR-0023): the stocks advisor's latest advice, from the stocks host's own Cosmos DB account in its
// own resource group, never the crypto engines' databases. The host writes one document, `advice-latest` in the `advice`
// container, after each close (stocks/README.md has its shape); this module checks every field and keeps only what the tab
// shows, so a malformed document gives gaps, not a broken page. Without the stocks settings the tab says it isn't set up.

const { CosmosReader } = require('./cosmos');

const CONTAINER = 'advice';
const LATEST = 'advice-latest';
const DAY_MS = 24 * 3600 * 1000;
/** Advice older than this many days is stale: a long weekend is three days without a close, a holiday makes four. */
const STALE_DAYS = 4;
const MAX_ROWS = 200;

const ACTION_ORDER = ['sell', 'set-stop', 'raise-stop', 'buy']; // protecting what is held comes before buying
const REGIMES = new Set(['risk-on', 'risk-off', 'unknown']);
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

const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const positive = (v) => (num(v) !== null && v > 0 ? v : null);
const symbol = (v) => (typeof v === 'string' && SYMBOL.test(v) ? v : null);
const code = (v) => (typeof v === 'string' && CODE.test(v) ? v : null);
const currency = (v) => (typeof v === 'string' && CURRENCY.test(v) ? v : null);
const day = (v) => (typeof v === 'string' && DAY.test(v) && Number.isFinite(Date.parse(v)) ? v : null);
const iso = (v) => (typeof v === 'string' && v.length <= 40 && Number.isFinite(Date.parse(v)) ? new Date(v).toISOString() : null);
const name = (v) => (typeof v === 'string' && v.trim() ? v.trim().slice(0, 80) : null);
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

/** What the Stocks tab shows from the advisor's document, every field checked. */
function stocksView(value, now) {
  const v = value && typeof value === 'object' ? value : {};
  const asOf = day(v.asOf);
  const today = Date.parse(new Date(now).toISOString().slice(0, 10));
  const ageDays = asOf ? Math.round((today - Date.parse(asOf)) / DAY_MS) : null;
  return {
    asOf,
    ageDays,
    stale: ageDays === null || ageDays > STALE_DAYS,
    generatedAt: iso(v.generatedAt),
    regime: REGIMES.has(v.regime) ? v.regime : 'unknown',
    benchmark: symbol(v.benchmark),
    baseCurrency: currency(v.baseCurrency),
    capital: positive(v.capital),
    cash: num(v.cash),
    equity: positive(v.equity),
    actions: list(v.actions).map(action).filter(Boolean)
      .map((a, i) => ({ a, i })).sort((x, y) => ACTION_ORDER.indexOf(x.a.kind) - ACTION_ORDER.indexOf(y.a.kind) || x.i - y.i).map((x) => x.a),
    holdings: list(v.holdings).map(holding).filter(Boolean),
    notes: list(v.notes).map(note).filter(Boolean),
  };
}

/** The Stocks tab's answer: whether the stocks database is set up, and the latest advice when there is one. */
async function loadStocks(reader, now) {
  const generatedAt = new Date(now).toISOString();
  if (!reader) return { configured: false, advice: null, generatedAt };
  const doc = await reader.readDocument(CONTAINER, LATEST);
  return { configured: true, advice: doc ? stocksView(doc.value ?? null, now) : null, generatedAt };
}

module.exports = { stocksReader, stocksView, loadStocks, CONTAINER, LATEST, STALE_DAYS };
