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
import { createServer } from 'node:http'
import { chromium } from 'playwright'

const BUNDLE = 'browser/TimeEntry.Host/bin/Release/net8.0/browser-wasm/AppBundle'
const PORT = 8099
const STUB_PORT = 8098

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

// A stand-in for the GitHub API.
//
// Earlier this section pointed the page at api.github.com with an invalid
// token, which made every CI run issue a real request to a third party to
// observe a 401. The API root is now part of addressing a repository, so a
// stub can stand in — and it can be asked what the page actually sent, which
// the real API could never be.
const apiRequests = []

const cors = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET, POST, PATCH, OPTIONS',
  'access-control-allow-headers': '*'
}

const stub = createServer((request, response) => {
  // The page is on a different origin, so every call is preceded by a CORS
  // preflight. Answering those with the same 404 as everything else made the
  // browser reject the preflight and never send the real request — so the
  // stub saw only OPTIONS, with no credential on them, and the check below
  // failed for a reason that had nothing to do with the credential.
  if (request.method === 'OPTIONS') {
    response.writeHead(204, cors)
    response.end()
    return
  }

  let body = ''
  request.on('data', (chunk) => (body += chunk))
  request.on('end', () => {
    apiRequests.push({
      method: request.method,
      url: request.url,
      authorization: request.headers.authorization ?? null
    })
    // Everything 404s. The page must report that honestly rather than appear
    // to have saved; what it does with a success is covered by the F# tests,
    // which can assert on the store.
    response.writeHead(404, { 'content-type': 'application/json', ...cors })
    response.end('{"message":"Not Found"}')
  })
})

stub.listen(STUB_PORT)

