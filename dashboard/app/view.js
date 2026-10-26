'use strict';
// What the page shows, worked out from the API's answers: pure functions, no DOM, so they are tested in Node
// (dashboard/api/test/view.test.js) and used by app.js in the browser as window.CixView.

(function expose(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.CixView = api;
}(typeof window !== 'undefined' ? window : globalThis, () => {
  const SYMBOL = /^[A-Z0-9]{2,12}-[A-Z]{3,4}$/;

  /** A move as an arrow, a signed percentage and words for screen readers; flat under 0.05%. */
  function change(fraction, period) {
    if (fraction === null || fraction === undefined || !Number.isFinite(Number(fraction))) {
      return { text: '–', dir: 'none', label: `no ${period} change yet` };
    }
    const pct = Number(fraction) * 100;
    const rounded = Math.abs(pct) < 0.05 ? 0 : pct;
    const dir = rounded > 0 ? 'up' : rounded < 0 ? 'down' : 'flat';
    const arrow = { up: '▲', down: '▼', flat: '■' }[dir];
    const sign = rounded > 0 ? '+' : '';
    return { text: `${arrow} ${sign}${rounded.toFixed(rounded === 0 ? 1 : Math.abs(rounded) < 10 ? 2 : 1)}%`, dir, label: `${dir === 'flat' ? 'flat' : `${dir} ${Math.abs(rounded).toFixed(2)}%`} over ${period}` };
  }

  /**
   * One row per coin: every coin in the latest scan, plus anything held that the scan didn't list, held ones first.
   * The moves come from the scan (the reference time is the scan's); holdings say what is held.
   */
  function coins(view) {
    const market = Array.isArray(view?.briefing?.market) ? view.briefing.market : [];
    const holdings = new Map((view?.portfolio?.holdings ?? []).filter((h) => SYMBOL.test(h?.symbol ?? '')).map((h) => [h.symbol, h]));
    const rows = market.filter((r) => SYMBOL.test(r?.symbol ?? '')).map((r) => ({
      symbol: r.symbol,
      quote: r.symbol.split('-')[1],
      rank: r.rank ?? null,
      signal: r.signal ?? null,
      note: r.note ?? null,
      hour: change(r.return1h, '1 hour'),
      day: change(r.return24h, '24 hours'),
      held: holdings.has(r.symbol),
      value: holdings.get(r.symbol)?.valueQuote ?? null,
      inScan: true,
    }));
    for (const [symbol, h] of holdings) {
      if (!rows.some((r) => r.symbol === symbol)) {
        rows.push({ symbol, quote: symbol.split('-')[1], rank: null, signal: null, note: 'not in the latest scan', hour: change(null, '1 hour'), day: change(null, '24 hours'), held: true, value: h.valueQuote ?? null, inScan: false });
      }
    }
    return rows.sort((a, b) => (Number(b.held) - Number(a.held)) || ((a.rank ?? 1e9) - (b.rank ?? 1e9)) || a.symbol.localeCompare(b.symbol));
  }

  const PORTIONS = { '0.25': '25%', '0.5': 'half', '1': 'all' };

  /**
   * The request the page sends for a choice in the dialog, or an error message. id is a fresh UUID per click: sending the
   * same id again (a retry) is the same request, never a second one.
   */
  function requestBody(choice, id) {
    const { side, mode, symbol } = choice;
    if (!SYMBOL.test(symbol ?? '')) return { error: 'Pick a coin.' };
    if (side === 'sell') {
      if (!(String(choice.portion) in PORTIONS)) return { error: 'Pick how much to sell.' };
      if (mode !== 'direct' && mode !== 'agent') return { error: 'Pick how to sell.' };
      return { body: { id, side, mode, symbol, portion: String(choice.portion), note: cleanNote(choice.note), tradingMode: 'paper' } };
    }
    if (side === 'buy') {
      const amount = String(choice.quoteAmount ?? '').trim().replace(',', '.');
      if (!/^\d+(\.\d{1,2})?$/.test(amount) || !(Number(amount) > 0)) return { error: 'Enter an amount to buy for, like 25.' };
      if (Number(amount) > 1000) return { error: 'At most 1000 per request.' };
      return { body: { id, side, mode: 'agent', symbol, quoteAmount: amount, note: cleanNote(choice.note), tradingMode: 'paper' } };
    }
    return { error: 'Pick buy or sell.' };
  }

  function cleanNote(note) {
    const text = String(note ?? '').replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim();
    return text ? Array.from(text).slice(0, 280).join('') : undefined;
  }

  /** What the confirmation says will happen, in words. */
  function describe(body) {
    const quote = body.symbol.split('-')[1];
    if (body.side === 'sell') {
      const how = body.mode === 'direct'
        ? 'The engine sells at the bid now, without asking the trader.'
        : 'The trader analyses it first and may decline or sell less.';
      return `Sell ${PORTIONS[body.portion]} of your ${body.symbol} position (paper). ${how} The risk gate checks the order either way.`;
    }
    return `Ask the trader to buy ${body.symbol} for ${body.quoteAmount} ${quote} (paper). It decides, sets a stop-loss, and may decline; the risk gate checks the order.`;
  }

  const STATUS = {
    new: { label: 'Sent, waiting for the engine', tone: 'pending' },
    'awaiting-agent': { label: 'With the trader', tone: 'pending' },
    executing: { label: 'Sending the order', tone: 'pending' },
    placed: { label: 'Order placed, waiting for the fill', tone: 'pending' },
    unconfirmed: { label: "Sent; the exchange's answer was lost, being reconciled", tone: 'warn' },
    filled: { label: 'Filled', tone: 'ok' },
    closed: { label: 'Closed', tone: 'warn' },
    declined: { label: 'Declined by the trader', tone: 'warn' },
    rejected: { label: 'Refused', tone: 'bad' }, // by the engine or the risk gate: the rule says which
    failed: { label: 'Refused by the exchange', tone: 'bad' },
    invalid: { label: 'Not valid', tone: 'bad' },
    duplicate: { label: 'Duplicate of a request in progress', tone: 'warn' },
    expired: { label: 'Expired', tone: 'warn' },
  };

  /** Where a request stands, for the page: a label, a tone, whether it's over, and the engine's or trader's words. */
  function requestState(item) {
    const known = STATUS[item?.status] ?? { label: item?.status ?? 'unknown', tone: 'warn' };
    const o = item?.outcome ?? {};
    const s = item?.status === 'rejected' && o.rule ? { ...known, label: `Refused (${o.rule})` } : known;
    const detail = [o.rationale, o.reason].filter((x) => typeof x === 'string' && x.trim()).join(' · ') || null;
    const filled = item?.status === 'filled' && o.filledSize ? `${o.filledSize}${o.averagePrice ? ` at ${o.averagePrice}` : ''}` : null;
    return { label: s.label, tone: s.tone, final: Boolean(item?.final), detail, filled };
  }

  function what(item) {
    if (item.side === 'sell') return `Sell ${PORTIONS[item.portion] ?? item.portion ?? ''} ${item.symbol ?? ''}`.replace(/\s+/g, ' ').trim();
    return `Buy ${item.symbol ?? ''} for ${item.quoteAmount ?? '?'} ${String(item.symbol ?? '').split('-')[1] ?? ''}`.trim();
  }

  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  /** A short axis label from a period's first day: "13 Oct" for days and weeks (the Monday), "Oct" for months. */
  function tick(from, period) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(from ?? '');
    if (!m) return '';
    const month = MONTHS[Number(m[2]) - 1];
    return period === 'month' ? (m[2] === '01' ? `${month} ${m[1]}` : month) : `${Number(m[3])} ${month}`;
  }

  /** Bars for the activity chart: buy and sell volume stacked per period, scaled to the largest turnover. */
  function chart(periods, { width = 640, height = 160, gap = 6, labelEvery = 1, period = 'day' } = {}) {
    const list = Array.isArray(periods) ? periods : [];
    // Amounts for the owner; the public page gets counts only (amounts would give prices away), so it charts trades.
    const amounts = list.length > 0 && list.every((p) => p.turnover !== null && p.turnover !== undefined);
    const buyOf = (p) => (amounts ? p.buyVolume : p.buys);
    const sellOf = (p) => (amounts ? p.sellVolume : p.sells);
    const max = Math.max(0, ...list.map((p) => (amounts ? Number(p.turnover) : Number(p.buys) + Number(p.sells)) || 0));
    const slot = list.length ? width / list.length : width;
    const bar = Math.max(2, slot - gap);
    const scale = (v) => (max > 0 ? ((Number(v) || 0) / max) * height : 0);
    return {
      width, height, max, amounts,
      bars: list.map((p, i) => {
        const buy = scale(buyOf(p));
        const sell = scale(sellOf(p));
        return {
          x: i * slot + gap / 2, width: bar,
          buy: { y: height - buy, height: buy },
          sell: { y: height - buy - sell, height: sell },
          label: i % labelEvery === 0 || i === list.length - 1 ? tick(p.from, period) : '',
          title: amounts
            ? `${p.label}: ${p.trades} trade${p.trades === 1 ? '' : 's'}, bought ${p.buyVolume}, sold ${p.sellVolume}`
            : `${p.label}: ${p.trades} trade${p.trades === 1 ? '' : 's'}, ${p.buys} buy${p.buys === 1 ? '' : 's'}, ${p.sells} sell${p.sells === 1 ? '' : 's'}`,
        };
      }),
    };
  }

  /** This period against the previous one, as a signed percentage, or a word when there's nothing to compare. */
  function versus(current, previous) {
    const now = Number(current);
    const before = Number(previous);
    if (!Number.isFinite(now) || !Number.isFinite(before)) return { text: '', dir: 'none' };
    if (before === 0) return now === 0 ? { text: 'same as before', dir: 'flat' } : { text: 'none before', dir: 'up' };
    const pct = ((now - before) / before) * 100;
    return { text: `${pct > 0 ? '▲ +' : pct < 0 ? '▼ ' : '■ '}${pct.toFixed(0)}% vs previous`, dir: pct > 0 ? 'up' : pct < 0 ? 'down' : 'flat' };
  }

  /** Whether the engine takes requests now, from the dashboard's engine section: a reason when it doesn't, else null. */
  function requestsBlocked(engine) {
    if (!engine || engine.state !== 'running') return 'The engine isn\'t running, so it can\'t take requests now.';
    const m = engine.manualRequests;
    if (!m) return 'This engine doesn\'t report manual requests (an older version?).';
    if (m.state === 'off') return 'Manual requests are switched off in the engine.';
    if (m.state !== 'ok') return 'The engine can\'t read requests right now (the requests container may be missing).';
    return null;
  }

  /** Who is signed in, from /.auth/me: owner only with a Microsoft sign-in and the owner role. */
  function viewer(me) {
    const p = me?.clientPrincipal ?? null;
    if (!p) return { signedIn: false, owner: false, name: null };
    const roles = Array.isArray(p.userRoles) ? p.userRoles : [];
    return { signedIn: true, owner: p.identityProvider === 'aad' && roles.includes('owner'), name: p.userDetails ?? null };
  }

  return { change, coins, requestBody, describe, requestState, requestsBlocked, what, chart, tick, versus, viewer, STATUS, PORTIONS };
}));
