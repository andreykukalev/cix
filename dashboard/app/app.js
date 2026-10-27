'use strict';
// CIX dashboard: reads /api/dashboard, /api/activity, /api/journal, /api/owner/requests, /api/owner/strategies and /api/owner/pairs
// and renders them, and sends paper buy and sell requests (ADR-0011, ADR-0012), strategy switches (#108, ADR-0015) and pair
// additions and removals (ADR-0016): the only writes,
// each one confirmed first. The whole site is the owner's
// (ADR-0014): the platform sends anyone else to sign in, and the API answers only the confirmed owner.
// Every value is written with textContent, never as HTML, so journal text can't inject markup. view.js holds the logic.

const REFRESH_MS = 30_000;
const POLL_MS = 3_000;
const POLL_FOR_MS = 15 * 60_000;
const V = window.CixView;
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

function ago(iso) {
  const t = ms(iso);
  if (t === null) return '–';
  const s = Math.round((Date.now() - t) / 1000);
  if (s < 0) return 'just now';
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

function when(iso) {
  const t = ms(iso);
  if (t === null) return el('span', { class: 'muted', text: '–' });
  const d = new Date(t);
  const sameDay = d.toDateString() === new Date().toDateString();
  const text = sameDay ? d.toLocaleTimeString() : d.toLocaleString();
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
  tile('tile-engine', engineTone, { running: 'Running', stale: 'Not responding', unknown: 'Unknown' }[e.state],
    e.heartbeatAt ? [`heartbeat ${ago(e.heartbeatAt)}`, e.strategy ? ` · ${e.strategy}` : ''] : 'no status record yet');

  tile('tile-trading', e.halted ? 'bad' : e.halted === false ? 'ok' : 'warn', e.halted ? 'Halted' : e.halted === false ? 'Active' : '–',
    e.halted ? `${e.haltCategory ?? ''} ${e.haltedSince ? `since ${ago(e.haltedSince)}` : ''}`.trim() : view.mode === 'live' ? 'real money' : 'paper money');

  const mv = e.marketView;
  const regime = mv?.active ? mv.regime : 'Neutral';
  tile('tile-view', regime === 'RiskOff' ? 'warn' : 'ok', { RiskOn: 'Risk-on', RiskOff: 'Risk-off', Neutral: 'Neutral' }[regime] ?? regime,
    mv?.active ? `until ${new Date(mv.validUntil).toLocaleTimeString()}${mv.avoid?.length ? ` · avoid ${mv.avoid.join(', ')}` : ''}` : 'no active view');

  const quoteAge = ms(e.oldestQuoteAt) === null ? null : (Date.now() - ms(e.oldestQuoteAt)) / 1000;
  tile('tile-data', quoteAge === null ? 'warn' : quoteAge > 120 ? 'bad' : 'ok', quoteAge === null ? '–' : quoteAge > 120 ? 'Stale' : 'Fresh',
    [e.oldestQuoteAt ? `oldest price ${ago(e.oldestQuoteAt)}` : 'no prices yet', e.unquotedSymbols?.length ? ` · none for ${e.unquotedSymbols.join(', ')}` : '']);

  const s = view.sync;
  const syncAge = ms(s.exchange) === null ? null : (Date.now() - ms(s.exchange)) / 1000;
  tile('tile-sync', s.reconcileProblem ? 'bad' : syncAge === null ? 'warn' : syncAge > 600 ? 'warn' : 'ok',
    s.exchange ? ago(s.exchange) : '–',
    [s.reconcileProblem ? says(s.reconcileProblem, s.reconcileProblemRu) : 'portfolio with the exchange', s.database ? ` · database ${ago(s.database)}` : '']);
}

function renderPortfolio(view) {
  const p = view.portfolio;
  document.getElementById('valued-at').textContent = p.valuedAt ? `valued ${ago(p.valuedAt)}` : 'not valued yet';
  document.getElementById('figures').replaceChildren(
    figure('Equity', money(p.equity, p.currency)),
    figure('Cash', money(p.cash, p.currency)),
    figure('P&L today', money(p.pnlToday, p.currency), signed(p.pnlToday)),
    figure('Peak equity', money(p.peakEquity, p.currency)),
    figure('Drawdown', p.drawdownPct === null || p.drawdownPct === undefined ? '–' : `${p.drawdownPct.toFixed(2)} %`, p.drawdownPct > 0 ? 'neg' : ''),
    figure('Exposure', money(p.exposure, p.currency)),
  );
  rows('holdings', p.holdings ?? [], (h) => [
    h.symbol, td(money(h.valueQuote, p.currency), 'num'), td(price(h.entryPrice), 'num'),
    td(h.protected ? price(h.stopLoss) : el('span', { class: 'badge bad', text: 'none' }), 'num'), td(price(h.takeProfit), 'num'), h.planStatus ?? '–',
  ], 'Nothing held.');
  rows('balances', p.balances ?? [], (b) => [b.currency, td(amount(b.available), 'num'), td(amount(b.reserved), 'num')],
    view.mode === 'paper' ? 'No paper account yet.' : 'Balances are shown for paper trading only.');
}

function renderCycle(view) {
  const c = view.cycle;
  const scan = c.lastScan;
  document.getElementById('cycle').replaceChildren(
    figure('Last scan', scan ? ago(scan.at) : '–'),
    figure('Coins ranked', scan ? `${scan.ranked} of ${Number(scan.ranked) + Number(scan.excluded)}` : '–'),
    figure('Signals', scan ? `${scan.entrySignals} buy · ${scan.exitSignals} sell` : '–'),
    figure('Watching', scan?.signalKey || 'no signals'),
    figure('Last trader run', c.lastTraderRun ? ago(c.lastTraderRun.lastAt) : '–'),
  );
  rows('orders', c.ordersInFlight ?? [], (o) => [when(o.sentAt), o.symbol, o.side, td(amount(o.size, 0), 'num'), td(price(o.limitPrice), 'num'), o.source, shortId(o.clientOrderId)], 'No orders working.');
  const agents = Object.entries(view.agents ?? {});
  rows('agents', agents, ([name, a]) => [
    name, a.lastRun ? shortId(a.lastRun.runId) : '–', a.lastRun ? when(a.lastRun.lastAt) : '–',
    td(String(a.runsToday), 'num'), td(String(a.lastRun?.calls ?? 0), 'num'), td(String(a.lastRun?.orders ?? 0), 'num'),
  ], 'No agent runs in the last 48 hours.');
}

function renderTables(view) {
  rows('trades', view.trades, (t) => [
    when(t.at), t.symbol ?? '–', t.side ?? '–', td(amount(t.filledSize, 0), 'num'), td(price(t.averagePrice), 'num'), td(price(t.fee), 'num'),
    t.source ?? '–', t.status ?? '–', shortId(t.clientOrderId),
  ], 'No fills in the last 48 hours.');
  rows('decisions', view.decisions, (d) => [
    when(d.at), d.actor, d.action ?? '–', d.symbol ?? '–', d.side ?? '–', td(amount(d.size, 0), 'num'), td(price(d.price), 'num'),
    el('span', { class: `badge ${{ placed: 'ok', rejected: 'bad', failed: 'bad', uncertain: 'warn' }[d.status] ?? ''}`, text: d.status ?? '–' }),
    td(why(d), 'why'),
  ], 'No decisions in the last 48 hours.');
  rows('problems', view.problems, (p) => [when(p.at), p.source, p.kind, td(says(p.message, p.messageRu), 'why')], 'Nothing went wrong in the last 48 hours.');
  if (!state.journal) renderActivity(view.activity, 'No journal entries in the last 48 hours.');
  rows('state', view.stateDocuments, (s) => [el('span', { class: 'mono', text: s.id }), s.type ?? '–', when(s.updatedAt), td(s.about ?? '', 'why')],
    'No state documents.');
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
  if (x.raw) wrap.append(el('details', { class: 'raw', lang: 'en' }, el('summary', { text: 'Technical' }), el('p', { class: 'mono', text: x.raw })));
  return wrap;
}

function renderActivity(entries, emptyText) {
  rows('activity', entries, (e) => [when(e.at), e.kind, e.actor, shortId(e.runId), td(e.summary, 'why'), shortId(e.id)], emptyText);
}

const DIRECTION_LABEL = { risk: 'Risk', opportunity: 'Opportunity', watch: 'Watch' };
const DIRECTION_TONE = { risk: 'bad', opportunity: 'ok', watch: 'warn' };
const SEVERITY_LABEL = { 3: 'high', 2: 'medium', 1: 'low' };

/** An external link: https only (the API checks too), opened in a new tab without access back to this page. */
function sourceLink(url, host) {
  let href = null;
  try { href = new URL(url).protocol === 'https:' ? url : null; } catch { /* not a URL */ }
  return href ? el('a', { href, target: '_blank', rel: 'noopener noreferrer nofollow', text: host || 'source' }) : null;
}

function chips(symbols) {
  return (symbols ?? []).map((s) => el('span', { class: 'chip', text: s }));
}

function renderBriefing(view) {
  const b = view.briefing ?? {};
  const a = b.analyst;
  document.getElementById('briefing-meta').textContent = [
    a ? `analyst ${ago(a.recordedAt)}${a.active ? '' : ' (view expired)'}` : 'no analyst view in 24 hours',
    b.scanAt ? `scan ${ago(b.scanAt)}${b.scanStale ? ' (stale)' : ''}` : 'no scan yet',
  ].join(' · ');

  const summary = document.getElementById('analyst-summary');
  summary.replaceChildren(...(a ? [
    el('span', { class: `badge ${a.regime === 'risk-off' ? 'warn' : a.regime === 'risk-on' ? 'ok' : ''}`, text: a.regime ?? 'unknown' }), ' ',
    says(a.summary ?? '', a.summaryRu),
    a.avoid?.length && a.active ? el('span', {}, ' Avoid: ', ...chips(a.avoid)) : null,
  ].filter((n) => n !== null) : ['The analyst has not recorded a view in the last 24 hours.']));

  const highlights = a?.highlights ?? [];
  document.getElementById('highlights').replaceChildren(...(highlights.length
    ? highlights.map((h) => el('li', { class: `card ${h.direction}` },
      el('div', { class: 'head' },
        el('span', { class: `badge ${DIRECTION_TONE[h.direction] ?? ''}`, text: DIRECTION_LABEL[h.direction] ?? h.direction }),
        el('span', { class: 'badge', text: `${h.impact} impact` }),
        says(h.title, h.titleRu, 'title'),
        ...chips(h.symbols)),
      h.detail ? el('p', { class: 'detail' }, says(h.detail, h.detailRu)) : null,
      el('p', { class: 'meta' }, sourceLink(h.source, h.sourceHost))))
    : [el('li', { class: 'muted', text: a ? 'No major news or events flagged.' : '' })]));

  const attention = b.attention ?? [];
  document.getElementById('attention').replaceChildren(...(attention.length
    ? attention.map((item) => el('li', { class: `card ${item.direction}` },
      el('div', { class: 'head' },
        el('span', { class: `badge ${DIRECTION_TONE[item.direction] ?? ''}`, text: DIRECTION_LABEL[item.direction] ?? item.direction }),
        el('span', { class: 'chip', text: item.symbol }),
        item.held ? el('span', { class: 'badge', text: 'held' }) : null,
        el('span', { class: 'muted small', text: `${SEVERITY_LABEL[item.severity] ?? ''} priority` })),
      el('ul', { class: 'reasons' }, item.reasons.map((r, i) => el('li', {}, says(r, item.reasonsRu?.[i]))))))
    : [el('li', { class: 'muted', text: b.market?.length ? 'Nothing unusual in the latest scan.' : 'No scan yet.' })]));

}

function move(c) {
  return el('span', { class: `move ${c.dir}`, 'aria-label': c.label, title: c.label, text: c.text });
}

function renderCoins(view) {
  const b = view.briefing ?? {};
  const scan = new Map((b.market ?? []).map((r) => [r.symbol, r]));
  document.getElementById('coins-meta').textContent = b.scanAt
    ? `moves up to the scan at ${new Date(b.scanAt).toLocaleTimeString()} (${ago(b.scanAt)})${b.scanStale ? ', stale' : ''}`
    : 'no scan yet';
  const trading = state.mode === 'paper'; // requests are paper only
  const blocked = trading ? V.requestsBlocked(view.engine) : null;
  if (blocked) document.getElementById('coins-meta').textContent += ` · requests paused: ${blocked}`;
  for (const node of document.querySelectorAll('#coins .paper-only')) node.hidden = !trading;
  rows('coins', V.coins(view), (c) => {
    const r = scan.get(c.symbol) ?? {};
    const cells = [
      el('span', { class: 'chip', text: c.symbol }),
      c.held ? el('span', { class: 'badge ok', text: 'held' }) : el('span', { class: 'muted', text: '–' }),
      td(move(c.hour), 'num'), td(move(c.day), 'num'),
      td(c.rank === null ? '–' : String(c.rank), 'num'), r.trend ?? '–',
      td(r.volatility === null || r.volatility === undefined ? '–' : `${(r.volatility * 100).toFixed(1)}%`, 'num'),
      td(r.volumeRatio === null || r.volumeRatio === undefined ? '–' : `${r.volumeRatio.toFixed(1)}×`, 'num'),
      c.signal ? el('span', { class: `badge ${c.signal === 'buy' ? 'ok' : 'bad'}`, text: c.signal }) : '–',
      td(c.note ?? '', 'muted'),
    ];
    if (trading) cells.push(td(tradeButtons(c), 'actions'));
    return cells;
  }, 'No scan yet.');
}

function tradeButtons(c) {
  const busy = [...state.pending.values()].some((p) => p.symbol === c.symbol);
  const blocked = V.requestsBlocked(state.view?.engine);
  const buy = el('button', { type: 'button', class: 'buy', 'aria-label': `Buy ${c.symbol}`, text: 'Buy' });
  buy.addEventListener('click', () => openRequest('buy', c));
  const sell = el('button', { type: 'button', class: 'sell', 'aria-label': `Sell ${c.symbol}`, text: 'Sell' });
  sell.addEventListener('click', () => openRequest('sell', c));
  if (busy || blocked) {
    buy.disabled = true; sell.disabled = true;
    buy.title = sell.title = blocked ?? 'A request for this coin is in progress.';
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
  constructor() { super('Your sign-in has ended.'); }
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
  if (response.status === 403) throw new Error(body.error ?? "This account isn't the dashboard's confirmed owner (run setup-dashboard-requests.ps1 again).");
  if (!response.ok) throw new Error(body.error ?? `The dashboard API answered ${response.status}.`);
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
  banner('Your sign-in has ended.', true, { href: `${SIGN_IN}${encodeURIComponent(location.hash)}`, text: 'Sign in again' });
  document.getElementById('updated').textContent = 'Signed out';
}

async function refresh() {
  if (state.signedOut) return;
  try {
    const view = await getJson(`/api/dashboard?mode=${encodeURIComponent(state.mode)}`);
    state.view = view;
    renderTiles(view);
    renderBriefing(view);
    renderCoins(view);
    renderPortfolio(view);
    renderCycle(view);
    renderTables(view);
    const readErrors = view.problems.filter((p) => p.kind === 'read');
    banner(view.engine.state === 'stale'
      ? `The engine hasn't reported for ${ago(view.engine.heartbeatAt).replace(' ago', '')}: it may be stopped, or unable to reach the database.`
      : readErrors.length ? readErrors.map((p) => p.message).join(' ') : null, view.engine.state === 'stale');
    document.getElementById('updated').textContent = `Updated ${new Date(view.generatedAt).toLocaleTimeString()}`;
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    banner(`Couldn't load the dashboard: ${error.message}`, true);
    document.getElementById('updated').textContent = 'Update failed';
  }
  if (!state.signedOut) await Promise.all([refreshActivity(), refreshRequests(), state.tab === 'strategy' ? refreshStrategies() : null, state.tab === 'coins' ? refreshPairs() : null]);
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
    meta.textContent = 'Activity is counted for paper trading only for now.';
    document.getElementById('volume-cards').replaceChildren();
    document.getElementById('volume-chart').replaceChildren(svg('title', { id: 'volume-chart-title' }, 'No data'));
    return;
  }
  try {
    renderVolume(await getJson(`/api/activity?period=${encodeURIComponent(state.period)}`));
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    meta.textContent = `Couldn't load trading activity: ${error.message}`;
  }
}

function renderVolume(a) {
  const c = a.current;
  const p = a.previous ?? {};
  const unit = { day: 'today', week: 'this week', month: 'this month' }[a.period];
  const before = { day: 'yesterday', week: 'last week', month: 'last month' }[a.period];
  document.getElementById('volume-meta').textContent =
    `${c.label} (${unit}, UTC) against ${p.label ?? 'nothing'} (${before}). ${a.total.trades} trades, turnover ${a.total.turnover} ${a.currency} from ${a.total.from} to ${a.total.to}.${a.truncated ? ' Not all fills could be counted: the totals are incomplete.' : ''}`;
  const card = (label, value, now, prev, sub) => {
    const vs = V.versus(now, prev);
    return el('div', { class: 'figure' },
      el('span', { class: 'label', text: label }),
      el('span', { class: 'amount', text: value }),
      el('span', { class: sub ? 'vs' : `vs ${vs.dir}`, text: sub ?? vs.text }));
  };
  document.getElementById('volume-cards').replaceChildren(
    card('Turnover', `${c.turnover} ${a.currency}`, c.turnover, p.turnover),
    card('Bought', `${c.buyVolume} ${a.currency}`, c.buyVolume, p.buyVolume),
    card('Sold', `${c.sellVolume} ${a.currency}`, c.sellVolume, p.sellVolume),
    card('Trades', String(c.trades), c.trades, p.trades, `${c.buys} buys · ${c.sells} sells`),
    card('Your requests', String(c.manual), c.manual, p.manual, 'trades from the dashboard'),
    card('Fees', `${c.fees} ${a.currency}`, c.fees, p.fees, 'not in turnover'),
  );
  const g = V.chart(a.periods, { width: 640, height: 160, labelEvery: a.periods.length > 12 ? 2 : 1, period: a.period });
  const chartNode = document.getElementById('volume-chart');
  chartNode.replaceChildren(svg('title', { id: 'volume-chart-title' }, `Buy and sell volume by ${a.period}, largest ${g.max.toFixed(2)} ${a.currency}`));
  document.getElementById('volume-unit').textContent = `volume in ${a.currency}`;
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
  document.getElementById('who').textContent = state.viewer.signedIn ? state.viewer.name ?? 'Signed in' : '';
}

async function refreshRequests() {
  try {
    const history = await getJson('/api/owner/requests');
    renderRequests(history.requests);
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    rows('requests', [], () => [], `Couldn't load your requests: ${error.message}`);
  }
}

function renderRequests(items) {
  rows('requests', items, (r) => {
    const st = V.requestState(r);
    const steps = el('details', {}, el('summary', { text: `${r.events.length} step${r.events.length === 1 ? '' : 's'}` }),
      el('ol', { class: 'steps' }, r.events.map((e) => el('li', {}, when(e.at), ` ${e.status ?? ''} by ${e.by ?? '?'}${e.detail ? `: ${e.detail}` : ''}`))));
    return [
      when(r.createdAt), V.what(r), r.mode === 'direct' ? 'direct' : 'trader',
      el('span', { class: `badge ${st.tone}`, text: st.label }),
      td([st.filled ? `Filled ${st.filled}. ` : '', st.detail ?? (st.final ? '' : 'in progress')].join(''), 'why'),
      steps,
    ];
  }, 'No requests in the last 7 days.');
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
  document.getElementById('request-title').textContent = `${side === 'buy' ? 'Buy' : 'Sell'} ${coin.symbol} (paper)`;
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
  send.textContent = 'Sending…';
  try {
    const response = await api('/api/owner/requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(r.body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ?? `The dashboard answered ${response.status}.`);
    document.getElementById('request-dialog').close();
    track(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = `Not confirmed: ${e.message} Send again to retry: it stays the same, single request. To change it, cancel and start a new one.`;
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = 'Send request';
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
    const extra = [st.filled ? `filled ${st.filled}` : null, st.detail].filter(Boolean).join(' · ');
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
      requestStatus(`${V.what(item)}: still in progress after 15 minutes; see the Requests tab.`, 'warn');
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
    document.getElementById('strategy-note').textContent = `Couldn't load the strategies: ${error.message}`;
  }
}

function renderStrategies(view) {
  const running = view.strategies.find((s) => s.active) ?? null;
  const sel = V.selectionState(view.selection);
  document.getElementById('strategy-meta').textContent = view.active?.since ? `since ${new Date(view.active.since).toLocaleString()}` : '';
  document.getElementById('strategy-running').replaceChildren(
    figure('Strategy', running ? V.strategyName(running) : view.active ? view.active.label : '–'),
    figure('Selection', view.selection ? `${sel.label}${view.selection.requested ? ` · ${view.selection.requested}` : ''}` : 'not reported', sel.tone === 'bad' ? 'neg' : ''),
    figure('Checked', view.selection?.checkedAt ? ago(view.selection.checkedAt) : '–'),
  );
  const problems = view.selection?.problems ?? [];
  document.getElementById('strategy-note').textContent = [
    view.readOnlyReason,
    problems.length ? problems.join(' ') : null,
    running?.riskProfile ?? null,
  ].filter(Boolean).join(' ');

  // One card per strategy that trades, Safe, Balanced and Risky first.
  const tradable = view.strategies.filter((s) => s.canTrade);
  const byKey = Object.fromEntries(view.groups.flatMap((g) => g.parameters).map((p) => [p.key, p]));
  const facts = [['orderQuote', 'Largest order'], ['maxPositionQuotePerSymbol', 'Most in one coin'], ['maxExposureFraction', 'Most in coins'],
    ['dailyLossStopQuote', 'Daily loss stop'], ['entryCooldownMinutes', 'Cooldown']];
  document.getElementById('strategy-cards').replaceChildren(...(tradable.length ? tradable.map((s) => {
    const button = el('button', { type: 'button', class: s.active ? 'ghost' : 'primary', text: s.active ? 'Running' : `Switch to ${V.strategyName(s)}` });
    button.disabled = s.active || !view.editable || state.strategyPending;
    if (!view.editable) button.title = view.readOnlyReason;
    button.addEventListener('click', () => openStrategy(s));
    return el('li', { class: `card ${s.active ? 'running' : ''}` },
      el('div', { class: 'head' }, el('span', { class: 'title', text: V.strategyName(s) }), s.active ? el('span', { class: 'badge ok', text: 'running' }) : null,
        s.featured ? null : el('span', { class: 'badge', text: 'other' })),
      s.purpose ? el('p', { class: 'detail', text: s.purpose }) : null,
      s.riskProfile ? el('p', { class: 'meta muted small', text: s.riskProfile }) : null,
      el('dl', {}, facts.flatMap(([key, label]) => [el('dt', { text: label }), el('dd', { text: V.paramValue(s.values[key], byKey[key]) })])),
      el('div', { class: 'actions' }, button));
  }) : [el('li', { class: 'muted', text: 'No strategies in the database yet: the engine adds them when it starts.' })]));

  // Every parameter: one column per strategy that trades, then the range, what a change reaches, and whose limit it is.
  const head = document.querySelector('#params thead tr');
  head.replaceChildren(el('th', { text: 'Parameter' }), ...tradable.map((s) => el('th', { class: 'num', text: V.strategyName(s) })),
    el('th', { text: 'Valid range' }), el('th', { text: 'A change reaches' }), el('th', { text: 'Limit' }));
  const body = document.querySelector('#params tbody');
  const columns = tradable.length + 4;
  body.replaceChildren(...(view.groups.length ? view.groups.flatMap((g) => [
    el('tr', { class: 'group' }, el('td', { colspan: String(columns), text: g.title })),
    ...g.parameters.map((p) => el('tr', {},
      el('td', { class: 'param', 'data-label': 'Parameter' }, el('strong', { text: p.label }), el('p', { text: p.explanation })),
      ...tradable.map((s) => el('td', { class: `num ${s.active ? 'running' : ''}`, 'data-label': V.strategyName(s), text: V.paramValue(s.values[p.key], p) })),
      el('td', { 'data-label': 'Valid range', text: V.paramRange(p) }),
      el('td', { 'data-label': 'A change reaches', text: p.applies }),
      el('td', { 'data-label': 'Limit', text: V.paramLimit(p, view.mandatory) }))),
  ]) : [el('tr', { class: 'empty' }, el('td', { colspan: String(columns), text: 'The engine hasn\'t written the parameter schema yet.' }))]));

  rows('mandatory', view.mandatory, (m) => [
    el('strong', { text: m.label }), td(m.value, 'num'), td(m.explanation, 'why'),
    m.source === 'risk-limits' ? 'config/risk-limits.json (owner only)' : 'the engine (fixed)',
  ], 'Not reported yet.');

  rows('switches', view.requests, (r) => [
    when(r.createdAt), `${r.name}@v${r.version}${r.note ? ` · ${r.note}` : ''}`,
    el('span', { class: `badge ${{ accepted: 'ok', new: 'pending', superseded: '', invalid: 'bad', expired: 'warn' }[r.status] ?? 'warn'}`, text: r.status }),
    td(r.detail ?? (r.status === 'new' ? 'waiting for the engine' : ''), 'why'),
  ], view.editable ? 'No switches in the last 7 days.' : 'Live has no switches yet.');
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
  document.getElementById('strategy-title').textContent = `Switch to ${V.strategyName(target)} (paper)`;
  document.getElementById('strategy-purpose').textContent = [target.purpose, target.riskProfile].filter(Boolean).join(' ');
  const changes = V.strategyChanges(running, target, view.groups);
  document.getElementById('strategy-changes').replaceChildren(...(changes.length
    ? changes.map((c) => el('li', { class: c.openPositions ? 'open' : '' }, `${c.label}: ${c.from} → ${c.to}`, c.openPositions ? el('strong', { text: ' (open positions too)' }) : null))
    : [el('li', { text: 'No parameter differs from the running strategy.' })]));
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
  send.textContent = 'Sending…';
  try {
    const response = await api('/api/owner/strategy-requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(r.body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ?? `The dashboard answered ${response.status}.`);
    document.getElementById('strategy-dialog').close();
    trackStrategy(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = `Not confirmed: ${e.message} Send again to retry: it stays the same, single request.`;
    error.hidden = false;
    send.disabled = false;
  } finally {
    send.textContent = 'Switch strategy';
  }
}

/** Follows a switch until the engine has taken it, then until the engine status reports the outcome. */
function trackStrategy(item) {
  state.strategyPending = true;
  const since = Date.now();
  const label = `${item.name}@v${item.version}`;
  requestStatus(`Strategy switch to ${label}: sent, waiting for the engine…`, 'pending');
  if (state.strategies) renderStrategies(state.strategies);
  const timer = setInterval(async () => {
    const stop = (text, tone) => {
      clearInterval(timer);
      state.strategyPending = false;
      requestStatus(text, tone);
      refreshStrategies();
    };
    if (Date.now() - since > 5 * 60_000) { stop(`Strategy switch to ${label}: no answer after 5 minutes; see Your strategy switches.`, 'warn'); return; }
    try {
      const response = await api(`/api/owner/strategy-requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`);
      if (!response.ok) return; // not readable yet, or offline for a moment
      const it = await response.json();
      if (it.status === 'new') return;
      if (it.status !== 'accepted') { stop(`Strategy switch to ${label}: ${it.status}. ${it.detail ?? ''}`.trim(), 'bad'); return; }
      // Accepted: the engine validates and switches; its status reports within a minute.
      await refreshStrategies();
      const sel = state.strategies?.selection;
      // The status names this request when its time matches (an earlier switch to the same version has another).
      if (sel?.requested === label && Date.parse(sel.requestedAt ?? '') === Date.parse(item.createdAt) && ['active', 'refused'].includes(sel.state)) {
        stop(sel.state === 'active'
          ? `Strategy switch to ${label}: active. New trades follow it from the next scan.`
          : `Strategy switch to ${label}: refused. ${sel.problems.join(' ')}`, sel.state === 'active' ? 'ok' : 'bad');
      } else {
        requestStatus(`Strategy switch to ${label}: accepted; the engine applies it before the next trader run…`, 'pending');
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
    document.getElementById('pairs-note').textContent = `Couldn't load the trading pairs: ${error.message}`;
  }
}

function renderPairs(view) {
  const busy = !view.editable || state.pairPending;
  document.getElementById('pairs-meta').textContent = [
    view.cap ? `${view.tradable.length} of at most ${view.cap}` : `${view.tradable.length} tradable`,
    view.changedAt ? `changed ${ago(view.changedAt)}` : null,
  ].filter(Boolean).join(' · ');
  document.getElementById('pairs-note').textContent = view.readOnlyReason
    ?? (view.full ? 'The most pairs the safety limits allow are tradable: remove one to add another.'
      : view.openToAnyPair ? `You can add any ${view.quoteCurrency ?? ''} pair the exchange lists; the engine checks it has enough trading first.`.replace('  ', ' ')
        : 'You can turn the pairs in config/risk-limits.json off and on again here. Adding other coins needs that list widened first.');
  document.getElementById('pairs-tradable').replaceChildren(...(view.tradable.length ? view.tradable.map((p) => {
    const remove = el('button', { type: 'button', class: 'ghost', title: `Stop trading ${p.symbol}`, text: p.pending === 'remove' ? 'removing…' : 'Remove' });
    remove.disabled = busy || !!p.pending || view.tradable.length <= 1;
    remove.addEventListener('click', () => openPair('remove', p.symbol));
    return el('li', {}, el('span', { text: p.symbol }), remove);
  }) : [el('li', { class: 'muted', text: 'No pairs listed yet.' })]));

  const form = document.getElementById('pairs-add');
  const choice = document.getElementById('pairs-choice');
  const options = view.available.filter((p) => !p.pending);
  form.hidden = !view.editable;
  choice.replaceChildren(...(options.length
    ? options.map((p) => el('option', { value: p.symbol, text: p.symbol }))
    : [el('option', { value: '', text: 'none available' })]));
  choice.disabled = busy || view.full || !options.length;
  document.getElementById('pairs-add-button').disabled = choice.disabled;

  rows('pair-requests', view.requests, (r) => [
    when(r.createdAt), `${r.action === 'remove' ? 'Remove' : 'Add'} ${r.symbol ?? '?'}${r.note ? ` · ${r.note}` : ''}`,
    el('span', { class: `badge ${{ accepted: 'ok', new: 'pending', invalid: 'bad', rejected: 'bad', expired: 'warn' }[r.status] ?? 'warn'}`, text: r.status }),
    td(r.detail ?? (r.status === 'new' ? 'waiting for the engine' : ''), 'why'),
  ], view.editable ? 'No pair changes in the last 7 days.' : 'Live has no pair changes.');
}

let pairChoice = null;

function openPair(action, symbol) {
  if (!state.pairs?.editable || !symbol) return;
  pairChoice = { action, symbol, id: crypto.randomUUID() }; // one id per dialog: a retry is the same request
  const form = document.getElementById('pair-form');
  form.reset();
  for (const input of form.querySelectorAll('input')) input.disabled = false;
  document.getElementById('pair-title').textContent = `${action === 'remove' ? 'Remove' : 'Add'} ${symbol} (paper)`;
  document.getElementById('pair-summary').textContent = action === 'remove'
    ? `The engine stops trading ${symbol}: no new orders, prices no longer polled. It refuses if you still hold ${symbol.split('-')[0]} or have an open order in it.`
    : `The engine checks that the exchange lists ${symbol} as active and that it traded enough over the last 24 hours, then starts loading its prices. The scanner can signal it once it has a day of hourly candles. Every order in it goes through the same safety limits.`;
  document.getElementById('pair-send').textContent = action === 'remove' ? 'Remove pair' : 'Add pair';
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
  send.textContent = 'Sending…';
  try {
    const response = await api('/api/owner/pair-requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(body),
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ?? `The dashboard answered ${response.status}.`);
    document.getElementById('pair-dialog').close();
    trackPair(answer.request);
  } catch (e) {
    if (e instanceof SignedOut) { signedOut(); return; }
    error.textContent = `Not confirmed: ${e.message} Send again to retry: it stays the same, single request.`;
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
  const label = `${item.action === 'remove' ? 'Removing' : 'Adding'} ${item.symbol}`;
  requestStatus(`${label}: sent, waiting for the engine…`, 'pending');
  refreshPairs();
  const timer = setInterval(async () => {
    const stop = (text, tone) => {
      clearInterval(timer);
      state.pairPending = false;
      requestStatus(text, tone);
      refreshPairs();
    };
    if (Date.now() - since > 5 * 60_000) { stop(`${label}: no answer after 5 minutes; see Your pair changes.`, 'warn'); return; }
    try {
      const response = await api(`/api/owner/pair-requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`);
      if (!response.ok) return; // not readable yet, or offline for a moment
      const it = await response.json();
      if (it.status === 'new') return;
      stop(`${label}: ${it.status}. ${it.detail ?? ''}`.trim(), it.status === 'accepted' ? 'ok' : 'bad');
    } catch (error) {
      if (error instanceof SignedOut) { clearInterval(timer); signedOut(); }
    }
  }, POLL_MS * 2);
}

async function showJournalDay(day, kind) {
  try {
    const entries = await getJson(`/api/journal?day=${encodeURIComponent(day)}&kind=${encodeURIComponent(kind)}&limit=200`);
    state.journal = { day, kind };
    renderActivity(entries, `No ${kind || ''} entries on ${day}.`.replace('  ', ' '));
  } catch (error) {
    if (error instanceof SignedOut) { signedOut(); return; }
    banner(`Couldn't load the journal: ${error.message}`, true);
  }
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

/** The language of the financial content (summaries, news, explanations); the page itself stays in English. */
function setLang(lang) {
  state.lang = V.contentLang(lang);
  try { localStorage.setItem('cix-lang', state.lang); } catch { /* storage may be unavailable */ }
  for (const button of document.querySelectorAll('[data-lang]')) button.setAttribute('aria-pressed', String(button.dataset.lang === state.lang));
  if (state.view) {
    renderTiles(state.view);
    renderBriefing(state.view);
    renderTables(state.view);
  }
}

function setMode(mode) {
  state.mode = mode;
  state.strategies = null;
  state.pairs = null;
  try { localStorage.setItem('cix-mode', mode); } catch { /* storage may be unavailable */ }
  for (const button of document.querySelectorAll('[data-mode]')) button.setAttribute('aria-pressed', String(button.dataset.mode === mode));
  const badge = document.getElementById('mode-badge');
  badge.textContent = mode;
  badge.className = `badge ${mode === 'live' ? 'live' : ''}`;
  refresh();
}

document.addEventListener('DOMContentLoaded', async () => {
  let saved = 'paper';
  try { saved = localStorage.getItem('cix-mode') === 'live' ? 'live' : 'paper'; } catch { /* default */ }
  for (const button of document.querySelectorAll('[data-mode]')) button.addEventListener('click', () => setMode(button.dataset.mode));
  let lang = null;
  try { lang = localStorage.getItem('cix-lang'); } catch { /* default */ }
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
