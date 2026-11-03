'use strict';
// The Stocks mode (ADR-0023), the third button of the Paper | Live | Stocks switch: the stocks advisor's latest advice from
// /api/stocks. The owner carries it out by hand in Revolut; the one thing sent from here is the owner's real portfolio
// (ADR-0026): the whole of it (POST /api/owner/stocks-portfolio) or one trade made at Revolut (POST /api/owner/stocks-trade),
// which the next nightly run applies in order, doing the trade's arithmetic itself. Kept apart from the
// crypto tabs, which hide while it shows: app.js calls refreshStocks when it opens and on each refresh while it shows (and
// reads nothing of crypto's then), and renderStocks when the language changes. Uses app.js's helpers (el, rows, td,
// tile, amount, price, getJson, t) and its state, so it loads after app.js. Every value is written as text, never as HTML.

const stocks = {
  answer: null, error: null, sent: null, clientId: null, failed: null,
  // The trades this page sent (the read may not show them yet), and the trade dialog's kind, retry id and failed figures.
  sentTrades: [], tradeKind: 'buy', tradeClientId: null, tradeFailed: null,
};

async function refreshStocks() {
  if (state.signedOut) return;
  try {
    stocks.answer = await getJson('/api/stocks');
    stocks.error = null;
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    stocks.error = error.message;
  }
  renderStocks();
}

/** An action in words. */
function stockAction(kind) {
  switch (kind) {
    case 'sell': return t('Sell');
    case 'set-stop': return t('Set a stop');
    case 'raise-stop': return t('Raise the stop');
    case 'buy': return t('Buy');
    default: return kind;
  }
}

/** Why the advisor says so, from its reason code; a code the page doesn't know is shown as it is. */
function stockReason(code) {
  switch (code) {
    case 'breakout': return t('New 50-day high in an uptrend');
    case 'trend-broken': return t('Closed under its 200-day average');
    case 'below-stop': return t('Closed at or under the stop');
    case 'below-trailing-stop': return t('Closed under the trailing level');
    case 'no-stop': return t('No stop at the broker yet');
    case 'trail': return t('The trailing stop moved up');
    case 'risk-off-tighten': return t('Market risk-off: a tighter trail');
    case 'regime-risk-off': return t('Market risk-off: no new entries');
    case 'regime-unknown': return t('No fresh benchmark data: no new entries');
    case 'no-data': return t('No price data');
    case 'stale-data': return t('The latest prices are too old');
    case 'not-enough-data': return t('Not enough price history');
    case 'no-fx-rate': return t('No exchange rate for its currency');
    case 'too-small': return t('The allocation is too small for one share');
    case 'no-cash': return t('Not enough free cash');
    case 'max-positions': return t('All position slots are taken');
    case 'check-holding': return t('Check the recorded stop: two closes are already under it');
    case 'duplicate-holding': return t('Recorded twice: check the holdings');
    case 'too-expensive': return t('A share costs more than your price cap');
    case 'theme-limit': return t('Its theme already holds its share of the budget');
    case 'call-budget': return t("The day's price requests are spent: older prices used");
    case 'data-error': return t('The price provider failed: older prices used');
    case 'adjusted': return t('A split or dividend: the position was moved into the new prices');
    default: return code ?? '–';
  }
}

/** A watched stock's status in words, with its badge tone: green for an entry signal, orange for prices that are too old. */
function watchStatus(status) {
  switch (status) {
    case 'held': return [t('Held'), 'pending'];
    case 'breakout': return [t('Entry signal'), 'ok'];
    case 'uptrend': return [t('Uptrend, under its high'), 'signal'];
    case 'overextended': return [t('Overheated: wait for a pullback'), ''];
    case 'too-volatile': return [t('Too jumpy to buy'), ''];
    case 'below-trend': return [t('No uptrend'), ''];
    case 'too-expensive': return [t('Over your price cap'), ''];
    case 'stale-data': return [t('Old prices'), 'warn'];
    case 'not-enough-data': return [t('Not enough price history'), ''];
    case 'no-data': return [t('No price data'), ''];
    default: return [status ?? '–', ''];
  }
}

