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
// The dashboard is the owner's only (ADR-0014), so explanations keep every level and price. Each explanation comes in
// English and in Russian (the owner's language): the Russian keeps names, abbreviations and trading terms (stop-loss,
// ATR, EMA20, take-profit, the agents' names) in English, and text the dashboard didn't write stays as written.

const ENTRY = /^signal entry (\S+) rank (\d+):\s*(.*?)(?:;\s*stop (\S+),\s*target (\S+),\s*risk (\S+))?\s*$/i;
const EXIT = /^(signal|protective) exit (\S+) (stop|target|trend-down|[\w-]+):\s*(.*)$/i;
const DIRECT = /^Owner's direct sell(?:\.|:\s*(.*))?$/;

const EXIT_REASON = {
  stop: 'The price fell to the position\'s stop-loss',
  target: 'The price rose to the position\'s take-profit',
  'trend-down': 'The trend turned down (20-hour average crossed below the 50-hour average)',
};

const EXIT_REASON_RU = {
  stop: 'Цена опустилась до stop-loss позиции',
  target: 'Цена поднялась до take-profit позиции',
  'trend-down': 'Тренд развернулся вниз (20-часовая средняя пересекла 50-часовую сверху вниз)',
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

const GATE_RULES_RU = {
  'kill-switch': 'торговля остановлена (kill switch)',
  'symbol-allowlist': 'монеты нет в allowlist',
  'invalid-order': 'ордер составлен неверно',
  'pair-inactive': 'пара не торгуется на бирже',
  step: 'размер или цена не попадают в шаг биржи',
  'market-data': 'не было рыночных данных, чтобы оценить цену',
  'stale-market-data': 'рыночные данные слишком устарели, чтобы оценить цену',
  'price-band': 'limit-цена слишком далеко от рынка',
  'order-rate': 'слишком много ордеров за последнюю минуту',
  'exchange-max-size': 'уменьшен до максимума биржи',
  'exchange-min-size': 'меньше минимума биржи',
  'no-position': 'не было позиции, чтобы продать',
  'position-size': 'уменьшен до размера позиции',
  'drawdown-halt': 'лимит drawdown остановил новые покупки',
  'daily-loss': 'дневной лимит убытка остановил новые покупки',
  'max-order': 'ограничен лимитом на один ордер',
  'position-cap': 'ограничен лимитом позиции на монету',
  'allocation-cap': 'ограничен общей аллокацией',
  'placement-unresolved': 'предыдущий ордер ещё сверяется',
  'duplicate-client-order-id': 'этот ордер уже был отправлен',
  'pairs-unavailable': 'не удалось прочитать список пар биржи',
  'unknown-symbol': 'на бирже нет этой монеты',
  'portfolio-unavailable': 'не удалось оценить портфель',
  'state-unavailable': 'не удалось прочитать состояние engine',
  'post-only-would-cross': 'post-only ордер пересёк бы стакан',
  'exchange-refused': 'биржа его отклонила',
};

// Everything else the explanations say, per language. Values in parentheses are the records' own (levels, codes, text).
const WORDS = {
  en: {
    exitReason: EXIT_REASON,
    rules: GATE_RULES,
    trend: { up: 'up', down: 'down', flat: 'flat' },
    testEntry: (trend) => `Test entry: the strategy buys whatever passes the filters, whatever the trend (now ${trend})`,
    setup: (setup) => `Setup: ${setup}`,
    aboveEma: (x) => `Price is ${x}× the hourly range (ATR) above its 20-hour average (EMA20)`,
    moves: (hour, day) => `Moves: last hour ${hour}, last 24 hours ${day}`,
    stop: 'stop',
    target: 'target',
    riskLoss: (amount) => `about ${amount} lost if the stop is hit`,
    riskPlan: (parts) => `Risk plan: ${parts}`,
    verb: { buy: 'Buy', sell: 'Sell', other: 'Order' },
    placed: 'Placed',
    placedWith: (rule) => `Placed, ${rule}`,
    uncertain: "Sent; the exchange's answer was lost, so it is held until reconciled",
    rejected: (rule) => `Refused before sending: ${rule ?? 'see the reason'}`,
    failed: 'The exchange refused it',
    cancelled: 'Cancellation requested',
    notfound: 'Nothing to cancel: it had already filled or been cancelled',
    notown: "Not cancelled: the engine didn't place that order",
    status: (status) => `Status: ${status}`,
    cancel: (symbol) => `Cancel an open order${symbol ? ` for ${symbol}` : ''}`,
    entry: (verb, sym, rank) => `${verb} ${sym}: the scanner's #${rank} ranked coin had an entry signal`,
    exitSignal: (reason) => `Exit signal: ${reason}`,
    protective: 'The engine sends this sell itself, without an agent run, whenever a stop or target is reached',
    traderExit: 'The trader acted on the scanner\'s exit signal',
    detail: (detail) => `Detail: ${detail}`,
    direct: (verb, symbol) => `${verb} ${symbol}: your direct sell request`,
    yourNote: (note) => `Your note: ${note}`,
    directPoint: 'Requested from the dashboard; the engine sells at the bid, without an agent run',
    agreed: (verb, symbol) => `${verb} ${symbol}: the trader agreed to your request`,
    reasoning: (text) => `Trader's reasoning: ${text}`,
  },
  ru: {
    exitReason: EXIT_REASON_RU,
    rules: GATE_RULES_RU,
    trend: { up: 'вверх', down: 'вниз', flat: 'боковой' },
    testEntry: (trend) => `Тестовый вход: стратегия покупает всё, что проходит фильтры, при любом тренде (сейчас ${trend})`,
    setup: (setup) => `Сетап: ${setup}`,
    aboveEma: (x) => `Цена выше своей 20-часовой средней (EMA20) на ${x}× часового диапазона (ATR)`,
    moves: (hour, day) => `Движение: за последний час ${hour}, за 24 часа ${day}`,
    stop: 'stop',
    target: 'target',
    riskLoss: (amount) => `около ${amount} убытка, если сработает stop`,
    riskPlan: (parts) => `План риска: ${parts}`,
    verb: { buy: 'Покупка', sell: 'Продажа', other: 'Ордер' },
    placed: 'Размещён',
    placedWith: (rule) => `Размещён, ${rule}`,
    uncertain: 'Отправлен; ответ биржи потерян, поэтому ордер удерживается до сверки',
    rejected: (rule) => `Отклонён до отправки: ${rule ?? 'см. причину'}`,
    failed: 'Биржа его отклонила',
    cancelled: 'Запрошена отмена',
    notfound: 'Нечего отменять: ордер уже исполнен или отменён',
    notown: 'Не отменён: этот ордер разместил не engine',
    status: (status) => `Статус: ${status}`,
    cancel: (symbol) => `Отмена открытого ордера${symbol ? ` по ${symbol}` : ''}`,
    entry: (verb, sym, rank) => `${verb} ${sym}: у монеты №${rank} в рейтинге сканера появился сигнал входа`,
    exitSignal: (reason) => `Сигнал выхода: ${reason}`,
    protective: 'Engine сам отправляет эту продажу, без запуска агента, когда цена доходит до stop или target',
    traderExit: 'Trader действовал по сигналу выхода от сканера',
    detail: (detail) => `Подробности: ${detail}`,
    direct: (verb, symbol) => `${verb} ${symbol}: ваш запрос на прямую продажу`,
    yourNote: (note) => `Ваша заметка: ${note}`,
    directPoint: 'Запрошено из dashboard; engine продаёт по bid, без запуска агента',
    agreed: (verb, symbol) => `${verb} ${symbol}: trader согласился с вашим запросом`,
    reasoning: (text) => `Обоснование trader: ${text}`,
  },
};

function pctText(text) {
  return text && text !== 'n/a' ? text : null;
}

/** "uptrend pullback 0.31 ATR above EMA20, 1h +0.22%, 24h +0.38%" as readable points, or null if it doesn't match. */
function entryEvidence(reason, lang = 'en') {
  const w = WORDS[lang] ?? WORDS.en;
  const m = /^(.*?),?\s+(-?[\d.]+) ATR above EMA20,\s*1h (\S+),\s*24h (\S+)$/.exec(reason.trim());
  if (!m) return null;
  const setup = m[1].trim();
  const trend = /^test entry \(trend (up|down|flat)\)$/.exec(setup); // the engine's words only
  const points = [trend ? w.testEntry(w.trend[trend[1]]) : w.setup(setup), w.aboveEma(m[2])];
  const hour = pctText(m[3]);
  const day = pctText(m[4]);
  if (hour || day) points.push(w.moves(hour ?? 'n/a', day ?? 'n/a'));
  return points;
}

function pctFrom(level, price) {
  const l = Number(level);
  const p = Number(price);
  if (!Number.isFinite(l) || !Number.isFinite(p) || p <= 0) return null;
  const v = ((l - p) / p) * 100;
  return `${v > 0 ? '+' : ''}${v.toFixed(2)}%`;
}

function riskPoint(d, riskQuote, w) {
  const stop = d.stopLoss ?? null;
  const target = d.takeProfit ?? null;
  if (stop === null && target === null && riskQuote === null) return null;
  const price = d.approvedPrice ?? d.requestedPrice ?? null;
  const parts = [];
  if (stop !== null) parts.push(`${w.stop} ${stop}${pctFrom(stop, price) ? ` (${pctFrom(stop, price)})` : ''}`);
  if (target !== null) parts.push(`${w.target} ${target}${pctFrom(target, price) ? ` (${pctFrom(target, price)})` : ''}`);
  const currency = typeof d.symbol === 'string' ? d.symbol.split('-')[1] ?? '' : '';
  if (riskQuote !== null) parts.push(w.riskLoss(`${riskQuote}${currency ? ` ${currency}` : ''}`));
  return { text: w.riskPlan(parts.join(', ')), stop, target, riskQuote };
}

// The order's side as a verb. Never "bought" or "sold": a placed order may not have filled (fills are in Trades).
function verb(side, w) {
  if (side === 'buy') return w.verb.buy;
  if (side === 'sell') return w.verb.sell;
  return w.verb.other;
}

function rule(d, w) {
  return (typeof d.rule === 'string' && Object.hasOwn(w.rules, d.rule) ? w.rules[d.rule] : null) ?? d.rule ?? null;
}

/** What happened to the order, with the gate's own text as its reason. */
function outcome(d, w) {
  const why = d.reason ? ` (${d.reason})` : '';
  switch (d.status) {
    case 'placed': return d.rule && d.rule !== 'ok' ? `${w.placedWith(rule(d, w))}${why}` : w.placed;
    case 'uncertain': return w.uncertain;
    case 'rejected': return `${w.rejected(rule(d, w))}${why}`;
    case 'failed': return `${w.failed}${why}`;
    case 'cancelled': return w.cancelled;
    case 'notfound': return w.notfound;
    case 'notown': return w.notown;
    default: return d.status ? w.status(d.status) : null;
  }
}

function lowerFirst(text) {
  return `${text.charAt(0).toLowerCase()}${text.slice(1)}`;
}

/**
 * @param {{kind: string, actor: string, data?: object, summary?: string}} entry A journal "order" entry.
 * @param {'en'|'ru'} [lang] The language of the dashboard's own words; the records' text stays as written.
 * @returns {{headline: string, points: string[], risk: object|null, outcome: string|null, source: string, raw: string|null}}
 */
function explain(entry, lang = 'en') {
  const w = WORDS[lang] ?? WORDS.en;
  const d = entry.data ?? {};
  const tool = d.tool ?? null;
  const symbol = d.symbol ?? '';
  const rationale = typeof d.rationale === 'string' ? d.rationale.trim() : '';
  const raw = rationale || null;
  const out = outcome(d, w);

  if (tool === 'cancel_order') {
    return { headline: w.cancel(symbol), points: rationale ? [rationale] : [], risk: null, outcome: out, source: entry.actor ?? 'trader', raw };
  }

  const entryMatch = ENTRY.exec(rationale);
  if (entryMatch) {
    const [, sym, rank, reason, stop, target, risk] = entryMatch;
    return {
      headline: w.entry(verb('buy', w), sym, rank),
      points: entryEvidence(reason, lang) ?? [reason],
      risk: riskPoint({ ...d, stopLoss: d.stopLoss ?? stop ?? null, takeProfit: d.takeProfit ?? target ?? null }, risk ?? null, w),
      outcome: out,
      source: 'scanner signal',
      raw,
    };
  }

  const exitMatch = EXIT.exec(rationale);
  if (exitMatch) {
    const [, origin, sym, reason, detail] = exitMatch;
    const why = Object.hasOwn(w.exitReason, reason) ? w.exitReason[reason] : w.exitSignal(reason);
    return {
      headline: `${verb('sell', w)} ${sym}: ${lowerFirst(why)}`,
      points: [origin.toLowerCase() === 'protective' ? w.protective : w.traderExit, ...(detail ? [w.detail(detail)] : [])],
      risk: null,
      outcome: out,
      source: origin.toLowerCase() === 'protective' ? 'protective exit' : 'scanner signal',
      raw,
    };
  }

  if (tool === 'manual-direct' || DIRECT.test(rationale)) {
    const note = DIRECT.exec(rationale)?.[1];
    return {
      headline: w.direct(verb('sell', w), symbol),
      points: [note ? w.yourNote(note) : w.directPoint],
      risk: null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  if (tool === 'manual-agent') {
    return {
      headline: w.agreed(verb(d.side, w), symbol),
      points: rationale ? [w.reasoning(rationale)] : [],
      risk: d.side === 'buy' ? riskPoint(d, null, w) : null,
      outcome: out,
      source: 'owner request',
      raw,
    };
  }

  // Anything else: the agent's own words, as written.
  return {
    headline: `${verb(d.side, w)}${symbol ? ` ${symbol}` : ''}`,
    points: rationale ? [rationale] : [],
    risk: d.side === 'buy' ? riskPoint(d, null, w) : null,
    outcome: out,
    source: tool ?? entry.actor ?? 'unknown',
    raw,
  };
}

module.exports = { explain, entryEvidence, pctFrom, GATE_RULES, GATE_RULES_RU };
