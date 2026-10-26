'use strict';
// CIX dashboard: reads /api/dashboard, /api/activity and /api/journal and renders them. For the signed-in owner it also
// reads /api/owner/* and sends paper buy and sell requests (ADR-0011, ADR-0012): the only writes, each one confirmed first.
// Every value is written with textContent, never as HTML, so journal text can't inject markup. view.js holds the logic.

const REFRESH_MS = 30_000;
const POLL_MS = 3_000;
const POLL_FOR_MS = 15 * 60_000;
const V = window.CixView;
const state = { mode: 'paper', journal: null, period: 'day', viewer: { signedIn: false, owner: false, name: null }, view: null, pending: new Map() };

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

function money(value, currency) {
  return value === null || value === undefined ? '–' : `${amount(value)} ${currency ?? ''}`.trim();
}

function rows(tableId, items, render, emptyText) {
  const body = document.querySelector(`#${tableId} tbody`);
  const columns = document.querySelectorAll(`#${tableId} thead th`).length;
  body.replaceChildren(...(items.length
    ? items.map((item) => el('tr', {}, render(item).map((cell) => (cell instanceof Node && cell.tagName === 'TD' ? cell : el('td', {}, cell)))))
    : [el('tr', { class: 'empty' }, el('td', { colspan: String(columns), text: emptyText }))]));
}

function td(content, cls) {
  return el('td', { class: cls }, content);
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
    [s.reconcileProblem ?? 'portfolio with the exchange', s.database ? ` · database ${ago(s.database)}` : '']);
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
  rows('holdings', p.holdings ?? [], (h) => [h.symbol, td(money(h.valueQuote, p.currency), 'num'), h.planStatus ?? '–', h.protected ? 'yes' : 'no'], 'Nothing held.');
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
  rows('orders', c.ordersInFlight ?? [], (o) => [when(o.sentAt), o.symbol, o.side, td(amount(o.size, 0), 'num'), o.source, shortId(o.clientOrderId)], 'No orders working.');
  const agents = Object.entries(view.agents ?? {});
  rows('agents', agents, ([name, a]) => [
    name, a.lastRun ? shortId(a.lastRun.runId) : '–', a.lastRun ? when(a.lastRun.lastAt) : '–',
    td(String(a.runsToday), 'num'), td(String(a.lastRun?.calls ?? 0), 'num'), td(String(a.lastRun?.orders ?? 0), 'num'),
  ], 'No agent runs in the last 48 hours.');
}

function renderTables(view) {
  rows('trades', view.trades, (t) => [
    when(t.at), t.symbol ?? '–', t.side ?? '–', td(amount(t.filledSize, 0), 'num'), t.source ?? '–', t.status ?? '–', shortId(t.clientOrderId),
  ], 'No fills in the last 48 hours.');
  rows('decisions', view.decisions, (d) => [ // for the owner, replaced by /api/owner/decisions (with levels) when that loads
    when(d.at), d.actor, d.action ?? '–', d.symbol ?? '–', d.side ?? '–', td(amount(d.size, 0), 'num'),
    el('span', { class: `badge ${{ placed: 'ok', rejected: 'bad', failed: 'bad', uncertain: 'warn' }[d.status] ?? ''}`, text: d.status ?? '–' }),
    td(why(d), 'why'),
  ], 'No decisions in the last 48 hours.');
  rows('problems', view.problems, (p) => [when(p.at), p.source, p.kind, td(p.message, 'why')], 'Nothing went wrong in the last 48 hours.');
  if (!state.journal) renderActivity(view.activity, 'No journal entries in the last 48 hours.');
  rows('state', view.stateDocuments, (s) => [el('span', { class: 'mono', text: s.id }), s.type ?? '–', when(s.updatedAt), td(s.about ?? '', 'why')],
    'No state documents.');
}

