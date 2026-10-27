'use strict';
// CIX dashboard: reads /api/dashboard, /api/activity, /api/journal, /api/owner/requests, /api/owner/strategies and /api/owner/pairs
// and renders them, and sends paper buy and sell requests (ADR-0011, ADR-0012), strategy switches (#108, ADR-0015) and pair
// additions and removals (ADR-0016): the only writes,
// each one confirmed first. The whole site is the owner's
// (ADR-0014): the platform sends anyone else to sign in, and the API answers only the confirmed owner.
// Every value is written with textContent, never as HTML, so journal text can't inject markup. view.js holds the logic.
// Every word goes through i18n.js (t): the language switch re-renders the whole page in Russian or English at once.

const REFRESH_MS = 30_000;
const POLL_MS = 3_000;
const POLL_FOR_MS = 15 * 60_000;
const V = window.CixView;
const I = window.CixI18n;
const t = I.t;
const state = { mode: 'paper', lang: 'ru', journal: null, period: 'day', viewer: { signedIn: false, name: null }, view: null, pending: new Map(), signedOut: false, timer: null, tab: 'overview', strategies: null, pairs: null };
const SIGN_IN = '/.auth/login/aad?post_login_redirect_uri=/';

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined || value === null) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function ms(iso) {
  const t = Date.parse(iso ?? '');
  return Number.isFinite(t) ? t : null;
}

/** How long since a time, without "ago": 5 min. */
function since(iso) {
  const at = ms(iso);
  if (at === null) return '–';
  const s = Math.max(0, Math.round((Date.now() - at) / 1000));
  if (s < 60) return t('{n}s', { n: s });
  if (s < 3600) return t('{n} min', { n: Math.round(s / 60) });
  if (s < 86400) return t('{n} h', { n: Math.round(s / 3600) });
  return t('{n} d', { n: Math.round(s / 86400) });
}

function ago(iso) {
  const at = ms(iso);
  if (at === null) return '–';
  if (at > Date.now()) return t('just now');
  return t('{time} ago', { time: since(iso) });
}

function timeOf(iso) {
  return new Date(iso).toLocaleTimeString(I.locale());
}

function when(iso) {
  const at = ms(iso);
  if (at === null) return el('span', { class: 'muted', text: '–' });
  const d = new Date(at);
  const sameDay = d.toDateString() === new Date().toDateString();
  const text = sameDay ? d.toLocaleTimeString(I.locale()) : d.toLocaleString(I.locale());
  return el('time', { datetime: d.toISOString(), title: `${d.toISOString()} (${ago(iso)})`, text });
}

function shortId(id) {
  if (!id) return el('span', { class: 'muted', text: '–' });
  const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$|^[0-9a-f]{32}$/i.test(id); // readable ids show in full
  return el('span', { class: 'mono', title: id, text: isGuid ? `${id.slice(0, 8)}…` : id });
}

function amount(value, digits = 2) {
  if (value === null || value === undefined || value === '') return '–';
  const n = Number(value);
  if (!Number.isFinite(n)) return String(value);
  return n.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: Math.max(digits, 8) });
}

/** A price or level as the engine wrote it (exact decimal string), or a dash. */
function price(value) {
  return value === null || value === undefined || value === '' ? '–' : String(value);
}

function money(value, currency) {
  return value === null || value === undefined ? '–' : `${amount(value)} ${currency ?? ''}`.trim();
}

/** Fills a table's body. Each cell carries its column's heading (data-label), which small screens show on the card. */
function rows(tableId, items, render, emptyText) {
  const body = document.querySelector(`#${tableId} tbody`);
  const headings = [...document.querySelectorAll(`#${tableId} thead th`)].filter((th) => !th.hidden).map((th) => th.textContent.trim());
  const labelled = (cell, i) => {
    const node = cell instanceof Node && cell.tagName === 'TD' ? cell : el('td', {}, cell);
    if (headings[i]) node.dataset.label = headings[i];
    return node;
  };
  body.replaceChildren(...(items.length
    ? items.map((item) => el('tr', {}, render(item).map(labelled)))
    : [el('tr', { class: 'empty' }, el('td', { colspan: String(headings.length || 1), text: emptyText }))]));
}

function td(content, cls) {
  return el('td', { class: cls }, content);
}

/**
 * Text that comes in English and maybe Russian, in the content language (V.pick), as a span marked with its language.
 * Text the dashboard has no Russian for is shown in English. Shown in Russian, the English stays a hover away, so a
 * translation can always be checked against the original (the analyst writes both).
 */
function says(en, ru, cls) {
  const p = V.pick(en, ru, state.lang);
  return el('span', { class: cls, lang: p.lang, title: p.lang === 'ru' && en ? `EN: ${en}` : null, text: p.text ?? '' });
}

function tile(id, tone, value, sub) {
  const node = document.getElementById(id);
  node.className = `tile ${tone ?? ''}`;
  node.querySelector('.value').textContent = value;
  node.querySelector('.sub').replaceChildren(...[sub].flat().filter(Boolean).map((s) => (s instanceof Node ? s : document.createTextNode(s))));
}

function figure(label, value, cls) {
  return el('div', { class: 'figure' }, el('span', { class: 'label', text: label }), el('span', { class: `amount ${cls ?? ''}`, text: value }));
}

function signed(value) {
  const n = Number(value);
  return Number.isFinite(n) && n !== 0 ? (n > 0 ? 'pos' : 'neg') : '';
}

function renderTiles(view) {
  const e = view.engine;
  const engineTone = { running: 'ok', stale: 'bad', unknown: 'warn' }[e.state];
  tile('tile-engine', engineTone, t({ running: 'Running', stale: 'Not responding', unknown: 'Unknown' }[e.state] ?? 'Unknown'),
    e.heartbeatAt ? t('heartbeat {ago}', { ago: ago(e.heartbeatAt) }) : t('no status record yet'));

  tile('tile-trading', e.halted ? 'bad' : e.halted === false ? 'ok' : 'warn', e.halted ? t('Halted') : e.halted === false ? t('Active') : '–',
    e.halted ? `${e.haltCategory ? t(e.haltCategory) : ''} ${e.haltedSince ? t('since {ago}', { ago: ago(e.haltedSince) }) : ''}`.trim() : view.mode === 'live' ? t('real money') : t('paper money'));

  renderStrategyTile(e);

  const mv = e.marketView;
  const regime = mv?.active ? mv.regime : 'Neutral';
  tile('tile-view', regime === 'RiskOff' ? 'warn' : 'ok', t({ RiskOn: 'Risk-on', RiskOff: 'Risk-off', Neutral: 'Neutral' }[regime] ?? regime),
    mv?.active ? `${t('until {time}', { time: timeOf(mv.validUntil) })}${mv.avoid?.length ? ` · ${t('avoid {coins}', { coins: mv.avoid.join(', ') })}` : ''}` : t('no active view'));

  const quoteAge = ms(e.oldestQuoteAt) === null ? null : (Date.now() - ms(e.oldestQuoteAt)) / 1000;
  tile('tile-data', quoteAge === null ? 'warn' : quoteAge > 120 ? 'bad' : 'ok', quoteAge === null ? '–' : quoteAge > 120 ? t('Stale') : t('Fresh'),
    [e.oldestQuoteAt ? t('oldest price {ago}', { ago: ago(e.oldestQuoteAt) }) : t('no prices yet'), e.unquotedSymbols?.length ? ` · ${t('none for {coins}', { coins: e.unquotedSymbols.join(', ') })}` : '']);

  const s = view.sync;
  const syncAge = ms(s.exchange) === null ? null : (Date.now() - ms(s.exchange)) / 1000;
  tile('tile-sync', s.reconcileProblem ? 'bad' : syncAge === null ? 'warn' : syncAge > 600 ? 'warn' : 'ok',
    s.exchange ? ago(s.exchange) : '–',
    [s.reconcileProblem ? says(s.reconcileProblem, s.reconcileProblemRu) : t('portfolio with the exchange'), s.database ? ` · ${t('database {ago}', { ago: ago(s.database) })}` : '']);
}

