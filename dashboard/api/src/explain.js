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
// English and in Russian (the owner's language), in the dashboard's own Russian terms (движок, трейдер, стоп-лосс,
// тейк-профит); only abbreviations (ATR, EMA20) and tickers stay in English. The engine's own sentences (the risk gate's
// reasons, the scanner's setups and exit details) are translated from their known formats; an agent's own words are shown
// in Russian when the agent wrote them in Russian too (rationaleRu), else as written.

const ENTRY = /^signal entry (\S+) rank (\d+):\s*(.*?)(?:;\s*stop (\S+),\s*target (\S+),\s*risk (\S+))?\s*$/i;
const EXIT = /^(signal|protective) exit (\S+) (stop|target|trend-down|[\w-]+):\s*(.*)$/i;
const DIRECT = /^Owner's direct sell(?:\.|:\s*(.*))?$/;

const EXIT_REASON = {
  stop: 'The price fell to the position\'s stop-loss',
  target: 'The price rose to the position\'s take-profit',
  'trend-down': 'The trend turned down (20-hour average crossed below the 50-hour average)',
};

const EXIT_REASON_RU = {
  stop: 'Цена опустилась до стоп-лосса позиции',
  target: 'Цена поднялась до тейк-профита позиции',
  'trend-down': 'Тренд развернулся вниз (быстрая средняя пересекла медленную сверху вниз)',
};

// The rule codes the risk gate (src/Cix.Domain/Risk/RiskGate.cs) and the execution service write, in words.
const GATE_RULES = {
  'kill-switch': 'trading is halted (kill switch)',
  'symbol-allowlist': 'the coin is not among the watched pairs',
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
  'placement-uncertain': "the exchange's answer was lost",
  'engine-lease': "this engine doesn't hold the trading lease",
  'strategy-daily-loss-stop': "the strategy's daily loss stop halts new buys",
  'strategy-positions-per-run': "the strategy's buys per run are used up",
  'strategy-cooldown': 'the strategy waits between buys',
  'strategy-order-size': "capped at the strategy's order size",
  'strategy-position-cap': "capped at the strategy's per-coin cap",
  'strategy-exposure-cap': "capped at the strategy's total exposure cap",
  'strategy-filter': "the strategy's filters leave the coin out now",
};

const GATE_RULES_RU = {
  'kill-switch': 'торговля остановлена (аварийный выключатель)',
  'symbol-allowlist': 'монеты нет среди отслеживаемых пар',
  'invalid-order': 'ордер составлен неверно',
  'pair-inactive': 'пара не торгуется на бирже',
  step: 'размер или цена не кратны шагу биржи',
  'market-data': 'нет рыночных данных для оценки цены',
  'stale-market-data': 'рыночные данные слишком устарели для оценки цены',
  'price-band': 'лимитная цена слишком далеко от рынка',
  'order-rate': 'слишком много ордеров за последнюю минуту',
  'exchange-max-size': 'уменьшен до максимума биржи',
  'exchange-min-size': 'меньше минимума биржи',
  'no-position': 'нет позиции для продажи',
  'position-size': 'уменьшен до размера позиции',
  'drawdown-halt': 'лимит просадки остановил новые покупки',
  'daily-loss': 'дневной лимит убытка остановил новые покупки',
  'max-order': 'ограничен лимитом на один ордер',
  'position-cap': 'ограничен лимитом позиции на монету',
  'allocation-cap': 'ограничен общим лимитом средств',
  'placement-unresolved': 'предыдущий ордер ещё сверяется',
  'duplicate-client-order-id': 'этот ордер уже был отправлен',
  'pairs-unavailable': 'не удалось прочитать список пар биржи',
  'unknown-symbol': 'на бирже нет этой монеты',
  'portfolio-unavailable': 'не удалось оценить портфель',
  'state-unavailable': 'не удалось прочитать состояние движка',
  'post-only-would-cross': 'post-only ордер исполнился бы сразу',
  'exchange-refused': 'биржа его отклонила',
  'placement-uncertain': 'ответ биржи потерян',
  'engine-lease': 'у этого движка нет права торговать',
  'strategy-daily-loss-stop': 'дневной стоп стратегии по убытку остановил новые покупки',
  'strategy-positions-per-run': 'покупки стратегии на этот запуск исчерпаны',
  'strategy-cooldown': 'стратегия выдерживает паузу между покупками',
  'strategy-order-size': 'ограничен размером ордера стратегии',
  'strategy-position-cap': 'ограничен лимитом стратегии на монету',
  'strategy-exposure-cap': 'ограничен общим лимитом вложений стратегии',
  'strategy-filter': 'фильтры стратегии сейчас отсеивают монету',
};

