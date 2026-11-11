'use strict';
// The owner's Stop and Start for the whole stocks advisory (ADR-0032). Each press is one document, `switch-N` in the stocks
// database's `control` container, numbered from 1 with no gaps; the dashboard's writer identity can only create it there, so
// a press is never changed or removed. The newest decides: `pause` stops the nightly run, the market search and both passes
// of the Claude analyst (no EODHD request, no Claude run) until a `resume`. The host finds the newest the same way and
// checks it again (Cix.Stocks.Host.StocksSwitch): a newest press that can't be read counts as started, as before any press.

const CONTAINER = 'control';
const ACTIONS = new Set(['pause', 'resume']);
/** No owner presses this often: past it the search stops looking (StocksSwitch.MaxSequence). */
const MAX_SEQUENCE = 1 << 20;

const switchId = (n) => `switch-${n}`;

/** A press the page sent, checked: pause or resume. Throws RangeError. */
function switchValue(body, now) {
  const action = body && typeof body === 'object' ? body.action : undefined;
  if (!ACTIONS.has(action)) throw new RangeError('Send pause or resume.');
  return { action, submittedAt: new Date(now).toISOString() };
}

/** The document for press number n. */
function switchDocument(value, n) {
  return { id: switchId(n), value: { sequence: n, ...value } };
}

/** Stored press number n, or unreadable when it isn't one (another number, another action, no time). */
function switchView(doc, n) {
  const v = doc?.value;
  const at = v && typeof v === 'object' && typeof v.submittedAt === 'string' && v.submittedAt.length <= 40 ? Date.parse(v.submittedAt) : NaN;
  if (!Number.isFinite(at) || !ACTIONS.has(v.action) || Number(v.sequence) !== n) return { sequence: n, unreadable: true };
  return { sequence: n, action: v.action, submittedAt: new Date(at).toISOString() };
}

/**
 * The newest press, in a few reads: the presses have no gaps, so this doubles until one is missing, then halves the range
 * between the last there and the first missing. Null when nothing was ever pressed (or the container isn't there yet).
 */
async function latestSwitch(reader) {
  let doc = await reader.readDocument(CONTAINER, switchId(1));
  if (!doc) return null;
  let found = 1;
  let missing = 2;
  for (; missing <= MAX_SEQUENCE; missing *= 2) {
    const next = await reader.readDocument(CONTAINER, switchId(missing));
    if (!next) break;
    [found, doc] = [missing, next];
  }
  while (missing - found > 1) {
    const middle = found + Math.floor((missing - found) / 2);
    const next = await reader.readDocument(CONTAINER, switchId(middle));
    if (next) [found, doc] = [middle, next];
    else missing = middle;
  }
  return switchView(doc, found);
}

/** What the Stocks page shows: stopped or not, since when, and the press's number (0: never pressed). */
function advisorState(latest) {
  if (!latest) return { paused: false, since: null, sequence: 0, unreadable: false };
  if (latest.unreadable) return { paused: false, since: null, sequence: latest.sequence, unreadable: true };
  return { paused: latest.action === 'pause', since: latest.submittedAt, sequence: latest.sequence, unreadable: false };
}

module.exports = { CONTAINER, MAX_SEQUENCE, switchValue, switchDocument, switchView, latestSwitch, advisorState };
