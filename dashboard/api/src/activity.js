'use strict';
// Trading activity from executed trades: turnover (buy and sell volume) and trade count, by UTC day, week (Monday to
// Sunday) or month. A trade is one journal "fill" entry: the engine writes exactly one per order that filled at least in
// part, with the filled amount in the quote currency, so each executed order counts once and nothing pending, refused or
// cancelled unfilled is counted. Turnover is buy volume plus sell volume. Fees are reported apart and not included.
// Sums are exact: amounts are added as fixed-point integers (1e-8), never as floating point.

const SCALE = 100_000_000n;
const PERIODS = { day: 14, week: 12, month: 12 };

/** A decimal string or number as an integer number of 1e-8 units, or null when it isn't a plain decimal. */
function units(value) {
  const text = typeof value === 'number' ? value.toFixed(8) : String(value ?? '').trim();
  const match = /^(-?)(\d+)(?:\.(\d{1,8})\d*)?$/.exec(text);
  if (!match) return null;
  const whole = BigInt(match[2]) * SCALE + BigInt((match[3] ?? '').padEnd(8, '0'));
  return match[1] ? -whole : whole;
}

/** 1e-8 units as a decimal string with two places (rounded half up), for display. */
function money(u) {
  const cents = (u + (u >= 0n ? 500_000n : -500_000n)) / 1_000_000n;
  const sign = cents < 0n ? '-' : '';
  const abs = cents < 0n ? -cents : cents;
  return `${sign}${abs / 100n}.${String(abs % 100n).padStart(2, '0')}`;
}

function startOf(period, ms) {
  const d = new Date(ms);
  if (period === 'day') return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  if (period === 'week') {
    const day = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
    return day - ((d.getUTCDay() + 6) % 7) * 86_400_000; // back to Monday
  }
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1);
}

function next(period, start) {
  const d = new Date(start);
  if (period === 'day') return start + 86_400_000;
  if (period === 'week') return start + 7 * 86_400_000;
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1);
}

function isoDay(ms) {
  return new Date(ms).toISOString().slice(0, 10);
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function label(period, start) {
  const d = new Date(start);
  if (period === 'month') return `${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
  const end = new Date(next(period, start) - 86_400_000);
  const short = (x) => `${x.getUTCDate()} ${MONTHS[x.getUTCMonth()]}`;
  return period === 'day' ? short(d) : `${short(d)} – ${short(end)}`;
}

/** The periods to show, oldest first, the last one containing now. */
function periodsFor(period, now) {
  if (!PERIODS[period]) throw new RangeError('period must be day, week or month');
  const starts = [startOf(period, now)];
  while (starts.length < PERIODS[period]) {
    const prev = startOf(period, starts[0] - 1);
    starts.unshift(prev);
  }
  return starts.map((start) => ({ start, end: next(period, start) }));
}

/** The first UTC day whose journal the aggregation needs. */
function fromDay(period, now) {
  return isoDay(periodsFor(period, now)[0].start);
}

/**
 * @param {{at: string, data: object}[]} fills Journal "fill" entries (the entry's at and data).
 * @param {'day'|'week'|'month'} period
 * @param {number} now Milliseconds since the epoch.
 */
function activity(fills, period, now) {
  const buckets = periodsFor(period, now).map(({ start, end }) => ({
    start, end, trades: 0, buys: 0, sells: 0, manual: 0, buy: 0n, sell: 0n, fees: 0n,
  }));
  let currency = null;
  const skipped = { notQuote: 0, duplicate: 0 };
  const seen = new Set();
  for (const f of fills) {
    const at = Date.parse(f.at ?? '');
    const d = f.data ?? {};
    const quote = units(d.filledQuote);
    if (!Number.isFinite(at) || quote === null || quote <= 0n) continue; // nothing filled: not a trade
    // One fill entry per order; an order's id seen twice (a journal write retried) is still one trade.
    if (typeof d.clientOrderId === 'string' && d.clientOrderId) {
      if (seen.has(d.clientOrderId)) { skipped.duplicate += 1; continue; }
      seen.add(d.clientOrderId);
    }
    const bucket = buckets.find((b) => at >= b.start && at < b.end);
    if (!bucket) continue;
    const pairQuote = typeof d.symbol === 'string' ? d.symbol.split('-')[1] ?? null : null;
    currency ??= pairQuote;
    if (pairQuote && currency && pairQuote !== currency) { skipped.notQuote += 1; continue; } // another quote currency: not summed
    bucket.trades += 1;
    if (typeof d.source === 'string' && d.source.startsWith('manual-')) bucket.manual += 1;
    if (d.side === 'sell') { bucket.sells += 1; bucket.sell += quote; } else { bucket.buys += 1; bucket.buy += quote; }
    const fee = units(d.fee);
    if (fee !== null && (!d.feeCurrency || d.feeCurrency === currency)) bucket.fees += fee;
  }

  const rows = buckets.map((b) => ({
    from: isoDay(b.start),
    to: isoDay(b.end - 86_400_000),
    label: label(period, b.start),
    trades: b.trades,
    buys: b.buys,
    sells: b.sells,
    manual: b.manual,
    buyVolume: money(b.buy),
    sellVolume: money(b.sell),
    turnover: money(b.buy + b.sell),
    fees: money(b.fees),
  }));
  const current = rows[rows.length - 1];
  const previous = rows[rows.length - 2] ?? null;
  const total = buckets.reduce((t, b) => ({ trades: t.trades + b.trades, volume: t.volume + b.buy + b.sell }), { trades: 0, volume: 0n });
  return {
    period,
    amounts: true,
    currency: currency ?? 'EUR',
    periods: rows,
    current,
    previous,
    total: { trades: total.trades, turnover: money(total.volume), from: rows[0].from, to: current.to },
    skipped,
  };
}

/**
 * The public page's view: counts only. Amounts would give fill prices away (a day's buy volume over its one fill's size),
 * and the public page never shows a price; the owner gets the amounts from /api/owner/activity.
 */
function publicActivity(a) {
  const counts = ({ from, to, label, trades, buys, sells, manual }) => ({
    from, to, label, trades, buys, sells, manual, buyVolume: null, sellVolume: null, turnover: null, fees: null,
  });
  return {
    ...a,
    periods: a.periods.map(counts),
    current: counts(a.current),
    previous: a.previous ? counts(a.previous) : null,
    total: { ...a.total, turnover: null },
    amounts: false,
  };
}

module.exports = { activity, publicActivity, periodsFor, fromDay, units, money, PERIODS };