/**
 * The running strategy, as the engine reports it: on the Overview's Active strategy tile, and in the header on every tab.
 * The mode is always manual: the owner switches strategy on the Strategy tab; nothing switches by itself.
 */
function renderStrategyTile(engine) {
  const a = V.activeStrategy(engine);
  const stale = engine?.state !== 'running';
  const line = (text) => el('span', { class: 'line', text });
  const notes = a ? [
    line(t('Risk profile: {profile}', { profile: a.profile })),
    line(t('Mode: {mode}', { mode: a.mode })),
    line(`${a.sourceText}${a.since ? ` ${ago(a.since)}` : ''}`),
    a.switchingTo ? line(t('switching to {name}…', { name: a.switchingTo })) : null,
    a.refused ? line(t('switch to {name} refused', { name: a.refused })) : null,
  ] : [stale ? t('the engine isn\'t reporting') : t('not reported yet')];
  tile('tile-strategy', a ? (stale ? 'warn' : a.tone) : 'warn', a ? a.title : '–', notes);

  const badge = document.getElementById('strategy-badge');
  badge.textContent = a ? `${a.title}${a.profileKey === 'custom' ? ` · ${a.profile}` : ''} · ${a.mode}${a.switchingTo ? ` → ${a.switchingTo}` : ''}` : t('strategy: –');
  badge.className = `badge strategy ${a?.switchingTo ? 'pending' : a?.refused ? 'bad' : a ? a.tone : ''}`;
  badge.title = a
    ? t('The strategy the engine runs now: {name}, {profile} risk, {mode} mode, {source}. Open the Strategy tab to switch.',
      { name: a.title, profile: a.profile, mode: a.mode.toLowerCase(), source: a.sourceText })
    : t('The engine hasn\'t reported its strategy yet.');
}

function renderPortfolio(view) {
  const p = view.portfolio;
  document.getElementById('valued-at').textContent = p.valuedAt ? t('valued {ago}', { ago: ago(p.valuedAt) }) : t('not valued yet');
  document.getElementById('figures').replaceChildren(
    figure(t('Equity'), money(p.equity, p.currency)),
    figure(t('Cash'), money(p.cash, p.currency)),
    figure(t('P&L today'), money(p.pnlToday, p.currency), signed(p.pnlToday)),
    figure(t('Peak equity'), money(p.peakEquity, p.currency)),
    figure(t('Drawdown'), p.drawdownPct === null || p.drawdownPct === undefined ? '–' : `${p.drawdownPct.toFixed(2)} %`, p.drawdownPct > 0 ? 'neg' : ''),
    figure(t('Exposure'), money(p.exposure, p.currency)),
  );
  rows('holdings', p.holdings ?? [], (h) => [
    h.symbol, td(money(h.valueQuote, p.currency), 'num'), td(price(h.entryPrice), 'num'),
    td(h.protected ? price(h.stopLoss) : el('span', { class: 'badge bad', text: t('none') }), 'num'), td(price(h.takeProfit), 'num'), h.planStatus ? t(h.planStatus) : '–',
  ], t('Nothing held.'));
  rows('balances', p.balances ?? [], (b) => [b.currency, td(amount(b.available), 'num'), td(amount(b.reserved), 'num')],
    view.mode === 'paper' ? t('No paper account yet.') : t('Balances are shown for paper trading only.'));
}

function renderCycle(view) {
  const c = view.cycle;
  const scan = c.lastScan;
  document.getElementById('cycle').replaceChildren(
    figure(t('Last scan'), scan ? ago(scan.at) : '–'),
    figure(t('Coins ranked'), scan ? t('{ranked} of {total}', { ranked: scan.ranked, total: Number(scan.ranked) + Number(scan.excluded) }) : '–'),
    figure(t('Signals'), scan ? t('{buy} buy · {sell} sell', { buy: scan.entrySignals, sell: scan.exitSignals }) : '–'),
    figure(t('Watching'), scan?.signalKey || t('no signals')),
    figure(t('Last trader run'), c.lastTraderRun ? ago(c.lastTraderRun.lastAt) : '–'),
  );
  rows('orders', c.ordersInFlight ?? [], (o) => [when(o.sentAt), o.symbol, t(o.side), td(amount(o.size, 0), 'num'), td(price(o.limitPrice), 'num'), o.source, shortId(o.clientOrderId)], t('No orders working.'));
  const agents = Object.entries(view.agents ?? {});
  rows('agents', agents, ([name, a]) => [
    name, a.lastRun ? shortId(a.lastRun.runId) : '–', a.lastRun ? when(a.lastRun.lastAt) : '–',
    td(String(a.runsToday), 'num'), td(String(a.lastRun?.calls ?? 0), 'num'), td(String(a.lastRun?.orders ?? 0), 'num'),
  ], t('No agent runs in the last 48 hours.'));
}

function renderTables(view) {
  rows('trades', view.trades, (f) => [
    when(f.at), f.symbol ?? '–', f.side ? t(f.side) : '–', td(amount(f.filledSize, 0), 'num'), td(price(f.averagePrice), 'num'), td(price(f.fee), 'num'),
    f.source ?? '–', f.status ? t(f.status) : '–', shortId(f.clientOrderId),
  ], t('No fills in the last 48 hours.'));
  rows('decisions', view.decisions, (d) => [
    when(d.at), t(d.actor), d.action ?? '–', d.symbol ?? '–', d.side ? t(d.side) : '–', td(amount(d.size, 0), 'num'), td(price(d.price), 'num'),
    el('span', { class: `badge ${{ placed: 'ok', rejected: 'bad', failed: 'bad', uncertain: 'warn' }[d.status] ?? ''}`, text: d.status ? t(d.status) : '–' }),
    td(why(d), 'why'),
  ], t('No decisions in the last 48 hours.'));
  rows('problems', view.problems, (p) => [when(p.at), t(p.source), t(p.kind), td(says(p.message, p.messageRu), 'why')], t('Nothing went wrong in the last 48 hours.'));
  if (!state.journal) renderActivity(view.activity, t('No journal entries in the last 48 hours.'));
  rows('state', view.stateDocuments, (s) => [el('span', { class: 'mono', text: s.id }), s.type ?? '–', when(s.updatedAt), td(s.about ? t(s.about) : '', 'why')],
    t('No state documents.'));
}

/** The reason first, then the evidence, the risk plan and what happened; the technical text folded away under it. */
function why(d) {
  const ru = state.lang === 'ru' && d.explanationRu;
  const x = ru ? d.explanationRu : d.explanation;
  if (!x) return d.rationale ?? d.summary ?? '';
  const wrap = el('div', { class: 'explain', lang: ru ? 'ru' : 'en' },
    el('p', { class: 'headline', text: x.headline }),
    x.points?.length ? el('ul', { class: 'points' }, x.points.map((p) => el('li', { text: p }))) : null,
    x.risk ? el('p', { class: 'risk', text: x.risk.text }) : null,
    x.outcome ? el('p', { class: 'outcome', text: x.outcome }) : null);
  if (x.raw) wrap.append(el('details', { class: 'raw', lang: 'en' }, el('summary', { text: t('Technical') }), el('p', { class: 'mono', text: x.raw })));
  return wrap;
}

function renderActivity(entries, emptyText) {
  rows('activity', entries, (e) => [when(e.at), e.kind, e.actor, shortId(e.runId), td(e.summary, 'why'), shortId(e.id)], emptyText);
}

