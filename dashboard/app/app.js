'use strict';
// CIX dashboard: reads /api/dashboard (and /api/journal) and renders it. Read-only: there is no request here that changes
// anything. Every value is written with textContent, never as HTML, so journal text can't inject markup.

const REFRESH_MS = 30_000;
const state = { mode: 'paper', journal: null };

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
  rows('decisions', view.decisions, (d) => [
    when(d.at), d.actor, d.action ?? '–', d.symbol ?? '–', d.side ?? '–', td(amount(d.size, 0), 'num'),
    el('span', { class: `badge ${{ placed: 'ok', rejected: 'bad', failed: 'bad', uncertain: 'warn' }[d.status] ?? ''}`, text: d.status ?? '–' }),
    td(d.rationale ?? d.summary ?? '', 'why'),
  ], 'No decisions in the last 48 hours.');
  rows('problems', view.problems, (p) => [when(p.at), p.source, p.kind, td(p.message, 'why')], 'Nothing went wrong in the last 48 hours.');
  if (!state.journal) renderActivity(view.activity, 'No journal entries in the last 48 hours.');
  rows('state', view.stateDocuments, (s) => [el('span', { class: 'mono', text: s.id }), s.type ?? '–', when(s.updatedAt), td(s.about ?? '', 'why')],
    'No state documents.');
}

function renderActivity(entries, emptyText) {
  rows('activity', entries, (e) => [when(e.at), e.kind, e.actor, shortId(e.runId), td(e.summary, 'why'), shortId(e.id)], emptyText);
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
    renderTiles(view);
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

document.addEventListener('DOMContentLoaded', () => {
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
  setMode(saved);
  setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
});
