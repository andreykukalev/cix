'use strict';
// The engine's own sentences in Russian, for the owner's dashboard: why a request or a pair change ended as it did, why the
// scanner gave a coin no entry or filtered it out, and the risk gate's reasons (explain.js). The engine writes them in
// English from fixed formats (src/Cix.Engine/Manual/ManualRequestService.cs, Pairs/TradablePairs.cs, AgentRuns/,
// src/Cix.Domain/Scanning/MarketScanner.cs, Risk/TradableSymbols.cs); each format is read here and said again in Russian,
// with figures, tickers and abbreviations (ATR, EMA, UTC) as they are. A sentence in no known format gets no Russian (null),
// and the page shows the English: never a guess. Pure functions: tested on their own (test/engineText.test.js).

const { reasonRu } = require('./explain');

const TREND_RU = { up: 'вверх', down: 'вниз', flat: 'боковой', unknown: 'неизвестен' };
const SIDE_RU = { buy: 'покупку', sell: 'продажу' };
// The engine's halt categories (src/Cix.Engine/Status/EngineStatus.cs, HaltCategoryFor), as a refused Resume names them.
const HALT_RU = {
  'Paused by the owner.': 'приостановлена владельцем.',
  'Reconciliation found the account changed outside the engine.': 'сверка обнаружила изменения на счёте, сделанные не движком.',
  'A safety limit was reached (drawdown, daily loss or stale market data).': 'достигнут предел безопасности (просадка, дневной убыток или устаревшие рыночные данные).',
  'Too many orders in a row were rejected by the risk gate.': 'риск-контроль отклонил слишком много ордеров подряд.',
  'Critical: drawdown reached the hard ceiling. Nothing new until you resume; stop-losses keep selling.': 'критическое состояние: просадка достигла жёсткого потолка. Ничего нового до возобновления; стоп-лоссы продолжают продавать.',
  'Waiting for the owner to confirm before trading starts.': 'ждёт подтверждения владельца перед началом торговли.',
  "The saved trading pairs couldn't all be restored; check holdings before resuming.": 'не все сохранённые торговые пары удалось восстановить; проверьте позиции перед возобновлением.',
  'Halted.': 'остановлена.',
};

