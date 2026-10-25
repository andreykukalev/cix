'use strict';
// Loads the real Functions registration when @azure/functions is installed (CI installs it; the dashboard publish step
// does too). Skipped where the package isn't available.
const test = require('node:test');
const assert = require('node:assert/strict');

let functionsAvailable = true;
try {
  require.resolve('@azure/functions');
} catch {
  functionsAvailable = false;
}

test('the API registers its two GET endpoints', { skip: !functionsAvailable && '@azure/functions is not installed' }, () => {
  assert.doesNotThrow(() => require('../src/functions/dashboard.js'));
});
