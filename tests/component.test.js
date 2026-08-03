import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const html = await readFile('index.html', 'utf8');
const app = await readFile('src/app.js', 'utf8');

test('primary routes and persistent navigation are defined', () => {
  for (const route of ['today', 'track', 'month', 'more']) {
    assert.match(html, new RegExp(`data-view="${route}"`));
    assert.match(html, new RegExp(`data-route="${route}"`));
  }
});

test('demo authority is explicit', () => {
  assert.match(html, />Demo data</);
  assert.match(html, /> Demo fixture</);
  assert.match(app, /prototype data is local demo data/);
});

test('detail workspace provides open and close behavior', () => {
  assert.match(app, /function openSheet/);
  assert.match(app, /function closeSheet/);
  assert.match(app, /sheet\.hidden = false/);
  assert.match(app, /sheet\.hidden = true/);
});
