'use strict';
// The owner's real stocks portfolio (ADR-0026): what they hold at Revolut's broker and the capital they give the advisor.
// The Stocks page sends the whole portfolio each time, as one submission, `portfolio-N` in the stocks database's requests
// container; the writer identity can only create it there. The host applies the newest one on its next nightly run and
// checks every field again (Cix.Stocks.Host.OwnerPortfolio): the rules here are the same, so the page can say what's wrong
// before anything is sent. Numbers are written as decimal strings, so they reach the host exactly (CLAUDE.md rule 7).

const CONTAINER = 'requests';
/** The most submissions after the applied one: the host looks at no more (OwnerPortfolio.MaxPending). */
const MAX_PENDING = 50;
const MAX_HOLDINGS = 50;
const MAX_CAPITAL = 1_000_000;
const MAX_FIGURE = 1_000_000; // a quantity or a price
// US shares only: the owner's broker account is in USD. The ticker part follows Cix.Stocks.StockSymbol.
const SYMBOL = /^[A-Z0-9][A-Z0-9_-]{0,19}\.US$/;
const DECIMAL = /^\d{1,7}(\.\d{1,8})?$/;
const DAY = /^\d{4}-\d{2}-\d{2}$/;
const CLIENT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A portfolio the page can't send: the message says which field, in words the page translates. */
class PortfolioError extends RangeError {}

/** A positive decimal as text, at most max; optional ones may be empty (null). */
function decimal(v, what, max, optional = false) {
  if (optional && (v === undefined || v === null || v === '')) return null;
  const s = typeof v === 'string' ? v.trim() : typeof v === 'number' && Number.isFinite(v) ? String(v) : '';
  if (!DECIMAL.test(s)) throw new PortfolioError(`${what} must be a number, like 12.5.`);
  const n = Number(s);
  if (!(n > 0) || n > max) throw new PortfolioError(`${what} must be more than 0 and at most ${max}.`);
  return s.replace(/^0+(?=\d)/, ''); // no leading zeros: 007 is 7
}

/** A real calendar day, not after the owner's today: the page sends their local date, so up to a day ahead of UTC's. */
function day(v, latest) {
  if (v === undefined || v === null || v === '') return null;
  const s = typeof v === 'string' ? v.trim() : '';
  if (!DAY.test(s) || !Number.isFinite(Date.parse(s)) || new Date(s).toISOString().slice(0, 10) !== s) throw new PortfolioError('The day bought must be a date.');
  if (s > latest) throw new PortfolioError('The day bought cannot be after today.');
  return s;
}

// Control, format (bidi overrides, zero-width) and line-separator characters out, so a name can't disguise the ticker beside it.
function name(v) {
  const s = typeof v === 'string' ? v.replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, '').trim() : '';
  return s ? [...s].slice(0, 80).join('') : null;
}

function holding(h, latest) {
  if (!h || typeof h !== 'object') throw new PortfolioError('Each holding needs a ticker, the shares and the price paid.');
  const symbol = typeof h.symbol === 'string' ? h.symbol.trim().toUpperCase() : '';
  if (!SYMBOL.test(symbol)) throw new PortfolioError('A ticker must be a US share, like EPAM.US.');
  return {
    symbol,
    name: name(h.name),
    quantity: decimal(h.quantity, `The shares of ${symbol}`, MAX_FIGURE),
    averageCost: decimal(h.averageCost, `The price paid for ${symbol}`, MAX_FIGURE),
    stop: decimal(h.stop, `The stop of ${symbol}`, MAX_FIGURE, true),
    openedOn: day(h.openedOn, latest),
  };
}

/** The submission the page sent, checked: capital and holdings, each symbol once. Throws PortfolioError. */
function portfolioValue(body, now) {
  const b = body && typeof body === 'object' ? body : {};
  const latest = new Date(new Date(now).getTime() + 86_400_000).toISOString().slice(0, 10); // as OwnerPortfolio.Apply
  const capital = decimal(b.capital, 'The capital', MAX_CAPITAL);
  if (!Array.isArray(b.holdings)) throw new PortfolioError('The holdings must be a list.');
  if (b.holdings.length > MAX_HOLDINGS) throw new PortfolioError(`At most ${MAX_HOLDINGS} holdings.`);
  const holdings = b.holdings.map((h) => holding(h, latest));
  const seen = new Set();
  for (const h of holdings) {
    if (seen.has(h.symbol)) throw new PortfolioError(`${h.symbol} is listed twice.`);
    seen.add(h.symbol);
  }
  const clientId = typeof b.clientId === 'string' && CLIENT_ID.test(b.clientId) ? b.clientId : null;
  return { submittedAt: new Date(now).toISOString(), capital, holdings, clientId };
}

/** The document for submission number n. */
function portfolioDocument(value, n) {
  return { id: `portfolio-${n}`, value: { sequence: n, ...value } };
}

/** Whether two submissions hold the same capital and holdings. */
function samePortfolio(a, b) {
  const key = (p) => JSON.stringify({ capital: p.capital, holdings: p.holdings });
  return key(a) === key(b);
}

/** The first submission number the host hasn't seen: after the applied one and after a refused one. */
function firstUnseen(advice) {
  return Math.max(advice?.owner?.appliedSequence ?? 0, advice?.refusal?.sequence ?? 0) + 1;
}

/** Stored submission number n as the page shows it (and fills the form from); unreadable when it isn't one. */
function submissionView(doc, n) {
  const v = doc?.value;
  const sentAt = v && typeof v === 'object' && typeof v.submittedAt === 'string' ? Date.parse(v.submittedAt) : NaN;
  if (!Number.isFinite(sentAt)) return { sequence: n, unreadable: true };
  try {
    return { sequence: n, ...portfolioValue(v, sentAt) };
  } catch (error) {
    if (error instanceof PortfolioError) return { sequence: n, unreadable: true };
    throw error;
  }
}

/**
 * The submissions sent since the host last looked, from number start: the newest (what the next run applies), and the next
 * free number; next is null when MAX_PENDING are waiting already.
 */
async function pendingPortfolio(reader, start) {
  let newest = null;
  for (let n = start; n < start + MAX_PENDING; n += 1) {
    const doc = await reader.readDocument(CONTAINER, `portfolio-${n}`);
    if (!doc) return { newest, next: n };
    newest = submissionView(doc, n);
  }
  return { newest, next: null };
}

module.exports = { portfolioValue, portfolioDocument, firstUnseen, samePortfolio, pendingPortfolio, submissionView, PortfolioError, CONTAINER, MAX_PENDING, MAX_HOLDINGS };
