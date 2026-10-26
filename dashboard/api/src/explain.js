'use strict';
// Plain-language explanations of trading decisions, built only from what the decision records say: the order's fields,
// the risk gate's verdict, and the rationale formats the engine and the strategy specs write. Text it doesn't recognise
// (an agent's own words) is shown as written, never paraphrased. Pure functions: tested on their own.
//
// Recognised rationale formats:
//   signal entry <SYM> rank <n>: <setup> <x> ATR above EMA20, 1h <±p%>, 24h <±p%>; stop <a>, target <b>, risk <r>
//   signal exit <SYM> <reason>: <detail>          (strategy specs, step "Exits")
//   protective exit <SYM> <reason>: <detail>      (the engine's own stop or target sell)
//   Owner's direct sell[: note]                   (manual request, ADR-0011)
//
// With reveal false (the public page) no price or level appears: stop and target show as set or not, and the entry's
// distance from them as a percentage only when revealed. The owner's view reveals everything.

const ENTRY = /^signal entry (\S+) rank (\d+):\s*(.*?)(?:;\s*stop (\S+),\s*target (\S+),\s*risk (\S+))?\s*$/i;
const EXIT = /^(signal|protective) exit (\S+) (stop|target|trend-down|[\w-]+):\s*(.*)$/i;
const DIRECT = /^Owner's direct sell(?:\.|:\s*(.*))?$/;

const EXIT_REASON = {
  stop: 'The price fell to the position\'s stop-loss',
  target: 'The price rose to the position\'s take-profit',
  'trend-down': 'The trend turned down (20-hour average crossed below the 50-hour average)',
};

// The rule codes the risk gate (src/Cix.Domain/Risk/RiskGate.cs) and the execution service write, in words.
const GATE_RULES = {
  'kill-switch': 'trading is halted (kill switch)',
  'symbol-allowlist': 'the coin is not on the allowlist',
  'invalid-order': 'the order was malformed',
  'pair-inactive': 'the pair is not tradable on the exchange',
  step: "the size or price didn't fit the exchange's increments",
  'market-data': 'there was no market data to price it',
  'stale-market-data': 'market data was too old to price it',
  'price-band': 'the limit price was too far from the market',
  'order-rate': 'too many orders in the last minute',
  'exchange-max-size': 'reduced to the exchange maximum',
  'exchange-min-size': 'below the exchange minimum',
  'no-position': 'there was no position to sell',
  'position-size': 'reduced to the position held',
  'drawdown-halt': 'the drawdown limit halts new buys',
  'daily-loss': 'the daily loss limit halts new buys',
  'max-order': 'capped at the per-order limit',
  'position-cap': 'capped at the per-coin position limit',
  'allocation-cap': 'capped at the total allocation',
  'placement-unresolved': 'an earlier order is still being reconciled',
  'duplicate-client-order-id': 'the same order was already sent',
  'pairs-unavailable': "the exchange's pair list couldn't be read",
  'unknown-symbol': 'the exchange doesn\'t list the coin',
  'portfolio-unavailable': "the portfolio couldn't be valued",
  'state-unavailable': "the engine's state couldn't be read",
  'post-only-would-cross': 'a post-only order would have crossed the book',
  'exchange-refused': 'the exchange refused it',
};

// The parts of a rationale the public page may show as written, checked strictly: the trader writes the rationale freely,
// so anything that doesn't look exactly like the engine's own format (a coin, a small rank, a ratio, a percentage) could
// be a price, and is masked. The owner sees everything.
const SAFE = {
  symbol: /^[A-Z0-9]{2,12}-[A-Z]{3,4}$/,
  rank: /^\d{1,2}$/,
  ratio: /^-?\d{1,2}(\.\d{1,2})?$/,
  move: /^[+-]?\d{1,2}(\.\d{1,2})?%$/,
};
const MASK = '…';

function pctText(text) {
  return text && text !== 'n/a' ? text : null;
}

/** The part as written when revealed or when it matches its strict shape; otherwise masked. */
function checked(part, shape, reveal) {
  if (part === undefined || part === null) return part;
  return reveal || SAFE[shape].test(part) ? part : MASK;
}