/** The market-wide search's panel (ADR-0025): its status, or the candidates the host ranked. */
function renderCandidates(c, advice) {
  const notice = document.getElementById('stocks-candidates-banner');
  const meta = document.getElementById('stocks-candidates-meta');
  const say = (text, tone) => {
    notice.hidden = !text;
    notice.className = `banner ${tone ?? ''}`;
    notice.textContent = text ?? '';
  };
  const status = c?.status ?? null;
  if (!stocks.answer || stocks.error) say(null);
  else if (!c) say(t("The market search hasn't run yet."));
  else if (status === 'off') say(t("The market search is off: it needs EODHD's paid plan (EOD All World), as the free key can't download the whole market. It is turned on in stocks/config/stocks.json."));
  else if (status === 'call-budget') say(t("The day's search calls are spent: no search tonight."), 'warn');
  else if (status === 'data-error') say(t('EODHD refused or failed the whole-market download: no search tonight. A free key is always refused.'), 'bad');
  else if (status === 'not-ready') say(t("EODHD's download wasn't for the latest session yet: the next run searches again."), 'warn');
  else if (status === 'advice-behind') say(t("The advice's prices came late, so the search waited for them: the next run searches."), 'warn');
  else if (status === 'done' && advice?.asOf && c.asOf && c.asOf < advice.asOf) {
    say(t('These candidates are from the close of {day}, older than the advice: the latest search failed.', { day: c.asOf }), 'warn');
  }
  else if (status === 'done' && c.regime === 'risk-off') say(t('Market risk-off: the advisor buys nothing new until it recovers. These are for watching.'), 'bad');
  else say(null);
  meta.textContent = status === 'done' && c.asOf
    ? t('{shortlisted} checked of {listed} stocks, close of {day}', { shortlisted: c.shortlisted ?? '–', listed: c.listed ?? '–', day: c.asOf }) : '';
  const shown = status === 'done' ? c.candidates : [];
  rows('stocks-candidates', shown, (x) => [
    [stockName(x), x.venue ? [' · ', el('span', { class: 'muted small', text: x.venue })] : null],
    el('span', { class: `badge signal ${x.status === 'breakout' ? 'ok' : ''}`, text: x.status === 'breakout' ? t('Entry signal') : t('Near its high') }),
    td(stockPrice(x.close, 'USD'), 'num'),
    td(stockPrice(x.breakoutHigh, 'USD'), 'num'),
    td(stockPrice(x.stop, 'USD'), 'num'),
    td(x.shares === null ? '–' : amount(x.shares, 0), 'num'),
    td(x.amount === null ? '–' : money(x.amount, c.baseCurrency), 'num'),
    td(el('span', { class: x.momentum === null ? '' : `move ${x.momentum >= 0 ? 'up' : 'down'}`, text: percent(x.momentum) }), 'num'),
    analystMark(x.symbol),
  ], status === 'done' ? t('No stock in the market passes the entry rule tonight.') : advice ? '–' : t('No advice yet.'));
}

// --- Claude's view (ADR-0027): the analyst's word on the holdings, and its picks among the buys and candidates ----------

/** Claude's view when it is of the advice's close and done; null otherwise (the rules' advice stands alone). */
function currentAnalysis() {
  const v = stocks.answer?.analysis ?? null;
  return v && v.current && v.status === 'done' ? v : null;
}

/** A candidate's mark from Claude: its pick rank, or left out (the reason a hover away), or nothing. */
function analystMark(symbol) {
  const v = currentAnalysis();
  const pick = v?.picks.find((p) => p.symbol === symbol);
  if (pick) return el('span', { class: 'badge ok', title: pick.reason, text: t('Pick {rank}', { rank: pick.rank }) });
  const skip = v?.skips.find((k) => k.symbol === symbol);
  if (skip) return el('span', { class: 'badge bad', title: skip.reason, text: t('Left out') });
  return '–';
}

function analystAction(action) {
  switch (action) {
    case 'hold': return [t('Hold'), 'ok'];
    case 'sell-half': return [t('Sell half'), 'bad'];
    case 'sell-all': return [t('Sell all'), 'bad'];
    case 'add': return [t('Add more'), 'signal'];
    default: return [action ?? '–', ''];
  }
}

/** Why Claude's view is missing, or is not of this close: what the banner says, and its tone. */
function analysisNotice(v, advice) {
  if (!stocks.answer?.configured || stocks.error || !advice) return [null];
  if (!v) return [t("Claude hasn't looked at this advice yet: it runs after the nightly advice. Until then the rules' advice stands.")];
  if (!v.current) {
    return [t("Claude's view is of the close of {day}, older than the advice: tonight's run hasn't finished or didn't run. The rules' advice stands.", { day: v.asOf ?? '–' }), 'warn'];
  }
  if (v.status === 'refused') return [t("Claude's answer couldn't be read, so it was left out: the rules' advice stands."), 'warn'];
  if (v.status === 'not-run') {
    switch (v.reason) {
      case 'usage-limit': return [t("Claude limit: Claude didn't run for this close, so the rules' advice stands."), 'warn'];
      case 'credentials': return [t("Claude couldn't sign in: the token in the stocks Key Vault is missing or expired. The rules' advice stands."), 'bad'];
      case 'timeout': return [t("Claude's run took too long and was stopped: the rules' advice stands."), 'warn'];
      default: return [t("Claude's run failed: the rules' advice stands."), 'warn'];
    }
  }
  return [null];
}

/** Days from today (the owner's) to an earnings day, or null. */
function daysTo(dayText) {
  if (!dayText) return null;
  return Math.round((Date.parse(dayText) - Date.parse(localToday())) / (24 * 3600 * 1000));
}

/** An earnings report this close or soon is worth a card: within this many days. */
const EARNINGS_SOON_DAYS = 14;

function earningsCell(dayText) {
  const n = daysTo(dayText);
  if (n === null) return '–';
  return el('span', { class: n >= 0 && n <= EARNINGS_SOON_DAYS ? 'badge warn' : '', text: dayText });
}

