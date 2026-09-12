// The browser bridge. TE-R-092 limits this file to WASM loading, event
// forwarding, transport and DOM binding — no business rules, no validation,
// no totals, no filtering, no sorting, no unit conversion.
//
// Read it with that claim in mind: every number and every label placed into
// the DOM below arrived from the F# kernel already computed. The file's whole
// vocabulary is `getElementById`, `textContent`, `append`, `JSON.parse` and
// `JSON.stringify`. `tools/check-domain-architecture.mjs` enforces that
// mechanically — it fails the build if `.reduce`, `.sort`, `.filter`,
// `Math.`, `* 60` or `/ 1000` appears here.

import { dotnet } from './_framework/dotnet.js'

const { getAssemblyExports, getConfig } = await dotnet.create()
const exports = await getAssemblyExports(getConfig().mainAssemblyName)
const kernel = exports.TimeEntry.Host.Interop

// Every call to the kernel is a string in and a string out. One wrapper, so
// the shape of the boundary is visible in one place.
const ask = (fn, request) => JSON.parse(fn(JSON.stringify(request)))

// Transport: fetch the stored documents. In the deployed app these come from
// the GitHub API; here local fixtures stand in so the architectural path can
// be exercised without credentials.
const [storedEntry, catalogue, versionFixture] = await Promise.all([
  fetch('./sample-entry.json').then((r) => r.json()),
  fetch('./sample-catalogue.json').then((r) => r.json()),
  fetch('./sample-versions.json').then((r) => r.json())
])

// Version tokens travel beside the documents, not inside them: a token is a
// hash OF a document. The store knows path -> blob SHA; the page carries that
// map and hands it back with every command so the domain can refuse a stale
// write (TE-R-070). An entry absent from this map has no known version, and
// every mutation against it is refused — which is the right answer, not a
// gap.
const versions = versionFixture.versions

const DATE = '2026-09-10'

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

// The only state the page holds is the stored documents it has loaded, the
// projection the kernel last computed from them, and what the person has
// typed. UI state is never domain authority (TE-R-098): `entries` here is a
// cache of persisted documents, and `view` is derived, never edited.
const derive = (entries) => ask(kernel.ViewDay, { date: DATE, entries, catalogue })

// Every command carries the same three things beside itself: the catalogue it
// was chosen against, the entries it was composed against, and the versions
// of those entries. One function, so no call site can omit one.
const send = (command) =>
  ask(kernel.Dispatch, { catalogue, entries: state.entries, versions, command })

let state = Object.freeze({
  entries: [storedEntry],
  view: derive([storedEntry]),
  selectedUnits: null,
  message: null,
  effects: []
})

const update = (change) => {
  state = Object.freeze({ ...state, ...change })
  render()
}

// ---------------------------------------------------------------------------
// The form's choices, all computed in F#
// ---------------------------------------------------------------------------

const choices = ask(kernel.CatalogueChoices, { catalogue })
const grid = ask(kernel.DurationGrid, { maxUnits: 10 })

const fillSelect = (id, items) => {
  const select = document.getElementById(id)
  if (!select) return
  select.replaceChildren(
    ...items.map((item) => {
      const option = document.createElement('option')
      option.value = item.id
      option.textContent = item.name
      // `selectable` was decided by the domain (DF-TE-0007). An archived
      // reference is still listed — hiding it would make the rejection it
      // produces unexplainable — but it cannot be chosen.
      option.disabled = !item.selectable
      return option
    })
  )
}

// Ids are manual-entry.html's own.
fillSelect('manual-project', choices.projects ?? [])
fillSelect('manual-type', choices.activityTypes ?? [])

const fillDurationGrid = () => {
  const container = document.getElementById('duration-grid')
  if (!container) return
  container.replaceChildren(
    ...(grid.options ?? []).map((option) => {
      const button = document.createElement('button')
      button.type = 'button'
      button.className = 'duration-option'
      // The label is the kernel's; the value is a unit count. The bridge
      // never converts between them.
      button.textContent = option.label
      button.dataset.units = String(option.units)
      button.setAttribute('aria-pressed', 'false')
      button.addEventListener('click', () =>
        update({ selectedUnits: option.units, message: null, effects: [] })
      )
      return button
    })
  )
}

fillDurationGrid()

// ---------------------------------------------------------------------------
// Render
// ---------------------------------------------------------------------------

const setText = (id, text) => {
  const element = document.getElementById(id)
  if (element) element.textContent = text
}

// Removing an entry. Native <details> discloses the reason field, which the
// domain requires — a void without a stated reason is unrepresentable
// (`VoidEntryRequest.Reason`), so there is no path here that omits it.
//
// The expected version comes from the version map, not from the page's idea
// of freshness. An entry with no known version submits an empty token and
// the domain refuses it; the page does not pre-empt that judgement.
const voidControl = (entryId) => {
  const details = document.createElement('details')
  details.className = 'section'
  const summary = document.createElement('summary')
  summary.textContent = 'Remove'
  const form = document.createElement('form')
  form.className = 'field'
  const label = document.createElement('label')
  const input = document.createElement('input')
  input.type = 'text'
  input.required = true
  input.placeholder = 'Why is this being removed?'
  input.id = `void-reason-${entryId}`
  label.htmlFor = input.id
  label.textContent = 'Reason'
  const button = document.createElement('button')
  button.className = 'button'
  button.type = 'submit'
  button.textContent = 'Remove entry'
  form.append(label, input, button)
  form.addEventListener('submit', (event) => {
    event.preventDefault()
    absorb(
      send({
        kind: 'void',
        entryId,
        // No fallback: an entry whose version this page does not know
        // sends no version, and the kernel refuses the command. Substituting
        // an empty token would be the browser inventing a value.
        expectedVersion: versions[entryId],
        reason: input.value,
        occurredAtMs: Date.now()
      })
    )
  })
  details.append(summary, form)
  return details
}

