'use strict';
// The market briefing at the top of the dashboard: the few things worth a look before stepping in by hand. Two sources,
// both already in Cosmos DB:
//   - the analyst's latest market view (a `market-view` journal entry): regime, reasoning, avoided coins and up to five
//     highlights (important news and events, each with a source link), which the analyst writes for the owner only;
//   - the latest scan, from the engine status (`lastScan.market`): each coin's moves, volatility, volume and signal.
// From the scan it picks out what needs attention: active buy or sell signals, big moves, volume spikes, unusual
// volatility and coins the analyst said to avoid. Pure: no I/O.
//
// The owner reads Russian best: from analyst v3 the view carries a Russian summary and Russian highlight titles and
// details next to the English ones, and each reason a coin needs attention comes in both languages. Russian text the
// analyst didn't write is simply missing (null), and the page falls back to the English. The engine leaves out a
// translation that doesn't fit; the caps here only guard the page, cutting anything longer as for the English.
//
// Nothing here is an order level. The analyst never sees the account's positions, stops or targets (its brief has no
// holdings), and the scan rows carry moves as fractions, not prices. Text from the analyst comes from the web, so it is
// length-capped here and the page renders it as text only; links must be https.

const { engineTextRu } = require('./engineText');

const MAX_HIGHLIGHTS = 5;
const MAX_ATTENTION = 8;
const VIEW_MAX_AGE_MS = 24 * 3600 * 1000; // the API reads today's and yesterday's journal, so 24 hours is always covered
const SCAN_STALE_MS = 2 * 3600 * 1000; // the scan interval can be set up to an hour

// What counts as worth a look. Moves are fractions (0.05 = 5%).
const THRESHOLDS = Object.freeze({
  move24h: 0.05, // a 24h move of 5% or more
  bigMove24h: 0.1, // 10% or more is high severity
  move1h: 0.02, // a 1h move of 2% or more
  volumeRatio: 2.5, // last hour's volume at 2.5 times the usual hourly volume
  volatility: 0.02, // hourly range (ATR) of 2% of the price or more
});

// Exclusion codes from the scanner worth a look: [severity, direction, reason, reason in Russian].
const EXCLUSIONS = Object.freeze({
  'too-volatile': [2, 'risk', 'Excluded: too volatile to trade', 'Исключена: слишком волатильна для торговли'],
  'wide-spread': [2, 'risk', 'Excluded: spread too wide (thin market)', 'Исключена: слишком широкий спред (низкая ликвидность)'],
  stale: [1, 'watch', 'Excluded: no fresh market data', 'Исключена: нет свежих рыночных данных'],
});

const IMPACTS = new Set(['high', 'medium']);
const DIRECTIONS = new Set(['risk', 'opportunity', 'watch']);
const SYMBOL = /^[A-Z0-9]{1,15}-[A-Z0-9]{2,10}$/;

function time(value) {
  const t = Date.parse(value ?? '');
  return Number.isFinite(t) ? t : null;
}

function num(value) {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** Text for display: strings only, control and format characters removed, length capped. */
function text(value, max) {
  if (typeof value !== 'string') return null;
  const cleaned = value.replace(/[\p{Cc}\p{Cf}\p{Co}]/gu, '').trim();
  if (!cleaned) return null;
  return cleaned.length > max ? `${cleaned.slice(0, max - 1)}…` : cleaned;
}

/** An https link and its host, or null. */
function link(value) {
  if (typeof value !== 'string' || value.length > 300) return null;
  try {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.username || url.password) return null;
    return { url: url.href, host: url.hostname.replace(/^www\./, '') };
  } catch {
    return null;
  }
}

function symbols(value) {
  return Array.isArray(value) ? [...new Set(value.filter((s) => typeof s === 'string' && SYMBOL.test(s)))].slice(0, 10) : [];
}

/** The analyst's highlights from a market-view entry, checked again: the engine validated them, the page trusts no one. */
function highlights(raw) {
  let list;
  try {
    list = JSON.parse(raw ?? '[]');
  } catch {
    return [];
  }
  if (!Array.isArray(list)) return [];
  const out = [];
  for (const h of list) {
    if (out.length >= MAX_HIGHLIGHTS) break;
    const title = text(h?.title, 160);
    const source = link(h?.source);
    const impact = typeof h?.impact === 'string' ? h.impact.toLowerCase() : '';
    const direction = typeof h?.direction === 'string' ? h.direction.toLowerCase() : '';
    if (!title || !source || !IMPACTS.has(impact) || !DIRECTIONS.has(direction)) continue;
    out.push({
      title, impact, direction, symbols: symbols(h.symbols), detail: text(h.detail, 300), source: source.url, sourceHost: source.host,
      titleRu: text(h.titleRu, 200), detailRu: text(h.detailRu, 400),
    });
  }
  return out;
}

/** The latest market view in the window, or null. */
function analystView(entries, now) {
  const latest = (entries ?? [])
    .filter((e) => e?.kind === 'market-view' && time(e.at) !== null && now - time(e.at) <= VIEW_MAX_AGE_MS)
    .sort((a, b) => time(b.at) - time(a.at))[0];
  if (!latest) return null;
  const d = latest.data ?? {};
  const validUntil = time(d.validUntil) === null ? null : d.validUntil;
  return {
    recordedAt: latest.at,
    validUntil,
    active: validUntil !== null && time(validUntil) > now,
    regime: ['risk-on', 'neutral', 'risk-off'].includes(d.regime) ? d.regime : null,
    summary: text(latest.summary, 1000),
    summaryRu: text(d.summaryRu, 1500),
    avoid: symbols(String(d.avoid ?? '').split(',')),
    sources: String(d.sources ?? '').split(' ').map(link).filter(Boolean).slice(0, 10),
    highlights: highlights(d.highlights),
    specVersion: latest.specVersion ?? null,
  };
}