const shutdown = () => {
  server.kill()
  stub.close()
}

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

  // The fixture is 52 + 18 + 30 exact minutes = 6_000_000 ms.
  //
  // The 52-minute entry carries the whole of DF-TE-0002 on its own: exactly
  // 3_120_000 ms stored, 9 six-minute units billed, 54 minutes displayed. The
  // gap between 52 and 54 is the rounding being a projection rather than a
  // store, and it is asserted on the ROW so the day's composition can change
  // without weakening the claim.
  const fixtureRow = kernel.view.entries.find(
    (e) => e.description === 'Reviewed composition evidence.'
  )
  check(
    'exact duration is preserved to the millisecond',
    fixtureRow?.durationMilliseconds === 3120000,
    `${fixtureRow?.durationMilliseconds} ms`
  )
  check(
    'billable units are projected, not stored',
    fixtureRow?.billableUnits === 9,
    '52 exact minutes bills as 9 units'
  )
  check(
    'display time comes from the projection',
    fixtureRow?.displayTime === '54m',
    fixtureRow?.displayTime
  )
  check(
    'day totals sum exact time, then project once',
    kernel.view.totalMilliseconds === 6000000 && kernel.view.totalBillableUnits === 17,
    `${kernel.view.totalMilliseconds} ms, ${kernel.view.totalBillableUnits} units`
  )
  check('capabilities are computed by F#', fixtureRow?.capabilities.length === 5,
    fixtureRow?.capabilities.join(','))

  // The DOM must show what the kernel said, not its own arithmetic.
  const total = (await page.textContent('#daily-total'))?.trim()
  const count = (await page.textContent('#activity-count'))?.trim()
  const rows = await page.locator('#timeline article.record').count()

  check('the page renders the kernel total verbatim', total === '1h 42m', total)
  check('the page renders the kernel count', count === '3 entries', count)

  // The markup is today.html's, so its structure must be present — not a
  // lookalike built here.
  check(
    'records use the adopted record structure',
    (await page.locator('#timeline article.record .record-body .record-head .duration').count()) === 3
  )
  // "Research · Echelon Foundry": the catalogue lookup and the composition
  // are both the kernel's.
  check(
    'the classification line is composed by the kernel from the catalogue',
    (await page
      .locator('#timeline article.record', { hasText: 'Reviewed composition evidence.' })
      .locator('.record-project')
      .textContent())?.trim() === 'Research · Echelon Foundry'
  )
  check(
    'badge words and tone come from the kernel',
    (await page.locator('#timeline .meta-row .badge.badge-good').first().textContent()) === 'Timer'
  )
  check('the page renders one row per projected entry', rows === 3, `${rows} rows`)

  // -------------------------------------------------------------------------
  // The form's choices are the kernel's, not the markup's
  // -------------------------------------------------------------------------

  check(
    'the C# shim exports Persist as a promise-returning export',
    kernel.exportKeys.includes('Persist'),
    JSON.stringify(kernel.exportKeys)
  )
  check(
    'the page reports that it is not connected to a repository',
    (await page.textContent('#sync-status'))?.trim() === 'Not signed in',
    (await page.textContent('#sync-status'))?.trim()
  )

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

  // 5 units = 30 exact minutes = 1_800_000 ms. Added to the 6_000_000 ms
  // already recorded that gives 7_800_000 ms, which is 130 exact minutes and
  // bills as 22 units — displayed as 2h 12m. Every one of those numbers is
  // the kernel's; the assertions below only read them back out of the DOM.
  await page.click('#duration-grid button:nth-child(5)')
  check(
    'selecting a duration marks exactly one option',
    (await page.locator('#duration-grid button.selected').count()) === 1
  )

  await page.fill('#manual-description', 'Reviewed the ledger schema')
  await page.click('#create-form button[type=submit]')
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 4, { timeout: 15000 })

  const after = await page.evaluate(() => globalThis.__kernel.view)
  check(
    'an accepted command adds exact time, to the millisecond',
    after.totalMilliseconds === 7800000,
    `${after.totalMilliseconds} ms`
  )
  check('billable units are re-projected, not accumulated', after.totalBillableUnits === 22)

  const newTotal = (await page.textContent('#daily-total'))?.trim()
  const newRows = await page.locator('#timeline article.record').count()
  check('the page re-renders from the kernel after a command', newTotal === '2h 12m', newTotal)
  check('the new entry appears in the timeline', newRows === 4, `${newRows} rows`)

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
    (await page.evaluate(() => globalThis.__kernel.view.totalMilliseconds)) === 7800000
  )

  // -------------------------------------------------------------------------
  // Voiding, and the version token that governs it
  // -------------------------------------------------------------------------

  // The remove control appears only where the kernel reported CanVoid. Two
  // active entries, so two controls.
  check(
    'a remove control appears per entry the kernel says may be voided',
    (await page.locator('#timeline article.record details', { hasText: 'Remove' }).count()) === 4
  )
  check(
    'and a correct control likewise',
    (await page.locator('#timeline article.record details', { hasText: 'Correct' }).count()) === 4
  )

  // Rows are addressed by their description rather than by position: the
  // projection's ordering is the kernel's to choose, and a positional
  // selector here would quietly encode an assumption about it.
  const row = (text) => page.locator('#timeline article.record', { hasText: text })

  // The reason field is behind a native <details> disclosure, so it has to
  // be opened the way a person would.
  // Scoped by the disclosure's own label, because each record now carries
  // more than one.
  const openRemove = async (text) => {
    const control = row(text).locator('details', { hasText: 'Remove' })
    await control.locator('summary').click()
    return control
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
  // the totals, so 4_680_000 ms remains — 78 exact minutes, 13 units.
  const loaded = await openRemove('Reviewed composition evidence.')
  await loaded.locator('input[type=text]').fill('Recorded twice')
  await loaded.locator('button[type=submit]').click()
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 3, { timeout: 15000 })

  const voided = await page.evaluate(() => globalThis.__kernel.view)
  check(
    'an accepted void removes its time from the totals',
    voided.totalMilliseconds === 4680000,
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
    (await page.textContent('#daily-total'))?.trim() === '1h 18m',
    (await page.textContent('#daily-total'))?.trim()
  )

  // -------------------------------------------------------------------------
  // Disclosure, then restore
  // -------------------------------------------------------------------------

  // The removed entry is gone from the list but its existence is still
  // reported — never silently dropped (TE-R-030).
  check(
    'a removed entry is disclosed even while hidden',
    (await page.textContent('#excluded-count'))?.trim() === '(1 removed)',
    (await page.textContent('#excluded-count'))?.trim()
  )
  check('and it is not in the list', (await page.locator('#timeline article.record').count()) === 3)

  // Ticking the box asks the KERNEL a different question. If the page were
  // filtering a list it already had, the removed entry would never have been
  // in it to show.
  await page.check('#show-removed')
  await page.waitForFunction(() => globalThis.__kernel.view.entries.length === 4, { timeout: 15000 })
  check('showing removed entries brings it back into the list', true)
  check(
    'the total does not change when a removed entry is merely shown',
    (await page.textContent('#daily-total'))?.trim() === '1h 18m',
    (await page.textContent('#daily-total'))?.trim()
  )
  check(
    'the removed entry is badged as removed',
    (await page.locator('#timeline .badge', { hasText: 'Removed' }).count()) === 1
  )

  // Restore is offered only where the kernel said CanRestore — which is the
  // removed entry, and only it.
  check(
    'restore is offered on exactly the removed entry',
    (await page.locator('#timeline article.record details', { hasText: 'Restore' }).count()) === 1
  )

  const removed = row('Reviewed composition evidence.')
  await removed.locator('details', { hasText: 'Restore' }).locator('summary').click()
  await removed
    .locator('details', { hasText: 'Restore' })
    .locator('input[type=text]')
    .fill('Removed in error')
  await removed.locator('details', { hasText: 'Restore' }).locator('button[type=submit]').click()
  await page.waitForFunction(() => globalThis.__kernel.view.totalMilliseconds === 7800000, {
    timeout: 15000
  })
  check('restoring returns its time to the totals', true, '7800000 ms')
  check(
    'the restore reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistRestore'
  )

  // -------------------------------------------------------------------------
  // Correction
  // -------------------------------------------------------------------------

  // The loaded entry, not the one created in this session: only the loaded
  // one has a persisted version, and a correction must name the version it
  // read (TE-R-070). An entry this page created has nowhere to have got one
  // from, because effects are not performed yet — see WI-0033.
  const target = row('Reviewed composition evidence.').locator('details', { hasText: 'Correct' })
  await target.locator('summary').click()

  // The form is pre-filled from the projection: the entry's current project
  // is selected, chosen by id rather than by matching a display name.
  check(
    'the correction form is pre-filled with the entry\'s current project',
    (await target.locator('select').first().inputValue()) === 'echelon-foundry',
    await target.locator('select').first().inputValue()
  )

  // 52 minutes -> 1 hour, and onto a different project. The day gains the
  // difference: 7_800_000 - 3_120_000 + 3_600_000 = 8_280_000 ms.
  await target.locator('select').first().selectOption('northline')
  await target.locator('select').last().selectOption('10')
  await target.locator('input[type=text]').fill('Logged against the wrong client')
  await target.locator('button[type=submit]').click()
  const changed = await page
    .waitForFunction(() => globalThis.__kernel.view.totalMilliseconds === 8280000, { timeout: 15000 })
    .then(() => true)
    .catch(() => false)

  if (!changed) {
    // Say WHY rather than only that it timed out: a silent timeout here
    // hides a domain rejection behind a stopwatch.
    check('a correction is accepted', false, (await page.textContent('#create-message'))?.trim())
  }

  const corrected = await page.evaluate(() => globalThis.__kernel.view)
  check('a correction changes the total, exactly', corrected.totalMilliseconds === 8280000, `${corrected.totalMilliseconds} ms`)
  // Still two entries: a correction is a new revision of the SAME entry, not
  // a second entry (TE-R-050).
  check('a correction does not create a second entry', corrected.entries.length === 4)
  // Scoped to the corrected entry's own row: two other fixture entries are
  // already on Northline, so a page-wide count would pass for the wrong
  // reason.
  check(
    'the corrected entry shows its new classification',
    (await row('Reviewed composition evidence.').locator('.record-project').textContent())?.trim() ===
      'Research · Northline Studio',
    (await row('Reviewed composition evidence.').locator('.record-project').textContent())?.trim()
  )
  check(
    'the correction reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistCorrection'
  )

  // -------------------------------------------------------------------------
  // Split, with the preview TE-R-044 asks for
  // -------------------------------------------------------------------------

  // The entry is now 1 hour (10 units) after the correction above.
  const splitter = row('Reviewed composition evidence.').locator('details', { hasText: 'Split' })
  await splitter.locator('summary').click()

  // Two parts by default: one is not a split.
  check('a split opens with two parts', (await splitter.locator('.form-grid').count()) === 2)
  check(
    'and refuses to submit until they balance',
    await splitter.locator('button[type=submit]').isDisabled()
  )

  const durations = splitter.locator('select[id$=duration]')
  const summary = async () => (await splitter.locator('p.field-help').textContent())?.trim()

  // An empty part is reported as empty, ahead of any remainder: a remainder
  // is not a meaningful number while a part has no duration at all.
  await durations.nth(0).selectOption('4')
  check(
    'an incomplete part is reported before any remainder',
    (await summary()) === '1 part(s) still need a duration.',
    await summary()
  )

  // 4 + 4 units is 48 minutes against the hour this entry now holds.
  await durations.nth(1).selectOption('4')
  check(
    'the preview reports the unallocated remainder, computed in F#',
    (await summary()) === '12m is still unallocated.',
    await summary()
  )

  await durations.nth(1).selectOption('8')
  check(
    'and reports an overage rather than a negative number to interpret',
    (await summary()) === 'The parts exceed the entry by 12m.',
    await summary()
  )

  await durations.nth(1).selectOption('6')
  check('a balanced split says so', (await summary()) === 'The parts account for all of the time.', await summary())
  check(
    'and only then may it be submitted',
    await splitter.locator('button[type=submit]').isEnabled()
  )

  await splitter.locator('button[type=submit]').click()
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 5, { timeout: 15000 })

  const afterSplit = await page.evaluate(() => globalThis.__kernel.view)
  // The source leaves the totals and its two children replace it, so the
  // day's exact total is unchanged — which is the whole point (TE-R-040).
  check(
    'a split preserves the day total exactly',
    afterSplit.totalMilliseconds === 8280000,
    `${afterSplit.totalMilliseconds} ms`
  )
  check(
    'the split reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistSplit'
  )
  check(
    'the source is replaced, not duplicated',
    (await page.locator('#timeline article.record').count()) === 5,
    `${await page.locator('#timeline article.record').count()} rows`
  )

  // -------------------------------------------------------------------------
  // Evidence
  // -------------------------------------------------------------------------

  // Before the merge, deliberately: the merge supersedes both proposal
  // entries, and a superseded entry offers no capabilities at all — including
  // this one. Attaching afterwards would be testing against an entry the
  // kernel had already, correctly, closed to changes.
  const evidenced = row('Client proposal outline')
  check(
    'an entry with no evidence carries no evidence badge',
    (await evidenced.locator('.badge', { hasText: 'evidence' }).count()) === 0
  )

  const attach = evidenced.locator('details', { hasText: 'Attach evidence' })
  await attach.locator('summary').click()
  await attach.locator('input[type=url]').fill('https://example.invalid/brief.pdf')
  await attach.locator('input[type=text]').fill('The brief')
  await attach.locator('button[type=submit]').click()

  // The badge is the projection's, counted and worded in F#.
  await page
    .waitForFunction(
      () =>
        globalThis.__kernel.view.entries.some((e) =>
          e.badges.some((b) => b.text === '1 evidence')
        ),
      { timeout: 15000 }
    )
    .then(() => check('attaching evidence is reflected in the kernel\'s badges', true, '1 evidence'))
    .catch(async () =>
      check(
        'attaching evidence is reflected in the kernel\'s badges',
        false,
        (await page.textContent('#create-message'))?.trim()
      )
    )

  check(
    'the attachment reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistEvidenceAttachment',
    (await page.textContent('#create-effects'))?.trim()
  )

  // -------------------------------------------------------------------------
  // Merge
  // -------------------------------------------------------------------------

  // The two client-proposal entries are the only ones left that still carry
  // versions: a merge needs sources read at a known version, and everything
  // this session created has none.
  check('the merge panel is hidden until something is selected', await page.isHidden('#merge-panel'))

  const tick = (text) =>
    row(text).locator('input[type=checkbox]').check()

  await tick('Client proposal outline')
  check('selecting one entry opens the panel', await page.isVisible('#merge-panel'))
  check(
    'and one entry is reported as not enough, in the kernel\'s words',
    (await page.textContent('#merge-summary'))?.trim() === 'Choose at least two entries to merge.',
    (await page.textContent('#merge-summary'))?.trim()
  )

  // 18 + 30 exact minutes. The page never added them.
  await tick('Client proposal pricing')
  check(
    'two selected entries preview their combined exact time',
    (await page.textContent('#merge-summary'))?.trim() === '2 entries totalling 48m.',
    (await page.textContent('#merge-summary'))?.trim()
  )

  const beforeMerge = await page.evaluate(() => globalThis.__kernel.view.totalMilliseconds)

  await page.fill('#merge-reason', 'Same task, two timers')
  await page.click('#merge-form button[type=submit]')
  await page.waitForFunction(() => globalThis.__kernel.view.countedEntries === 4, { timeout: 15000 })

  const afterMerge = await page.evaluate(() => globalThis.__kernel.view)
  // A merge re-labels time; it neither creates nor destroys any.
  check(
    'a merge preserves the day total exactly',
    afterMerge.totalMilliseconds === beforeMerge,
    `${afterMerge.totalMilliseconds} ms`
  )
  check(
    'the merge reports the persistence effect it needs',
    (await page.textContent('#create-effects'))?.trim() === 'Requested: PersistMerge',
    (await page.textContent('#create-effects'))?.trim()
  )
  check('the selection is cleared once the merge lands', await page.isHidden('#merge-panel'))
  // Superseded, not deleted — and superseded is not the same as removed, so
  // "show removed" does not bring them back either (DF-TE-0006).
  check(
    'the merged sources leave the counted list',
    afterMerge.excludedEntries >= 3,
    `${afterMerge.excludedEntries} excluded`
  )

}

