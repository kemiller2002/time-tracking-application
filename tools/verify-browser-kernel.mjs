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
  const rows = await page.locator('#timeline article.record').count()

  check('the page renders the kernel total verbatim', total === '54m', total)
  check('the page renders the kernel count', count === '1 entry', count)

  // The markup is today.html's, so its structure must be present — not a
  // lookalike built here.
  check(
    'records use the adopted record structure',
    (await page.locator('#timeline article.record .record-body .record-head .duration').count()) === 1
  )
  // "Research · Echelon Foundry": the catalogue lookup and the composition
  // are both the kernel's.
  check(
    'the classification line is composed by the kernel from the catalogue',
    (await page.textContent('#timeline .record-project'))?.trim() === 'Research · Echelon Foundry',
    (await page.textContent('#timeline .record-project'))?.trim()
  )
  check(
    'badge words and tone come from the kernel',
    (await page.locator('#timeline .meta-row .badge.badge-good').first().textContent()) === 'Timer'
  )
  check('the page renders one row per projected entry', rows === 1, `${rows} rows`)

  // -------------------------------------------------------------------------
  // The form's choices are the kernel's, not the markup's
  // -------------------------------------------------------------------------

  check(
    'the C# shim exports DurationGrid and CatalogueChoices',
    kernel.exportKeys.includes('DurationGrid') && kernel.exportKeys.includes('CatalogueChoices'),
    JSON.stringify(kernel.exportKeys)
  )

  // The grid is generated from MillisecondsPerBillableUnit, so its third
  // option must label itself 18 minutes while carrying the count 3. If the
  // markup ever hard-codes the labels again, this diverges.
  const third = kernel.grid.options?.[2]
  check(
    'the duration grid is computed in F#, labels and all',
    third?.units === 3 && third?.label === '18 min' && third?.displayMinutes === 18,
    JSON.stringify(third)
  )
  check(
    'the grid renders one button per kernel option',
    (await page.locator('#duration-grid button').count()) === kernel.grid.options.length,
    `${kernel.grid.options.length} options`
  )

  // DF-TE-0007: an archived project is offered but not selectable, and that
  // decision is the domain's — the page reads `selectable`, it does not
  // inspect `active`.
  const disabled = await page.locator('#manual-project option[disabled]').allTextContents()
  check(
    'an archived project is listed but not selectable',
    disabled.length === 1 && disabled[0] === 'Retired Client',
    JSON.stringify(disabled)
  )

  // -------------------------------------------------------------------------
  // Submitting a command
  // -------------------------------------------------------------------------

  // An incomplete command is refused by the kernel, not by the bridge.
  await page.click('#create-form button[type=submit]')
  const refusal = (await page.textContent('#create-message'))?.trim()
  check(
    'submitting with no duration is refused by the kernel',
    refusal === "missing 'durationMs' or 'durationUnits'",
    refusal
  )

  // 5 units = 30 exact minutes = 1_800_000 ms. Added to the 3_120_000 ms
  // already recorded that gives 4_920_000 ms, which is 82 exact minutes and
  // bills as 14 units — displayed as 1h 24m. Every one of those numbers is
  // the kernel's; the assertions below only read them back out of the DOM.
  await page.click('#duration-grid button:nth-child(5)')
  check(
    'selecting a duration marks exactly one option',
    (await page.locator('#duration-grid button.selected').count()) === 1
  )

  await page.fill('#manual-description', 'Reviewed the ledger schema')
  await page.click('#create-form button[type=submit]')
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 2, { timeout: 15000 })

  const after = await page.evaluate(() => globalThis.__kernel.view)
  check(
    'an accepted command adds exact time, to the millisecond',
    after.totalMilliseconds === 4920000,
    `${after.totalMilliseconds} ms`
  )
  check('billable units are re-projected, not accumulated', after.totalBillableUnits === 14)

  const newTotal = (await page.textContent('#daily-total'))?.trim()
  const newRows = await page.locator('#timeline article.record').count()
  check('the page re-renders from the kernel after a command', newTotal === '1h 24m', newTotal)
  check('the new entry appears in the timeline', newRows === 2, `${newRows} rows`)

  // TE-R-093: the browser reports the requested effect; it does not perform
  // it. The effect is named, and no request left the page.
  const effects = (await page.textContent('#create-effects'))?.trim()
  check('the requested effect is named, not performed', effects === 'Requested: PersistNewEntry', effects)

  // -------------------------------------------------------------------------
  // A domain rejection reaches the page as text
  // -------------------------------------------------------------------------

  // Selecting the archived project has to be forced past the disabled
  // attribute, because the point is to prove the KERNEL refuses it — a
  // disabled <option> only proves the markup was rendered correctly.
  await page.evaluate(() => {
    const select = document.getElementById('manual-project')
    select.value = 'retired-client'
  })
  await page.click('#duration-grid button:nth-child(2)')
  await page.click('#create-form button[type=submit]')
  const rejection = (await page.textContent('#create-message'))?.trim()
  check(
    'an archived project is refused by the domain, and the refusal is rendered',
    rejection?.includes('ProjectIsArchived') === true,
    rejection
  )
  check(
    'a rejected command leaves the ledger unchanged',
    (await page.evaluate(() => globalThis.__kernel.view.totalMilliseconds)) === 4920000
  )

  // -------------------------------------------------------------------------
  // Voiding, and the version token that governs it
  // -------------------------------------------------------------------------

  // The remove control appears only where the kernel reported CanVoid. Two
  // active entries, so two controls.
  check(
    'a remove control appears per entry the kernel says may be voided',
    (await page.locator('#timeline article.record details').count()) === 2
  )

  // Rows are addressed by their description rather than by position: the
  // projection's ordering is the kernel's to choose, and a positional
  // selector here would quietly encode an assumption about it.
  const row = (text) => page.locator('#timeline article.record', { hasText: text })

  // The reason field is behind a native <details> disclosure, so it has to
  // be opened the way a person would.
  const openRemove = async (text) => {
    await row(text).locator('summary').click()
    return row(text)
  }

  // The entry created in this session has no persisted version yet, so its
  // void must be refused. The page does not pre-empt that by hiding the
  // control, and it does not fabricate a token to get past it either: the
  // command simply carries no version and the kernel refuses it. A command
  // that cannot name the version it read must never reach a transition
  // (TE-R-070).
  const fresh = await openRemove('Reviewed the ledger schema')
  await fresh.locator('input[type=text]').fill('Wrong project')
  await fresh.locator('button[type=submit]').click()
  const stale = (await page.textContent('#create-message'))?.trim()
  check(
    'voiding an entry with no known version is refused',
    stale === "missing 'expectedVersion'",
    stale
  )

  // The loaded entry does have a version, supplied beside the document
  // rather than inside it, so its void is accepted. Its 3_120_000 ms leaves
  // the totals, so 1_800_000 ms remains — 30 exact minutes, 5 units.
  const loaded = await openRemove('Reviewed composition evidence.')
  await loaded.locator('input[type=text]').fill('Recorded twice')
  await loaded.locator('button[type=submit]').click()
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 1, { timeout: 15000 })

  const voided = await page.evaluate(() => globalThis.__kernel.view)
  check(
    'an accepted void removes its time from the totals',
    voided.totalMilliseconds === 1800000,
    `${voided.totalMilliseconds} ms`
  )
  // The default day view counts only what counts, and still reports that a
  // non-counting entry exists rather than dropping it silently (TE-R-030).
  check('a voided entry is excluded from the count, not deleted', voided.excludedEntries === 1)
  check(
    'the void reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistVoid'
  )
  check(
    'the page renders the reduced total',
    (await page.textContent('#daily-total'))?.trim() === '30m',
    (await page.textContent('#daily-total'))?.trim()
  )
}

check('no page errors', errors.length === 0, errors.slice(0, 2).join(' | '))

await browser.close()
server.kill()

console.log('')
console.log(failures === 0 ? 'browser kernel: all checks passed' : `browser kernel: ${failures} FAILURE(S)`)
process.exit(failures === 0 ? 0 : 1)