/** The latest scan's rows, as numbers. */
function marketRows(lastScan) {
  return (Array.isArray(lastScan?.market) ? lastScan.market : [])
    .filter((r) => typeof r?.symbol === 'string' && SYMBOL.test(r.symbol))
    .map((r) => ({
      symbol: r.symbol,
      rank: num(r.rank),
      trend: typeof r.trend === 'string' ? r.trend : null,
      return1h: num(r.return1h),
      return24h: num(r.return24h),
      volatility: num(r.volatility),
      volumeRatio: num(r.volumeRatio),
      signal: typeof r.signal === 'string' ? r.signal : null,
      note: text(r.note, 80),
      detail: text(r.detail, 300), // the scanner's figure and threshold behind the note
      detailRu: engineTextRu(text(r.detail, 300)),
    }));
}

const pct = (x) => `${x > 0 ? '+' : ''}${(x * 100).toFixed(1)}%`;
const sellWhy = (signal) => signal.replace(/^sell\s*/, '').replace(/[()]/g, '');
const SELL_WHY_RU = { stop: 'стоп-лосс', target: 'тейк-профит', 'trend-down': 'тренд развернулся вниз', exit: 'выход' };

/**
 * Coins that need a look, most pressing first: one item per coin with every reason (in English, and in Russian in
 * reasonsRu), a severity (3 high, 2 medium, 1 low) and a direction (risk, opportunity or watch).
 */
function attention(rows, avoid, held) {
  const items = [];
  const avoided = new Set(avoid);
  for (const r of rows) {
    const reasons = [];
    const reasonsRu = [];
    let severity = 0;
    const dirs = new Set();
    const add = (s, dir, why, whyRu) => {
      reasons.push(why);
      reasonsRu.push(whyRu);
      severity = Math.max(severity, s);
      dirs.add(dir);
    };
    if (r.signal === 'buy') add(2, 'opportunity', 'Buy signal from the scanner', 'Сканер дал сигнал на покупку');
    else if (r.signal?.startsWith('sell')) add(3, 'risk', `Sell signal: ${sellWhy(r.signal) || 'exit'}`, `Сигнал на продажу: ${SELL_WHY_RU[sellWhy(r.signal) || 'exit'] ?? sellWhy(r.signal)}`);
    if (r.return24h !== null && Math.abs(r.return24h) >= THRESHOLDS.move24h) {
      add(Math.abs(r.return24h) >= THRESHOLDS.bigMove24h ? 3 : 2, r.return24h < 0 ? 'risk' : 'opportunity', `${pct(r.return24h)} in 24h`, `${pct(r.return24h)} за 24 часа`);
    }
    if (r.return1h !== null && Math.abs(r.return1h) >= THRESHOLDS.move1h) {
      add(2, r.return1h < 0 ? 'risk' : 'opportunity', `${pct(r.return1h)} in the last hour`, `${pct(r.return1h)} за последний час`);
    }
    if (r.volumeRatio !== null && r.volumeRatio >= THRESHOLDS.volumeRatio) {
      add(1, 'watch', `Volume ${r.volumeRatio.toFixed(1)}× usual`, `Объём в ${r.volumeRatio.toFixed(1)} раза выше обычного`);
    }
    if (r.volatility !== null && r.volatility >= THRESHOLDS.volatility) {
      add(1, 'watch', `Volatile: ${(r.volatility * 100).toFixed(1)}% hourly range`, `Волатильность: часовой диапазон ${(r.volatility * 100).toFixed(1)}%`);
    }
    if (avoided.has(r.symbol)) add(2, 'risk', 'Analyst: avoid new entries', 'Аналитик: не открывать новые позиции');
    // Excluded coins carry no numbers, only why: the abnormal ones are often the ones to look at.
    if (r.rank === null && Object.hasOwn(EXCLUSIONS, r.note ?? '')) add(...EXCLUSIONS[r.note]);
    if (severity === 0) continue;
    // A held coin falling matters more than one we don't own.
    if (held.has(r.symbol) && dirs.has('risk')) severity = Math.min(3, severity + 1);
    const direction = dirs.has('risk') ? 'risk' : dirs.has('opportunity') ? 'opportunity' : 'watch';
    items.push({ symbol: r.symbol, severity, direction, held: held.has(r.symbol), reasons, reasonsRu });
  }
  return items
    .sort((a, b) => b.severity - a.severity || b.reasons.length - a.reasons.length || a.symbol.localeCompare(b.symbol))
    .slice(0, MAX_ATTENTION);
}

/**
 * @param {object|null} status The engine status document's value.
 * @param {object[]} entries Journal entries (any kinds); the latest market view among them is used.
 * @param {number} now Milliseconds since the epoch.
 */
function briefing(status, entries, now) {
  const view = analystView(entries, now);
  const rows = marketRows(status?.lastScan);
  const scanAt = status?.lastScan?.at ?? null;
  const held = new Set((status?.portfolio?.holdings ?? []).map((h) => h?.symbol).filter(Boolean));
  const avoid = view?.active ? view.avoid : [];
  return {
    analyst: view,
    scanAt,
    scanStale: scanAt === null || time(scanAt) === null || now - time(scanAt) > SCAN_STALE_MS,
    attention: attention(rows, avoid, held),
    market: rows,
    thresholds: THRESHOLDS,
  };
}

module.exports = { briefing, analystView, highlights, attention, marketRows, link, THRESHOLDS, MAX_ATTENTION };
