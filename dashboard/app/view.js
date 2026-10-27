'use strict';
// What the page shows, worked out from the API's answers: pure functions, no DOM, so they are tested in Node
// (dashboard/api/test/view.test.js) and used by app.js in the browser as window.CixView. Words go through i18n.js (t), so
// they come in the page's language; in Node it is English unless a test sets another.

(function expose(root, factory) {
  const i18n = root.CixI18n ?? (typeof require === 'function' ? require('./i18n.js') : null);
  const api = factory(i18n);
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.CixView = api;
}(typeof window !== 'undefined' ? window : globalThis, (i18n) => {
  const t = i18n ? i18n.t : (text, vars) => String(text).replace(/\{(\w+)\}/g, (m, k) => (vars && k in vars ? String(vars[k]) : m));
  const SYMBOL = /^[A-Z0-9]{2,12}-[A-Z]{3,4}$/;

  /** A move as an arrow, a signed percentage and words for screen readers; flat under 0.05%. */
  function change(fraction, period) {
    if (fraction === null || fraction === undefined || !Number.isFinite(Number(fraction))) {
      return { text: '–', dir: 'none', label: t('no {period} change yet', { period: t(period) }) };
    }
    const pct = Number(fraction) * 100;
    const rounded = Math.abs(pct) < 0.05 ? 0 : pct;
    const dir = rounded > 0 ? 'up' : rounded < 0 ? 'down' : 'flat';
    const arrow = { up: '▲', down: '▼', flat: '■' }[dir];
    const sign = rounded > 0 ? '+' : '';
    const move = dir === 'flat' ? t('flat') : t(dir === 'up' ? 'up {pct}%' : 'down {pct}%', { pct: Math.abs(rounded).toFixed(2) });
    return { text: `${arrow} ${sign}${rounded.toFixed(rounded === 0 ? 1 : Math.abs(rounded) < 10 ? 2 : 1)}%`, dir, label: t('{move} over {period}', { move, period: t(period) }) };
  }

  // Why a coin got no buy signal, in three kinds the page keeps apart: filtered out by the market filters before ranking
  // (the scanner's exclusions, except the analyst's news flag), turned down by the strategy's entry rules, or held back by
  // a limit or a block although the rules were not what stopped it (the signal limit, the strategy's pause, a halt, a news
  // flag, a holding or an order).
  const BLOCKS = new Set(['trading halted', 'risk-off (market view)', 'already held', 'open order', 'signal limit reached', 'strategy exposure cap reached']);
  const BLOCK_DETAILS = {
    'trading halted': 'Trading is halted, so the scanner suggests no buys.',
    'risk-off (market view)': "The market analyst's view is risk-off: no new buys until it ends.",
    'already held': 'Already in the portfolio: the strategy opens no second position in a coin.',
    'open order': 'An order in this coin is still working.',
    'strategy exposure cap reached': "The strategy's total exposure cap is used up: no new buys until something is sold.",
  };
  const WHY_LABELS = { filtered: 'Filtered out', rule: 'Strategy rule', blocked: 'Held back', pending: 'New pair' };

  /** Why there is no buy signal for a scan row: its kind, a label for the kind, the short reason and the sentence behind it. */
  function why(r, lang) {
    const note = typeof r?.note === 'string' && r.note ? r.note : null;
    if (!note || r.signal === 'buy') return null;
    const said = pick(r.detail ?? null, r.detailRu ?? null, lang).text;
    const kind = note === 'news' || BLOCKS.has(note) || /^cooldown until /.test(note) ? 'blocked'
      : r.rank === null || r.rank === undefined ? 'filtered' : 'rule';
    const detail = said ?? (Object.hasOwn(BLOCK_DETAILS, note) ? t(BLOCK_DETAILS[note])
      : /^cooldown until /.test(note) ? t('The strategy waits between buys.') : null);
    return { kind, label: t(WHY_LABELS[kind]), text: t(note), detail };
  }

  /**
   * One row per coin: every coin in the latest scan, then the tradable pairs the scan hasn't covered yet (just added: the
   * engine scans them within a minute), then anything held that neither lists, held ones first. The moves come from the
   * scan (the reference time is the scan's); holdings say what is held; pairs is the Coins tab's /api/owner/pairs answer.
   */
  function coins(view, pairs, lang) {
    const market = Array.isArray(view?.briefing?.market) ? view.briefing.market : [];
    const holdings = new Map((view?.portfolio?.holdings ?? []).filter((h) => SYMBOL.test(h?.symbol ?? '')).map((h) => [h.symbol, h]));
    const row = (symbol, rest) => ({
      symbol, quote: symbol.split('-')[1], rank: null, signal: null, note: null, why: null,
      hour: change(null, '1 hour'), day: change(null, '24 hours'), held: holdings.has(symbol), value: holdings.get(symbol)?.valueQuote ?? null, inScan: false, ...rest,
    });
    const rows = market.filter((r) => SYMBOL.test(r?.symbol ?? '')).map((r) => row(r.symbol, {
      rank: r.rank ?? null,
      signal: r.signal ?? null,
      note: r.note ?? null,
      why: why(r, lang),
      hour: change(r.return1h, '1 hour'),
      day: change(r.return24h, '24 hours'),
      inScan: true,
    }));
    const listed = new Set(rows.map((r) => r.symbol));
    for (const p of Array.isArray(pairs?.tradable) ? pairs.tradable : []) {
      if (!SYMBOL.test(p?.symbol ?? '') || listed.has(p.symbol)) continue;
      listed.add(p.symbol);
      const note = t('waiting for its first scan');
      rows.push(row(p.symbol, {
        note,
        why: { kind: 'pending', label: t(WHY_LABELS.pending), text: note, detail: t('Tradable now: its prices are loading, and the engine scans a new pair within a minute of adding it.') },
      }));
    }
    for (const [symbol] of holdings) {
      if (!listed.has(symbol)) rows.push(row(symbol, { note: t('not in the latest scan') }));
    }
    return rows.sort((a, b) => (Number(b.held) - Number(a.held)) || ((a.rank ?? 1e9) - (b.rank ?? 1e9)) || a.symbol.localeCompare(b.symbol));
  }

  /**
   * Why a pair can't be removed now, or null. The engine checks again and has the final word; this says it before the
   * owner asks: any balance in the coin (however small: a residual below the exchange minimum counts) or an order working.
   */
  function removeBlock(symbol, view) {
    const coin = String(symbol ?? '').split('-')[0];
    const held = (view?.portfolio?.holdings ?? []).find((h) => h?.symbol === symbol);
    if (held) {
      return t('There is still a {coin} balance (about {value} {quote}). A pair can be removed only when its balance is zero: sell it first.',
        { coin, value: held.valueQuote ?? '?', quote: String(symbol).split('-')[1] ?? '' });
    }
    if ((view?.cycle?.ordersInFlight ?? []).some((o) => o?.symbol === symbol)) {
      return t('An order in {symbol} is still working: cancel it or let it fill first.', { symbol });
    }
    return null;
  }

  const PORTIONS = { '0.25': '25%', '0.5': 'half', '1': 'all' };
  const SELL_DESCRIPTIONS = { // one per portion: in Russian the words around it change with it
    '0.25': 'Sell 25% of your {symbol} position (paper). {how} The risk gate checks the order either way.',
    '0.5': 'Sell half of your {symbol} position (paper). {how} The risk gate checks the order either way.',
    '1': 'Sell all of your {symbol} position (paper). {how} The risk gate checks the order either way.',
  };

  /**
   * The request the page sends for a choice in the dialog, or an error message. id is a fresh UUID per click: sending the
   * same id again (a retry) is the same request, never a second one.
   */
  function requestBody(choice, id) {
    const { side, mode, symbol } = choice;
    if (!SYMBOL.test(symbol ?? '')) return { error: t('Pick a coin.') };
    if (side === 'sell') {
      if (!(String(choice.portion) in PORTIONS)) return { error: t('Pick how much to sell.') };
      if (mode !== 'direct' && mode !== 'agent') return { error: t('Pick how to sell.') };
      return { body: { id, side, mode, symbol, portion: String(choice.portion), note: cleanNote(choice.note), tradingMode: 'paper' } };
    }
    if (side === 'buy') {
      const amount = String(choice.quoteAmount ?? '').trim().replace(',', '.');
      if (!/^\d+(\.\d{1,2})?$/.test(amount) || !(Number(amount) > 0)) return { error: t('Enter an amount to buy for, like 25.') };
      if (Number(amount) > 1000) return { error: t('At most 1000 per request.') };
      return { body: { id, side, mode: 'agent', symbol, quoteAmount: amount, note: cleanNote(choice.note), tradingMode: 'paper' } };
    }
    return { error: t('Pick buy or sell.') };
  }

  function cleanNote(note) {
    const text = String(note ?? '').replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim();
    return text ? Array.from(text).slice(0, 280).join('') : undefined;
  }

  /** What the confirmation says will happen, in words. */
  function describe(body) {
    const quote = body.symbol.split('-')[1];
    if (body.side === 'sell') {
      const how = body.mode === 'direct'
        ? t('The engine sells at the bid now, without asking the trader.')
        : t('The trader analyses it first and may decline or sell less.');
      return t(SELL_DESCRIPTIONS[body.portion], { symbol: body.symbol, how });
    }
    return t('Ask the trader to buy {symbol} for {amount} {quote} (paper). It decides, sets a stop-loss, and may decline; the risk gate checks the order.', { symbol: body.symbol, amount: body.quoteAmount, quote });
  }

  const STATUS = {
    new: { label: 'Sent, waiting for the engine', tone: 'pending' },
    'awaiting-agent': { label: 'With the trader', tone: 'pending' },
    executing: { label: 'Sending the order', tone: 'pending' },
    placed: { label: 'Order placed, waiting for the fill', tone: 'pending' },
    unconfirmed: { label: "Sent; the exchange's answer was lost, being reconciled", tone: 'warn' },
    filled: { label: 'Filled', tone: 'ok' },
    closed: { label: 'Closed', tone: 'warn' },
    declined: { label: 'Declined by the trader', tone: 'warn' },
    rejected: { label: 'Refused', tone: 'bad' }, // by the engine or the risk gate: the rule says which
    failed: { label: 'Refused by the exchange', tone: 'bad' },
    invalid: { label: 'Not valid', tone: 'bad' },
    duplicate: { label: 'Duplicate of a request in progress', tone: 'warn' },
    expired: { label: 'Expired', tone: 'warn' },
  };

  /**
   * Where a request stands, for the page: a label, a tone, whether it's over, and the trader's or the engine's words, in the
   * page's language when they come in it, each said once (a decline's reason is its rationale: shown once, not twice).
   */
  function requestState(item, lang = i18n ? i18n.getLang() : 'en') {
    const known = STATUS[item?.status] ?? { label: item?.status ?? 'unknown', tone: 'warn' };
    const o = item?.outcome ?? {};
    const s = item?.status === 'rejected' && o.rule ? { ...known, label: t('Refused ({rule})', { rule: o.rule }) } : { ...known, label: t(known.label) };
    const rationale = typeof o.rationale === 'string' && o.rationale.trim() ? o.rationale.trim() : null;
    const reason = typeof o.reason === 'string' && o.reason.trim() && o.reason.trim() !== rationale ? o.reason.trim() : null;
    const said = [pick(rationale, o.rationaleRu, lang), pick(reason, o.reasonRu, lang)];
    const detail = said.map((p) => p.text).filter(Boolean).join(' · ') || null;
    // Shown in Russian, the English it came from stays a hover away: the trader writes its Russian apart from its English.
    const english = said.some((p) => p.lang === 'ru') ? [rationale, reason].filter(Boolean).join(' · ') : null;
    const filled = item?.status === 'filled' && o.filledSize ? (o.averagePrice ? t('{size} at {price}', { size: o.filledSize, price: o.averagePrice }) : String(o.filledSize)) : null;
    return { label: s.label, tone: s.tone, final: Boolean(item?.final), detail, english, filled };
  }

  /** A request's step as the page lists it: the engine's words in the page's language, and none where they repeat the rationale. */
  function stepDetail(event, item, lang = i18n ? i18n.getLang() : 'en') {
    const detail = typeof event?.detail === 'string' && event.detail.trim() ? event.detail.trim() : null;
    if (!detail || detail === String(item?.outcome?.rationale ?? '').trim()) return null;
    return pick(detail, event.detailRu, lang).text;
  }

  function what(item) {
    if (item.side === 'sell') return t('Sell {portion} {symbol}', { portion: PORTIONS[item.portion] ? t(PORTIONS[item.portion]) : item.portion ?? '', symbol: item.symbol ?? '' }).replace(/\s+/g, ' ').trim();
    return t('Buy {symbol} for {amount} {quote}', { symbol: item.symbol ?? '', amount: item.quoteAmount ?? '?', quote: String(item.symbol ?? '').split('-')[1] ?? '' }).trim();
  }

  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  /** A short axis label from a period's first day: "13 Oct" for days and weeks (the Monday), "Oct" for months. */
  function tick(from, period) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(from ?? '');
    if (!m) return '';
    const month = t(MONTHS[Number(m[2]) - 1]);
    const named = m[2] === '05' ? t('May (month)') : month; // a month on its own: in Russian May differs from "13 May"
    return period === 'month' ? (m[2] === '01' ? `${named} ${m[1]}` : named) : `${Number(m[3])} ${month}`;
  }

  /** Bars for the activity chart: buy and sell volume stacked per period, scaled to the largest turnover. */
  function chart(periods, { width = 640, height = 160, gap = 6, labelEvery = 1, period = 'day' } = {}) {
    const list = Array.isArray(periods) ? periods : [];
    const max = Math.max(0, ...list.map((p) => Number(p.turnover) || 0));
    const slot = list.length ? width / list.length : width;
    const bar = Math.max(2, slot - gap);
    const scale = (v) => (max > 0 ? ((Number(v) || 0) / max) * height : 0);
    return {
      width, height, max,
      bars: list.map((p, i) => {
        const buy = scale(p.buyVolume);
        const sell = scale(p.sellVolume);
        return {
          x: i * slot + gap / 2, width: bar,
          buy: { y: height - buy, height: buy },
          sell: { y: height - buy - sell, height: sell },
          label: i % labelEvery === 0 || i === list.length - 1 ? tick(p.from, period) : '',
          title: t(p.trades === 1 ? '{label}: {trades} trade, bought {buy}, sold {sell}' : '{label}: {trades} trades, bought {buy}, sold {sell}',
            { label: months(p.label), trades: p.trades, buy: p.buyVolume, sell: p.sellVolume }),
        };
      }),
    };
  }

  /** This period against the previous one, as a signed percentage, or a word when there's nothing to compare. */
  function versus(current, previous) {
    const now = Number(current);
    const before = Number(previous);
    if (!Number.isFinite(now) || !Number.isFinite(before)) return { text: '', dir: 'none' };
    if (before === 0) return now === 0 ? { text: t('same as before'), dir: 'flat' } : { text: t('none before'), dir: 'up' };
    const pct = ((now - before) / before) * 100;
    return { text: t('{change}% vs previous', { change: `${pct > 0 ? '▲ +' : pct < 0 ? '▼ ' : '■ '}${pct.toFixed(0)}` }), dir: pct > 0 ? 'up' : pct < 0 ? 'down' : 'flat' };
  }

  /** Whether the engine takes requests now, from the dashboard's engine section: a reason when it doesn't, else null. */
  function requestsBlocked(engine) {
    if (!engine || engine.state !== 'running') return t('The engine isn\'t running, so it can\'t take requests now.');
    const m = engine.manualRequests;
    if (!m) return t('This engine doesn\'t report manual requests (an older version?).');
    if (m.state === 'off') return t('Manual requests are switched off in the engine.');
    if (m.state !== 'ok') return t('The engine can\'t read requests right now (the requests container may be missing).');
    return null;
  }

  // --- Strategy tab (#108) ---------------------------------------------------------------------------------------------

  /** A parameter's value in words: on/off, a share as a percentage, minutes as hours, amounts with their unit. */
  function paramValue(value, param) {
    if (value === null || value === undefined || value === '') return '–';
    if (param?.kind === 'boolean') return value === true || value === 'true' ? t('on') : t('off');
    const n = Number(value);
    if (!Number.isFinite(n)) return String(value);
    const unit = param?.unit;
    if (unit === 'fraction') return `${+(n * 100).toFixed(3)}%`;
    if (unit === 'minutes') return n === 0 ? t('none') : n % 60 === 0 ? t('{n} h', { n: n / 60 }) : t('{n} min', { n });
    if (unit === 'EUR') return `${n} EUR`;
    if (unit === 'ATR') return `${n} ATR`;
    if (unit === 'candles') return t('{n} h', { n });
    return String(n);
  }

  /** A parameter's valid range, as the engine enforces it. */
  function paramRange(param) {
    if (param.kind === 'boolean') return t('on or off');
    if (param.min === null && param.max === null) return '–';
    return t('{min} to {max}', { min: paramValue(param.min, param), max: paramValue(param.max, param) });
  }

  /** Whose limit a parameter is: the strategy's own, capped by a mandatory safety limit where one applies. */
  function paramLimit(param, mandatory) {
    if (!param.boundBy) return t('Strategy-specific');
    const m = (mandatory ?? []).find((x) => x.key === param.boundBy);
    return t('Strategy-specific, within the mandatory {limit}', { limit: m ? `${t(m.label).toLowerCase()} (${m.value})` : param.boundBy });
  }

  /** What switching from one strategy to another changes, parameter by parameter, and whether open positions feel it. */
  function strategyChanges(from, to, groups) {
    const changes = [];
    for (const group of groups ?? []) {
      for (const p of group.parameters) {
        const a = from?.values?.[p.key] ?? null;
        const b = to?.values?.[p.key] ?? null;
        if (String(a) !== String(b)) changes.push({ key: p.key, label: t(p.label), from: paramValue(a, p), to: paramValue(b, p), openPositions: p.appliesToOpenPositions });
      }
    }
    return changes;
  }

  /** The switch request the dialog sends, or what is missing. */
  function strategyRequestBody(choice, id) {
    if (!/^[a-z0-9-]{1,40}$/.test(choice?.name ?? '') || !Number.isInteger(choice?.version)) return { error: t('Pick a strategy.') };
    return { body: { id, name: choice.name, version: choice.version, note: cleanNote(choice.note), tradingMode: 'paper' } };
  }

  /** A strategy's name for people: Safe v1 (Безопасная v1 in Russian, for the three featured ones). */
  function strategyName(s) {
    const name = String(s?.name ?? '');
    const title = PROFILES[name] ? t(PROFILES[name].title) : `${name.charAt(0).toUpperCase()}${name.slice(1)}`;
    return `${title} v${s?.version ?? '?'}`;
  }

  /** The three preset strategies (risk profiles); every other version is shown as Custom. */
  const PROFILES = {
    safe: { title: 'Safe', profile: 'Safe', tone: 'ok' },
    balanced: { title: 'Balanced', profile: 'Balanced', tone: '' },
    risky: { title: 'Risky', profile: 'Risky', tone: 'warn' },
  };

  const SELECTION = {
    config: { label: 'From configuration', tone: '' },
    active: { label: 'Active', tone: 'ok' },
    pending: { label: 'Switch pending', tone: 'pending' },
    refused: { label: 'Switch refused', tone: 'bad' },
    unreadable: { label: 'Database unreadable', tone: 'warn' },
  };

  /**
   * What the pairs panel says above the list, and what the empty add list shows, from /api/owner/pairs. When only
   * config/risk-limits.json's own pairs are permitted and all are tradable, it says what the owner adds to widen it.
   */
  function pairsNote(view) {
    const options = (view?.available ?? []).filter((p) => !p.pending);
    const ownerMustWiden = !!view?.configuredOnly && !options.length;
    const note = view?.readOnlyReason ? t(view.readOnlyReason)
      : ownerMustWiden ? t('No other pairs can be added yet: the safety limits permit only the pairs in config/risk-limits.json, and all of them are already tradable. To trade other coins, the owner adds "AllowedQuoteCurrency": "EUR" (any EUR pair the exchange lists) and "MaxTradableSymbols" (how many at once, for example 15) to config/risk-limits.json; once that is deployed, they can be added and removed here without another deploy.')
        : view?.full ? t('The most pairs the safety limits allow are tradable: remove one to add another.')
          : view?.openToAnyPair ? t('You can add any {quote} pair the exchange lists; the engine checks it has enough trading first.', { quote: view.quoteCurrency ?? '' }).replace('  ', ' ')
            : t('You can turn the pairs in config/risk-limits.json off and on again here. Adding other coins needs that list widened first.');
    const empty = view?.configuredOnly ? t('no other pairs permitted') : view?.full ? t('the limit is reached') : t('none available');
    return { note, empty, options };
  }

  /** Where the engine stands on the latest selection, for the badge. */
  function selectionState(selection) {
    const s = SELECTION[selection?.state] ?? { label: 'Unknown', tone: 'warn' };
    return { label: t(s.label), tone: s.tone };
  }

  /**
   * The strategy the engine runs, from the dashboard's engine section (engine.strategy is name@vN#hash, and
   * strategySelection says whether it came from configuration or a switch on the dashboard): its name, its risk profile
   * (Safe, Balanced and Risky are the three presets; any other version is Custom), the mode, and a switch still in progress.
   * Null while the engine reports no strategy. Switching is manual: the owner picks; nothing switches by itself.
   */
  function activeStrategy(engine) {
    const m = /^([a-z0-9-]{1,40})@v(\d{1,4})(?:#[0-9a-f]+)?$/.exec(engine?.strategy ?? '');
    if (!m) return null;
    const name = m[1];
    const version = Number(m[2]);
    const profile = PROFILES[name] ?? { profile: 'Custom', tone: '' };
    const sel = engine.strategySelection ?? null;
    const fromDashboard = !!sel?.activatedAt; // set only when a switch has run, never for the configured strategy
    const switching = sel?.state === 'pending' && sel.requested && sel.requested !== `${name}@v${version}` ? sel.requested : null;
    const target = switching ? /^([a-z0-9-]{1,40})@v(\d{1,4})$/.exec(switching) : null;
    return {
      name,
      version,
      title: strategyName({ name, version }),
      profile: t(profile.profile),
      profileKey: PROFILES[name] ? name : 'custom',
      tone: sel?.state === 'refused' ? 'warn' : profile.tone, // the running strategy is fine; a later switch was refused
      mode: t('Manual'),
      source: fromDashboard ? 'dashboard' : 'configuration',
      sourceText: fromDashboard ? t('chosen on the dashboard') : t('from the configuration'),
      since: fromDashboard ? sel?.activatedAt ?? null : null,
      selection: sel ? selectionState(sel) : null,
      switchingTo: target ? strategyName({ name: target[1], version: Number(target[2]) }) : switching,
      refused: sel?.state === 'refused' ? sel.requested ?? null : null,
    };
  }

  // --- Content language ------------------------------------------------------------------------------------------------

  const LANGS = ['ru', 'en'];

  /** The page's language: a saved choice, else Russian (the owner's language). */
  function contentLang(saved) {
    return LANGS.includes(saved) ? saved : 'ru';
  }

  /**
   * The text to show for something that comes in English and maybe Russian: the Russian when that's the language and
   * there is some, else the English. lang says which it is, for the page's lang attribute.
   */
  function pick(en, ru, lang) {
    if (lang === 'ru' && typeof ru === 'string' && ru.trim()) return { text: ru, lang: 'ru' };
    return { text: en ?? null, lang: 'en' };
  }

  /** Month abbreviations in a label (13 Oct – 19 Oct) in the page's language. */
  function months(label) {
    return String(label ?? '').replace(/^May(?= \d{4}$)/, t('May (month)'))
      .replace(/\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b/g, (m) => t(m));
  }

  // Health monitors beside the engine's own tiles: each says ok, warn or bad, a short value and the lines behind it. `ago`
  // formats a time as "5 min ago" in the page's language; `now` is milliseconds since the epoch.
  const MIN = 60e3;
  const HEALTH = {
    databaseWarn: 3 * MIN, databaseBad: 10 * MIN, // the engine writes its status every minute
    scanWarn: 7 * MIN, scanBad: 15 * MIN, // a scan every 5 minutes
    analystWarn: 7 * 60 * MIN, analystBad: 13 * 60 * MIN, // a run every 6 hours; a market view lasts at most 12
    requestsBad: 2 * MIN, // the engine checks the owner's requests every 5 seconds
    staleOrder: 50 * MIN, // the specs' tidy rule: the trader cancels its orders older than this
  };
  const JOB_STATES = { done: 'Done', failed: 'Failed', 'not-run': "Didn't start", running: 'Running now' };
  const PURPOSES = { signals: 'for new signals', heartbeat: 'position review', scheduled: 'scheduled', requests: 'for your requests', analyst: 'market check' };

  function age(iso, now) {
    const at = Date.parse(iso ?? '');
    return Number.isFinite(at) ? now - at : null;
  }

  function jobMonitor(job, ago, extra) {
    if (!job?.last) return { tone: 'warn', value: t('No runs yet'), sub: [t('none in the last 2 days')] };
    const l = job.last;
    const tone = l.state === 'failed' ? 'bad' : l.state === 'not-run' || job.failedToday > 0 ? 'warn' : 'ok';
    return {
      tone,
      value: t(JOB_STATES[l.state] ?? 'Running now'),
      sub: [
        `${t('last run {ago}', { ago: ago(l.at) })}${PURPOSES[l.purpose] ? ` · ${t(PURPOSES[l.purpose])}` : ''}`,
        job.failedToday > 0 ? t('{n} failed today', { n: job.failedToday }) : null,
        job.notRunToday > 0 ? t("{n} didn't start today (usage limit or credentials)", { n: job.notRunToday }) : null,
        ...extra,
      ].filter(Boolean),
    };
  }

  /** The monitors the engine's status doesn't cover by itself: database, scanner, the three agent jobs, and positions and orders. */
  function monitors(view, now, ago) {
    const e = view?.engine ?? {};
    const jobs = view?.jobs ?? {};
    const out = {};

    const db = age(view?.sync?.database, now);
    out.database = db === null
      ? { tone: 'warn', value: '–', sub: [t('no status record yet')] }
      : { tone: db > HEALTH.databaseBad ? 'bad' : db > HEALTH.databaseWarn ? 'warn' : 'ok', value: db > HEALTH.databaseWarn ? t('Not written') : t('Written'), sub: [t('last write {ago}', { ago: ago(view.sync.database) })] };

    const scan = view?.cycle?.lastScan ?? null;
    const scanAge = age(scan?.at, now);
    out.scanner = scanAge === null
      ? { tone: 'warn', value: t('No scan yet'), sub: [t('the first scan runs a minute after the engine starts')] }
      : {
        tone: scanAge > HEALTH.scanBad ? 'bad' : scanAge > HEALTH.scanWarn ? 'warn' : 'ok',
        value: scanAge > HEALTH.scanWarn ? t('Late') : t('Scanning'),
        sub: [ago(scan.at), t('{ranked} of {total} coins ranked', { ranked: scan.ranked, total: Number(scan.ranked) + Number(scan.excluded) })],
      };

    // A run for new signals that ended without an order: right when it skipped them on purpose, wrong when something
    // stopped its orders before the engine saw them. Its report says which.
    const trader = jobs.trader ?? null;
    const last = trader?.last;
    const run = last ? (view?.agents?.trader?.recentRuns ?? []).find((r) => r.runId === last.runId) : null;
    const noOrder = last?.purpose === 'signals' && last.state === 'done' && run && run.orders === 0;
    out.trader = jobMonitor(trader, ago, [
      noOrder ? t('it placed no order for the signals: see its report') : null,
      trader?.cappedAt ? t('daily cap of trader runs reached') : null,
    ]);
    if (out.trader.tone === 'ok' && (noOrder || trader?.cappedAt)) out.trader.tone = 'warn';

    out.analyst = jobMonitor(jobs.analyst ?? null, ago, []);
    const analystAge = age(jobs.analyst?.last?.at, now);
    if (analystAge !== null && analystAge > HEALTH.analystWarn) {
      out.analyst.tone = analystAge > HEALTH.analystBad ? 'bad' : out.analyst.tone === 'bad' ? 'bad' : 'warn';
      out.analyst.value = t('Overdue');
    }

    const m = e.manualRequests ?? null;
    const pollAge = age(m?.lastPollAt, now);
    const reqJob = jobs['trader-requests'] ?? null;
    if (!m) out.requests = { tone: 'warn', value: '–', sub: [t('not reported yet')] };
    else if (m.state !== 'ok') out.requests = { tone: 'warn', value: t({ off: 'Off', unavailable: 'Unavailable', 'not-used': 'Not used' }[m.state] ?? m.state), sub: [t('the engine takes no requests now')] };
    else {
      out.requests = {
        tone: pollAge === null || pollAge > HEALTH.requestsBad ? 'bad' : reqJob?.last?.state === 'failed' ? 'warn' : 'ok',
        value: pollAge === null || pollAge > HEALTH.requestsBad ? t('Not checked') : t('Taking requests'),
        sub: [
          m.lastPollAt ? t('checked {ago}', { ago: ago(m.lastPollAt) }) : null,
          m.awaitingAgent > 0 ? t('{n} waiting for the trader', { n: m.awaitingAgent }) : null,
          reqJob?.last?.state === 'failed' ? t('the last request run failed') : null,
        ].filter(Boolean),
      };
    }

    const holdings = view?.portfolio?.holdings ?? [];
    const unprotected = holdings.filter((h) => h.protected === false).map((h) => h.symbol);
    const orders = view?.cycle?.ordersInFlight ?? [];
    const old = orders.filter((o) => (age(o.sentAt, now) ?? 0) > HEALTH.staleOrder).map((o) => o.symbol);
    out.positions = {
      tone: unprotected.length ? 'bad' : old.length ? 'warn' : 'ok',
      value: unprotected.length ? t('No stop') : t('{held} held · {orders} orders', { held: holdings.length, orders: orders.length }),
      sub: [
        unprotected.length ? t('without a stop-loss: {coins}', { coins: unprotected.join(', ') }) : holdings.length ? t('every position has a stop-loss') : t('nothing held'),
        old.length ? t('orders older than 50 min: {coins}', { coins: old.join(', ') }) : null,
      ].filter(Boolean),
    };
    return out;
  }

  // The scanner's signal key (buy:SOL-EUR|sell:BTC-EUR:stop|stale-order:ETH-EUR), the items the next trader run acts on.
  const SELL_REASONS = { stop: 'at the stop-loss', target: 'at the take-profit', 'trend-down': 'the trend turned down' };

  /** The signal key as one item per coin, buys first: its kind (buy, sell or stale), the coin and a sentence for its title. */
  function signalItems(key) {
    const order = { buy: 0, sell: 1, stale: 2 };
    return String(key ?? '').split('|').filter(Boolean).map((item) => {
      const [kind, symbol, reason] = item.split(':');
      if (kind === 'buy' && symbol) return { kind: 'buy', symbol, title: t('Buy signal: the trader may buy it in its next run.') };
      if (kind === 'sell' && symbol) {
        const why = Object.hasOwn(SELL_REASONS, reason) ? t(SELL_REASONS[reason]) : reason ?? '';
        return { kind: 'sell', symbol, title: t('Sell signal ({why}): the trader sells it in its next run.', { why }) };
      }
      if (kind === 'stale-order' && symbol) return { kind: 'stale', symbol, title: t('An order older than 50 minutes: the trader cancels it in its next run.') };
      return null;
    }).filter(Boolean).sort((a, b) => order[a.kind] - order[b.kind] || a.symbol.localeCompare(b.symbol));
  }

  /** Who is signed in, from /.auth/me, for the header. Who may see what is decided by the platform and the API, not here. */
  function viewer(me) {
    const p = me?.clientPrincipal ?? null;
    return p ? { signedIn: true, name: p.userDetails ?? null } : { signedIn: false, name: null };
  }

  return {
    change, coins, why, removeBlock, BLOCK_DETAILS, WHY_LABELS, stepDetail, requestBody, describe, requestState, requestsBlocked, what, chart, tick, versus, viewer, STATUS, PORTIONS,
    paramValue, paramRange, paramLimit, strategyChanges, strategyRequestBody, strategyName, selectionState, activeStrategy, PROFILES, pairsNote, SELECTION, SELL_DESCRIPTIONS,
    contentLang, pick, months, signalItems, monitors, HEALTH,
  };
}));
