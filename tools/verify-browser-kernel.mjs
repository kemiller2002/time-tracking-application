#!/usr/bin/env node

// End-to-end verification of the browser boundary in a real browser.
//
//   npm run check:browser
//
// Proves the whole architectural path the execution instruction asks for
// (§29): HTML/CSS -> JS bridge -> C# interop shim -> F# WASM kernel ->
// projection -> DOM. Nothing here computes a domain value; it asserts that
// the values the page displays were computed by F#.
//
// Requires: dotnet workload install wasm-tools wasm-experimental, then
// dotnet publish of browser/TimeEntry.Host.

import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { chromium } from 'playwright'

const BUNDLE = 'browser/TimeEntry.Host/bin/Release/net8.0/browser-wasm/AppBundle'
const PORT = 8099

if (!existsSync(`${BUNDLE}/index.html`)) {
  console.error(`no app bundle at ${BUNDLE} — run: dotnet publish browser/TimeEntry.Host -c Release`)
  process.exit(1)
}

// In the agent sandbox the preinstalled browser is pinned and Playwright's
// own download is blocked. In CI Playwright manages its own, so let it
// resolve normally there.
const CHROME = process.env.PLAYWRIGHT_MANAGED_CHROMIUM
  ? undefined
  : '/opt/pw-browsers/chromium-1194/chrome-linux/chrome'

const server = spawn('python3', ['-m', 'http.server', String(PORT)], {
  cwd: BUNDLE,
  stdio: 'ignore'
})

const shutdown = () => server.kill()
process.on('exit', shutdown)

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
await wait(1500)

let failures = 0
const check = (name, ok, detail) => {
  if (ok) {
    console.log(`  PASS  ${name.padEnd(52)} ${detail ?? ''}`)
  } else {
    failures += 1
    console.log(`  FAIL  ${name.padEnd(52)} ${detail ?? ''}`)
  }
}

const browser = await chromium.launch(CHROME ? { executablePath: CHROME } : {})
const page = await browser.newPage()
const errors = []
page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)))
page.on('console', (m) => {
  // favicon.ico is absent by design and not worth a file.
  if (m.type() === 'error' && !m.text().includes('favicon')) errors.push(m.text().slice(0, 200))
})

console.log('browser kernel verification')

await page.goto(`http://localhost:${PORT}/index.html`, { waitUntil: 'load' })
const ready = await page
  .waitForFunction(() => globalThis.__kernel !== undefined, { timeout: 90000 })
  .then(() => true)
  .catch(() => false)

check('the WASM runtime starts and the kernel responds', ready, ready ? '' : 'timed out')

if (ready) {
  const kernel = await page.evaluate(() => globalThis.__kernel)

  check(
    'the C# shim exports ViewDay',
    kernel.exportKeys.includes('ViewDay'),
    JSON.stringify(kernel.exportKeys)
  )
  check('the kernel answers successfully', kernel.view.ok === true)

  // 52 exact minutes = 3_120_000 ms. Under RoundUp that is 9 six-minute
  // units, which DISPLAYS as 54 minutes. The gap between 52 and 54 is
  // DF-TE-0002 working: exact time is preserved and billing is a projection.
  check(
    'exact duration is preserved to the millisecond',
    kernel.view.totalMilliseconds === 3120000,
    `${kernel.view.totalMilliseconds} ms`
  )
  check(
    'billable units are projected, not stored',
    kernel.view.totalBillableUnits === 9,
    `52 exact minutes bills as 9 units`
  )
  check(
    'display time comes from the projection',
    kernel.view.displayHours === 0 && kernel.view.displayMinutes === 54,
    `${kernel.view.displayHours}h ${kernel.view.displayMinutes}m`
  )
  check('capabilities are computed by F#', kernel.view.entries[0].capabilities.length === 5,
    kernel.view.entries[0].capabilities.join(','))

  // The DOM must show what the kernel said, not its own arithmetic.
  const total = (await page.textContent('#daily-total'))?.trim()
  const count = (await page.textContent('#activity-count'))?.trim()
  const rows = await page.locator('#timeline li').count()

  check('the page renders the kernel total verbatim', total === '0h 54m', total)
  check('the page renders the kernel count', count === '1 activity', count)
  check('the page renders one row per projected entry', rows === 1, `${rows} rows`)
}

check('no page errors', errors.length === 0, errors.slice(0, 2).join(' | '))

await browser.close()
server.kill()

console.log('')
console.log(failures === 0 ? 'browser kernel: all checks passed' : `browser kernel: ${failures} FAILURE(S)`)
process.exit(failures === 0 ? 0 : 1)