/** "uptrend pullback 0.31 ATR above EMA20, 1h +0.22%, 24h +0.38%" as readable points, or null if it doesn't match. */
function entryEvidence(reason, shown = (t) => t, reveal = true) {
  const m = /^(.*?),?\s+(-?[\d.]+) ATR above EMA20,\s*1h (\S+),\s*24h (\S+)$/.exec(reason.trim());
  if (!m) return null;
  const setup = m[1].trim();
  const trend = /^test entry \(trend (up|down|flat)\)$/.exec(setup); // the engine's words only
  const points = [
    trend ? `Test entry: the strategy buys whatever passes the filters, whatever the trend (now ${trend[1]})` : `Setup: ${shown(setup)}`,
    `Price is ${checked(m[2], 'ratio', reveal)}× the hourly range (ATR) above its 20-hour average (EMA20)`,
  ];
  const hour = checked(pctText(m[3]), 'move', reveal);
  const day = checked(pctText(m[4]), 'move', reveal);
  if (hour || day) points.push(`Moves: last hour ${hour ?? 'n/a'}, last 24 hours ${day ?? 'n/a'}`);
  return points;
}

function pctFrom(level, price) {
  const l = Number(level);
  const p = Number(price);
  if (!Number.isFinite(l) || !Number.isFinite(p) || p <= 0) return null;
  const v = ((l - p) / p) * 100;
  return `${v > 0 ? '+' : ''}${v.toFixed(2)}%`;
}

function riskPoint(d, reveal, riskQuote) {
  const stop = d.stopLoss ?? null;
  const target = d.takeProfit ?? null;
  if (stop === null && target === null && riskQuote === null) return null;
  if (!reveal) {
    return {
      text: stop !== null && target !== null ? 'Stop-loss and take-profit set' : stop !== null ? 'Stop-loss set, no take-profit' : 'No stop-loss',
    };
  }
  const price = d.approvedPrice ?? d.requestedPrice ?? null;
  const parts = [];
  if (stop !== null) parts.push(`stop ${stop}${pctFrom(stop, price) ? ` (${pctFrom(stop, price)})` : ''}`);
  if (target !== null) parts.push(`target ${target}${pctFrom(target, price) ? ` (${pctFrom(target, price)})` : ''}`);
  const currency = typeof d.symbol === 'string' ? d.symbol.split('-')[1] ?? '' : '';
  if (riskQuote !== null) parts.push(`about ${riskQuote}${currency ? ` ${currency}` : ''} lost if the stop is hit`);
  return { text: `Risk plan: ${parts.join(', ')}`, stop, target, riskQuote };
}

// The order's side as a verb. Never "bought" or "sold": a placed order may not have filled (fills are in Trades).
function verb(side) {
  if (side === 'buy') return 'Buy';
  if (side === 'sell') return 'Sell';
  return 'Order';
}

function rule(d) {
  return GATE_RULES[d.rule] ?? d.rule ?? null;
}

/** What happened to the order. reason is the gate's own text: shown to the owner, masked on the public page. */
function outcome(d, shown) {
  const why = d.reason ? ` (${shown(d.reason)})` : '';
  switch (d.status) {
    case 'placed': return d.rule && d.rule !== 'ok' ? `Placed, ${rule(d)}${why}` : 'Placed';
    case 'uncertain': return "Sent; the exchange's answer was lost, so it is held until reconciled";
    case 'rejected': return `Refused before sending: ${rule(d) ?? 'see the reason'}${why}`;
    case 'failed': return `The exchange refused it${why}`;
    case 'cancelled': return 'Cancellation requested';
    case 'notfound': return 'Nothing to cancel: it had already filled or been cancelled';
    case 'notown': return "Not cancelled: the engine didn't place that order";
    default: return d.status ? `Status: ${d.status}` : null;
  }
}

/**
 * @param {{kind: string, actor: string, data?: object, summary?: string}} entry A journal "order" entry.
 * @param {{reveal?: boolean, mask?: (s: string) => string}} options mask hides levels in free text on the public page.
 * @returns {{headline: string, points: string[], risk: object|null, outcome: string|null, source: string, raw: string|null}}
 */
