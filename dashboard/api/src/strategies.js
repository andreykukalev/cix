'use strict';
// The Strategy tab (#108): Safe, Balanced, Risky and the other strategy versions, every parameter explained with its value,
// range and whether it is the strategy's own or a mandatory safety limit, and the owner's switch requests. The engine
// writes the schema and the versions (state container) and does every check again; a switch is a request document the
// engine takes (ADR-0015), because the dashboard's writer can only create documents in the requests container. Pure
// functions: tested on their own.

const { cleanNote, RequestError } = require('./requests');

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const NAME = /^[a-z0-9-]{1,40}$/;
const REQUEST_TYPE = 'strategy-selection';
const FEATURED = ['safe', 'balanced', 'risky'];
const GROUPS = {
  trend: 'Trend indicators',
  entry: 'Entry conditions',
  size: 'Position size',
  stops: 'Stop-loss and take-profit',
  exposure: 'Exposure',
  frequency: 'Trading frequency',
  exits: 'Exit rules',
  filters: 'Coin filters',
  costs: 'Costs the scanner assumes',
  ranking: 'Ranking weights',
};
const APPLIES = {
  'new-trades': 'New trades only',
  'open-positions-too': 'New trades and open positions',
};

/**
 * The request document for a switch, or a RequestError saying what is wrong.
 * @param {object} body id (a fresh UUID per dialog), name, version, note, tradingMode.
 * @param {number} now Milliseconds since the epoch: the request's time is the server's.
 */
function strategyRequestDocument(body, now) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new RequestError('The request must be a JSON object.');
  const { id, name, version } = body;
  if (typeof id !== 'string' || !UUID.test(id)) throw new RequestError('id must be a UUID.');
  if (typeof name !== 'string' || !NAME.test(name)) throw new RequestError('name must be a strategy name.');
  if (!Number.isInteger(version) || version < 1 || version > 9999) throw new RequestError('version must be a whole number from 1 to 9999.');
  const tradingMode = body.tradingMode ?? 'paper';
  if (tradingMode !== 'paper') throw new RequestError('Only paper trading switches strategy from the dashboard; live strategy changes come with live trading (#71).');
  const at = new Date(now).toISOString();
  const doc = {
    id: id.toLowerCase(),
    day: at.slice(0, 10),
    createdAt: at,
    type: REQUEST_TYPE,
    name,
    version,
    requestedBy: 'owner', // a role, never the account's name or email
    tradingMode,
    status: 'new',
  };
  const note = cleanNote(body.note);
  if (note) doc.note = note;
  return doc;
}

/** A switch request as the tab's history shows it. */
function requestItem(doc) {
  const events = Array.isArray(doc.events) ? doc.events : [];
  const last = events[events.length - 1] ?? {};
  return {
    id: doc.id,
    createdAt: doc.createdAt ?? null,
    name: doc.name ?? null,
    version: doc.version ?? null,
    note: doc.note ?? null,
    status: doc.status ?? 'new',
    detail: last.detail ?? null,
    updatedAt: doc.updatedAt ?? null,
  };
}

/** The value at a schema key (orderQuote, scanner.stopAtr) in a spec's parameters, or null. */
function valueAt(parameters, key) {
  let node = parameters;
  for (const part of key.split('.')) {
    if (!node || typeof node !== 'object' || Array.isArray(node)) return null;
    node = node[part];
  }
  return node === undefined ? null : node;
}

/** name@vN#hash as the engine labels a spec. */
function parseLabel(label) {
  const m = /^([a-z0-9-]{1,40})@v(\d{1,4})(?:#([0-9a-f]+))?$/.exec(label ?? '');
  return m ? { label, name: m[1], version: Number(m[2]), hash: m[3] ?? null } : null;
}

function strategyItem(spec, keys, active) {
  const name = String(spec.name ?? '');
  const version = Number(spec.version);
  const values = {};
  for (const key of keys) values[key] = valueAt(spec.parameters, key);
  const tools = Array.isArray(spec.allowedTools) ? spec.allowedTools : [];
  return {
    name,
    version,
    key: `${name}@v${version}`,
    status: String(spec.status ?? '').toLowerCase(),
    purpose: typeof spec.purpose === 'string' ? spec.purpose : null,
    riskProfile: typeof spec.riskProfile === 'string' ? spec.riskProfile : null,
    canTrade: tools.includes('place_order'),
    featured: FEATURED.includes(name),
    active: !!active && active.name === name && active.version === version,
    values,
  };
}

function order(a, b) {
  const fa = FEATURED.indexOf(a.name);
  const fb = FEATURED.indexOf(b.name);
  if (fa !== fb) return (fa < 0 ? 99 : fa) - (fb < 0 ? 99 : fb);
  return a.name === b.name ? b.version - a.version : a.name.localeCompare(b.name);
}

/**
 * Everything the tab shows for one mode.
 * @param {{mode: string, schema: object|null, versions: object[], status: object|null, requests: object[]}} input
 *   schema: the engine's strategy-schema value; versions: strategy-version values; status: engine-status value.
 */
function strategiesView({ mode, schema, versions, status, requests }, now) {
  const parameters = Array.isArray(schema?.parameters) ? schema.parameters : [];
  const keys = parameters.map((p) => p.key);
  const active = parseLabel(status?.strategy);
  const groups = [];
  for (const p of parameters) {
    let group = groups.find((g) => g.key === p.group);
    if (!group) groups.push(group = { key: p.group, title: GROUPS[p.group] ?? p.group, parameters: [] });
    group.parameters.push({
      key: p.key,
      label: p.label,
      explanation: p.explanation,
      kind: p.kind,
      min: p.min ?? null,
      max: p.max ?? null,
      unit: p.unit ?? null,
      applies: APPLIES[p.applies] ?? p.applies,
      appliesToOpenPositions: p.applies === 'open-positions-too',
      boundBy: p.boundBy ?? null,
    });
  }
  const strategies = (Array.isArray(versions) ? versions : [])
    .filter((v) => v && typeof v === 'object' && NAME.test(String(v.name ?? '')) && Number.isInteger(Number(v.version)))
    .map((v) => strategyItem(v, keys, active))
    .sort(order);
  const selection = status?.strategySelection ?? null;
  return {
    generatedAt: new Date(now).toISOString(),
    mode,
    editable: mode === 'paper',
    readOnlyReason: mode === 'paper' ? null : 'Live trading runs the strategy set in its configuration. Switching it here comes with live trading (#71), with an explicit confirmation.',
    active: active ? { ...active, since: selection?.activatedAt ?? status?.startedAt ?? null } : null,
    selection: selection ? {
      state: selection.state ?? null,
      requested: selection.requested ?? null,
      requestedAt: selection.requestedAt ?? null,
      problems: Array.isArray(selection.problems) ? selection.problems : [],
      checkedAt: selection.checkedAt ?? null,
    } : null,
    schemaAt: schema?.writtenAt ?? null,
    groups,
    mandatory: Array.isArray(schema?.mandatory) ? schema.mandatory.map((m) => ({ key: m.key, label: m.label, explanation: m.explanation, value: m.value, source: m.source })) : [],
    strategies,
    requests: (Array.isArray(requests) ? requests : []).map(requestItem)
      .sort((a, b) => (Date.parse(b.createdAt ?? '') || 0) - (Date.parse(a.createdAt ?? '') || 0)),
  };
}

module.exports = { strategyRequestDocument, strategiesView, requestItem, valueAt, parseLabel, REQUEST_TYPE, GROUPS };
