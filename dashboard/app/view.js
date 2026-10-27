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

  /**
   * One row per coin: every coin in the latest scan, plus anything held that the scan didn't list, held ones first.
   * The moves come from the scan (the reference time is the scan's); holdings say what is held.
   */
  function coins(view) {
    const market = Array.isArray(view?.briefing?.market) ? view.briefing.market : [];
    const holdings = new Map((view?.portfolio?.holdings ?? []).filter((h) => SYMBOL.test(h?.symbol ?? '')).map((h) => [h.symbol, h]));
    const rows = market.filter((r) => SYMBOL.test(r?.symbol ?? '')).map((r) => ({
      symbol: r.symbol,
      quote: r.symbol.split('-')[1],
      rank: r.rank ?? null,
      signal: r.signal ?? null,
      note: r.note ?? null,
      hour: change(r.return1h, '1 hour'),
      day: change(r.return24h, '24 hours'),
      held: holdings.has(r.symbol),
      value: holdings.get(r.symbol)?.valueQuote ?? null,
      inScan: true,
    }));
    for (const [symbol, h] of holdings) {
      if (!rows.some((r) => r.symbol === symbol)) {
        rows.push({ symbol, quote: symbol.split('-')[1], rank: null, signal: null, note: t('not in the latest scan'), hour: change(null, '1 hour'), day: change(null, '24 hours'), held: true, value: h.valueQuote ?? null, inScan: false });
      }
    }
    return rows.sort((a, b) => (Number(b.held) - Number(a.held)) || ((a.rank ?? 1e9) - (b.rank ?? 1e9)) || a.symbol.localeCompare(b.symbol));
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

  /** Where a request stands, for the page: a label, a tone, whether it's over, and the engine's or trader's words. */
  function requestState(item) {
    const known = STATUS[item?.status] ?? { label: item?.status ?? 'unknown', tone: 'warn' };
    const o = item?.outcome ?? {};
    const s = item?.status === 'rejected' && o.rule ? { ...known, label: t('Refused ({rule})', { rule: o.rule }) } : { ...known, label: t(known.label) };
    const detail = [o.rationale, o.reason].filter((x) => typeof x === 'string' && x.trim()).join(' · ') || null;
    const filled = item?.status === 'filled' && o.filledSize ? (o.averagePrice ? t('{size} at {price}', { size: o.filledSize, price: o.averagePrice }) : String(o.filledSize)) : null;
    return { label: s.label, tone: s.tone, final: Boolean(item?.final), detail, filled };
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

  /** Who is signed in, from /.auth/me, for the header. Who may see what is decided by the platform and the API, not here. */
  function viewer(me) {
    const p = me?.clientPrincipal ?? null;
    return p ? { signedIn: true, name: p.userDetails ?? null } : { signedIn: false, name: null };
  }

  return {
    change, coins, requestBody, describe, requestState, requestsBlocked, what, chart, tick, versus, viewer, STATUS, PORTIONS,
    paramValue, paramRange, paramLimit, strategyChanges, strategyRequestBody, strategyName, selectionState, activeStrategy, PROFILES, pairsNote, SELECTION, SELL_DESCRIPTIONS,
    contentLang, pick, months,
  };
}));