const DIRECTION_LABEL = { risk: 'Risk', opportunity: 'Opportunity', watch: 'Watch' }; // through t() when shown
const DIRECTION_TONE = { risk: 'bad', opportunity: 'ok', watch: 'warn' };
const SEVERITY_LABEL = { 3: 'high', 2: 'medium', 1: 'low' };

/** An external link: https only (the API checks too), opened in a new tab without access back to this page. */
function sourceLink(url, host) {
  let href = null;
  try { href = new URL(url).protocol === 'https:' ? url : null; } catch { /* not a URL */ }
  return href ? el('a', { href, target: '_blank', rel: 'noopener noreferrer nofollow', text: host || t('source') }) : null;
}

function chips(symbols) {
  return (symbols ?? []).map((s) => el('span', { class: 'chip', text: s }));
}

function renderBriefing(view) {
  const b = view.briefing ?? {};
  const a = b.analyst;
  document.getElementById('briefing-meta').textContent = [
    a ? `${t('analyst {ago}', { ago: ago(a.recordedAt) })}${a.active ? '' : ` ${t('(view expired)')}`}` : t('no analyst view in 24 hours'),
    b.scanAt ? `${t('scan {ago}', { ago: ago(b.scanAt) })}${b.scanStale ? ` ${t('(stale)')}` : ''}` : t('no scan yet'),
  ].join(' · ');

  const summary = document.getElementById('analyst-summary');
  summary.replaceChildren(...(a ? [
    el('span', { class: `badge ${a.regime === 'risk-off' ? 'warn' : a.regime === 'risk-on' ? 'ok' : ''}`, text: t(a.regime ?? 'unknown') }), ' ',
    says(a.summary ?? '', a.summaryRu),
    a.avoid?.length && a.active ? el('span', {}, ` ${t('Avoid:')} `, ...chips(a.avoid)) : null,
  ].filter((n) => n !== null) : [t('The analyst has not recorded a view in the last 24 hours.')]));

  const highlights = a?.highlights ?? [];
  document.getElementById('highlights').replaceChildren(...(highlights.length
    ? highlights.map((h) => el('li', { class: `card ${h.direction}` },
      el('div', { class: 'head' },
        el('span', { class: `badge ${DIRECTION_TONE[h.direction] ?? ''}`, text: t(DIRECTION_LABEL[h.direction] ?? h.direction) }),
        el('span', { class: 'badge', text: t(`${h.impact} impact`) }),
        says(h.title, h.titleRu, 'title'),
        ...chips(h.symbols)),
      h.detail ? el('p', { class: 'detail' }, says(h.detail, h.detailRu)) : null,
      el('p', { class: 'meta' }, sourceLink(h.source, h.sourceHost))))
    : [el('li', { class: 'muted', text: a ? t('No major news or events flagged.') : '' })]));

  const attention = b.attention ?? [];
  document.getElementById('attention').replaceChildren(...(attention.length
    ? attention.map((item) => el('li', { class: `card ${item.direction}` },
      el('div', { class: 'head' },
        el('span', { class: `badge ${DIRECTION_TONE[item.direction] ?? ''}`, text: t(DIRECTION_LABEL[item.direction] ?? item.direction) }),
        el('span', { class: 'chip', text: item.symbol }),
        item.held ? el('span', { class: 'badge', text: t('held') }) : null,
        el('span', { class: 'muted small', text: t(`${SEVERITY_LABEL[item.severity] ?? 'low'} priority`) })),
      el('ul', { class: 'reasons' }, item.reasons.map((r, i) => el('li', {}, says(r, item.reasonsRu?.[i]))))))
    : [el('li', { class: 'muted', text: b.market?.length ? t('Nothing unusual in the latest scan.') : t('No scan yet.') })]));

}

function move(c) {
  return el('span', { class: `move ${c.dir}`, 'aria-label': c.label, title: c.label, text: c.text });
}

function renderCoins(view) {
  const b = view.briefing ?? {};
  const scan = new Map((b.market ?? []).map((r) => [r.symbol, r]));
  document.getElementById('coins-meta').textContent = b.scanAt
    ? `${t('moves up to the scan at {time} ({ago})', { time: timeOf(b.scanAt), ago: ago(b.scanAt) })}${b.scanStale ? t(', stale') : ''}`
    : t('no scan yet');
  const trading = state.mode === 'paper'; // requests are paper only
  const blocked = trading ? V.requestsBlocked(view.engine) : null;
  if (blocked) document.getElementById('coins-meta').textContent += ` · ${t('requests paused: {reason}', { reason: blocked })}`;
  for (const node of document.querySelectorAll('#coins .paper-only')) node.hidden = !trading;
  rows('coins', V.coins(view), (c) => {
    const r = scan.get(c.symbol) ?? {};
    const cells = [
      el('span', { class: 'chip', text: c.symbol }),
      c.held ? el('span', { class: 'badge ok', text: t('held') }) : el('span', { class: 'muted', text: '–' }),
      td(move(c.hour), 'num'), td(move(c.day), 'num'),
      td(c.rank === null ? '–' : String(c.rank), 'num'), r.trend ? t(r.trend) : '–',
      td(r.volatility === null || r.volatility === undefined ? '–' : `${(r.volatility * 100).toFixed(1)}%`, 'num'),
      td(r.volumeRatio === null || r.volumeRatio === undefined ? '–' : `${r.volumeRatio.toFixed(1)}×`, 'num'),
      c.signal ? el('span', { class: `badge ${c.signal === 'buy' ? 'ok' : 'bad'}`, text: t(c.signal) }) : '–',
      td(c.note ? t(c.note) : '', 'muted'),
    ];
    if (trading) cells.push(td(tradeButtons(c), 'actions'));
    return cells;
  }, t('No scan yet.'));
}

function tradeButtons(c) {
  const busy = [...state.pending.values()].some((p) => p.symbol === c.symbol);
  const blocked = V.requestsBlocked(state.view?.engine);
  const buy = el('button', { type: 'button', class: 'buy', 'aria-label': t('Buy {symbol}', { symbol: c.symbol }), text: t('Buy') });
  buy.addEventListener('click', () => openRequest('buy', c));
  const sell = el('button', { type: 'button', class: 'sell', 'aria-label': t('Sell {symbol}', { symbol: c.symbol }), text: t('Sell') });
  sell.addEventListener('click', () => openRequest('sell', c));
  if (busy || blocked) {
    buy.disabled = true; sell.disabled = true;
    buy.title = sell.title = blocked ?? t('A request for this coin is in progress.');
  }
  return c.held ? [buy, ' ', sell] : [buy];
}

function banner(text, bad, link) {
  const node = document.getElementById('banner');
  node.hidden = !text;
  node.replaceChildren(...(text ? [text] : []), ...(link ? [' ', el('a', { href: link.href, text: link.text })] : []));
  node.className = `banner ${bad ? 'bad' : ''}`;
}

/** The sign-in has ended (the platform answers with a redirect to Microsoft's sign-in, which fetch doesn't follow). */
class SignedOut extends Error {
  constructor() { super(t('Your sign-in has ended.')); }
}

/** A call to the dashboard's own API. Redirects aren't followed: one means the session is over. */
async function api(url, options = {}) {
  const response = await fetch(url, { ...options, redirect: 'manual', credentials: 'same-origin', headers: { accept: 'application/json', ...options.headers } });
  if (response.type === 'opaqueredirect' || response.status === 401) throw new SignedOut();
  return response;
}

async function getJson(url) {
  const response = await api(url);
  const body = await response.json().catch(() => ({}));
  if (response.status === 403) throw new Error(body.error ? t(body.error) : t("This account isn't the dashboard's confirmed owner (run setup-dashboard-requests.ps1 again)."));
  if (!response.ok) throw new Error(body.error ? t(body.error) : t('The dashboard API answered {status}.', { status: response.status }));
  return body;
}