function explain(entry, { reveal = false, mask = (s) => s } = {}) {
  const d = entry.data ?? {};
  const tool = d.tool ?? null;
  const symbol = d.symbol ?? '';
  const rationale = typeof d.rationale === 'string' ? d.rationale.trim() : '';
  const shown = (text) => (reveal ? text : mask(text));
  // A direct sell quotes the owner's note, and the trader's answer to a request may: neither is public.
  const raw = !rationale ? null : !reveal && tool === 'manual-direct' ? "Owner's direct sell."
    : !reveal && tool === 'manual-agent' ? "The trader's answer to an owner request." : shown(rationale);
  const out = outcome(d, shown);

  if (tool === 'cancel_order') {
    return { headline: `Cancel an open order${symbol ? ` for ${symbol}` : ''}`, points: rationale ? [shown(rationale)] : [], risk: null, outcome: out, source: entry.actor ?? 'trader', raw };
  }

  const entryMatch = ENTRY.exec(rationale);
  if (entryMatch) {
    const [, sym, rank, reason, stop, target, risk] = entryMatch;
    const evidence = entryEvidence(reason, shown, reveal);
    return {
      headline: `${verb('buy')} ${checked(sym, 'symbol', reveal)}: the scanner's #${checked(rank, 'rank', reveal)} ranked coin had an entry signal`,
      points: evidence ?? [shown(reason)],
      risk: riskPoint({ ...d, stopLoss: d.stopLoss ?? stop ?? null, takeProfit: d.takeProfit ?? target ?? null }, reveal, risk ?? null),
      outcome: out,
      source: 'scanner signal',
      raw,
    };
  }

  const exitMatch = EXIT.exec(rationale);
  if (exitMatch) {
    const [, origin, sym, reason, detail] = exitMatch;
    const why = EXIT_REASON[reason] ?? `Exit signal: ${reveal ? reason : 'see the trader\'s text'}`;
    return {
      headline: `${verb('sell')} ${checked(sym, 'symbol', reveal)}: ${why.charAt(0).toLowerCase()}${why.slice(1)}`,
      points: [origin.toLowerCase() === 'protective'
        ? 'The engine sends this sell itself, without an agent run, whenever a stop or target is reached'
        : 'The trader acted on the scanner\'s exit signal', ...(detail ? [reveal ? `Detail: ${detail}` : null] : [])].filter(Boolean),
      risk: null,
      outcome: out,
      source: origin.toLowerCase() === 'protective' ? 'protective exit' : 'scanner signal',
      raw,
    };
  }

  if (tool === 'manual-direct' || DIRECT.test(rationale)) {
    // The owner's note is for the owner and the trader: the public page doesn't show it.
    const note = reveal ? DIRECT.exec(rationale)?.[1] : null;
    return {
      headline: `${verb('sell')} ${symbol}: ${reveal ? 'your' : "the owner's"} direct sell request`,
      points: [note ? `Your note: ${note}` : 'Requested from the dashboard; the engine sells at the bid, without an agent run'],
      risk: null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  if (tool === 'manual-agent') {
    return {
      headline: `${verb(d.side)} ${symbol}: the trader agreed to ${reveal ? 'your' : "the owner's"} request`,
      points: !reveal ? ["The trader analysed the owner's request and decided to act on it"] : rationale ? [`Trader's reasoning: ${rationale}`] : [],
      risk: d.side === 'buy' ? riskPoint(d, reveal, null) : null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  // Anything else: the agent's own words, as written.
  return {
    headline: `${verb(d.side)}${symbol ? ` ${symbol}` : ''}`,
    points: rationale ? [shown(rationale)] : [],
    risk: d.side === 'buy' ? riskPoint(d, reveal, null) : null,
    outcome: out,
    source: tool ?? entry.actor ?? 'unknown',
    raw,
  };
}

module.exports = { explain, entryEvidence, pctFrom, GATE_RULES };