function renderAnalysis(advice) {
  const v = stocks.answer?.analysis ?? null;
  const done = currentAnalysis();
  const [text, tone] = analysisNotice(v, advice);
  const notice = document.getElementById('stocks-analysis-banner');
  notice.hidden = !text;
  notice.className = `banner ${tone ?? ''}`;
  notice.textContent = text ?? '';
  document.getElementById('stocks-analysis-meta').textContent = v?.asOf
    ? [v.pass === 'pre-open' && v.status === 'done'
      ? t('close of {day}, checked before the open {ago}', { day: v.asOf, ago: ago(v.generatedAt) })
      : t('close of {day}, written {ago}', { day: v.asOf, ago: ago(v.generatedAt) }),
      done?.dropped ? t('lines left out as outside the advice: {count}', { count: done.dropped }) : null].filter(Boolean).join(' · ')
    : '';

  // Red cards on top: what Claude would sell; orange: a held stock reporting earnings soon.
  const cards = [];
  for (const h of done?.holdings ?? []) {
    const soon = daysTo(h.earnings);
    if (h.action === 'sell-half' || h.action === 'sell-all') {
      const [label] = analystAction(h.action);
      cards.push(el('li', { class: 'card risk' },
        el('div', { class: 'head' }, el('span', { class: 'badge bad', text: label }), el('span', { class: 'chip', text: h.symbol }),
          h.earnings ? el('span', { class: 'muted small', text: t('earnings {day}', { day: h.earnings }) }) : null),
        el('p', { class: 'detail' }, says(h.reason, h.reasonRu))));
    } else if (soon !== null && soon >= 0 && soon <= EARNINGS_SOON_DAYS) {
      cards.push(el('li', { class: 'card watch' },
        el('div', { class: 'head' }, el('span', { class: 'badge warn', text: t('Earnings {day}', { day: h.earnings }) }), el('span', { class: 'chip', text: h.symbol })),
        el('p', { class: 'detail' }, says(h.reason, h.reasonRu))));
    }
  }
  const list = document.getElementById('stocks-analysis-cards');
  list.hidden = cards.length === 0;
  list.replaceChildren(...cards);

  document.getElementById('stocks-analysis-body').hidden = !done;
  const market = document.getElementById('stocks-analysis-market');
  market.replaceChildren(done?.market ? says(done.market, done.marketRu) : '');
  rows('stocks-analysis-holdings', done?.holdings ?? [], (h) => {
    const [label, cls] = analystAction(h.action);
    return [stockName(h), el('span', { class: `badge ${cls}`, text: label }), earningsCell(h.earnings), td(says(h.reason, h.reasonRu), 'why')];
  }, t('No holdings to look at.'));
  rows('stocks-analysis-picks', done?.picks ?? [], (p) => [td(String(p.rank), 'num'), stockName(p), earningsCell(p.earnings), td(says(p.reason, p.reasonRu), 'why')],
    t('No pick tonight: Claude would buy none of them.'));
  rows('stocks-analysis-skips', done?.skips ?? [], (k) => [stockName(k), earningsCell(k.earnings), td(says(k.reason, k.reasonRu), 'why')], t('Nothing left out.'));
}

function stockName(item) {
  return [el('span', { class: 'chip', text: item.symbol }), item.name ? [' ', el('span', { class: 'muted small', text: item.name })] : null];
}

function stockPrice(value, currency) {
  return value === null || value === undefined ? '–' : [price(value), currency ? ` ${currency}` : ''];
}

function percent(value) {
  if (value === null || value === undefined) return '–';
  const rounded = Math.round(value * 1000) / 10;
  const text = Math.abs(rounded).toLocaleString(I.locale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  return `${rounded > 0 ? '+' : rounded < 0 ? '−' : ''}${text}%`;
}

function actionCells(x) {
  return [
    el('span', { class: `badge ${x.kind === 'sell' ? 'bad' : x.kind === 'buy' ? 'ok' : 'warn'}`, text: stockAction(x.kind) }),
    stockName(x),
    td(x.quantity === null ? '–' : amount(x.quantity, 0), 'num'),
    td(stockPrice(x.referencePrice, x.currency), 'num'),
    td(stockPrice(x.stop, x.currency), 'num'),
    td(stockReason(x.reason), 'why'),
  ];
}

function holdingCells(h) {
  return [
    stockName(h),
    td(amount(h.quantity, 0), 'num'),
    td(stockPrice(h.averageCost, h.currency), 'num'),
    td(stockPrice(h.lastClose, h.currency), 'num'),
    td(el('span', { class: h.change === null ? '' : `move ${h.change >= 0 ? 'up' : 'down'}`, text: percent(h.change) }), 'num'),
    td(stockPrice(h.stop, h.currency), 'num'),
    td(h.toStop === null ? (h.stop === null ? el('span', { class: 'badge bad', text: t('no stop') }) : '–')
      : el('span', { class: h.toStop <= 0 ? 'badge bad' : '', text: percent(h.toStop) }), 'num'),
    h.openedOn ?? '–',
  ];
}

/** Why the host refused a portfolio or a trade, from its code (Cix.Stocks.Host.OwnerPortfolio). */
function refusalReason(code) {
  switch (code) {
    case 'capital': return t('the capital is missing or too large, or a sale\'s loss leaves none');
    case 'holdings': return t('too many holdings');
    case 'symbol': return t('a ticker is not a US share, or is listed twice');
    case 'figures': return t('a number of shares, a price, the fees or a stop is wrong');
    case 'opened': return t('a day bought or traded is after the day it was sent');
    case 'no-portfolio': return t('a trade came before any portfolio: save the whole portfolio first');
    case 'not-held': return t('a sale of a stock the portfolio does not hold');
    case 'too-many': return t('a sale of more shares than the portfolio holds');
    case 'after-refused': return t('it came after a portfolio that was refused: save the whole portfolio again, then the trade');
    default: return t("it couldn't be read");
  }
}

/**
 * When the next stocks run starts: 02:00 UTC, Tuesday to Saturday, each after a US close (infra/stocks/main.bicep's
 * schedule). A US holiday has no close, so its run applies nothing and the one after does.
 */
function nextStocksRun(now = new Date()) {
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), 2));
  while (d <= now || d.getUTCDay() < 2) d.setUTCDate(d.getUTCDate() + 1);
  return d;
}

