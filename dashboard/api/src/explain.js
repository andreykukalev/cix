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
// The dashboard is the owner's only (ADR-0014), so explanations keep every level and price.

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

function pctText(text) {
  return text && text !== 'n/a' ? text : null;
}

/** "uptrend pullback 0.31 ATR above EMA20, 1h +0.22%, 24h +0.38%" as readable points, or null if it doesn't match. */
function entryEvidence(reason) {
  const m = /^(.*?),?\s+(-?[\d.]+) ATR above EMA20,\s*1h (\S+),\s*24h (\S+)$/.exec(reason.trim());
  if (!m) return null;
  const setup = m[1].trim();
  const trend = /^test entry \(trend (up|down|flat)\)$/.exec(setup); // the engine's words only
  const points = [
    trend ? `Test entry: the strategy buys whatever passes the filters, whatever the trend (now ${trend[1]})` : `Setup: ${setup}`,
    `Price is ${m[2]}× the hourly range (ATR) above its 20-hour average (EMA20)`,
  ];
  const hour = pctText(m[3]);
  const day = pctText(m[4]);
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

function riskPoint(d, riskQuote) {
  const stop = d.stopLoss ?? null;
  const target = d.takeProfit ?? null;
  if (stop === null && target === null && riskQuote === null) return null;
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

/** What happened to the order, with the gate's own text as its reason. */
function outcome(d) {
  const why = d.reason ? ` (${d.reason})` : '';
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
 * @returns {{headline: string, points: string[], risk: object|null, outcome: string|null, source: string, raw: string|null}}
 */
function explain(entry) {
  const d = entry.data ?? {};
  const tool = d.tool ?? null;
  const symbol = d.symbol ?? '';
  const rationale = typeof d.rationale === 'string' ? d.rationale.trim() : '';
  const raw = rationale || null;
  const out = outcome(d);

  if (tool === 'cancel_order') {
    return { headline: `Cancel an open order${symbol ? ` for ${symbol}` : ''}`, points: rationale ? [rationale] : [], risk: null, outcome: out, source: entry.actor ?? 'trader', raw };
  }

  const entryMatch = ENTRY.exec(rationale);
  if (entryMatch) {
    const [, sym, rank, reason, stop, target, risk] = entryMatch;
    return {
      headline: `${verb('buy')} ${sym}: the scanner's #${rank} ranked coin had an entry signal`,
      points: entryEvidence(reason) ?? [reason],
      risk: riskPoint({ ...d, stopLoss: d.stopLoss ?? stop ?? null, takeProfit: d.takeProfit ?? target ?? null }, risk ?? null),
      outcome: out,
      source: 'scanner signal',
      raw,
    };
  }

  const exitMatch = EXIT.exec(rationale);
  if (exitMatch) {
    const [, origin, sym, reason, detail] = exitMatch;
    const why = EXIT_REASON[reason] ?? `Exit signal: ${reason}`;
    return {
      headline: `${verb('sell')} ${sym}: ${why.charAt(0).toLowerCase()}${why.slice(1)}`,
      points: [origin.toLowerCase() === 'protective'
        ? 'The engine sends this sell itself, without an agent run, whenever a stop or target is reached'
        : 'The trader acted on the scanner\'s exit signal', ...(detail ? [`Detail: ${detail}`] : [])],
      risk: null,
      outcome: out,
      source: origin.toLowerCase() === 'protective' ? 'protective exit' : 'scanner signal',
      raw,
    };
  }

  if (tool === 'manual-direct' || DIRECT.test(rationale)) {
    const note = DIRECT.exec(rationale)?.[1];
    return {
      headline: `${verb('sell')} ${symbol}: your direct sell request`,
      points: [note ? `Your note: ${note}` : 'Requested from the dashboard; the engine sells at the bid, without an agent run'],
      risk: null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  if (tool === 'manual-agent') {
    return {
      headline: `${verb(d.side)} ${symbol}: the trader agreed to your request`,
      points: rationale ? [`Trader's reasoning: ${rationale}`] : [],
      risk: d.side === 'buy' ? riskPoint(d, null) : null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  // Anything else: the agent's own words, as written.
  return {
    headline: `${verb(d.side)}${symbol ? ` ${symbol}` : ''}`,
    points: rationale ? [rationale] : [],
    risk: d.side === 'buy' ? riskPoint(d, null) : null,
    outcome: out,
    source: tool ?? entry.actor ?? 'unknown',
    raw,
  };
}

module.exports = { explain, entryEvidence, pctFrom, GATE_RULES };