// ---------------------------------------------------------------------------
// The credential seam, as far as it can be exercised without a repository
// ---------------------------------------------------------------------------
//
// No GitHub write happens here and none is claimed. What IS asserted is the
// part that can be: the token never reaches the DOM, connecting changes what
// the page says about itself, and a command with a connection takes the
// persist path — where it fails at the network rather than silently falling
// back to the in-page one.

// Everything above ran against fixtures and must have been error-free. Fixed
// here rather than at the end, because everything BELOW deliberately points
// the page at a repository that does not exist.
const errorsBeforeConnecting = [...errors]

if (ready) {
  await page.fill('#repo-owner', 'owner')
  await page.fill('#repo-name', 'repo')
  await page.fill('#repo-api-root', `http://localhost:${STUB_PORT}`)
  await page.fill('#repo-token', 'ghp_not_a_real_token')
  await page.click('#repo-form button[type=submit]')

  check(
    'connecting names the repository being written to',
    (await page.textContent('#sync-status'))?.trim() === 'owner/repo',
    (await page.textContent('#sync-status'))?.trim()
  )
  check(
    'the token field is cleared once stored',
    (await page.inputValue('#repo-token')) === '',
    'still populated'
  )
  // The token lives in sessionStorage and is never rendered back. If it ever
  // appears in the document, it is one screenshot away from being leaked.
  const markup = await page.content()
  check('the token never reaches the DOM', !markup.includes('ghp_not_a_real_token'))

  // Connecting also reads the ledger from the repository. There is no such
  // repository, so that read fails — and the page must say so rather than
  // silently keeping the fixture and looking connected.
  const loadReported = await page
    .waitForFunction(
      () => {
        const msg = document.getElementById('create-message')
        return msg && !msg.hidden && msg.textContent.length > 0
      },
      { timeout: 30000 }
    )
    .then(() => true)
    .catch(() => false)

  check('connecting reads the ledger, and reports a failed read', loadReported,
    (await page.textContent('#create-message'))?.trim())

  // The stub can be asked what arrived, which the real API never could. This
  // is the only place the credential's whole journey is observable: typed
  // into a form, through sessionStorage, into F#, out of the transport, onto
  // the wire.
  check(
    'the request reaches the configured API root, not github.com',
    apiRequests.length > 0 && apiRequests.every((r) => r.url.startsWith('/repos/owner/repo')),
    JSON.stringify(apiRequests.slice(0, 2))
  )
  check(
    'and carries the token as a bearer credential',
    apiRequests.every((r) => r.authorization === 'Bearer ghp_not_a_real_token'),
    apiRequests[0]?.authorization
  )

  // With a connection, a command takes the persist path. It cannot succeed —
  // there is no such repository — but it must FAIL AUDIBLY rather than appear
  // to save.
  //
  // The project is reset explicitly: an earlier check deliberately left the
  // selection on the archived one, and a command the DOMAIN refuses never
  // reaches the network. Without this the check below passes on a stale
  // rejection message and proves nothing.
  await page.selectOption('#manual-project', 'echelon-foundry')
  await page.click('#duration-grid button:nth-child(3)')
  await page.fill('#manual-description', 'Persist path smoke test')
  await page.click('#create-form button[type=submit]')

  const reported = await page
    .waitForFunction(
      () => {
        const el = document.getElementById('create-effects')
        const msg = document.getElementById('create-message')
        return (
          (el && !el.hidden && el.textContent.includes('Saved:')) ||
          (msg && !msg.hidden && msg.textContent.length > 0)
        )
      },
      { timeout: 30000 }
    )
    .then(() => true)
    .catch(() => false)

  check('a command with a connection takes the persist path and reports back', reported)

  const effects = (await page.textContent('#create-effects'))?.trim() ?? ''
  const message = (await page.textContent('#create-message'))?.trim() ?? ''
  // Either a reported failure or a reported outcome — never the "Requested:"
  // wording, which would mean it had quietly used the no-effect path.
  // The whole point: an accepted command with a connection must report what
  // the WRITE did, not what was requested. Against a repository that does not
  // exist that is a failure — which is the correct, audible answer.
  check(
    'and reports what the write did, not what was requested',
    effects.startsWith('Saved:') && effects.includes('failed'),
    effects || message
  )
}

// Scoped to the fixture-backed part of the run, not filtered by message text.
//
// The earlier version filtered on strings like "Failed to fetch", which is
// what THIS sandbox produces when the network is unreachable. CI can reach
// api.github.com, so the same deliberate request there returns 401 and the
// filter missed it — a check that encoded one environment's behaviour and
// failed in another. Counting errors up to the deliberate-failure section
// says what was actually meant: nothing should go wrong before we point the
// page at a repository that does not exist.
check(
  'no page errors while running against fixtures',
  errorsBeforeConnecting.length === 0,
  errorsBeforeConnecting.slice(0, 2).join(' | ')
)

await browser.close()
server.kill()

console.log('')
console.log(failures === 0 ? 'browser kernel: all checks passed' : `browser kernel: ${failures} FAILURE(S)`)
process.exit(failures === 0 ? 0 : 1)