const renderDay = (view) => {
  // Display strings come straight from the projection (TE-R-085) — already
  // formatted and already pluralised, so nothing here builds a string out of
  // a domain number.
  setText('daily-total', view.displayTotal)
  setText('activity-count', view.countLabel)

  const list = document.getElementById('timeline')
  if (!list) return

  // The record's shape is today.html's: article.record > div.record-body >
  // div.record-head > h2.activity-title + span.duration, then
  // p.record-project, p.record-description, div.meta-row.
  list.replaceChildren(
    ...view.entries.map((row) => {
      const article = document.createElement('article')
      article.className = 'record'

      const body = document.createElement('div')
      body.className = 'record-body'

      const head = document.createElement('div')
      head.className = 'record-head'
      const title = document.createElement('h2')
      title.className = 'activity-title'
      title.textContent = row.description ?? 'No description'
      const duration = document.createElement('span')
      duration.className = 'duration'
      duration.textContent = row.displayTime
      head.append(title, duration)

      // "Research · Echelon Foundry" — composed by the kernel from the
      // catalogue, not assembled here.
      const classification = document.createElement('p')
      classification.className = 'record-project'
      classification.textContent = row.classification

      const meta = document.createElement('div')
      meta.className = 'meta-row'
      // Badge words and tone class are both the kernel's.
      meta.append(
        ...row.badges.map((badge) => {
          const span = document.createElement('span')
          span.className = `badge ${badge.tone}`
          span.textContent = badge.text
          return span
        })
      )

      body.append(head, classification, meta)
      article.append(body)

      // Capabilities are read, never decided here (TE-R-097). `includes` is
      // reading a list the kernel computed — the page has no rule of its own
      // about when an entry may be voided.
      if (row.capabilities.includes('CanVoid')) article.append(voidControl(row.id))
      return article
    })
  )
}

const renderSelection = (selectedUnits) => {
  const container = document.getElementById('duration-grid')
  if (!container) return
  for (const button of container.children) {
    const chosen = button.dataset.units === String(selectedUnits)
    button.classList.toggle('selected', chosen)
    button.setAttribute('aria-pressed', chosen ? 'true' : 'false')
  }
}

const renderNotice = (id, text) => {
  const element = document.getElementById(id)
  if (!element) return
  element.textContent = text ?? ''
  element.hidden = !text
}

const render = () => {
  renderDay(state.view)
  renderSelection(state.selectedUnits)
  renderNotice('create-message', state.message)
  renderNotice(
    'create-effects',
    state.effects.length === 0 ? null : `Requested: ${state.effects.join(', ')}`
  )
}

render()

// ---------------------------------------------------------------------------
// Submit
// ---------------------------------------------------------------------------

const value = (id) => document.getElementById(id)?.value ?? ''

// The one place a kernel answer becomes new page state. Three outcomes, and
// the page treats a refusal as ordinary: an error is a malformed request, a
// rejection is the domain declining, and acceptance replaces the loaded set.
const absorb = (answer) => {
  if (answer.ok !== true) {
    update({ message: answer.error, effects: [] })
    return
  }

  if (answer.accepted !== true) {
    // A rejection is a normal answer, rendered verbatim.
    update({ message: answer.rejection, effects: [] })
    return
  }

  // The kernel returned the WHOLE resulting set in its stored form, with the
  // command's changes already folded in by identity. It replaces the loaded
  // set and the projection is recomputed from it — the page never patches its
  // view in place, because that is how a UI cache starts disagreeing with the
  // ledger (TE-R-098).
  //
  // `answer.effects` names what the domain asked the host to do (persist to
  // GitHub). The browser reports it and does not perform it (TE-R-093); the
  // interpreter owns that, and until it is wired here the change is in the
  // page only.
  update({
    entries: answer.entries,
    view: derive(answer.entries),
    selectedUnits: null,
    message: null,
    effects: answer.effects ?? []
  })
}

const submit = (event) => {
  event.preventDefault()

  // Note what is absent: no check that a project was chosen, no check that a
  // duration was selected, no date validation. An incomplete command is sent
  // as-is and the kernel refuses it. That is the point — if the bridge
  // pre-validated, the rule would exist twice (TE-R-092).
  absorb(
    send({
      kind: 'create',
      entryId: crypto.randomUUID(),
      projectId: value('manual-project'),
      activityTypeId: value('manual-type'),
      date: value('manual-date'),
      durationUnits: state.selectedUnits,
      description: value('manual-description'),
      occurredAtMs: Date.now()
    })
  )
}

document.getElementById('create-form')?.addEventListener('submit', submit)

// Exposed so the headless verification can assert on the kernel's answers.
globalThis.__kernel = {
  exportKeys: Object.keys(kernel),
  get view() {
    return state.view
  },
  get effects() {
    return state.effects
  },
  choices,
  grid
}