/** [pattern, Russian]: the Russian is a string, or a function of the match. Checked in order; the first match wins. */
const FORMATS = [
  // --- Owner requests (ManualRequestService.cs) ---------------------------------------------------------------------------
  [/^The trader didn't answer within (\d+) minutes\.$/, (m) => `Трейдер не ответил за ${m[1]} мин.`],
  [/^The request was (\d+) minutes old when the engine saw it \(at most (\d+)\); not acted on\. Send it again if it still applies\.$/,
    (m) => `Когда движок увидел запрос, ему было ${m[1]} мин. (допустимо не больше ${m[2]}); он не выполнен. Отправьте его ещё раз, если он ещё нужен.`],
  [/^At most (\d+) manual requests a day \(ManualRequests:MaxPerDay\)\.$/, (m) => `Не больше ${m[1]} ручных запросов в день (ManualRequests:MaxPerDay).`],
  [/^Another (buy|sell) request for (\S+) is still in progress\.$/, (m) => `Другой запрос на ${SIDE_RU[m[1]]} ${m[2]} ещё выполняется.`],
  [/^The active strategy doesn't let the trader place orders, so it can't act on requests\.$/,
    'Текущая стратегия не разрешает трейдеру размещать ордера, поэтому он не может выполнять запросы.'],
  [/^Received from (\S+)\.$/, (m) => `Получен от: ${m[1] === 'owner' ? 'владелец' : m[1]}.`],
  [/^Sent to the trader to analyse\.$/, 'Передан трейдеру на анализ.'],
  [/^Owner's direct sell\.$/, 'Прямая продажа по запросу владельца.'],
  [/^Owner's direct sell: (.+)$/, (m) => `Прямая продажа по запросу владельца: ${m[1]}`],
  [/^No current price for (\S+?)(?:; try again shortly or decline)?\.$/, (m) => `Нет текущей цены ${m[1]}.`],
  [/^The portfolio can't be valued right now\.$/, 'Сейчас не удаётся оценить портфель.'],
  [/^There is no sellable (\S+) position \(an open sell order may hold it\)\.$/,
    (m) => `Нет позиции ${m[1]}, которую можно продать (её может удерживать открытый ордер на продажу).`],
  [/^New buys are paused by the market view \(risk-off\) until (.+) UTC\.$/, (m) => `Новые покупки приостановлены взглядом на рынок (risk-off) до ${m[1]} UTC.`],
  [/^The resulting order size is zero\.$/, 'Размер ордера получился нулевым.'],
  [/^portion must be more than 0 and at most what the owner asked \((\S+)\)\.$/, (m) => `Доля должна быть больше 0 и не больше запрошенной владельцем (${m[1]}).`],
  [/^stopLoss and takeProfit apply to buys only\.$/, 'Стоп-лосс и тейк-профит задаются только для покупок.'],
  [/^A buy takes no portion: the owner set the amount\.$/, 'У покупки нет доли: сумму задал владелец.'],
  [/^A buy needs a stopLoss at least ([\d.,-]+ ?%) below the ask \((\S+)\)\.$/, (m) => `Покупке нужен стоп-лосс как минимум на ${m[1]} ниже ask (${m[2]}).`],
  [/^takeProfit must be at least ([\d.,-]+ ?%) above the ask \((\S+)\) and at most (\S+)\.$/, (m) => `Тейк-профит должен быть как минимум на ${m[1]} выше ask (${m[2]}) и не больше ${m[3]}.`],
  [/^Filled (\S+) at (\S+)\.$/, (m) => `Исполнено ${m[1]} по ${m[2]}.`],
  [/^The order ended without a fill\.$/, 'Ордер закрылся без исполнения.'],
  [/^The engine restarted while handling this request; the order is being reconciled\.$/, 'Движок перезапустился во время обработки запроса; ордер сверяется.'],
  [/^See the journal\.$/, 'Подробности в журнале.'],
  [/^The order didn't fill at once; cancelled so it can't hold the position\.$/, 'Ордер не исполнился сразу; он отменён, чтобы не удерживать позицию.'],
  [/^The engine has no record of how this order ended/,
    'У движка нет записи о том, чем закончился этот ордер (он остановился во время отправки или потерял ордер из виду). Проверьте историю ордеров на бирже, прежде чем отправлять запрос снова.'],

  // --- Pause and Resume (TradingControlService.cs) -------------------------------------------------------------------------
  [/^Trading halted; (\d+) open order\(s\) cancelled\.$/, (m) => `Торговля остановлена; отменено открытых ордеров: ${m[1]}.`],
  [/^Trading halted; (\d+) open order\(s\) cancelled, (\d+) couldn't be: check the exchange\.$/,
    (m) => `Торговля остановлена; отменено открытых ордеров: ${m[1]}, не удалось отменить: ${m[2]}. Проверьте биржу.`],
  [/^Trading was already halted; the halt and its reason stay as they were\.$/, 'Торговля уже была остановлена; остановка и её причина не изменились.'],
  [/^Trading wasn't halted; nothing to resume\.$/, 'Торговля не была остановлена; возобновлять нечего.'],
  [/^Trading resumed\. The risk limits still apply, and the automatic checks can halt it again\.$/,
    'Торговля возобновлена. Лимиты риска продолжают действовать, и автоматические проверки могут снова её остановить.'],
  [/^The halt changed after the page showed it: (.+) Check why on the dashboard, then press Resume again if you still want to\.$/,
    (m) => `Остановка изменилась после того, как страница её показала: ${HALT_RU[m[1]] ?? m[1]} Проверьте причину на панели и нажмите «Возобновить» ещё раз, если нужно.`],
  [/^This halt has no start time, so the engine can't tell it is the one you saw\. Resume it with the operator tool resume_trading\.$/,
    'У этой остановки нет времени начала, поэтому движок не может проверить, что это та, которую вы видели. Снимите её инструментом оператора resume_trading.'],
  [/^At most (\d+) Pause or Resume requests a day reach the engine; this one didn't\. To stop live trading now, see docs\/live-trading\.md\.$/,
    (m) => `До движка доходит не больше ${m[1]} запросов на паузу или возобновление в день; этот не дошёл. Чтобы срочно остановить реальную торговлю, смотрите docs/live-trading.md.`],
  [/^A Resume request needs haltSince: the start of the halt the page showed\.$/, 'Запросу на возобновление нужен haltSince: время начала остановки, которую показала страница.'],
  [/^A newer Pause or Resume request replaced this one\.$/, 'Его заменил более новый запрос на паузу или возобновление.'],
  [/^The request was more than 5 minutes old when the engine saw it; press the button again\.$/,
    'Когда движок увидел запрос, ему было больше 5 минут; нажмите кнопку ещё раз.'],
  [/^Done\.$/, 'Готово.'],

  // --- The analyst's Run now (AgentRuns/AgentRunPlanner.cs, AgentRunScheduler.cs, AnalystRunRequestService.cs) ---------------
  [/^Analyst run (\S+) queued; its market view usually arrives within 10 minutes\. The next scheduled run is (\d+) hours after this one\.$/,
    (m) => `Запуск аналитика ${m[1]} поставлен в очередь; взгляд на рынок обычно появляется в течение 10 минут. Следующий запуск по расписанию будет через ${m[2]} ч. после этого.`],
  [/^The analyst is already queued to run; its view arrives a few minutes after it starts\.$/,
    'Запуск аналитика уже в очереди; взгляд на рынок появится через несколько минут после старта.'],
  [/^The analyst is running now; its view arrives when the run ends\.$/, 'Аналитик уже работает; взгляд на рынок появится, когда запуск закончится.'],
  [/^The analyst started a run (\d+) min ago; ask again in (\d+) min \((\d+) min between runs, to spare the Claude limit\)\.$/,
    (m) => `Аналитик запускался ${m[1]} мин. назад; попросите снова через ${m[2]} мин. (между запусками не меньше ${m[3]} мин., чтобы беречь лимит Claude).`],
  [/^You have asked for (\d+) analyst runs today, the most a day; the scheduled runs go on \(AgentRuns:MaxAnalystNowPerDay\)\.$/,
    (m) => `Сегодня вы уже запросили ${m[1]} внеочередных запусков аналитика, это максимум на день; запуски по расписанию продолжаются (AgentRuns:MaxAnalystNowPerDay).`],
  [/^The analyst is off on this engine \(AgentRuns:Analyst\)\.$/, 'Аналитик на этом движке выключен (AgentRuns:Analyst).'],
  [/^The analyst run couldn't be queued; press the button again in a minute\.$/, 'Не удалось поставить запуск аналитика в очередь; нажмите кнопку ещё раз через минуту.'],
  [/^This engine doesn't queue the agents' runs \(AgentRuns:Mode Off\): the analyst runs on its own loop in Docker Compose\.$/,
    'Этот движок не ставит запуски агентов в очередь (AgentRuns:Mode Off): в Docker Compose аналитик работает по своему циклу.'],

  // --- Pair changes (TradablePairs.cs, TradableSymbols.cs) ----------------------------------------------------------------
  [/^(\S+) is already tradable\.$/, (m) => `${m[1]} уже торгуется.`],
  [/^(\S+) is not tradable; nothing to remove\.$/, (m) => `${m[1]} не торгуется; удалять нечего.`],
  [/^(\S+) added\. Its prices load now; the scanner can signal it once it has a day of hourly candles, and every order still goes through the risk gate\.$/,
    (m) => `${m[1]} добавлена. Её цены загружаются; сканер сможет дать по ней сигнал, когда наберётся день часовых свечей, а каждый ордер по-прежнему проходит риск-контроль.`],
  [/^(\S+) added: it was watched because the account holds it, and now the strategy may buy it too\.$/,
    (m) => `${m[1]} добавлена: её отслеживали, потому что она есть на счёте, а теперь стратегия может её и покупать.`],
  [/^(\S+) removed: no new orders in it\.$/, (m) => `${m[1]} удалена: новых ордеров по ней не будет.`],
  [/^Only (\S+) pairs are permitted by (config\/risk-limits(?:\.live)?\.json)\.$/, (m) => `${m[2]} разрешает только пары в ${m[1]}.`],
  [/^(config\/risk-limits(?:\.live)?\.json) doesn't permit it; only the owner can widen that list\.$/, (m) => `${m[1]} её не разрешает; расширить этот список может только владелец.`],
  [/^At most (\d+) pairs can be tradable at once; remove one first\.$/, (m) => `Одновременно могут торговаться не больше ${m[1]} пар; сначала удалите одну.`],
  [/^At most (\d+) pairs can be tradable at once \(config\/risk-limits\.json\)\.$/, (m) => `Одновременно могут торговаться не больше ${m[1]} пар (config/risk-limits.json).`],
  [/^Not permitted by config\/risk-limits\.json: (.+)\.$/, (m) => `Не разрешено в config/risk-limits.json: ${m[1]}.`],
  // The same two, as the engine words them since live has its own limits file (older records keep the wording above).
  [/^At most (\d+) pairs can be tradable at once \(the owner's limits file\)\.$/, (m) => `Одновременно могут торговаться не больше ${m[1]} пар (по файлу ограничений владельца).`],
  [/^Not permitted by the owner's limits file: (.+)\.$/, (m) => `Не разрешено файлом ограничений владельца: ${m[1]}.`],
  [/^The exchange couldn't be asked about this pair right now; send the request again later\.$/, 'Сейчас не удалось запросить биржу об этой паре; отправьте запрос позже.'],
  [/^Revolut X doesn't list it as an active pair\.$/, 'Revolut X не показывает её как активную пару.'],
  [/^Every pair must be quoted in (\S+)\.$/, (m) => `Все пары должны котироваться в ${m[1]}.`],
  [/^Revolut X doesn't list it as an active pair, so the engine can't price or sell it\.$/, 'Revolut X не показывает её как активную пару, поэтому движок не может её оценить или продать.'],
  [/^At most (\d+) pairs can be tradable at once; remove one the account doesn't hold\.$/, (m) => `Одновременно могут торговаться не больше ${m[1]} пар; удалите одну, которой нет на счёте.`],
  [/^(\S+) is not among the watched pairs \(add it on the Coins tab first\)\.$/, (m) => `${m[1]} нет среди отслеживаемых пар (сначала добавьте её на вкладке «Монеты»).`],
  [/^It traded (\S+) (\S+) in the last 24 hours; the running strategy needs at least (\S+)\.$/,
    (m) => `За последние 24 часа по ней наторговали на ${m[1]} ${m[2]}; текущей стратегии нужно не меньше ${m[3]}.`],
  [/^At least one pair must stay tradable\.$/, 'Хотя бы одна пара должна оставаться в торговле.'],
  [/^An order in it was just sent and isn't settled yet; send the request again in a minute\.$/, 'По ней только что отправлен ордер, и он ещё не завершён; повторите запрос через минуту.'],
  [/^The portfolio can't be valued right now, so the engine can't tell whether you hold it; send the request again later\.$/,
    'Сейчас не удаётся оценить портфель, поэтому движок не может проверить, есть ли она у вас; отправьте запрос позже.'],
  [/^It has an open order; cancel it or let it fill first\.$/, 'По ней есть открытый ордер; сначала отмените его или дождитесь исполнения.'],
  [/^You still hold it \(about (\S+) (\S+)\); sell it first\.$/, (m) => `Она ещё есть в портфеле (около ${m[1]} ${m[2]}); сначала продайте её.`],
  [/^You still hold it; sell it first\.$/, 'Она ещё есть в портфеле; сначала продайте её.'],
  [/^A residual of it is still on the account \(about (\S+) (\S+)\), below the exchange minimum, so it can't be sold on its own; a pair can be removed only when its balance is zero\. To clear it, buy enough to reach the minimum, then sell all\.$/,
    (m) => `На счёте ещё есть её остаток (около ${m[1]} ${m[2]}) меньше минимума биржи, поэтому отдельно его не продать; пару можно удалить только при нулевом остатке. Чтобы его убрать, докупите до минимума и продайте всё.`],
  [/^A buy in it is still being settled; send the request again in a minute\.$/, 'Покупка по ней ещё завершается; повторите запрос через минуту.'],
  [/^The request has no valid createdAt\.$/, 'В запросе нет корректного времени создания.'],
  [/^day must be the UTC date of createdAt\.$/, 'День запроса должен совпадать с датой его создания (UTC).'],
  [/^The request's time is in the future\.$/, 'Время запроса в будущем.'],
  [/^action must be add or remove\.$/, 'Действие должно быть «добавить» или «удалить».'],
  [/^symbol must be a pair such as SOL-EUR\.$/, 'Монета должна быть парой вида SOL-EUR.'],
  [/^The request was more than 5 minutes old when the engine saw it; send it again\.$/, 'Когда движок увидел запрос, ему было больше 5 минут; отправьте его ещё раз.'],

  // --- Scanner: why no entry, and why a coin was filtered out (MarketScanner.cs) ------------------------------------------
  [/^Passed every entry rule, but the strategy allows no buy signals\.$/, 'Прошла все правила входа, но стратегия сейчас не даёт сигналов на покупку.'],
  [/^Passed every entry rule, but the strategy allows (\d+) buy signal\(s\) per scan and higher-ranked (.+) took them\.$/,
    (m) => `Прошла все правила входа, но стратегия даёт не больше ${m[1]} сигн. на покупку за скан, и их заняли монеты выше в рейтинге: ${m[2]}.`],
  [/^The strategy buys only in an uptrend \(EMA(\d+) above EMA(\d+)\); the trend is (\w+)\.$/,
    (m) => `Стратегия покупает только в восходящем тренде (EMA${m[1]} выше EMA${m[2]}); сейчас тренд ${TREND_RU[m[3]] ?? m[3]}.`],
  [/^The exchange shows no ask price right now\.$/, 'Биржа сейчас не показывает цену продавца (ask).'],
  [/^Price is (\S+) ATR above EMA(\d+); the strategy buys a pullback, at most (\S+) ATR above it\.$/,
    (m) => `Цена на ${m[1]} ATR выше EMA${m[2]}; стратегия покупает на откате, не выше ${m[3]} ATR над ней.`],
  [/^Last hour (\S+); the strategy waits for an hour that isn't falling\.$/, (m) => `За последний час ${m[1] === 'n/a' ? 'нет данных' : m[1]}; стратегия ждёт час без падения.`],
  [/^A stop (\S+) ATR below the ask would be at or below zero\.$/, (m) => `Стоп-лосс на ${m[1]} ATR ниже ask оказался бы на нуле или ниже.`],
  [/^The stop \((\S+) ATR below the ask\) or the target is within (\S+) of the ask; an order needs at least (\S+)\.$/,
    (m) => `Стоп-лосс (${m[1]} ATR ниже ask) или тейк-профит ближе ${m[2]} к ask; ордеру нужно не меньше ${m[3]}.`],
  [/^The stop leaves no distance to size the order by\.$/, 'Стоп-лосс не оставляет расстояния, по которому можно рассчитать размер ордера.'],
  [/^The sized order rounds down to zero at the exchange's size step\.$/, 'Рассчитанный ордер округляется до нуля по шагу размера биржи.'],
  [/^The sized order, (\S+) (\S+) \(at most (\S+) per order and (\S+) at risk\), is below the exchange minimum of (\S+) (\S+)\.$/,
    (m) => `Рассчитанный ордер, ${m[1]} ${m[2]} (не больше ${m[3]} на ордер и ${m[4]} под риском), меньше минимума биржи ${m[5]} ${m[6]}.`],
  [/^The sized order, (\S+) (\S+) \(at most (\S+) (\S+) per order and (\S+) at risk\), is below the exchange minimum of (\S+) (\S+)\.$/,
    (m) => `Рассчитанный ордер, ${m[1]} ${m[2]} (не больше ${m[3]} ${m[4]} на ордер и ${m[5]} под риском), меньше минимума биржи ${m[6]} ${m[7]}.`],
  [/^Flagged by the market analyst until (.+) UTC\.$/, (m) => `Аналитик рынка отметил монету до ${m[1]} UTC.`],
  [/^Not among the tradable pairs \(the owner adds them from the dashboard, within config\/risk-limits\.json\)\.$/,
    'Не входит в торгуемые пары (владелец добавляет их на панели в пределах config/risk-limits.json).'],
  [/^Not among the tradable pairs \(the owner adds them from the dashboard, within the owner's limits file\)\.$/,
    'Не входит в торгуемые пары (владелец добавляет их на панели в пределах своего файла ограничений).'],
  [/^The exchange doesn't list this pair\.$/, 'Биржа не торгует этой парой.'],
  [/^The exchange has suspended trading in this pair\.$/, 'Биржа приостановила торги этой парой.'],
  [/^Market data is stale\.$/, 'Рыночные данные устарели.'],
  [/^Not enough candles yet for EMA, ATR or spread\.$/, 'Пока мало свечей, чтобы рассчитать EMA, ATR или спред.'],
  [/^Only (\d+) hourly candles in the last 24h; (\d+) needed\.$/, (m) => `За последние 24 часа только ${m[1]} часовых свечей; нужно ${m[2]}.`],
  [/^24h volume (\S+) is below the minimum (\S+)\.$/, (m) => `Объём торгов за 24 часа ${m[1]} ниже минимума ${m[2]}.`],
  [/^Spread (\S+) is above the maximum (\S+)\.$/, (m) => `Спред ${m[1]} выше допустимого ${m[2]}.`],
  [/^Hourly ATR (\S+) is above the maximum (\S+)\.$/, (m) => `Часовой ATR ${m[1]} выше допустимого ${m[2]}: монета слишком волатильна.`],
  [/^Hourly ATR (\S+) is under (\S+)× the round-trip cost (\S+)\.$/,
    (m) => `Часовой ATR ${m[1]} меньше ${m[2]}× издержек на покупку и продажу (${m[3]}): обычное движение не окупит сделку.`],
];

/** The engine's sentence in Russian, or null when it isn't in a known format. */
function engineTextRu(text) {
  if (typeof text !== 'string' || !text.trim()) return null;
  const s = text.trim();
  for (const [re, ru] of FORMATS) {
    const m = re.exec(s);
    if (m) return typeof ru === 'function' ? ru(m) : ru;
  }
  return reasonRu(s); // the risk gate's and the execution service's, which order explanations use too
}

module.exports = { engineTextRu, FORMATS };