/** The reason first, then the evidence, the risk plan and what happened; the technical text folded away under it. */
function why(d) {
  const x = d.explanation;
  if (!x) return d.rationale ?? d.summary ?? '';
  const wrap = el('div', { class: 'explain' },
    el('p', { class: 'headline', text: x.headline }),
    x.points?.length ? el('ul', { class: 'points' }, x.points.map((p) => el('li', { text: p }))) : null,
    x.risk ? el('p', { class: 'risk', text: x.risk.text }) : null,
    x.outcome ? el('p', { class: 'outcome', text: x.outcome }) : null);
  if (x.raw) wrap.append(el('details', { class: 'raw' }, el('summary', { text: 'Technical' }), el('p', { class: 'mono', text: x.raw })));
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
    a.summary ?? '',
    a.avoid?.length && a.active ? el('span', {}, ' Avoid: ', ...chips(a.avoid)) : null,
  ].filter((n) => n !== null) : ['The analyst has not recorded a view in the last 24 hours.']));

  const highlights = a?.highlights ?? [];
  document.getElementById('highlights').replaceChildren(...(highlights.length
    ? highlights.map((h) => el('li', { class: `card ${h.direction}` },
      el('div', { class: 'head' },
        el('span', { class: `badge ${DIRECTION_TONE[h.direction] ?? ''}`, text: DIRECTION_LABEL[h.direction] ?? h.direction }),
        el('span', { class: 'badge', text: `${h.impact} impact` }),
        el('span', { class: 'title', text: h.title }),
        ...chips(h.symbols)),
      h.detail ? el('p', { class: 'detail', text: h.detail }) : null,
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
      el('ul', { class: 'reasons' }, item.reasons.map((r) => el('li', { text: r })))))
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
  const owner = state.viewer.owner && state.mode === 'paper';
  const blocked = owner ? V.requestsBlocked(view.engine) : null;
  if (blocked) document.getElementById('coins-meta').textContent += ` · requests paused: ${blocked}`;
  for (const node of document.querySelectorAll('#coins .owner-only')) node.hidden = !owner;
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
    if (owner) cells.push(td(tradeButtons(c), 'actions'));
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

function banner(text, bad) {
  const node = document.getElementById('banner');
  node.hidden = !text;
  node.textContent = text ?? '';
  node.className = `banner ${bad ? 'bad' : ''}`;
}

async function getJson(url) {
  const response = await fetch(url, { headers: { accept: 'application/json' } });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error ?? `The dashboard API answered ${response.status}.`);
  return body;
}

async function refresh() {
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
    banner(`Couldn't load the dashboard: ${error.message}`, true);
    document.getElementById('updated').textContent = 'Update failed';
  }
  await Promise.all([refreshActivity(), refreshOwner()]);
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
  const meta = document.getElementById('volume-meta');
  if (state.mode !== 'paper') {
    meta.textContent = 'Activity is counted for paper trading only for now.';
    document.getElementById('volume-cards').replaceChildren();
    document.getElementById('volume-chart').replaceChildren(svg('title', { id: 'volume-chart-title' }, 'No data'));
    return;
  }
  try {
    // Amounts are the owner's only (with public trade sizes they'd give fill prices away); everyone else gets counts.
    const base = state.viewer.owner ? '/api/owner/activity' : '/api/activity';
    const a = await getJson(`${base}?period=${encodeURIComponent(state.period)}`);
    renderVolume(a);
  } catch (error) {
    meta.textContent = `Couldn't load trading activity: ${error.message}`;
  }
}

