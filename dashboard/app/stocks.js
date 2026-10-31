'use strict';
// The Stocks mode (ADR-0023), the third button of the Paper | Live | Stocks switch: the stocks advisor's latest advice from
// /api/stocks, read only. The owner carries it out by hand in Revolut; nothing here sends anything. Kept apart from the
// crypto tabs, which hide while it shows: app.js calls refreshStocks when it opens and on each refresh while it shows (and
// reads nothing of crypto's then), and renderStocks when the language changes. Uses app.js's helpers (el, rows, td,
// tile, amount, price, getJson, t) and its state, so it loads after app.js. Every value is written as text, never as HTML.

const stocks = { answer: null, error: null };

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
  ], status === 'done' ? t('No stock in the market passes the entry rule tonight.') : advice ? '–' : t('No advice yet.'));
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
  tile('tile-stocks-account', '', a?.equity !== null && a?.equity !== undefined ? money(a.equity, a.baseCurrency) : '–',
    a ? [t('allocation {amount}', { amount: money(a.capital, a.baseCurrency) }), ' · ', t('cash {amount}', { amount: money(a.cash, a.baseCurrency) })] : '–');

  rows('stocks-actions', a?.actions ?? [], (x) => [
    el('span', { class: `badge ${x.kind === 'sell' ? 'bad' : x.kind === 'buy' ? 'ok' : 'warn'}`, text: stockAction(x.kind) }),
    stockName(x),
    td(x.quantity === null ? '–' : amount(x.quantity, 0), 'num'),
    td(stockPrice(x.referencePrice, x.currency), 'num'),
    td(stockPrice(x.stop, x.currency), 'num'),
    td(stockReason(x.reason), 'why'),
  ], a ? t('Nothing to do: hold what you have, with its stops.') : t('No advice yet.'));

  rows('stocks-holdings', a?.holdings ?? [], (h) => [
    stockName(h),
    td(amount(h.quantity, 0), 'num'),
    td(stockPrice(h.averageCost, h.currency), 'num'),
    td(stockPrice(h.lastClose, h.currency), 'num'),
    td(el('span', { class: h.change === null ? '' : `move ${h.change >= 0 ? 'up' : 'down'}`, text: percent(h.change) }), 'num'),
    td(stockPrice(h.stop, h.currency), 'num'),
    td(h.toStop === null ? (h.stop === null ? el('span', { class: 'badge bad', text: t('no stop') }) : '–')
      : el('span', { class: h.toStop <= 0 ? 'badge bad' : '', text: percent(h.toStop) }), 'num'),
    h.openedOn ?? '–',
  ], t('No holdings recorded.'));

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
