'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { activity, periodsFor, fromDay, units, money } = require('../src/activity');

const NOW = Date.parse('2026-10-26T13:00:00Z'); // a Monday

const fill = (at, side, filledQuote, extra = {}) => ({ at, data: { symbol: 'LINK-EUR', side, filledQuote, fee: '0.01', feeCurrency: 'EUR', clientOrderId: `${at}-${side}`, ...extra } });

test('decimal sums are exact', () => {
  assert.equal(money(units('0.1') + units('0.2')), '0.30');
  assert.equal(money(units('10.005')), '10.01');
  assert.equal(money(units('-1.234')), '-1.23');
  assert.equal(units('1e3'), null);
  assert.equal(units('abc'), null);
});

test('periods: UTC days, weeks from Monday, calendar months, the last one holding now', () => {
  const days = periodsFor('day', NOW);
  assert.equal(days.length, 14);
  assert.equal(new Date(days.at(-1).start).toISOString(), '2026-10-26T00:00:00.000Z');
  const weeks = periodsFor('week', Date.parse('2026-10-25T23:59:59Z')); // a Sunday
  assert.equal(new Date(weeks.at(-1).start).toISOString(), '2026-10-19T00:00:00.000Z');
  assert.equal(weeks.length, 12);
  const months = periodsFor('month', NOW);
  assert.equal(new Date(months[0].start).toISOString(), '2025-11-01T00:00:00.000Z');
  assert.equal(fromDay('month', NOW), '2025-11-01');
  assert.throws(() => periodsFor('year', NOW), RangeError);
});

test('counts each executed order once, buys and sells apart, fees not in turnover', () => {
  const view = activity([
    fill('2026-10-26T09:00:00Z', 'buy', '25.50'),
    fill('2026-10-26T10:00:00Z', 'sell', '26.10', { source: 'manual-direct' }),
    fill('2026-10-26T10:00:00Z', 'sell', '26.10', { source: 'manual-direct' }), // the same order written twice
    fill('2026-10-25T10:00:00Z', 'buy', '10'),
    fill('2026-10-26T11:00:00Z', 'buy', '0'), // nothing filled: not a trade
    fill('2026-10-26T11:00:00Z', 'buy', 'x', { clientOrderId: 'bad' }),
    fill('2026-10-26T12:00:00Z', 'buy', '5', { symbol: 'BTC-USD' }), // another quote currency: not summed
    fill('2026-10-01T12:00:00Z', 'buy', '99'), // before the first day shown
  ], 'day', NOW);

  assert.equal(view.currency, 'EUR');
  assert.deepEqual(view.current, {
    from: '2026-10-26', to: '2026-10-26', label: '26 Oct', trades: 2, buys: 1, sells: 1, manual: 1,
    buyVolume: '25.50', sellVolume: '26.10', turnover: '51.60', fees: '0.02',
  });
  assert.equal(view.previous.trades, 1);
  assert.equal(view.previous.turnover, '10.00');
  assert.deepEqual(view.total, { trades: 3, turnover: '61.60', from: '2026-10-13', to: '2026-10-26' });
  assert.deepEqual(view.skipped, { notQuote: 1, duplicate: 1 });
});

test('weeks and months add the same trades into longer periods', () => {
  const fills = [fill('2026-10-19T00:00:00Z', 'buy', '1'), fill('2026-10-25T23:59:59Z', 'sell', '2'), fill('2026-10-26T00:00:00Z', 'buy', '4')];
  const weeks = activity(fills, 'week', NOW);
  assert.equal(weeks.previous.label, '19 Oct – 25 Oct');
  assert.equal(weeks.previous.turnover, '3.00');
  assert.equal(weeks.current.turnover, '4.00');
  const months = activity(fills, 'month', NOW);
  assert.equal(months.current.label, 'Oct 2026');
  assert.equal(months.current.trades, 3);
  assert.equal(months.current.turnover, '7.00');
});

test('no trades: zeros, not gaps', () => {
  const view = activity([], 'week', NOW);
  assert.equal(view.periods.length, 12);
  assert.ok(view.periods.every((p) => p.trades === 0 && p.turnover === '0.00'));
});
