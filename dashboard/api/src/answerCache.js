'use strict';
// The reads' answers, kept a few seconds per instance (functions/dashboard.js): refreshes and several open tabs share one
// database read. A write that changes what a read shows forgets its answer (functions/owner.js).

const CACHE_MS = 20_000;
const cache = new Map();

// Concurrent requests for the same thing share one database read (the promise is cached, not just the result).
async function cached(key, load, ttl = CACHE_MS) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttl) return hit.body;
  const body = load();
  cache.set(key, { at: Date.now(), body });
  if (cache.size > 200) cache.delete(cache.keys().next().value);
  try {
    return await body;
  } catch (error) {
    cache.delete(key); // don't keep a failure
    throw error;
  }
}

/** Drops a cached answer, so this instance's next read is fresh (after the owner changed what it shows). */
function forget(key) {
  cache.delete(key);
}

module.exports = { cached, forget, CACHE_MS };