/** A run's start in the owner's own time: "Tue, 3 Nov, 03:00". */
function runTime(d) {
  return d.toLocaleString(I.locale(), { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}

/** The portfolio sent and not yet applied: the newest the page knows of, its own last send included. */
function waitingPortfolio(a) {
  const seen = Math.max(a?.owner?.appliedSequence ?? 0, a?.refusal?.sequence ?? 0);
  return [stocks.answer?.pending, stocks.sent].filter((p) => p && p.sequence > seen).sort((x, y) => y.sequence - x.sequence)[0] ?? null;
}

/** The trades sent and not yet applied, in the order the next run applies them, the page's own sends included. */
function waitingTrades(a) {
  const seen = Math.max(a?.owner?.appliedSequence ?? 0, a?.refusal?.sequence ?? 0);
  const all = new Map();
  for (const x of [...(stocks.answer?.pendingTrades ?? []), ...stocks.sentTrades]) if (x && x.sequence > seen) all.set(x.sequence, x);
  return [...all.values()].sort((x, y) => x.sequence - y.sequence);
}

function tradeKind(kind) {
  return el('span', { class: `badge ${kind === 'sell' ? 'bad' : 'ok'}`, text: kind === 'sell' ? t('Sale') : t('Purchase') });
}

/** A trade as the host recorded it: its profit for a sale, against the average price paid, fees off. */
function tradeCells(x) {
  return [
    x.day ?? '–',
    tradeKind(x.kind),
    stockName(x),
    td(amount(x.quantity, 0), 'num'),
    td(stockPrice(x.price, 'USD'), 'num'),
    td(x.fees ? money(x.fees, 'USD') : '–', 'num'),
    td(x.profit === null || x.profit === undefined ? '–' : el('span', { class: `move ${x.profit >= 0 ? 'up' : 'down'}`, text: money(x.profit, 'USD') }), 'num'),
  ];
}

/** A trade sent and not yet applied: the next run works out its profit. */
function waitingTradeCells(x) {
  if (x.unreadable || !x.trade) return ['–', '–', '–', td('–', 'num'), td('–', 'num'), td('–', 'num'), td(el('span', { class: 'badge warn', text: t("can't be read") }), 'num')];
  const w = x.trade;
  return [
    w.day,
    tradeKind(w.kind),
    stockName(w),
    td(amount(w.quantity, 0), 'num'),
    td(stockPrice(w.price, 'USD'), 'num'),
    td(w.fees && Number(w.fees) > 0 ? money(w.fees, 'USD') : '–', 'num'),
    td(el('span', { class: 'badge warn', text: t('waiting for the next run') }), 'num'),
  ];
}

/** The owner's real portfolio (ADR-0026): its advice, what is waiting for the next run, or the ask to enter it. */
function renderOwner(a) {
  const o = a?.owner ?? null;
  const waiting = waitingPortfolio(a);
  const trades = waitingTrades(a);
  const configured = Boolean(stocks.answer?.configured) && !stocks.error;
  document.getElementById('stocks-portfolio-open').hidden = !configured;
  // A trade changes a portfolio, so it needs one first: applied, or waiting before it.
  const tradable = configured && Boolean(o || (waiting && !waiting.unreadable));
  document.getElementById('stocks-trade-buy').hidden = !tradable;
  document.getElementById('stocks-trade-sell').hidden = !tradable;
  const notice = document.getElementById('stocks-owner-banner');
  const say = (text, tone) => {
    notice.hidden = !text;
    notice.className = `banner ${tone ?? ''}`;
    notice.textContent = text ?? '';
  };
  const waits = [];
  if (waiting?.unreadable) waits.push(t('A portfolio change is waiting for the next run, but the page cannot read it.'));
  else if (waiting) {
    waits.push(t('Sent {when}: capital {capital}, {count} holdings. The next run after a US close applies it: {next}.', {
      when: new Date(waiting.submittedAt).toLocaleString(I.locale()), capital: money(waiting.capital, 'USD'), count: waiting.holdings.length,
      next: runTime(nextStocksRun()),
    }));
  }
  if (trades.length) waits.push(t('Trades waiting for the next run: {count}. It recomputes the capital and the holdings from them: {next}.', { count: trades.length, next: runTime(nextStocksRun()) }));
  if (!configured) say(null);
  else if (waits.length) say(waits.join(' '), 'warn');
  else if (a?.refusal) {
    const r = a.refusal;
    const reason = refusalReason(r.reason);
    say(r.kind === 'buy' && r.symbol ? t('Your purchase of {symbol} was refused: {reason}. Check the portfolio and the trades, and record it again, corrected.', { symbol: r.symbol, reason })
      : r.kind === 'sell' && r.symbol ? t('Your sale of {symbol} was refused: {reason}. Check the portfolio and the trades, and record it again, corrected.', { symbol: r.symbol, reason })
        : t('A change you sent was refused: {reason}. Check the portfolio and the trades, and send it again, corrected.', { reason }), 'bad');
  }
  else if (!o) say(t('Enter your capital and what you hold at Revolut, so the advice is for your real account. Until then the advice below is for the paper portfolio only.'));
  else say(null);

  document.getElementById('stocks-owner-meta').textContent = o
    ? t('capital {capital} · free {cash} · invested {invested} · worth {value}', {
      capital: money(o.capital, 'USD'), cash: money(o.cash, 'USD'), invested: money(o.invested, 'USD'), value: money(o.value, 'USD'),
    }) : '';
  document.getElementById('stocks-owner-body').hidden = !o;
  rows('stocks-owner-actions', o?.actions ?? [], actionCells, t('Nothing to do: hold what you have, with its stops.'));
  rows('stocks-owner-holdings', o?.holdings ?? [], holdingCells, t('No holdings: all in cash.'));
  rows('stocks-owner-notes', o?.notes ?? [], (n) => [stockName(n), td(stockReason(n.reason), 'why')], t('No notes.'));
  // The journal: the trades waiting for the next run, newest first, above those the host recorded (newest first too).
  document.getElementById('stocks-owner-trades-part').hidden = !configured || !(o || trades.length);
  const journal = [...[...trades].reverse().map((x) => ({ waiting: x })), ...(o?.trades ?? []).map((x) => ({ recorded: x }))];
  rows('stocks-owner-trades', journal, (j) => (j.waiting ? waitingTradeCells(j.waiting) : tradeCells(j.recorded)),
    t('No trades recorded yet: after a buy or a sell at Revolut, press I bought or I sold.'));
  // With a real portfolio the paper one stays beside it, as the benchmark.
  document.getElementById('h-stocks-actions').textContent = o ? t('Paper portfolio: what it does') : t('What to do in Revolut');
  document.getElementById('h-stocks-holdings').textContent = o ? t('Paper portfolio: holdings and stops') : t('Holdings and stops');
}

function renderStocks() {
  const meta = document.getElementById('stocks-meta');
  const notice = document.getElementById('stocks-banner');
  const a = stocks.answer?.advice ?? null;
  const showBanner = (text, tone) => {
    notice.hidden = !text;
    notice.className = `banner ${tone ?? ''}`;
    notice.textContent = text ?? '';
  };

  if (stocks.error) showBanner(t("Couldn't load the stocks advice: {error}", { error: stocks.error }), 'bad');
  else if (stocks.answer && !stocks.answer.configured) {
    showBanner(t("The stocks advisor isn't connected yet. After each market close it will list here what to buy, where to put the stop, and when to raise it or sell."));
  } else if (stocks.answer && !a) showBanner(t("The stocks advisor hasn't written any advice yet."));
  else if (a?.stale) showBanner(a.asOf ? t('The advice is old: the latest is for {day}. The stocks advisor may not have run.', { day: a.asOf }) : t("The advice has no date: don't act on it."), 'bad');
  else showBanner(null);

  // In the Stocks mode the header's update time is the stocks read's (refresh reads nothing else there).
  if (state.tab === 'stocks') {
    document.getElementById('updated').textContent = stocks.error ? t('Update failed')
      : stocks.answer ? t('Updated {time}', { time: timeOf(stocks.answer.generatedAt) }) : t('Loading…');
  }

  meta.textContent = a?.asOf && !a.stale && !stocks.error ? t('advice after the close of {day}: carry it out in Revolut at the next open', { day: a.asOf }) : '';

  const regime = a?.regime ?? null;
  tile('tile-stocks-market', { 'risk-on': 'ok', 'risk-off': 'bad', unknown: 'warn' }[regime] ?? '',
    regime === 'risk-on' ? t('Risk-on') : regime === 'risk-off' ? t('Risk-off') : regime ? t('Unknown') : '–',
    regime === 'risk-on' ? t('{benchmark} above its 200-day average: entries allowed', { benchmark: a.benchmark ?? '–' })
      : regime === 'risk-off' ? t('{benchmark} under its 200-day average: no new entries, tighter stops', { benchmark: a.benchmark ?? '–' })
        : regime ? t('no fresh benchmark data: no new entries') : '–');
  tile('tile-stocks-advice', !a ? '' : a.stale ? 'bad' : 'ok', a?.asOf ?? '–',
    a?.generatedAt ? t('written {ago}', { ago: ago(a.generatedAt) }) : t('no advice yet'));
  // The owner's real portfolio once there is one (free cash plus the holdings at the close), the paper one before; a
  // change sent and not yet applied marks the tile, so its figures aren't taken for the new ones.
  const o = a?.owner ?? null;
  const waiting = waitingPortfolio(a);
  const pendingTrades = waitingTrades(a).length;
  const requested = waiting && !waiting.unreadable
    ? el('span', { class: 'line', text: t('Change requested: capital {capital}, from {next}', { capital: money(waiting.capital, 'USD'), next: runTime(nextStocksRun()) }) })
    : waiting || pendingTrades ? el('span', { class: 'line', text: t('A change is waiting for the next run') }) : null;
  const tone = waiting || pendingTrades ? 'warn' : '';
  if (o) {
    tile('tile-stocks-account', tone, o.value !== null && o.cash !== null ? money(o.cash + o.value, 'USD') : '–',
      [t('allocation {amount}', { amount: money(o.capital, 'USD') }), ' · ', t('cash {amount}', { amount: money(o.cash, 'USD') }), requested]);
  } else {
    tile('tile-stocks-account', tone, a?.equity !== null && a?.equity !== undefined ? money(a.equity, a.baseCurrency) : '–',
      a ? [t('allocation {amount}', { amount: money(a.capital, a.baseCurrency) }), ' · ', t('cash {amount}', { amount: money(a.cash, a.baseCurrency) }), requested] : '–');
  }

  renderAnalysis(a);
  renderOwner(a);

  rows('stocks-actions', a?.actions ?? [], actionCells, a ? t('Nothing to do: hold what you have, with its stops.') : t('No advice yet.'));

  rows('stocks-holdings', a?.holdings ?? [], holdingCells, t('No holdings recorded.'));

  renderCandidates(stocks.answer?.candidates ?? null, a);

  rows('stocks-watchlist', a?.watchlist ?? [], (w) => {
    const [text, tone] = watchStatus(w.status);
    return [
      stockName(w),
      w.theme ?? '–',
      td(stockPrice(w.lastClose, w.currency), 'num'),
      td(stockPrice(w.breakoutHigh, w.currency), 'num'),
      td(w.toBreakout === null ? '–' : w.toBreakout < 0 ? t('above') : percent(w.toBreakout), 'num'),
      td(el('span', { class: w.vsTrend === null ? '' : `move ${w.vsTrend >= 0 ? 'up' : 'down'}`, text: percent(w.vsTrend) }), 'num'),
      el('span', { class: `badge signal ${tone}`, text }),
    ];
  }, a ? t('The advice has no watchlist yet: it comes with the next run.') : t('No advice yet.'));

  rows('stocks-notes', a?.notes ?? [], (n) => [stockName(n), td(stockReason(n.reason), 'why')], t('No notes.'));
}

// --- The portfolio dialog: the whole portfolio, sent as one submission ------------------------------------------------

const DECIMAL = /^\d{1,7}(\.\d{1,8})?$/;
const US_TICKER = /^[A-Z0-9][A-Z0-9_-]{0,19}\.US$/;

/** One holding's row: ticker, shares, price paid, stop, day bought, and a button to take it out. */
function portfolioRow(h = {}) {
  const input = (name, value, label, attrs = {}) => el('input', { type: 'text', name, value: value ?? '', 'aria-label': label, autocomplete: 'off', ...attrs });
  const cell = (name, value, label, attrs) => el('td', { 'data-label': label }, input(name, value, label, attrs));
  const row = el('tr', {},
    cell('symbol', h.symbol, t('Ticker'), { placeholder: 'EPAM', maxlength: '24' }),
    cell('quantity', h.quantity, t('Shares'), { inputmode: 'decimal' }),
    cell('averageCost', h.averageCost, t('Price paid, USD'), { inputmode: 'decimal' }),
    cell('stop', h.stop, t('Stop, USD'), { inputmode: 'decimal' }),
    cell('openedOn', h.openedOn, t('Bought on'), { type: 'date' }),
    td(el('button', { type: 'button', class: 'ghost remove', 'aria-label': t('Remove this holding'), text: '×' })));
  if (h.name) row.dataset.name = h.name;
  row.querySelector('.remove').addEventListener('click', () => row.remove());
  document.querySelector('#stocks-portfolio-rows tbody').append(row);
}

/** Opens the dialog with what is waiting for the next run, else the portfolio as the advice left it (its stops as placed). */
function openPortfolio() {
  const a = stocks.answer?.advice ?? null;
  const waiting = waitingPortfolio(a);
  const o = a?.owner ?? null;
  const placed = new Map((o?.actions ?? []).filter((x) => x.kind === 'set-stop' || x.kind === 'raise-stop').map((x) => [x.symbol, x.stop]));
  const start = waiting && !waiting.unreadable ? waiting
    : o ? {
      capital: o.capital,
      holdings: o.holdings.map((h) => ({ ...h, stop: placed.get(h.symbol) ?? h.stop })),
    } : { capital: '', holdings: [] };
  const form = document.getElementById('stocks-portfolio-form');
  form.elements.capital.value = start.capital ?? '';
  document.querySelector('#stocks-portfolio-rows tbody').replaceChildren();
  for (const h of start.holdings) portfolioRow({ ...h, symbol: h.symbol?.replace(/\.US$/, '') });
  if (start.holdings.length === 0) portfolioRow();
  // Trades waiting after that portfolio are not in the form: a portfolio saved now comes after them and replaces what they make.
  const after = waitingTrades(a).filter((x) => !waiting || x.sequence > waiting.sequence).length;
  const warning = document.getElementById('stocks-portfolio-trades');
  warning.hidden = after === 0;
  warning.textContent = after ? t('Trades waiting for the next run are not in this form: {count}. A portfolio saved now replaces what they would change, so enter it as it is after them.', { count: after }) : '';
  document.getElementById('stocks-portfolio-error').hidden = true;
  document.getElementById('stocks-portfolio-send').disabled = false;
  stocks.clientId = crypto.randomUUID(); // a retry of this dialog is the same submission
  stocks.failed = null;
  document.getElementById('stocks-portfolio-dialog').showModal();
}

/**
 * A number as typed, as the page sends it: spaces group thousands (10 000), and so do commas before a point (10,000.50); a
 * single comma otherwise is the decimal point (12,5). A comma before exactly three digits and no point (10,000 or 1,234)
 * could be either, so it is refused (null) rather than guessed.
 */
function typedNumber(v) {
  const s = v.trim().replace(/[\s\u00a0\u202f]/g, '');
  if (/^\d{1,3}(,\d{3})+\.\d+$/.test(s)) return s.replaceAll(',', '');
  if (/^\d{1,3}(,\d{3})+$/.test(s)) return null;
  if (/^\d+,\d+$/.test(s)) return s.replace(',', '.');
  return s;
}

/** Today in the owner's time zone, as the date picker gives it. */
function localToday() {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** The dialog's portfolio, checked as the host checks it; { error } names the first problem. */
function portfolioBody() {
  const form = document.getElementById('stocks-portfolio-form');
  const ambiguous = (v) => t('{value} could be thousands or a decimal: write it as 10000 or 10.5.', { value: v.trim() });
  const capitalTyped = form.elements.capital.value;
  const capital = typedNumber(capitalTyped);
  if (capital === null) return { error: ambiguous(capitalTyped) };
  if (!DECIMAL.test(capital) || !(Number(capital) > 0) || Number(capital) > 1_000_000) return { error: t('Enter the capital: a number above 0, up to 1 000 000.') };
  const holdings = [];
  const seen = new Set();
  for (const row of document.querySelectorAll('#stocks-portfolio-rows tbody tr')) {
    const get = (name) => row.querySelector(`[name="${name}"]`).value.trim();
    const raw = get('symbol').toUpperCase();
    if (!raw && !get('quantity') && !get('averageCost')) continue; // an empty row
    const symbol = raw.includes('.') ? raw : `${raw}.US`;
    if (!US_TICKER.test(symbol)) return { error: t('{symbol} is not a US ticker: enter it like EPAM or EPAM.US.', { symbol: raw || '–' }) };
    if (seen.has(symbol)) return { error: t('{symbol} is listed twice.', { symbol }) };
    seen.add(symbol);
    const figure = (name) => {
      const v = typedNumber(get(name));
      return v !== null && DECIMAL.test(v) && Number(v) > 0 && Number(v) <= 1_000_000 ? v : null;
    };
    const unclear = ['quantity', 'averageCost', 'stop'].find((name) => get(name) && typedNumber(get(name)) === null);
    if (unclear) return { error: `${symbol}: ${ambiguous(get(unclear))}` };
    const quantity = figure('quantity');
    const averageCost = figure('averageCost');
    if (!quantity || !averageCost) return { error: t('{symbol}: enter the shares and the price paid per share, as numbers above 0.', { symbol }) };
    const stop = get('stop') ? figure('stop') : null;
    if (get('stop') && !stop) return { error: t('{symbol}: the stop must be a number above 0, or empty.', { symbol }) };
    const openedOn = get('openedOn') || null;
    if (openedOn && openedOn > localToday()) return { error: t('{symbol}: the day bought cannot be after today.', { symbol }) };
    holdings.push({ symbol, name: row.dataset.name ?? null, quantity, averageCost, stop, openedOn });
  }
  if (holdings.length > 50) return { error: t('At most 50 holdings.') };
  return { body: { clientId: stocks.clientId, capital, holdings } };
}

async function sendPortfolio(event) {
  event.preventDefault();
  const error = document.getElementById('stocks-portfolio-error');
  const { body, error: problem } = portfolioBody();
  if (problem) { error.textContent = problem; error.hidden = false; return; }
  // Changed since a send that failed (and may have been saved all the same): a new submission, which then stands as the newest.
  const figures = JSON.stringify({ capital: body.capital, holdings: body.holdings });
  if (stocks.failed !== null && stocks.failed !== figures) body.clientId = stocks.clientId = crypto.randomUUID();
  stocks.failed = null;
  const send = document.getElementById('stocks-portfolio-send');
  const label = send.textContent;
  send.disabled = true;
  send.textContent = t('Sending…');
  try {
    const response = await api('/api/owner/stocks-portfolio', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ? t(answer.error) : t('The dashboard answered {status}.', { status: response.status }));
    stocks.sent = answer.pending ?? null;
    document.getElementById('stocks-portfolio-dialog').close();
    renderStocks();
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    stocks.failed = figures;
    error.textContent = t('Not saved: {error} Save again to retry: it stays one change.', { error: e.message });
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = label;
  }
}

// --- The trade dialog: one buy or sell made at Revolut, sent as one submission -------------------------------------------

/** Opens the trade dialog for a buy or a sell; the tickers held are offered, and the day is today. */
function openTrade(kind) {
  const a = stocks.answer?.advice ?? null;
  const waiting = waitingPortfolio(a);
  stocks.tradeKind = kind;
  const buy = kind === 'buy';
  document.getElementById('stocks-trade-title').textContent = buy ? t('I bought') : t('I sold');
  document.getElementById('stocks-trade-help').textContent = buy
    ? t('The shares are added to the holding at this price, the fees in its cost. The capital stays the same, so the free cash falls by what you paid.')
    : t('The shares come off the holding; the profit or loss against the average price paid, fees off, is added to the capital. Selling all of them closes the holding.');
  document.getElementById('stocks-trade-stop').hidden = !buy;
  const held = (waiting && !waiting.unreadable ? waiting.holdings : a?.owner?.holdings ?? []).map((h) => h.symbol);
  document.getElementById('stocks-trade-held').replaceChildren(...held.map((symbol) => el('option', { value: symbol.replace(/\.US$/, '') })));
  const form = document.getElementById('stocks-trade-form');
  form.reset();
  form.elements.day.value = localToday();
  const waits = waitingTrades(a).length;
  const note = document.getElementById('stocks-trade-waiting');
  note.hidden = waits === 0;
  note.textContent = waits ? t('Trades waiting for the next run: {count}. This one is applied after them.', { count: waits }) : '';
  document.getElementById('stocks-trade-error').hidden = true;
  document.getElementById('stocks-trade-send').disabled = false;
  stocks.tradeClientId = crypto.randomUUID(); // a retry of this dialog is the same trade
  stocks.tradeFailed = null;
  document.getElementById('stocks-trade-dialog').showModal();
}

/** The dialog's trade, checked as the host checks it; { error } names the first problem. */
function tradeBody() {
  const form = document.getElementById('stocks-trade-form');
  const get = (name) => form.elements[name].value.trim();
  const buy = stocks.tradeKind === 'buy';
  const raw = get('symbol').toUpperCase();
  const symbol = raw.includes('.') ? raw : `${raw}.US`;
  if (!US_TICKER.test(symbol)) return { error: t('{symbol} is not a US ticker: enter it like EPAM or EPAM.US.', { symbol: raw || '–' }) };
  const names = buy ? ['quantity', 'price', 'fees', 'stop'] : ['quantity', 'price', 'fees'];
  const unclear = names.find((name) => get(name) && typedNumber(get(name)) === null);
  if (unclear) return { error: t('{value} could be thousands or a decimal: write it as 10000 or 10.5.', { value: get(unclear) }) };
  const figure = (name) => {
    const v = typedNumber(get(name));
    return v !== null && DECIMAL.test(v) && Number(v) > 0 && Number(v) <= 1_000_000 ? v : null;
  };
  const quantity = figure('quantity');
  const price = figure('price');
  if (!quantity || !price) return { error: t('Enter the shares and the price per share, as numbers above 0.') };
  const feesTyped = get('fees') ? typedNumber(get('fees')) : null;
  if (feesTyped !== null && (!DECIMAL.test(feesTyped) || Number(feesTyped) > 1_000_000)) return { error: t('The fees must be a number from 0, or empty.') };
  const stop = buy && get('stop') ? figure('stop') : null;
  if (buy && get('stop') && !stop) return { error: t('{symbol}: the stop must be a number above 0, or empty.', { symbol }) };
  const day = get('day');
  if (!day) return { error: t('Enter the day of the trade.') };
  if (day > localToday()) return { error: t('The day of the trade cannot be after today.') };
  // What the next run would refuse, as far as the applied portfolio tells: with changes waiting the page can't tell.
  const a = stocks.answer?.advice ?? null;
  if (!waitingPortfolio(a) && waitingTrades(a).length === 0 && a?.owner) {
    const h = a.owner.holdings.find((x) => x.symbol === symbol);
    if (buy && !h && a.owner.holdings.length >= 50) return { error: t('At most 50 holdings: sell one first, or save the whole portfolio.') };
  }
  if (!buy && !waitingPortfolio(a) && waitingTrades(a).length === 0 && a?.owner) {
    const h = a.owner.holdings.find((x) => x.symbol === symbol);
    if (!h) return { error: t('{symbol} is not in your portfolio.', { symbol }) };
    if (Number(quantity) > Number(h.quantity)) return { error: t('{symbol}: you hold {held} shares.', { symbol, held: amount(h.quantity, 0) }) };
  }
  const name = (a?.owner?.holdings ?? []).find((x) => x.symbol === symbol)?.name ?? null;
  return { body: { clientId: stocks.tradeClientId, trade: { kind: stocks.tradeKind, symbol, name, quantity, price, fees: feesTyped, day, stop } } };
}

async function sendTrade(event) {
  event.preventDefault();
  const error = document.getElementById('stocks-trade-error');
  const { body, error: problem } = tradeBody();
  if (problem) { error.textContent = problem; error.hidden = false; return; }
  // Changed since a send that failed (and may have been saved all the same): a new trade.
  const figures = JSON.stringify(body.trade);
  if (stocks.tradeFailed !== null && stocks.tradeFailed !== figures) body.clientId = stocks.tradeClientId = crypto.randomUUID();
  stocks.tradeFailed = null;
  const send = document.getElementById('stocks-trade-send');
  const label = send.textContent;
  send.disabled = true;
  send.textContent = t('Sending…');
  try {
    const response = await api('/api/owner/stocks-trade', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ? t(answer.error) : t('The dashboard answered {status}.', { status: response.status }));
    if (answer.pending?.trade) stocks.sentTrades = [...stocks.sentTrades.filter((x) => x.sequence !== answer.pending.sequence), answer.pending];
    document.getElementById('stocks-trade-dialog').close();
    renderStocks();
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    stocks.tradeFailed = figures;
    error.textContent = t('Not saved: {error} Save again to retry: it stays one change.', { error: e.message });
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = label;
  }
}

document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('stocks-trade-buy').addEventListener('click', () => openTrade('buy'));
  document.getElementById('stocks-trade-sell').addEventListener('click', () => openTrade('sell'));
  document.getElementById('stocks-trade-form').addEventListener('submit', sendTrade);
  document.getElementById('stocks-trade-cancel').addEventListener('click', () => document.getElementById('stocks-trade-dialog').close());
  document.getElementById('stocks-portfolio-open').addEventListener('click', openPortfolio);
  document.getElementById('stocks-portfolio-add').addEventListener('click', () => portfolioRow());
  document.getElementById('stocks-portfolio-form').addEventListener('submit', sendPortfolio);
  document.getElementById('stocks-portfolio-cancel').addEventListener('click', () => document.getElementById('stocks-portfolio-dialog').close());
});

