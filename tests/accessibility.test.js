import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const html = await readFile('index.html', 'utf8');
const css = `${await readFile('src/styles.css', 'utf8')}\n${await readFile('src/accessibility-fixes.css', 'utf8')}`;

test('shell has core landmarks, skip navigation, and mobile viewport', () => {
  assert.match(html, /<header class="masthead">/);
  assert.match(html, /<main id="main"/);
  assert.match(html, /<nav class="bottom-nav" aria-label="Primary navigation">/);
  assert.match(html, /class="skip-link" href="#main"/);
  assert.match(html, /width=device-width, initial-scale=1/);
});

test('critical icon controls have accessible names', () => {
  assert.match(html, /id="sync-button"[^>]+aria-label=/);
  assert.match(html, /data-close-sheet aria-label="Back to previous view"/);
});

test('workflows use no dialogs or modal semantics', () => {
  assert.doesNotMatch(html, /<dialog|aria-modal|role="dialog"/i);
  assert.match(html, /<section id="sheet" class="detail-page"/);
});

test('accessibility adaptations are present', () => {
  assert.match(css, /prefers-reduced-motion/);
  assert.match(css, /forced-colors/);
  assert.match(css, /min-height:44px|min-height: 44px/);
  assert.match(css, /outline:3px solid var\(--focus\)/);
});