/** The session is over: stop reading, and take the figures off the screen rather than leave them up as if current. */
function signedOut() {
  state.signedOut = true;
  clearInterval(state.timer);
  for (const p of state.pending.values()) clearInterval(p.timer);
  state.pending.clear();
  document.getElementById('request-dialog').close();
  document.getElementById('strategy-dialog').close();
  document.getElementById('pair-dialog').close();
  for (const node of document.querySelectorAll('main > :not(#banner), .tabs')) node.hidden = true;
  // Back to the same tab after signing in: the hash is one of TABS, set by setTab.
  banner(t('Your sign-in has ended.'), true, { href: `${SIGN_IN}${encodeURIComponent(location.hash)}`, text: t('Sign in again') });
  document.getElementById('updated').textContent = t('Signed out');
}

async function refresh() {
  if (state.signedOut) return;
  try {
    const view = await getJson(`/api/dashboard?mode=${encodeURIComponent(state.mode)}`);
    if (view.mode !== state.mode) return; // the mode changed while this was loading
    state.view = view;
    renderDashboard(view);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    state.loadError = error.message;
    banner(t("Couldn't load the dashboard: {error}", { error: error.message }), true);
    document.getElementById('updated').textContent = t('Update failed');
  }
  if (!state.signedOut) await Promise.all([refreshActivity(), refreshRequests(), state.tab === 'strategy' ? refreshStrategies() : null, state.tab === 'coins' ? refreshPairs() : null]);
}

/** Everything /api/dashboard feeds, in the page's language: after each refresh, and again when the language changes. */
function renderDashboard(view) {
  state.loadError = null;
  renderTiles(view);
  renderBriefing(view);
  renderCoins(view);
  renderPortfolio(view);
  renderCycle(view);
  renderTables(view);
  const readErrors = view.problems.filter((p) => p.kind === 'read');
  banner(view.engine.state === 'stale'
    ? t("The engine hasn't reported for {time}: it may be stopped, or unable to reach the database.", { time: since(view.engine.heartbeatAt) })
    : readErrors.length ? readErrors.map((p) => V.pick(p.message, p.messageRu, state.lang).text).join(' ') : null, view.engine.state === 'stale');
  document.getElementById('updated').textContent = t('Updated {time}', { time: timeOf(view.generatedAt) });
}

// --- Trading activity -------------------------------------------------------------------------------------------------------

const SVG = 'http://www.w3.org/2000/svg';

function svg(tag, attrs, text) {
  const node = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, String(v));
  if (text !== undefined) node.textContent = text;
  return node;
}

async function refreshActivity() {
  if (state.signedOut) return;
  const meta = document.getElementById('volume-meta');
  if (state.mode !== 'paper') {
    state.activity = null;
    meta.textContent = t('Activity is counted for paper trading only for now.');
    document.getElementById('volume-cards').replaceChildren();
    document.getElementById('volume-chart').replaceChildren(svg('title', { id: 'volume-chart-title' }, t('No data')));
    return;
  }
  try {
    state.activity = await getJson(`/api/activity?period=${encodeURIComponent(state.period)}`);
    renderVolume(state.activity);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    state.activity = null;
    meta.textContent = t("Couldn't load trading activity: {error}", { error: error.message });
  }
}

function renderVolume(a) {
  const c = a.current;
  const p = a.previous ?? {};
  const unit = t({ day: 'today', week: 'this week', month: 'this month' }[a.period]);
  const before = t({ day: 'yesterday', week: 'last week', month: 'last month' }[a.period]);
  document.getElementById('volume-meta').textContent = t('{current} ({unit}, UTC) against {previous} ({before}). {trades} trades, turnover {turnover} {currency} from {from} to {to}.', {
    current: V.months(c.label), unit, previous: p.label ? V.months(p.label) : t('nothing'), before, trades: a.total.trades, turnover: a.total.turnover,
    currency: a.currency, from: a.total.from, to: a.total.to,
  }) + (a.truncated ? ` ${t('Not all fills could be counted: the totals are incomplete.')}` : '');
  const card = (label, value, now, prev, sub) => {
    const vs = V.versus(now, prev);
    return el('div', { class: 'figure' },
      el('span', { class: 'label', text: label }),
      el('span', { class: 'amount', text: value }),
      el('span', { class: sub ? 'vs' : `vs ${vs.dir}`, text: sub ?? vs.text }));
  };
  document.getElementById('volume-cards').replaceChildren(
    card(t('Turnover'), `${c.turnover} ${a.currency}`, c.turnover, p.turnover),
    card(t('Bought'), `${c.buyVolume} ${a.currency}`, c.buyVolume, p.buyVolume),
    card(t('Sold'), `${c.sellVolume} ${a.currency}`, c.sellVolume, p.sellVolume),
    card(t('Trades'), String(c.trades), c.trades, p.trades, t('{buys} buys · {sells} sells', { buys: c.buys, sells: c.sells })),
    card(t('Your requests'), String(c.manual), c.manual, p.manual, t('trades from the dashboard')),
    card(t('Fees'), `${c.fees} ${a.currency}`, c.fees, p.fees, t('not in turnover')),
  );
  const g = V.chart(a.periods, { width: 640, height: 160, labelEvery: a.periods.length > 12 ? 2 : 1, period: a.period });
  const chartNode = document.getElementById('volume-chart');
  chartNode.replaceChildren(svg('title', { id: 'volume-chart-title' }, t('Buy and sell volume by {period}, largest {max} {currency}', { period: t(a.period), max: g.max.toFixed(2), currency: a.currency })));
  document.getElementById('volume-unit').textContent = t('volume in {currency}', { currency: a.currency });
  for (const b of g.bars) {
    const group = svg('g', {});
    group.append(svg('title', {}, b.title));
    if (b.sell.height > 0) group.append(svg('rect', { x: b.x, y: b.sell.y, width: b.width, height: b.sell.height, class: 'sell' }));
    if (b.buy.height > 0) group.append(svg('rect', { x: b.x, y: b.buy.y, width: b.width, height: b.buy.height, class: 'buy' }));
    if (b.label) group.append(svg('text', { x: b.x + b.width / 2, y: 178, 'text-anchor': 'middle', class: 'tick' }, b.label));
    chartNode.append(group);
  }
  chartNode.append(svg('line', { x1: 0, y1: 160, x2: 640, y2: 160, class: 'axis' }));
}

function setPeriod(period) {
  state.period = period;
  try { localStorage.setItem('cix-period', period); } catch { /* storage may be unavailable */ }
  for (const button of document.querySelectorAll('[data-period]')) button.setAttribute('aria-pressed', String(button.dataset.period === period));
  refreshActivity();
}

// --- Who is signed in, and the owner's requests -----------------------------------------------------------------------------

async function loadViewer() {
  try {
    const response = await fetch('/.auth/me', { headers: { accept: 'application/json' }, redirect: 'manual' });
    state.viewer = V.viewer(response.ok ? await response.json() : null);
  } catch {
    state.viewer = V.viewer(null);
  }
  renderViewer();
}

function renderViewer() {
  document.getElementById('who').textContent = state.viewer.signedIn ? state.viewer.name ?? t('Signed in') : '';
}

async function refreshRequests() {
  try {
    const history = await getJson('/api/owner/requests');
    state.requests = history.requests;
    renderRequests(history.requests);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    state.requests = null;
    rows('requests', [], () => [], t("Couldn't load your requests: {error}", { error: error.message }));
  }
}

