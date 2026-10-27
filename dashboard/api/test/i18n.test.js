'use strict';
// The page's Russian (dashboard/app/i18n.js): every word the page shows has one, and the language switch reaches view.js.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const I = require('../../app/i18n.js');
const V = require('../../app/view.js');

const APP = path.join(__dirname, '../../app');
const read = (file) => fs.readFileSync(path.join(APP, file), 'utf8');

/** Runs fn with the page in Russian, then back in English for the other tests. */
function inRussian(fn) {
  I.setLang('ru');
  try { return fn(); } finally { I.setLang('en'); }
}

/** The literal texts app.js and view.js pass to t(). */
function literalKeys() {
  const keys = new Set();
  for (const file of ['app.js', 'view.js']) {
    for (const m of read(file).matchAll(/\bt\(\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g)) {
      keys.add((m[1] ?? m[2]).replace(/\\(['"])/g, '$1'));
    }
  }
  return [...keys];
}

/** index.html's own words: text between tags, titles, labels and placeholders, and the page title. */
function staticTexts() {
  const html = read('index.html');
  const body = html.slice(html.indexOf('<body'));
  const texts = new Set();
  for (const m of body.matchAll(/>([^<>]+)</g)) {
    const text = m[1].replace(/\s+/g, ' ').trim();
    if (text) texts.add(text);
  }
  for (const m of body.matchAll(/\s(?:title|aria-label|placeholder)="([^"]*)"/g)) texts.add(m[1]);
  texts.add(/<title>([^<]*)<\/title>/.exec(html)[1]);
  return [...texts];
}

const marks = (text) => [...String(text).matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

test('every word app.js and view.js show has its Russian', () => {
  const keys = literalKeys();
  assert.ok(keys.length > 150, 'the scan found the texts');
  assert.deepEqual(keys.filter((k) => !I.has(k)), []);
});

test('every word on the page itself has its Russian, or is a name or identifier that stays', () => {
  assert.deepEqual(staticTexts().filter((k) => !I.has(k) && !I.UNTRANSLATED.has(k)), []);
});

test('each Russian text fills the same values as its English', () => {
  const wrong = [...I.RU].filter(([en, ru]) => marks(en).join() !== marks(ru).join()).map(([en]) => en);
  assert.deepEqual(wrong, []);
});

test('t fills values, and falls back to the English for text it has no Russian for', () => {
  assert.equal(I.t('Updated {time}', { time: '10:00' }), 'Updated 10:00');
  inRussian(() => {
    assert.equal(I.t('Updated {time}', { time: '10:00' }), 'Обновлено в 10:00');
    assert.equal(I.t('Something the engine said.'), 'Something the engine said.');
    assert.equal(I.t('trend down'), 'тренд: вниз');
    assert.equal(I.t('sell (trend-down)'), 'продажа (тренд вниз)');
    assert.equal(I.t('Flagged by the market analyst until 2026-10-27 12:00 UTC.'), 'Отмечена аналитиком рынка до 2026-10-27 12:00 UTC.');
    assert.equal(I.t(null), null);
    assert.equal(I.t('{a} and {b}', { a: '{b}', b: 1 }), '{b} and 1', 'a value is never filled in again');
  });
  assert.equal(I.setLang('de'), 'en', 'only ru and en');
  assert.equal(I.locale(), undefined);
});

test('the language switch reaches view.js: statuses, descriptions and the active strategy', () => {
  inRussian(() => {
    assert.equal(V.requestState({ status: 'filled', final: true }).label, 'Исполнен');
    assert.equal(V.requestState({ status: 'rejected', outcome: { rule: 'max-order' } }).label, 'Отклонён (max-order)');
    assert.equal(V.describe({ side: 'buy', symbol: 'SOL-EUR', quoteAmount: '25' }),
      'Попросить трейдера купить SOL-EUR на 25 EUR (бумажная торговля). Он решает, ставит стоп-лосс и может отказаться; риск-контроль проверяет ордер.');
    assert.equal(V.change(0.0123, '24 hours').label, 'рост на 1.23% за 24 часа');
    assert.equal(V.paramValue(90, { unit: 'minutes' }), '90 мин');
    assert.equal(V.strategyName({ name: 'balanced', version: 1 }), 'Сбалансированная v1');
    assert.equal(V.strategyName({ name: 'good', version: 1 }), 'Good v1');
    const a = V.activeStrategy({ strategy: 'risky@v2#ab12', strategySelection: { state: 'active', activatedAt: '2026-10-27T10:00:00Z' } });
    assert.equal(a.title, 'Рискованная v2');
    assert.equal(a.profile, 'Рискованная');
    assert.equal(a.mode, 'Ручной');
    assert.equal(a.sourceText, 'выбрана на панели');
    assert.equal(V.months('13 Oct – 19 Oct'), '13 окт – 19 окт');
    assert.equal(I.locale(), 'ru-RU');
  });
  assert.equal(V.requestState({ status: 'filled', final: true }).label, 'Filled', 'and back in English');
});
