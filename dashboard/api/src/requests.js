'use strict';
// What every owner request from the dashboard shares: the error for a malformed one and the note the owner may add. The
// manual buy and sell requests this file once built (ADR-0011) were paper only and went with paper trading (ADR-0028).
// Pure functions: tested on their own.

const MAX_NOTE = 280;

class RequestError extends Error {}

/** One line, no control or format characters (they could disguise what the owner or the trader reads), bounded. */
function cleanNote(value) {
  if (value === undefined || value === null || value === '') return null;
  if (typeof value !== 'string') throw new RequestError('note must be text.');
  const text = value.replace(/[\p{Cc}\p{Cf}]/gu, ' ').replace(/\s+/g, ' ').trim();
  return text ? Array.from(text).slice(0, MAX_NOTE).join('') : null;
}

module.exports = { cleanNote, RequestError, MAX_NOTE };