function renderRequests(items) {
  rows('requests', items, (r) => {
    const st = V.requestState(r);
    const steps = el('details', {}, el('summary', { text: t(r.events.length === 1 ? '{n} step' : '{n} steps', { n: r.events.length }) }),
      el('ol', { class: 'steps' }, r.events.map((e) => el('li', {}, when(e.at), ` ${t('{status} by {by}', { status: t(e.status ?? ''), by: t(e.by ?? '?') })}${e.detail ? `: ${t(e.detail)}` : ''}`))));
    return [
      when(r.createdAt), V.what(r), r.mode === 'direct' ? t('direct') : t('trader'),
      el('span', { class: `badge ${st.tone}`, text: st.label }),
      td([st.filled ? `${t('Filled {filled}.', { filled: st.filled })} ` : '', st.detail ? t(st.detail) : (st.final ? '' : t('in progress'))].join(''), 'why'),
      steps,
    ];
  }, t('No requests in the last 7 days.'));
}

function requestStatus(text, tone) {
  const node = document.getElementById('request-status');
  node.hidden = !text;
  node.textContent = text ?? '';
  node.className = `request-status ${tone ?? ''}`;
}

let dialogChoice = null;

function openRequest(side, coin) {
  const dialog = document.getElementById('request-dialog');
  const form = document.getElementById('request-form');
  form.reset();
  dialogChoice = { side, symbol: coin.symbol, id: crypto.randomUUID() }; // one id per opened dialog: a retry is the same request
  document.getElementById('request-title').textContent = t(side === 'buy' ? 'Buy {symbol} (paper)' : 'Sell {symbol} (paper)', { symbol: coin.symbol });
  document.getElementById('sell-options').hidden = side !== 'sell';
  document.getElementById('sell-mode').hidden = side !== 'sell';
  document.getElementById('buy-amount').hidden = side !== 'buy';
  document.getElementById('buy-quote').textContent = coin.quote;
  if (side === 'sell') { form.elements.portion.value = '1'; form.elements.mode.value = 'agent'; }
  document.getElementById('request-error').hidden = true;
  document.getElementById('request-send').disabled = false;
  for (const input of form.querySelectorAll('input')) input.disabled = false;
  updateSummary();
  dialog.showModal();
}

function currentChoice() {
  const f = document.getElementById('request-form').elements;
  return {
    ...dialogChoice,
    portion: f.portion.value,
    mode: dialogChoice.side === 'sell' ? f.mode.value : 'agent',
    quoteAmount: f.quoteAmount.value,
    note: f.note.value,
  };
}

function updateSummary() {
  if (!dialogChoice) return;
  const r = V.requestBody(currentChoice(), dialogChoice.id);
  document.getElementById('request-summary').textContent = r.body ? V.describe(r.body) : r.error;
}