// The limits the gate's sentences name by code, and the strategy's by phrase, in Russian (genitive: "в пределах ...").
const LIMIT_NAMES_RU = { 'max-order': 'лимита на один ордер', 'position-cap': 'лимита позиции на монету', 'allocation-cap': 'общего лимита средств' };
function limitRu(what) {
  if (Object.hasOwn(LIMIT_NAMES_RU, what)) return LIMIT_NAMES_RU[what];
  let m = /^the strategy's order size of (\S+)$/.exec(what);
  if (m) return `размера ордера стратегии ${m[1]}`;
  m = /^the strategy's cap of (\S+) per coin$/.exec(what);
  if (m) return `лимита стратегии ${m[1]} на монету`;
  m = /^the strategy's total exposure cap of (\S+)$/.exec(what);
  if (m) return `общего лимита вложений стратегии ${m[1]}`;
  return null;
}

// The reasons the risk gate, the strategy's limits and the execution service write (src/Cix.Domain/Risk/RiskGate.cs,
// StrategyGate.cs, src/Cix.Engine/Execution/ExecutionService.cs), in Russian. A reason not listed here is left out of the
// Russian explanation, which then gives the rule alone: never English in the middle of Russian.
const REASONS_RU = [
  [/^The kill switch is engaged; no orders are allowed\.$/, () => 'Аварийный выключатель включён: ордера запрещены.'],
  [/^(\S+) is not in the allowed symbols\.$/, (m) => `${m[1]} нет среди торгуемых пар.`],
  [/^(\S+) is not among the watched pairs\.$/, (m) => `${m[1]} нет среди отслеживаемых пар.`],
  [/^(\S+) hasn't been scanned yet; the strategy's filters decide once it has\.$/, (m) => `${m[1]} ещё не проверена сканером; фильтры стратегии решат после проверки.`],
  [/^(\S+) is filtered out by the strategy \(([a-z-]+)\): (.+)$/, (m) => {
    const detail = require('./engineText').engineTextRu(m[3]); // the scanner's sentence; loaded here, as it loads this file
    return detail ? `${m[1]} отсеяна фильтром стратегии: ${detail}` : null;
  }],
  [/^The latest scan is from (\S+ \S+), too old for the strategy's filters; buys wait for a fresh one\.$/,
    (m) => `Последняя проверка сканера была ${m[1]}, это слишком давно для фильтров стратегии; покупки ждут новой.`],
  [/^(\S+) is watched because the account holds it, but the owner hasn't added it; the strategy buys it once the owner adds it on the Coins tab\.$/,
    (m) => `${m[1]} отслеживается, потому что есть на счёте, но владелец её не добавлял; стратегия будет её покупать, когда владелец добавит её на вкладке «Монеты».`],
  [/^The market scanner isn't running, so no coin passes the strategy's filters\.$/, () => 'Сканер рынка не работает, поэтому ни одна монета не проходит фильтры стратегии.'],
  [/^Size and limit price must be positive and the client order ID set\.$/, () => 'Размер и лимитная цена должны быть больше нуля, а ID ордера задан.'],
  [/^(\S+) is not tradable on the exchange\.$/, (m) => `${m[1]} не торгуется на бирже.`],
  [/^Size must be a multiple of (\S+) and price of (\S+)\.$/, (m) => `Размер должен быть кратен ${m[1]}, а цена — ${m[2]}.`],
  [/^No valid market data for this symbol\.$/, () => 'Нет актуальных рыночных данных по этой монете.'],
  [/^Market data is too old \(or from the future\) to price this order\.$/, () => 'Рыночные данные слишком старые, чтобы оценить ордер.'],
  [/^Limit price is ([\d.,-]+ ?%) from the reference price; the maximum is ([\d.,-]+ ?%)\.$/, (m) => `Лимитная цена отличается от рыночной на ${m[1]}; допустимо не больше ${m[2]}.`],
  [/^At most (\d+) orders per minute\.$/, (m) => `Не больше ${m[1]} ордеров в минуту.`],
  [/^Order reduced to the exchange maximum of (\S+)\.$/, (m) => `Ордер уменьшен до максимума биржи: ${m[1]}.`],
  [/^After (\S+), the order is below the exchange minimum \((\S+) base, (\S+) quote\)\.$/,
    (m) => `После ограничения (${GATE_RULES_RU[m[1]] ?? m[1]}) ордер меньше минимума биржи (${m[2]} в монете, ${m[3]} в валюте).`],
  [/^The order is below the exchange minimum \((\S+) base, (\S+) quote\)\.$/, (m) => `Ордер меньше минимума биржи (${m[1]} в монете, ${m[2]} в валюте).`],
  [/^Spot only: there is no position to sell\.$/, () => 'Только спот: позиции для продажи нет.'],
  [/^The position is too small to sell at this price\.$/, () => 'Позиция слишком мала, чтобы продать её по этой цене.'],
  [/^Sell reduced to the current position size\.$/, () => 'Продажа уменьшена до текущего размера позиции.'],
  [/^Drawdown is ([\d.,-]+ ?%); new buys are halted until the owner resumes trading\.$/, (m) => `Просадка ${m[1]}: новые покупки остановлены, пока владелец не возобновит торговлю.`],
  [/^The daily loss limit is reached; new buys are halted until tomorrow \(UTC\)\.$/, () => 'Достигнут дневной лимит убытка: новые покупки остановлены до завтра (UTC).'],
  [/^No room left under (.+)\.$/, (m) => (limitRu(m[1]) ? `Не осталось места в пределах ${limitRu(m[1])}.` : null)],
  [/^(?:Remaining room|The room left) under (.+) is too small for one unit\.$/, (m) => (limitRu(m[1]) ? `Оставшегося места в пределах ${limitRu(m[1])} не хватает даже на минимальный размер.` : null)],
  [/^Order reduced to (\S+)(?: quote)? to stay within (.+)\.$/, (m) => (limitRu(m[2]) ? `Ордер уменьшен до ${m[1]}, чтобы остаться в пределах ${limitRu(m[2])}.` : null)],
  [/^Within all limits\.$/, () => 'В пределах всех лимитов.'],
  [/^Within the strategy's limits\.$/, () => 'В пределах лимитов стратегии.'],
  [/^Sells aren't limited by the strategy\.$/, () => 'Стратегия не ограничивает продажи.'],
  [/^Today's loss has reached the strategy's daily stop of (\S+); no new buys until tomorrow \(UTC\)\.$/,
    (m) => `Сегодняшний убыток достиг дневного стопа стратегии (${m[1]}): новых покупок не будет до завтра (UTC).`],
  [/^This run already placed (\d+) buy\(s\); the strategy allows (\d+) per run\.$/, (m) => `За этот запуск уже размещено покупок: ${m[1]}; стратегия разрешает ${m[2]} за запуск.`],
  [/^The strategy waits (\d+) minutes between buys; the last was (\d+) minutes ago\.$/, (m) => `Стратегия выдерживает ${m[1]} мин. между покупками; последняя была ${m[2]} мин. назад.`],
  [/^An earlier order with this client order ID has an unknown outcome; it must be reconciled first\.$/, () => 'Исход предыдущего ордера с этим ID неизвестен: сначала нужна сверка.'],
  [/^This client order ID was already sent; use a new one for a new order\.$/, () => 'Ордер с этим ID уже отправлялся; для нового ордера нужен новый ID.'],
  [/^The exchange's trading rules could not be loaded\.$/, () => 'Не удалось загрузить торговые правила биржи.'],
  [/^The exchange doesn't list (\S+)\.$/, (m) => `На бирже нет ${m[1]}.`],
  [/^The engine could not record the order before sending it; nothing was sent\.$/, () => 'Движок не смог записать ордер перед отправкой, поэтому ничего не отправлено.'],
  [/^This engine doesn't hold the engine lease/, () => 'У этого движка нет права торговать в этом режиме (возможно, торгует другой движок); ничего не отправлено.'],
  [/^The order would have filled at once, so the post-only order was rejected\.$/, () => 'Ордер исполнился бы сразу, поэтому post-only ордер отклонён.'],
  [/^The exchange may or may not have the order; it counts as open until reconciliation finds out\.$/, () => 'Неизвестно, принят ли ордер биржей; до сверки он считается открытым.'],
  [/^The exchange refused the order\.$/, () => 'Биржа отклонила ордер.'],
];

/** The engine's reason in Russian, or null when its format isn't known. */
function reasonRu(reason) {
  const text = String(reason ?? '').trim();
  for (const [re, ru] of REASONS_RU) {
    const m = re.exec(text);
    if (m) return ru(m);
  }
  return null;
}

/** The scanner's entry setup ("uptrend pullback") in Russian. */
const SETUPS_RU = { 'uptrend pullback': 'откат в восходящем тренде' };

/** The scanner's exit detail (src/Cix.Domain/Scanning/MarketScanner.cs) in Russian, or null. */
function exitDetailRu(detail) {
  let m = /^bid (\S+) at or below stop (\S+)$/.exec(detail);
  if (m) return `bid ${m[1]} на уровне стоп-лосса ${m[2]} или ниже`;
  m = /^bid (\S+) at or above target (\S+)$/.exec(detail);
  if (m) return `bid ${m[1]} на уровне тейк-профита ${m[2]} или выше`;
  if (detail === 'the fast EMA crossed below the slow EMA') return 'быстрая EMA пересекла медленную сверху вниз';
  return null;
}


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
    reason: (text) => text,
  },
  ru: {
    exitReason: EXIT_REASON_RU,
    rules: GATE_RULES_RU,
    trend: { up: 'вверх', down: 'вниз', flat: 'боковой' },
    testEntry: (trend) => `Тестовый вход: стратегия покупает всё, что проходит фильтры, при любом тренде (сейчас тренд ${trend})`,
    setup: (setup) => `Сетап: ${SETUPS_RU[setup] ?? setup}`,
    aboveEma: (x) => `Цена выше своей 20-часовой средней (EMA20) на ${x}× часового диапазона (ATR)`,
    moves: (hour, day) => `Движение: за последний час ${hour}, за 24 часа ${day}`,
    stop: 'стоп-лосс',
    target: 'тейк-профит',
    riskLoss: (amount) => `около ${amount} убытка, если сработает стоп-лосс`,
    riskPlan: (parts) => `План риска: ${parts}`,
    verb: { buy: 'Покупка', sell: 'Продажа', other: 'Ордер' },
    placed: 'Размещён',
    placedWith: (rule) => `Размещён, ${rule}`,
    uncertain: 'Отправлен; ответ биржи потерян, поэтому ордер удерживается до сверки',
    rejected: (rule) => `Отклонён до отправки: ${rule ?? 'см. причину'}`,
    failed: 'Биржа его отклонила',
    cancelled: 'Запрошена отмена',
    notfound: 'Нечего отменять: ордер уже исполнен или отменён',
    notown: 'Не отменён: этот ордер разместил не движок',
    status: (status) => `Статус: ${status}`,
    cancel: (symbol) => `Отмена открытого ордера${symbol ? ` по ${symbol}` : ''}`,
    entry: (verb, sym, rank) => `${verb} ${sym}: у монеты №${rank} в рейтинге сканера появился сигнал на вход`,
    exitSignal: (reason) => `Сигнал на выход: ${reason}`,
    protective: 'Движок сам отправляет эту продажу, без запуска агента, когда цена доходит до стоп-лосса или тейк-профита',
    traderExit: 'Трейдер действовал по сигналу сканера на выход',
    detail: (detail) => `Подробности: ${exitDetailRu(detail) ?? detail}`,
    direct: (verb, symbol) => `${verb} ${symbol}: ваш запрос на прямую продажу`,
    yourNote: (note) => `Ваша заметка: ${note}`,
    directPoint: 'Запрошено из панели; движок продаёт по bid, без запуска агента',
    agreed: (verb, symbol) => `${verb} ${symbol}: трейдер согласился с вашим запросом`,
    reasoning: (text) => `Обоснование трейдера: ${text}`,
    reason: reasonRu,
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

/** What happened to the order, with the gate's own text as its reason (in Russian when its format is known, else left out). */
function outcome(d, w) {
  const said = d.reason ? w.reason(d.reason) : null;
  const why = said ? ` (${said})` : '';
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
  // The agent's own words: in Russian when it wrote them in Russian too. Shown once, in the points: the "Technical" fold
  // (raw) holds the engine's formats the explanation has read for you, or the English behind the agent's Russian.
  const own = (lang === 'ru' && typeof d.rationaleRu === 'string' && d.rationaleRu.trim()) || rationale;
  const raw = rationale || null;
  // The agent wrote its Russian apart from its English: the English, which the journal keeps, stays one click away so the
  // owner can always check the one against the other.
  const original = own !== rationale ? raw : null;
  const out = outcome(d, w);

  if (tool === 'cancel_order') {
    return { headline: w.cancel(symbol), points: own ? [own] : [], risk: null, outcome: out, source: entry.actor ?? 'trader', raw: original };
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
      raw: null, // the owner's own note, shown above
    };
  }

  if (tool === 'manual-agent') {
    return {
      headline: w.agreed(verb(d.side, w), symbol),
      points: own ? [w.reasoning(own)] : [],
      risk: d.side === 'buy' ? riskPoint(d, null, w) : null,
      outcome: out,
      source: 'owner request',
      raw: original,
    };
  }

  // Anything else: the agent's own words, as written.
  return {
    headline: `${verb(d.side, w)}${symbol ? ` ${symbol}` : ''}`,
    points: own ? [own] : [],
    risk: d.side === 'buy' ? riskPoint(d, null, w) : null,
    outcome: out,
    source: tool ?? entry.actor ?? 'unknown',
    raw: original,
  };
}

module.exports = { explain, entryEvidence, pctFrom, reasonRu, exitDetailRu, GATE_RULES, GATE_RULES_RU, REASONS_RU };