function renderVolume(a) {
  const c = a.current;
  const p = a.previous ?? {};
  const unit = { day: 'today', week: 'this week', month: 'this month' }[a.period];
  const before = { day: 'yesterday', week: 'last week', month: 'last month' }[a.period];
  const totals = a.amounts ? `${a.total.trades} trades, turnover ${a.total.turnover} ${a.currency}` : `${a.total.trades} trades`;
  document.getElementById('volume-meta').textContent =
    `${c.label} (${unit}, UTC) against ${p.label ?? 'nothing'} (${before}). ${totals} from ${a.total.from} to ${a.total.to}.${a.amounts ? '' : ' Amounts are shown to the owner only.'}${a.truncated ? ' Not all fills could be counted: the totals are incomplete.' : ''}`;
  const card = (label, value, now, prev, sub) => {
    const vs = V.versus(now, prev);
    return el('div', { class: 'figure' },
      el('span', { class: 'label', text: label }),
      el('span', { class: 'amount', text: value }),
      el('span', { class: sub ? 'vs' : `vs ${vs.dir}`, text: sub ?? vs.text }));
  };
  document.getElementById('volume-cards').replaceChildren(...(a.amounts ? [
    card('Turnover', `${c.turnover} ${a.currency}`, c.turnover, p.turnover),
    card('Bought', `${c.buyVolume} ${a.currency}`, c.buyVolume, p.buyVolume),
    card('Sold', `${c.sellVolume} ${a.currency}`, c.sellVolume, p.sellVolume),
    card('Trades', String(c.trades), c.trades, p.trades, `${c.buys} buys · ${c.sells} sells`),
    card('Your requests', String(c.manual), c.manual, p.manual, 'trades from the dashboard'),
    card('Fees', `${c.fees} ${a.currency}`, c.fees, p.fees, 'not in turnover'),
  ] : [
    card('Trades', String(c.trades), c.trades, p.trades),
    card('Buys', String(c.buys), c.buys, p.buys),
    card('Sells', String(c.sells), c.sells, p.sells),
    card('Owner requests', String(c.manual), c.manual, p.manual, 'trades from the dashboard'),
  ]));
  const g = V.chart(a.periods, { width: 640, height: 160, labelEvery: a.periods.length > 12 ? 2 : 1, period: a.period });
  const chartNode = document.getElementById('volume-chart');
  chartNode.replaceChildren(svg('title', { id: 'volume-chart-title' }, g.amounts
    ? `Buy and sell volume by ${a.period}, largest ${g.max.toFixed(2)} ${a.currency}`
    : `Buys and sells by ${a.period}, largest ${g.max} trades`));
  document.getElementById('volume-unit').textContent = g.amounts ? `volume in ${a.currency}` : 'number of trades';
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

// --- The owner: sign-in, decisions with levels, requests --------------------------------------------------------------------

async function loadViewer() {
  try {
    const response = await fetch('/.auth/me', { headers: { accept: 'application/json' } });
    state.viewer = V.viewer(response.ok ? await response.json() : null);
  } catch {
    state.viewer = V.viewer(null);
  }
  if (state.viewer.owner) {
    // The role alone isn't enough: the API takes only the confirmed account (setup-dashboard-requests.ps1).
    try {
      const probe = await fetch('/api/owner/requests', { headers: { accept: 'application/json' } });
      if (probe.status === 403) state.viewer = { ...state.viewer, owner: false, unconfirmed: true };
    } catch { /* offline: the buttons' requests will say */ }
  }
  const who = document.getElementById('who');
  const link = document.getElementById('sign-in');
  if (state.viewer.signedIn) {
    who.textContent = state.viewer.owner ? `Owner: ${state.viewer.name ?? 'signed in'}`
      : state.viewer.unconfirmed ? `Signed in as ${state.viewer.name ?? '?'}: owner role, not confirmed yet (run the setup script again)`
        : `Signed in as ${state.viewer.name ?? 'a guest'} (not the owner)`;
    link.textContent = 'Sign out';
    link.href = '/.auth/logout?post_logout_redirect_uri=/';
  }
  for (const node of document.querySelectorAll('section.owner-only')) node.hidden = !state.viewer.owner;
}

async function refreshOwner() {
  if (!state.viewer.owner) return;
  if (state.mode === 'paper') {
    try {
      const own = await getJson('/api/owner/decisions');
      rows('decisions', own.decisions, (d) => [
        when(d.at), d.actor, d.action ?? '–', d.symbol ?? '–', d.side ?? '–', td(amount(d.size, 0), 'num'),
        el('span', { class: `badge ${{ placed: 'ok', rejected: 'bad', failed: 'bad', uncertain: 'warn' }[d.status] ?? ''}`, text: d.status ?? '–' }),
        td(why(d), 'why'),
      ], 'No decisions in the last 48 hours.');
      document.getElementById('decisions-meta').textContent = 'the reason first, with stops, targets and risk in figures (only you see these)';
    } catch { /* the public decisions stay */ }
  }
  try {
    const history = await getJson('/api/owner/requests');
    renderRequests(history.requests);
  } catch (error) {
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
    const response = await fetch('/api/owner/requests', {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'application/json', 'x-cix-request': '1' },
      body: JSON.stringify(r.body),
      credentials: 'same-origin',
    });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error ?? `The dashboard answered ${response.status}.`);
    document.getElementById('request-dialog').close();
    track(answer.request);
  } catch (e) {
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
    refreshOwner();
  };
  entry.timer = setInterval(async () => {
    if (Date.now() - entry.since > POLL_FOR_MS) {
      requestStatus(`${V.what(item)}: still in progress after 15 minutes; see Your requests.`, 'warn');
      stop();
      return;
    }
    try {
      const response = await fetch(`/api/owner/requests/${encodeURIComponent(item.createdAt.slice(0, 10))}/${encodeURIComponent(item.id)}`, { headers: { accept: 'application/json' } });
      if (response.status === 404) return; // not readable yet
      if (!response.ok) return; // try again next time
      if (show(await response.json())) stop();
    } catch { /* offline for a moment: try again */ }
  }, POLL_MS);
}

async function showJournalDay(day, kind) {
  try {
    const entries = await getJson(`/api/journal?day=${encodeURIComponent(day)}&kind=${encodeURIComponent(kind)}&limit=200`);
    state.journal = { day, kind };
    renderActivity(entries, `No ${kind || ''} entries on ${day}.`.replace('  ', ' '));
  } catch (error) {
    banner(`Couldn't load the journal: ${error.message}`, true);
  }
}

function setMode(mode) {
  state.mode = mode;
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
  const journal = document.getElementById('journal-panel');
  try { journal.open = localStorage.getItem('cix-journal-open') === 'true'; } catch { /* closed */ }
  journal.addEventListener('toggle', () => { try { localStorage.setItem('cix-journal-open', String(journal.open)); } catch { /* fine */ } });
  const form = document.getElementById('request-form');
  form.addEventListener('submit', sendRequest);
  form.addEventListener('input', updateSummary);
  form.addEventListener('change', updateSummary);
  document.getElementById('request-cancel').addEventListener('click', () => document.getElementById('request-dialog').close());
  await loadViewer();
  setMode(saved);
  setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
});