async function sendRequest(event) {
  event.preventDefault();
  const r = V.requestBody(currentChoice(), dialogChoice.id);
  const error = document.getElementById('request-error');
  if (r.error) { error.textContent = r.error; error.hidden = false; return; }
  const send = document.getElementById('request-send');
  send.disabled = true; // no second click while this one is on its way
  // From the first send on, this dialog is one request: a retry sends exactly the same thing. To change it, start a new one.
  for (const input of document.getElementById('request-form').querySelectorAll('input')) input.disabled = true;
  send.textContent = t('Sending…');
  try {
    const response = await api('/api/owner/requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(r.body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ? t(answer.error) : t('The dashboard answered {status}.', { status: response.status }));
    document.getElementById('request-dialog').close();
    track(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = t('Not confirmed: {error} Send again to retry: it stays the same, single request. To change it, cancel and start a new one.', { error: e.message });
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = t('Send request');
  }
}

/** Follows a request until it is over (or 15 minutes pass), showing where it stands. */
function track(item) {
  if (state.pending.has(item.id)) return;
  const entry = { symbol: item.symbol, since: Date.now(), timer: null };
  state.pending.set(item.id, entry);
  if (state.view) renderCoins(state.view);
  const show = (it) => {
    const st = V.requestState(it);
    const extra = [st.filled ? t('filled {filled}', { filled: st.filled }) : null, st.detail ? t(st.detail) : null].filter(Boolean).join(' · ');
    requestStatus(`${V.what(it)}: ${st.label}${extra ? ` (${extra})` : ''}${st.final ? '' : '…'}`, st.final ? st.tone : 'pending');
    return st.final;
  };
  show(item);
  const stop = () => {
    clearInterval(entry.timer);
    state.pending.delete(item.id);
    if (state.view) renderCoins(state.view);
    refreshRequests();
  };
  entry.timer = setInterval(async () => {
    if (Date.now() - entry.since > POLL_FOR_MS) {
      requestStatus(t('{what}: still in progress after 15 minutes; see the Requests tab.', { what: V.what(item) }), 'warn');
      stop();
      return;
    }
    try {
      const response = await api(`/api/owner/requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`);
      if (response.status === 404) return; // not readable yet
      if (!response.ok) return; // try again next time
      if (show(await response.json())) stop();
    } catch (error) {
      if (error instanceof SignedOut) signedOut(); // else offline for a moment: try again
    }
  }, POLL_MS);
}

// --- Strategy (#108) ---------------------------------------------------------------------------------------------------------

async function refreshStrategies() {
  if (state.signedOut) return;
  try {
    const view = await getJson(`/api/owner/strategies?mode=${encodeURIComponent(state.mode)}`);
    if (view.mode !== state.mode) return; // the mode changed while this was loading
    state.strategies = view;
    renderStrategies(view);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    document.getElementById('strategy-note').textContent = t("Couldn't load the strategies: {error}", { error: error.message });
  }
}

function renderStrategies(view) {
  const running = view.strategies.find((s) => s.active) ?? null;
  const sel = V.selectionState(view.selection);
  const active = view.active ? V.activeStrategy({ strategy: view.active.label, strategySelection: { ...view.selection, activatedAt: view.selection?.state === 'active' ? view.active.since : null } }) : null;
  document.getElementById('strategy-meta').textContent = view.active?.since ? t('since {time}', { time: new Date(view.active.since).toLocaleString(I.locale()) }) : '';
  document.getElementById('strategy-running').replaceChildren(
    figure(t('Strategy'), running ? V.strategyName(running) : view.active ? view.active.label : '–'),
    figure(t('Risk profile'), active ? active.profile : '–'),
    figure(t('Mode'), t('Manual')),
    figure(t('Selection'), view.selection ? `${sel.label}${view.selection.requested ? ` · ${view.selection.requested}` : ''}` : t('not reported'), sel.tone === 'bad' ? 'neg' : ''),
    figure(t('Checked'), view.selection?.checkedAt ? ago(view.selection.checkedAt) : '–'),
  );
  const problems = view.selection?.problems ?? [];
  document.getElementById('strategy-note').textContent = [
    view.readOnlyReason ? t(view.readOnlyReason) : null,
    problems.length ? problems.map((p) => t(p)).join(' ') : null,
    running?.riskProfile ? t(running.riskProfile) : null,
  ].filter(Boolean).join(' ');

  // One card per strategy that trades, Safe, Balanced and Risky first.
  const tradable = view.strategies.filter((s) => s.canTrade);
  const byKey = Object.fromEntries(view.groups.flatMap((g) => g.parameters).map((p) => [p.key, p]));
  const facts = [['orderQuote', 'Largest order'], ['maxPositionQuotePerSymbol', 'Most in one coin'], ['maxExposureFraction', 'Most in coins'],
    ['dailyLossStopQuote', 'Daily loss stop'], ['entryCooldownMinutes', 'Cooldown']]; // labels through t() when shown
  document.getElementById('strategy-cards').replaceChildren(...(tradable.length ? tradable.map((s) => {
    const button = el('button', { type: 'button', class: s.active ? 'ghost' : 'primary', text: s.active ? t('Running') : t('Switch to {name}', { name: V.strategyName(s) }) });
    button.disabled = s.active || !view.editable || state.strategyPending;
    if (!view.editable) button.title = t(view.readOnlyReason);
    button.addEventListener('click', () => openStrategy(s));
    return el('li', { class: `card ${s.active ? 'running' : ''}` },
      el('div', { class: 'head' }, el('span', { class: 'title', text: V.strategyName(s) }), s.active ? el('span', { class: 'badge ok', text: t('running') }) : null,
        s.featured ? null : el('span', { class: 'badge', text: t('other') })),
      s.purpose ? el('p', { class: 'detail', text: t(s.purpose) }) : null,
      s.riskProfile ? el('p', { class: 'meta muted small', text: t(s.riskProfile) }) : null,
      el('dl', {}, facts.flatMap(([key, label]) => [el('dt', { text: t(label) }), el('dd', { text: V.paramValue(s.values[key], byKey[key]) })])),
      el('div', { class: 'actions' }, button));
  }) : [el('li', { class: 'muted', text: t('No strategies in the database yet: the engine adds them when it starts.') })]));

  // Every parameter: one column per strategy that trades, then the range, what a change reaches, and whose limit it is.
  const head = document.querySelector('#params thead tr');
  head.replaceChildren(el('th', { text: t('Parameter') }), ...tradable.map((s) => el('th', { class: 'num', text: V.strategyName(s) })),
    el('th', { text: t('Valid range') }), el('th', { text: t('A change reaches') }), el('th', { text: t('Limit') }));
  const body = document.querySelector('#params tbody');
  const columns = tradable.length + 4;
  body.replaceChildren(...(view.groups.length ? view.groups.flatMap((g) => [
    el('tr', { class: 'group' }, el('td', { colspan: String(columns), text: t(g.title) })),
    ...g.parameters.map((p) => el('tr', {},
      el('td', { class: 'param', 'data-label': t('Parameter') }, el('strong', { text: t(p.label) }), el('p', { text: t(p.explanation) })),
      ...tradable.map((s) => el('td', { class: `num ${s.active ? 'running' : ''}`, 'data-label': V.strategyName(s), text: V.paramValue(s.values[p.key], p) })),
      el('td', { 'data-label': t('Valid range'), text: V.paramRange(p) }),
      el('td', { 'data-label': t('A change reaches'), text: t(p.applies) }),
      el('td', { 'data-label': t('Limit'), text: V.paramLimit(p, view.mandatory) }))),
  ]) : [el('tr', { class: 'empty' }, el('td', { colspan: String(columns), text: t('The engine hasn\'t written the parameter schema yet.') }))]));

  rows('mandatory', view.mandatory, (m) => [
    el('strong', { text: t(m.label) }), td(t(m.value), 'num'), td(t(m.explanation), 'why'),
    m.source === 'risk-limits' ? t('config/risk-limits.json (owner only)') : t('the engine (fixed)'),
  ], t('Not reported yet.'));

  rows('switches', view.requests, (r) => [
    when(r.createdAt), `${r.name}@v${r.version}${r.note ? ` · ${r.note}` : ''}`,
    el('span', { class: `badge ${{ accepted: 'ok', new: 'pending', superseded: '', invalid: 'bad', expired: 'warn' }[r.status] ?? 'warn'}`, text: t(r.status) }),
    td(r.detail ? t(r.detail) : (r.status === 'new' ? t('waiting for the engine') : ''), 'why'),
  ], view.editable ? t('No switches in the last 7 days.') : t('Live has no switches yet.'));
}

let strategyChoice = null;

function openStrategy(target) {
  const view = state.strategies;
  if (!view?.editable) return;
  const running = view.strategies.find((s) => s.active) ?? null;
  strategyChoice = { name: target.name, version: target.version, id: crypto.randomUUID() }; // one id per dialog: a retry is the same request
  const form = document.getElementById('strategy-form');
  form.reset();
  for (const input of form.querySelectorAll('input')) input.disabled = false;
  document.getElementById('strategy-title').textContent = t('Switch to {name} (paper)', { name: V.strategyName(target) });
  document.getElementById('strategy-purpose').textContent = [target.purpose, target.riskProfile].filter(Boolean).map((x) => t(x)).join(' ');
  const changes = V.strategyChanges(running, target, view.groups);
  document.getElementById('strategy-changes').replaceChildren(...(changes.length
    ? changes.map((c) => el('li', { class: c.openPositions ? 'open' : '' }, `${c.label}: ${c.from} → ${c.to}`, c.openPositions ? el('strong', { text: ` (${t('open positions too')})` }) : null))
    : [el('li', { text: t('No parameter differs from the running strategy.') })]));
  document.getElementById('strategy-error').hidden = true;
  document.getElementById('strategy-send').disabled = false;
  document.getElementById('strategy-dialog').showModal();
}

async function sendStrategy(event) {
  event.preventDefault();
  const form = document.getElementById('strategy-form');
  const r = V.strategyRequestBody({ ...strategyChoice, note: form.elements.note.value }, strategyChoice.id);
  const error = document.getElementById('strategy-error');
  if (r.error) { error.textContent = r.error; error.hidden = false; return; }
  const send = document.getElementById('strategy-send');
  send.disabled = true;
  for (const input of form.querySelectorAll('input')) input.disabled = true; // from the first send on, a retry sends the same request
  send.textContent = t('Sending…');
  try {
    const response = await api('/api/owner/strategy-requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(r.body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ? t(answer.error) : t('The dashboard answered {status}.', { status: response.status }));
    document.getElementById('strategy-dialog').close();
    trackStrategy(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = t('Not confirmed: {error} Send again to retry: it stays the same, single request.', { error: e.message });
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = t('Switch strategy');
  }
}

/** Follows a switch until the engine has taken it, then until the engine status reports the outcome. */
function trackStrategy(item) {
  state.strategyPending = true;
  const started = Date.now();
  const label = `${item.name}@v${item.version}`;
  requestStatus(t('Strategy switch to {label}: sent, waiting for the engine…', { label }), 'pending');
  if (state.strategies) renderStrategies(state.strategies);
  const timer = setInterval(async () => {
    const stop = (text, tone) => {
      clearInterval(timer);
      state.strategyPending = false;
      requestStatus(text, tone);
      refresh(); // the header and the Active strategy tile show the outcome at once, not at the next 30-second refresh
      if (state.tab !== 'strategy') refreshStrategies();
    };
    if (Date.now() - started > 5 * 60_000) { stop(t('Strategy switch to {label}: no answer after 5 minutes; see Your strategy switches.', { label }), 'warn'); return; }
    try {
      const response = await api(`/api/owner/strategy-requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`);
      if (!response.ok) return; // not readable yet, or offline for a moment
      const it = await response.json();
      if (it.status === 'new') return;
      if (it.status !== 'accepted') { stop(`${t('Strategy switch to {label}: {status}.', { label, status: t(it.status) })} ${it.detail ? t(it.detail) : ''}`.trim(), 'bad'); return; }
      // Accepted: the engine validates and switches; its status reports within a minute.
      await refreshStrategies();
      const sel = state.strategies?.selection;
      // The status names this request when its time matches (an earlier switch to the same version has another).
      if (sel?.requested === label && Date.parse(sel.requestedAt ?? '') === Date.parse(item.createdAt) && ['active', 'refused'].includes(sel.state)) {
        stop(sel.state === 'active'
          ? t('Strategy switch to {label}: active. New trades follow it from the next scan.', { label })
          : `${t('Strategy switch to {label}: refused.', { label })} ${sel.problems.map((p) => t(p)).join(' ')}`, sel.state === 'active' ? 'ok' : 'bad');
      } else {
        requestStatus(t('Strategy switch to {label}: accepted; the engine applies it before the next trader run…', { label }), 'pending');
      }
    } catch (error) {
      if (error instanceof SignedOut) { clearInterval(timer); signedOut(); }
    }
  }, POLL_MS * 2);
}

// --- Trading pairs (ADR-0016) ------------------------------------------------------------------------------------------------

async function refreshPairs() {
  if (state.signedOut) return;
  try {
    const view = await getJson(`/api/owner/pairs?mode=${encodeURIComponent(state.mode)}`);
    if (view.mode !== state.mode) return; // the mode changed while this was loading
    state.pairs = view;
    renderPairs(view);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    document.getElementById('pairs-note').textContent = t("Couldn't load the trading pairs: {error}", { error: error.message });
  }
}

function renderPairs(view) {
  const busy = !view.editable || state.pairPending;
  const onlyConfigured = !view.quoteCurrency; // config/risk-limits.json permits only its own list: nothing else to add
  document.getElementById('pairs-meta').textContent = [
    view.cap ? t('{n} of at most {cap}', { n: view.tradable.length, cap: view.cap }) : t('{n} tradable', { n: view.tradable.length }),
    view.changedAt ? t('changed {ago}', { ago: ago(view.changedAt) }) : null,
  ].filter(Boolean).join(' · ');
  const options = view.available.filter((p) => !p.pending);
  document.getElementById('pairs-note').textContent = view.readOnlyReason
    ? t(view.readOnlyReason)
    : view.full ? t('The most pairs the safety limits allow are tradable: remove one to add another.')
      : view.openToAnyPair ? t('You can add any {quote} pair the exchange lists; the engine checks it has enough trading first.', { quote: view.quoteCurrency ?? '' }).replace('  ', ' ')
        : onlyConfigured && !options.length
          ? t('No other pairs can be added yet: the safety limits permit only the pairs in config/risk-limits.json, and all of them are already tradable. To trade other coins, the owner adds "AllowedQuoteCurrency": "EUR" (any EUR pair the exchange lists) and "MaxTradableSymbols" (how many at once, for example 15) to config/risk-limits.json; once that is deployed, they can be added and removed here without another deploy.')
          : t('You can turn the pairs in config/risk-limits.json off and on again here. Adding other coins needs that list widened first.');
  document.getElementById('pairs-tradable').replaceChildren(...(view.tradable.length ? view.tradable.map((p) => {
    const remove = el('button', { type: 'button', class: 'ghost', title: t('Stop trading {symbol}', { symbol: p.symbol }), text: p.pending === 'remove' ? t('removing…') : t('Remove') });
    remove.disabled = busy || !!p.pending || view.tradable.length <= 1;
    remove.addEventListener('click', () => openPair('remove', p.symbol));
    return el('li', {}, el('span', { text: p.symbol }), remove);
  }) : [el('li', { class: 'muted', text: t('No pairs listed yet.') })]));

  const form = document.getElementById('pairs-add');
  const choice = document.getElementById('pairs-choice');
  form.hidden = !view.editable;
  choice.replaceChildren(...(options.length
    ? options.map((p) => el('option', { value: p.symbol, text: p.symbol }))
    : [el('option', { value: '', text: view.full ? t('the limit is reached') : onlyConfigured ? t('no other pairs permitted') : t('none available') })]));
  choice.disabled = busy || view.full || !options.length;
  document.getElementById('pairs-add-button').disabled = choice.disabled;

  rows('pair-requests', view.requests, (r) => [
    when(r.createdAt), `${t(r.action === 'remove' ? 'Remove {symbol}' : 'Add {symbol}', { symbol: r.symbol ?? '?' })}${r.note ? ` · ${r.note}` : ''}`,
    el('span', { class: `badge ${{ accepted: 'ok', new: 'pending', invalid: 'bad', rejected: 'bad', expired: 'warn' }[r.status] ?? 'warn'}`, text: t(r.status) }),
    td(r.detail ? t(r.detail) : (r.status === 'new' ? t('waiting for the engine') : ''), 'why'),
  ], view.editable ? t('No pair changes in the last 7 days.') : t('Live has no pair changes.'));
}

let pairChoice = null;

function openPair(action, symbol) {
  if (!state.pairs?.editable || !symbol) return;
  pairChoice = { action, symbol, id: crypto.randomUUID() }; // one id per dialog: a retry is the same request
  const form = document.getElementById('pair-form');
  form.reset();
  for (const input of form.querySelectorAll('input')) input.disabled = false;
  document.getElementById('pair-title').textContent = t(action === 'remove' ? 'Remove {symbol} (paper)' : 'Add {symbol} (paper)', { symbol });
  document.getElementById('pair-summary').textContent = action === 'remove'
    ? t('The engine stops trading {symbol}: no new orders, prices no longer polled. It refuses if you still hold {coin} or have an open order in it.', { symbol, coin: symbol.split('-')[0] })
    : t('The engine checks that the exchange lists {symbol} as active and that it traded enough over the last 24 hours, then starts loading its prices. The scanner can signal it once it has a day of hourly candles. Every order in it goes through the same safety limits.', { symbol });
  document.getElementById('pair-send').textContent = action === 'remove' ? t('Remove pair') : t('Add pair');
  document.getElementById('pair-error').hidden = true;
  document.getElementById('pair-send').disabled = false;
  document.getElementById('pair-dialog').showModal();
}

async function sendPair(event) {
  event.preventDefault();
  const form = document.getElementById('pair-form');
  const body = { id: pairChoice.id, action: pairChoice.action, symbol: pairChoice.symbol, tradingMode: 'paper' };
  const note = form.elements.note.value.trim();
  if (note) body.note = note;
  const error = document.getElementById('pair-error');
  const send = document.getElementById('pair-send');
  const label = send.textContent;
  send.disabled = true;
  for (const input of form.querySelectorAll('input')) input.disabled = true; // from the first send on, a retry sends the same request
  send.textContent = t('Sending…');
  try {
    const response = await api('/api/owner/pair-requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ? t(answer.error) : t('The dashboard answered {status}.', { status: response.status }));
    document.getElementById('pair-dialog').close();
    trackPair(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = t('Not confirmed: {error} Send again to retry: it stays the same, single request.', { error: e.message });
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = label;
  }
}

/** Follows a pair change until the engine has decided it (it checks requests every 30 seconds). */
function trackPair(item) {
  state.pairPending = true;
  const since = Date.now();
  const label = t(item.action === 'remove' ? 'Removing {symbol}' : 'Adding {symbol}', { symbol: item.symbol });
  requestStatus(t('{label}: sent, waiting for the engine…', { label }), 'pending');
  refreshPairs();
  const timer = setInterval(async () => {
    const stop = (text, tone) => {
      clearInterval(timer);
      state.pairPending = false;
      requestStatus(text, tone);
      refreshPairs();
    };
    if (Date.now() - since > 5 * 60_000) { stop(t('{label}: no answer after 5 minutes; see Your pair changes.', { label }), 'warn'); return; }
    try {
      const response = await api(`/api/owner/pair-requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`);
      if (!response.ok) return; // not readable yet, or offline for a moment
      const it = await response.json();
      if (it.status === 'new') return;
      stop(`${label}: ${t(it.status)}. ${it.detail ? t(it.detail) : ''}`.trim(), it.status === 'accepted' ? 'ok' : 'bad');
    } catch (error) {
      if (error instanceof SignedOut) { clearInterval(timer); signedOut(); }
    }
  }, POLL_MS * 2);
}

async function showJournalDay(day, kind) {
  try {
    const entries = await getJson(`/api/journal?day=${encodeURIComponent(day)}&kind=${encodeURIComponent(kind)}&limit=200`);
    state.journal = { day, kind, entries };
    renderJournal();
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    banner(t("Couldn't load the journal: {error}", { error: error.message }), true);
  }
}

function renderJournal() {
  const { day, kind, entries } = state.journal;
  renderActivity(entries, kind ? t('No {kind} entries on {day}.', { kind, day }) : t('No entries on {day}.', { day }));
}

// --- Tabs ---------------------------------------------------------------------------------------------------------------------

const TABS = ['overview', 'coins', 'strategy', 'activity', 'requests', 'journal'];

/** Shows one tab's panel. The tab is kept in the address (#coins), so a reload or a bookmark opens the same one. */
function setTab(tab, { focus = false } = {}) {
  const name = TABS.includes(tab) ? tab : 'overview';
  for (const button of document.querySelectorAll('[role="tab"]')) {
    const selected = button.dataset.tab === name;
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
    if (selected && focus) button.focus();
  }
  if (!state.signedOut) for (const panel of document.querySelectorAll('.tab-panel')) panel.hidden = panel.id !== `panel-${name}`;
  if (location.hash !== `#${name}`) history.replaceState(null, '', `#${name}`);
  const changed = state.tab !== name;
  state.tab = name;
  if (changed && name === 'strategy') refreshStrategies(); // read only while the tab is open
  if (changed && name === 'coins') refreshPairs();
}

function setupTabs() {
  const buttons = [...document.querySelectorAll('[role="tab"]')];
  for (const button of buttons) {
    button.addEventListener('click', () => { setTab(button.dataset.tab); window.scrollTo({ top: 0 }); });
    button.addEventListener('keydown', (event) => { // arrow keys move between tabs, as for any tab list
      const step = { ArrowRight: 1, ArrowLeft: -1, Home: -Infinity, End: Infinity }[event.key];
      if (step === undefined) return;
      event.preventDefault();
      const i = buttons.indexOf(button);
      const next = Number.isFinite(step) ? (i + step + buttons.length) % buttons.length : step < 0 ? 0 : buttons.length - 1;
      setTab(buttons[next].dataset.tab, { focus: true });
    });
  }
  window.addEventListener('hashchange', () => setTab(location.hash.slice(1)));
  setTab(location.hash.slice(1));
}

// --- Language ------------------------------------------------------------------------------------------------------------

/** The page's own words (index.html): each text node and title, aria-label or placeholder that has a translation, with its English. */
const statics = [];

function collectStatics() {
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const key = node.nodeValue.replace(/\s+/g, ' ').trim(); // the HTML's line breaks aren't part of the words
    if (key && I.has(key)) statics.push({ node, key, lead: /^\s*/.exec(node.nodeValue)[0], trail: /\s*$/.exec(node.nodeValue)[0] });
  }
  for (const node of document.querySelectorAll('[title], [aria-label], [placeholder]')) {
    for (const attr of ['title', 'aria-label', 'placeholder']) {
      const key = node.getAttribute(attr);
      if (key && I.has(key)) statics.push({ node, attr, key });
    }
  }
  statics.push({ title: true, key: document.title });
}

function translateStatics() {
  for (const s of statics) {
    if (s.title) document.title = t(s.key);
    else if (s.attr) s.node.setAttribute(s.attr, t(s.key));
    else s.node.nodeValue = `${s.lead}${t(s.key)}${s.trail}`; // a node a render has since replaced is detached: no harm
  }
}

/**
 * The whole page in Russian or English at once: its own words, everything rendered from the last answers, and the content
 * that comes in both (summaries, news, explanations). Coin tickers, figures and the engine's technical text stay as they are.
 */
function setLang(lang) {
  state.lang = V.contentLang(lang);
  I.setLang(state.lang);
  document.documentElement.lang = state.lang;
  try { localStorage.setItem('cix-lang', state.lang); } catch { /* storage may be unavailable */ }
  for (const button of document.querySelectorAll('[data-lang]')) button.setAttribute('aria-pressed', String(button.dataset.lang === state.lang));
  translateStatics();
  renderModeBadge();
  renderViewer();
  if (state.view) renderDashboard(state.view);
  else if (!state.signedOut && !state.loadError) document.getElementById('updated').textContent = t('Loading…');
  if (state.journal) renderJournal();
  if (state.activity) renderVolume(state.activity);
  else if (state.mode !== 'paper') refreshActivity();
  if (state.requests) renderRequests(state.requests);
  if (state.strategies) renderStrategies(state.strategies);
  if (state.pairs) renderPairs(state.pairs);
  if (state.signedOut) signedOut();
  else if (state.loadError) {
    banner(t("Couldn't load the dashboard: {error}", { error: state.loadError }), true);
    document.getElementById('updated').textContent = t('Update failed');
  }
  if (document.getElementById('request-dialog').open) updateSummary();
}

function renderModeBadge() {
  const badge = document.getElementById('mode-badge');
  badge.textContent = t(state.mode);
  badge.className = `badge ${state.mode === 'live' ? 'live' : ''}`;
}

function setMode(mode) {
  state.mode = mode;
  state.view = null;
  state.strategies = null;
  state.pairs = null;
  state.activity = null;
  try { localStorage.setItem('cix-mode', mode); } catch { /* storage may be unavailable */ }
  for (const button of document.querySelectorAll('[data-mode]')) button.setAttribute('aria-pressed', String(button.dataset.mode === mode));
  renderModeBadge();
  refresh();
}

document.addEventListener('DOMContentLoaded', async () => {
  let saved = 'paper';
  try { saved = localStorage.getItem('cix-mode') === 'live' ? 'live' : 'paper'; } catch { /* default */ }
  for (const button of document.querySelectorAll('[data-mode]')) button.addEventListener('click', () => setMode(button.dataset.mode));
  let lang = null;
  try { lang = localStorage.getItem('cix-lang'); } catch { /* default */ }
  collectStatics(); // before anything renders: only the page's own English words
  state.mode = saved;
  for (const button of document.querySelectorAll('[data-lang]')) button.addEventListener('click', () => setLang(button.dataset.lang));
  setLang(lang);
  document.getElementById('refresh').addEventListener('click', refresh);
  const dayInput = document.getElementById('journal-day');
  dayInput.value = new Date().toISOString().slice(0, 10);
  document.getElementById('journal-filter').addEventListener('submit', (event) => {
    event.preventDefault();
    showJournalDay(dayInput.value, document.getElementById('journal-kind').value);
  });
  document.getElementById('journal-recent').addEventListener('click', () => {
    state.journal = null;
    refresh();
  });
  let period = 'day';
  try { period = ['day', 'week', 'month'].includes(localStorage.getItem('cix-period')) ? localStorage.getItem('cix-period') : 'day'; } catch { /* default */ }
  state.period = period;
  for (const button of document.querySelectorAll('[data-period]')) {
    button.setAttribute('aria-pressed', String(button.dataset.period === period));
    button.addEventListener('click', () => setPeriod(button.dataset.period));
  }
  setupTabs();
  const form = document.getElementById('request-form');
  form.addEventListener('submit', sendRequest);
  form.addEventListener('input', updateSummary);
  form.addEventListener('change', updateSummary);
  document.getElementById('request-cancel').addEventListener('click', () => document.getElementById('request-dialog').close());
  document.getElementById('strategy-form').addEventListener('submit', sendStrategy);
  document.getElementById('strategy-cancel').addEventListener('click', () => document.getElementById('strategy-dialog').close());
  document.getElementById('pairs-add').addEventListener('submit', (event) => {
    event.preventDefault();
    openPair('add', document.getElementById('pairs-choice').value);
  });
  document.getElementById('pair-form').addEventListener('submit', sendPair);
  document.getElementById('pair-cancel').addEventListener('click', () => document.getElementById('pair-dialog').close());
  await loadViewer();
  setMode(saved);
  state.timer = setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
});
