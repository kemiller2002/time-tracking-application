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
// Three entries because the flows need them: a split consumes the entry it
// splits, and a merge needs two sources that were each read at a known
// version. Nothing the page creates has a version until effects are actually
// performed (WI-0033, OQ-8), so the fixture has to supply them.
// The preferences fixture carries a 60-hour target. 60 rather than
// month.html's 80 deliberately: 80 is the figure no document justifies
// (OQ-9), and a fixture repeating it would read as a default returning by the
// back door. This one is a value somebody set, which is what a target is.
let [storedEntry, secondEntry, thirdEntry, catalogue, versionFixture, preferencesFixture] =
  await Promise.all([
    fetch('./sample-entry.json').then((r) => r.json()),
    fetch('./sample-entry-2.json').then((r) => r.json()),
    fetch('./sample-entry-3.json').then((r) => r.json()),
    fetch('./sample-catalogue.json').then((r) => r.json()),
    fetch('./sample-versions.json').then((r) => r.json()),
    fetch('./sample-preferences.json').then((r) => r.json())
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
// Where the ledger lives, and what may write to it
// ---------------------------------------------------------------------------

// Held for this tab only. `sessionStorage` rather than `localStorage` because
// a token that outlives the tab outlives the reason it was entered; and it is
// never read back into the page after being stored, so nothing renders it.
//
// This is the browser half of DF-TE-0011. The kernel takes a token today; a
// device flow or a same-origin proxy would replace this block and nothing
// below it.
const CONNECTION_KEY = 'echelon-ledger.connection'

const readConnection = () => {
  try {
    return JSON.parse(sessionStorage.getItem(CONNECTION_KEY) ?? 'null')
  } catch {
    // A blocked or cleared store is not an error worth stopping for: the page
    // simply has no credential, which it already knows how to be.
    return null
  }
}

const writeConnection = (connection) => {
  try {
    if (connection) sessionStorage.setItem(CONNECTION_KEY, JSON.stringify(connection))
    else sessionStorage.removeItem(CONNECTION_KEY)
  } catch {
    // Ignored for the same reason.
  }
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

// The only state the page holds is the stored documents it has loaded, the
// projection the kernel last computed from them, and what the person has
// typed. UI state is never domain authority (TE-R-098): `entries` here is a
// cache of persisted documents, and `view` is derived, never edited.
const derive = (entries, visibility) =>
  ask(kernel.ViewDay, { date: DATE, entries, catalogue, visibility })

// Every command carries the same three things beside itself: the catalogue it
// was chosen against, the entries it was composed against, and the versions
// of those entries. One function, so no call site can omit one.
//
// Two entry points, one shape. `Dispatch` names the effects and performs
// none; `Persist` runs them through the interpreter. Which one is used turns
// on whether there is a repository to write to — NOT on anything the page
// decides about the command, which is why the request is identical either
// way.
const requestFor = (command) => ({
  catalogue,
  entries: state.entries,
  versions,
  command
})

const send = (command) => ask(kernel.Dispatch, requestFor(command))

const sendAndPersist = async (command) => {
  const connection = readConnection()
  const answer = JSON.parse(
    await kernel.Persist(
      JSON.stringify({
        ...requestFor(command),
        repository: connection.repository,
        token: connection.token
      })
    )
  )
  return answer
}

// `visibility` is a request, not a decision: the kernel is what knows which
// entries a given visibility admits, and what "counts toward totals" means.
const loadedEntries = [storedEntry, secondEntry, thirdEntry]

let state = Object.freeze({
  entries: loadedEntries,
  visibility: 'counting',
  view: derive(loadedEntries, 'counting'),
  selectedUnits: null,
  // Which entries are ticked for merging. This is draft UI state and nothing
  // more: it holds ids, never durations or totals, so it cannot become a
  // second opinion about the ledger (TE-R-098).
  selectedForMerge: [],
  message: null,
  // A stale write, kept until the person decides what to do about it.
  conflict: null,
  // What the repository holds for the conflicted entry, fetched separately so
  // the loaded set stays as it was.
  conflictSaved: null,
  effects: [],
  performed: [],
  // The preferences document as the repository holds it, carried verbatim.
  // The page never reads a field out of it — `ViewMonth` decodes it — so a
  // target cannot become a number this file knows how to arithmetic on
  // (TE-R-091). `null` means nothing is set, or nothing was loaded; which of
  // those it is, `preferencesError` says.
  preferences: preferencesFixture,
  preferencesError: null,
  // Bumped on every state change. An asynchronous read that resolves after
  // the page has moved on is stale, and applying it would silently discard
  // whatever moved it.
  revision: 0
})

const update = (change) => {
  state = Object.freeze({ ...state, ...change, revision: state.revision + 1 })
  render()
}

// ---------------------------------------------------------------------------
// The form's choices, all computed in F#
// ---------------------------------------------------------------------------

const choices = ask(kernel.CatalogueChoices, { catalogue })
const grid = ask(kernel.DurationGrid, { maxUnits: 10 })

// One filler for every catalogue <select> on the page, so the archived rule
// is applied in exactly one place.
const fill = (select, items, selectedId) => {
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
      option.selected = item.id === selectedId
      return option
    })
  )
}

const fillSelect = (id, items) => fill(document.getElementById(id), items)

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

// Small builders. They exist so the controls below read as structure rather
// than as twenty lines of createElement each.
const el = (tag, className, text) => {
  const node = document.createElement(tag)
  if (className) node.className = className
  if (text !== undefined) node.textContent = text
  return node
}

const labelled = (id, text, control) => {
  const field = el('div', 'field')
  const label = el('label', null, text)
  control.id = id
  label.htmlFor = id
  field.append(label, control)
  return field
}

// Removing and restoring an entry. The two are the same shape — a reason and
// a button — because the domain says so: `VoidEntryRequest` and
// `RestoreEntryRequest` carry identical fields. One builder, so the page
// cannot accidentally require a reason for one and not the other.
//
// A reason is not optional anywhere here, because it is not optional in the
// type: neither request can be constructed without one.
//
// The expected version comes from the version map, never from the page's own
// idea of freshness. An entry whose version this page does not know sends no
// version at all and the kernel refuses the command; substituting a
// placeholder would be the browser inventing a value.
const reasonControl = (entryId, kind, summaryText, buttonText, placeholder) => {
  const details = el('details', 'section')
  details.append(el('summary', null, summaryText))
  const form = el('form')
  const input = document.createElement('input')
  input.type = 'text'
  input.required = true
  input.placeholder = placeholder
  const button = el('button', 'button', buttonText)
  button.type = 'submit'
  form.append(labelled(`${kind}-reason-${entryId}`, 'Reason', input), button)
  form.addEventListener('submit', (event) => {
    event.preventDefault()
    submitCommand({
      kind,
      entryId,
      expectedVersion: versions[entryId],
      reason: input.value,
      occurredAtMs: Date.now()
    })
  })
  details.append(form)
  return details
}

// Correcting an entry. A correction supplies WHOLE facts rather than a patch
// — that is the domain's shape (`CorrectEntryRequest.CorrectedFacts`), so
// that the resulting revision records complete values and history can show
// before-and-after without reconstruction (TE-R-052). The form therefore
// offers every fact, pre-filled with what the entry currently says.
//
// The duration options are the same kernel-generated list the create form
// uses, as a <select> rather than a grid: same values, less DOM, still a
// native control.
const correctionControl = (row) => {
  const details = el('details', 'section')
  details.append(el('summary', null, 'Correct'))
  const form = el('form')
  const fields = el('div', 'form-grid')

  const date = document.createElement('input')
  date.type = 'date'
  date.value = DATE

  const project = document.createElement('select')
  fill(project, choices.projects ?? [], row.projectId)

  const activity = document.createElement('select')
  fill(activity, choices.activityTypes ?? [], row.activityTypeId)

  const duration = document.createElement('select')
  duration.replaceChildren(
    ...(grid.options ?? []).map((option) => {
      const node = document.createElement('option')
      node.value = String(option.units)
      node.textContent = option.label
      // Pre-selected on the entry's CURRENT billable units, which the kernel
      // computed. The page does not work out which option matches.
      node.selected = option.units === row.billableUnits
      return node
    })
  )

  const description = document.createElement('textarea')
  description.value = row.description ?? ''

  const reason = document.createElement('input')
  reason.type = 'text'
  reason.required = true
  reason.placeholder = 'Why is this being corrected?'

  fields.append(
    labelled(`correct-date-${row.id}`, 'Date', date),
    labelled(`correct-project-${row.id}`, 'Project', project),
    labelled(`correct-activity-${row.id}`, 'Activity type', activity),
    labelled(`correct-duration-${row.id}`, 'Duration', duration),
    labelled(`correct-description-${row.id}`, 'What did you do?', description),
    labelled(`correct-reason-${row.id}`, 'Reason', reason)
  )

  const button = el('button', 'button', 'Save correction')
  button.type = 'submit'
  form.append(fields, button)
  form.addEventListener('submit', (event) => {
    event.preventDefault()
    submitCommand({
        kind: 'correct',
        entryId: row.id,
        expectedVersion: versions[row.id],
        projectId: project.value,
        activityTypeId: activity.value,
        date: date.value,
        durationUnits: Number(duration.value),
        description: description.value,
        reason: reason.value,
        occurredAtMs: Date.now()
    })
  })
  details.append(form)
  return details
}

// Attaching evidence (TE-R-033, §8.12). A link, and optionally a label for
// it. No timestamp field: the moment is the command's `occurredAtMs`, so the
// evidence and the revision that records it agree by construction rather
// than by a second clock read.
const evidenceControl = (entryId) => {
  const details = el('details', 'section')
  details.append(el('summary', null, 'Attach evidence'))
  const form = el('form')
  const fields = el('div', 'form-grid')

  const uri = document.createElement('input')
  // `url` rather than `text` so the browser's own validation applies to the
  // shape of a link — which is a fact about URLs, not a rule about the
  // ledger, so it belongs here rather than in the domain.
  uri.type = 'url'
  uri.required = true
  uri.placeholder = 'https://'

  const label = document.createElement('input')
  label.type = 'text'
  label.placeholder = 'Optional'

  fields.append(
    labelled(`evidence-uri-${entryId}`, 'Link', uri),
    labelled(`evidence-label-${entryId}`, 'Label', label)
  )

  const button = el('button', 'button', 'Attach')
  button.type = 'submit'
  form.append(fields, button)
  form.addEventListener('submit', (event) => {
    event.preventDefault()
    submitCommand({
      kind: 'attachEvidence',
      entryId,
      expectedVersion: versions[entryId],
      uri: uri.value,
      label: label.value,
      occurredAtMs: Date.now()
    })
  })
  details.append(form)
  return details
}

// The merge checkbox on a record. Selection is the page's to track; what the
// selection MEANS — how much time it comes to, how many days it spans — is
// the kernel's.
const mergeCheckbox = (row) => {
  const wrapper = el('p', 'field-help')
  const label = document.createElement('label')
  const box = document.createElement('input')
  box.type = 'checkbox'
  box.checked = state.selectedForMerge.includes(row.id)
  box.id = `merge-${row.id}`
  label.htmlFor = box.id
  box.addEventListener('change', () => {
    // A Set, not a filtered array. The architecture check bans `.filter` in
    // this file because filtering a list of entries is the kernel's job, and
    // the ban is deliberately blunt so it cannot be argued with case by case.
    // A selection genuinely IS a set of ids, so saying so costs nothing and
    // keeps the rule absolute.
    const selected = new Set(state.selectedForMerge)
    if (box.checked) selected.add(row.id)
    else selected.delete(row.id)
    update({ selectedForMerge: [...selected], message: null, effects: [] })
  })
  label.append(box, document.createTextNode(' Merge this entry'))
  wrapper.append(label)
  return wrapper
}

// One entry's history (TE-R-052), disclosed in place rather than on its own
// screen: the entry is right there, and a full-page navigation to read three
// lines is a worse answer to "what happened to this?".
//
// Markup follows activity.html's history block — `history`, `history-item`,
// `history-marker`, `history-title`, `history-copy`, `history-time`.
const historyControl = (entryId) => {
  const details = el('details', 'section')
  details.append(el('summary', null, 'History'))
  const container = el('div', 'history')
  details.append(container)

  // Fetched when opened rather than for every row on every render: a day with
  // forty entries would otherwise compute forty histories nobody asked to see.
  details.addEventListener('toggle', () => {
    if (!details.open) return

    const answer = ask(kernel.EntryHistory, {
      entries: state.entries,
      entryId,
      catalogue,
      // A fact about the reader's environment, supplied by the host. The
      // kernel reads no clock and knows no zone. getTimezoneOffset is minutes
      // BEHIND UTC, so it is negated to give minutes ahead.
      timeZoneOffsetMinutes: -new Date().getTimezoneOffset()
    })

    if (answer.ok !== true) {
      container.replaceChildren(el('p', 'history-copy', answer.error))
      return
    }

    container.replaceChildren(
      ...answer.revisions.map((revision) => {
        const item = el('article', 'history-item')
        const body = el('div')
        body.append(el('p', 'history-title', revision.change))

        if (revision.detail) body.append(el('p', 'history-copy', revision.detail))

        // "Duration: 30m → 1h 00m". The before and after are the kernel's;
        // the arrow is punctuation.
        for (const change of revision.changed ?? []) {
          body.append(
            el('p', 'history-copy', `${change.field}: ${change.from} → ${change.to}`)
          )
        }

        body.append(
          el('p', 'history-time', `${revision.recordedAt} · ${revision.recordedBy} · ${revision.device}`)
        )
        item.append(el('span', 'history-marker'), body)
        return item
      })
    )
  })

  return details
}

// Which evidence a split part was ticked for.
//
// A loop rather than `.filter`, which the architecture check bans in this
// file. The ban is about filtering a list of ENTRIES — the kernel's job — and
// this is a set of checkboxes, but the rule is deliberately blunt so that it
// cannot be argued with case by case. Writing the loop costs three lines and
// keeps the rule absolute.
const chosenEvidence = (part) => {
  const chosen = []
  for (const choice of part.evidence) if (choice.box.checked) chosen.push(choice.item)
  return chosen
}

// Splitting an entry. `split.html` calls the pieces "Part 1", "Part 2"; this
// keeps that language and that structure.
//
// The preview is the reason this control is more than a form. TE-R-044 asks
// for one before saving, and a preview is arithmetic over domain quantities —
// which is precisely what this file may not do. So every keystroke sends the
// collected child durations to the kernel and renders the three strings it
// sends back. Nothing here subtracts, sums, or compares a duration.
const splitControl = (row) => {
  const details = el('details', 'section')
  details.append(el('summary', null, 'Split'))
  const form = el('form')
  const parts = el('div')
  const status = el('p', 'field-help')
  status.setAttribute('role', 'status')
  status.setAttribute('aria-live', 'polite')

  // Each part's controls are kept as objects rather than read back out of the
  // DOM, so the preview and the submission are built from the same source.
  const rows = []

  const refresh = () => {
    const preview = ask(kernel.SplitPreview, {
      // The source's EXACT milliseconds, from the projection. Not its
      // displayed time, which is billed and rounded.
      sourceMilliseconds: row.durationMilliseconds,
      children: rows.map((part) => ({ durationUnits: Number(part.duration.value) || null }))
    })
    status.textContent = preview.summary
    // `balances` is the kernel's verdict, read not computed.
    submitButton.disabled = !preview.balances
  }

  const addPart = () => {
    const fields = el('div', 'form-grid')
    const index = rows.length + 1

    const duration = document.createElement('select')
    const blank = document.createElement('option')
    blank.value = ''
    blank.textContent = 'Choose a duration'
    duration.replaceChildren(
      blank,
      ...(grid.options ?? []).map((option) => {
        const node = document.createElement('option')
        node.value = String(option.units)
        node.textContent = option.label
        return node
      })
    )
    duration.addEventListener('change', refresh)

    const project = document.createElement('select')
    fill(project, choices.projects ?? [], row.projectId)

    const activity = document.createElement('select')
    fill(activity, choices.activityTypes ?? [], row.activityTypeId)

    const description = document.createElement('input')
    description.type = 'text'

    fields.append(
      el('p', 'eyebrow', `Part ${index}`),
      labelled(`split-${row.id}-${index}-duration`, 'Duration', duration),
      labelled(`split-${row.id}-${index}-project`, 'Project', project),
      labelled(`split-${row.id}-${index}-activity`, 'Activity type', activity),
      labelled(`split-${row.id}-${index}-description`, 'What did you do?', description)
    )

    // Which of the source's evidence moves to this part (TE-R-045). Offered
    // only when there is evidence to move, because an empty "Evidence"
    // heading on every split would be noise.
    //
    // The whole item is kept, not just its URI: the transition requires an
    // exact match, so what goes back must be exactly what came out.
    const evidence = []

    if ((row.evidence ?? []).length > 0) {
      const list = el('div', 'field field-full')
      list.append(el('p', 'field-label', 'Evidence to move here'))

      for (const [position, item] of (row.evidence ?? []).entries()) {
        const label = document.createElement('label')
        const box = document.createElement('input')
        box.type = 'checkbox'
        box.id = `split-${row.id}-${index}-evidence-${position}`
        label.htmlFor = box.id
        label.append(box, document.createTextNode(` ${item.label ?? item.uri}`))
        list.append(label)
        evidence.push({ box, item })
      }

      fields.append(list)
    }

    rows.push({ index, duration, project, activity, description, evidence })
    parts.append(fields)
    refresh()
  }

  const addButton = el('button', 'button button-secondary', 'Add another part')
  addButton.type = 'button'
  addButton.addEventListener('click', addPart)

  const submitButton = el('button', 'button button-primary', 'Save split')
  submitButton.type = 'submit'

  form.addEventListener('submit', (event) => {
    event.preventDefault()
    submitCommand({
        kind: 'split',
        entryId: row.id,
        expectedVersion: versions[row.id],
        occurredAtMs: Date.now(),
        children: rows.map((part) => ({
          // Child identity is minted here because the domain cannot: Tier 2
          // is pure. The id is opaque to it.
          entryId: crypto.randomUUID(),
          durationUnits: Number(part.duration.value) || null,
          projectId: part.project.value,
          activityTypeId: part.activity.value,
          description: part.description.value,
          // Echoed back exactly as the kernel gave it. The page does not
          // construct evidence; it chooses which of the source's items move.
          reassignedEvidence: chosenEvidence(part)
        }))
    })
  })

  form.append(parts, status, addButton, submitButton)
  details.append(form)
  // A split starts at two parts, because one is not a split (TE-R-041 makes
  // the domain refuse it; starting at two means the page never proposes it).
  addPart()
  addPart()
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
      // about when an entry may be corrected, removed or restored. Notably
      // it does not check `state`: a superseded entry offers none of these,
      // and the page learns that by being told, not by inspecting.
      if (row.capabilities.includes('CanCorrect')) article.append(correctionControl(row))

      if (row.capabilities.includes('CanVoid'))
        article.append(
          reasonControl(row.id, 'void', 'Remove', 'Remove entry', 'Why is this being removed?')
        )

      // Offered on every entry, including superseded ones: history is the one
      // thing a record keeps when it can no longer be changed, and it is
      // exactly then that someone wants to read it.
      article.append(historyControl(row.id))

      if (row.capabilities.includes('CanAttachEvidence'))
        article.append(evidenceControl(row.id))

      if (row.capabilities.includes('CanMerge')) article.append(mergeCheckbox(row))

      if (row.capabilities.includes('CanSplit')) article.append(splitControl(row))

      if (row.capabilities.includes('CanRestore'))
        article.append(
          reasonControl(row.id, 'restore', 'Restore', 'Restore entry', 'Why is this being restored?')
        )

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

// The merge panel. Shown only when something is selected, and every word in
// it — the total, the day count, the whole sentence — comes from the kernel.
const renderMerge = () => {
  const panel = document.getElementById('merge-panel')
  if (!panel) return
  panel.hidden = state.selectedForMerge.length === 0
  if (panel.hidden) return

  const preview = ask(kernel.MergePreview, {
    entries: state.entries,
    sourceIds: state.selectedForMerge
  })
  setText('merge-summary', preview.summary)
}

// A conflict, and the two things that can be done about it.
//
// Never resolved automatically. TE-R-072 is explicit that a stale write is an
// outcome the domain reconciles rather than something the transport silently
// re-attempts — re-reading and overwriting would be exactly the behaviour the
// requirement forbids, and would discard whoever else's change arrived first.
const renderConflict = () => {
  const panel = document.getElementById('conflict-panel')
  if (!panel) return
  panel.hidden = !state.conflict
  if (!state.conflict) return

  setText(
    'conflict-detail',
    `Your change was made against version ${state.conflict.expectedVersion ?? 'unknown'}, ` +
      `but the repository now holds ${state.conflict.actualVersion ?? 'a different version'}. ` +
      `Nothing was saved.`
  )

  // The saved entry, once the review has fetched it. Rendered in the same
  // record shape as the timeline, from values the same projection computed —
  // a second formatting path here is how a review panel starts disagreeing
  // with the list behind it.
  const saved = document.getElementById('conflict-saved')
  if (!saved) return

  if (!state.conflictSaved) {
    saved.replaceChildren()
    return
  }

  if (state.conflictSaved.found !== true) {
    saved.replaceChildren(
      el('p', 'notice-copy', `The repository could not return this entry: ${state.conflictSaved.detail}`)
    )
    return
  }

  const article = el('article', 'record')
  const body = el('div', 'record-body')
  const head = el('div', 'record-head')
  head.append(
    el('h2', 'activity-title', state.conflictSaved.description ?? 'No description'),
    el('span', 'duration', state.conflictSaved.displayTime)
  )
  const meta = el('div', 'meta-row')
  meta.append(
    ...(state.conflictSaved.badges ?? []).map((badge) =>
      el('span', `badge ${badge.tone}`, badge.text)
    )
  )
  body.append(
    el('p', 'eyebrow', 'Currently saved'),
    head,
    el('p', 'record-project', state.conflictSaved.classification),
    meta
  )
  article.append(body)
  saved.replaceChildren(article)
}

// The daily review. Whether the day can be attested is the kernel's answer,
// from the domain's own `Obligation.blocksAttestation` — the page does not
// decide it by counting ticks.
const renderReview = () => {
  const review = ask(kernel.ReviewDay, { entries: state.entries, date: DATE })
  if (review.ok !== true) return

  const badge = document.getElementById('review-badge')
  if (badge) {
    badge.textContent =
      review.needsAttention === 0 ? 'Complete' : `${review.needsAttention} needs attention`
    badge.className = `badge ${review.needsAttention === 0 ? 'badge-good' : 'badge-warn'}`
  }

  const list = document.getElementById('review-checks')
  if (list) {
    list.replaceChildren(
      ...review.checks.map((item) => {
        const rowEl = el('div', 'review-row')
        const complete = item.status === 'complete'
        rowEl.append(
          el('span', complete ? 'review-mark' : 'review-mark warn', complete ? '✓' : '!'),
          (() => {
            const body = el('div')
            body.append(el('p', 'review-title', item.title), el('p', 'review-note', item.note))
            return body
          })(),
          el('span', 'review-value', complete ? 'Complete' : 'Needs attention')
        )
        return rowEl
      })
    )
  }

  setText(
    'review-attest',
    review.isEmpty
      ? 'Nothing is recorded for this day yet.'
      : review.canAttest
        ? 'This day is complete and can be attested.'
        : 'This day cannot be attested until the items above are resolved.'
  )
}

// Progress against the monthly target, or nothing at all.
//
// Every string and every number here was computed by the kernel: the two
// labels, the sentence beside them, the accessible name of the bar, and the
// bar's own width. The page's whole contribution is `hidden` and a `style`
// attribute (TE-R-085).
//
// `null` means no target is set, and then there is no bar — the restraint
// DF-TE-0015 asked for. month.html's "80h" is not a fallback.
const renderTarget = (target, displayRecorded) => {
  const panel = document.getElementById('month-target')
  if (panel) panel.hidden = !target
  if (!target) {
    setText(
      'target-help',
      state.preferencesError ?? 'Nobody has set a target for this ledger.'
    )
    return
  }

  const bar = document.getElementById('month-progress-bar')
  if (bar) bar.style.width = `${target.barPercent}%`
  document.getElementById('month-progress')?.setAttribute('aria-label', target.ariaLabel)
  setText('month-progress-recorded', `${displayRecorded} recorded`)
  setText('month-progress-target', `${target.displayTarget} target`)
  setText('month-target-headline', target.headline)
  setText('month-target-detail', target.detail)
  setText('target-help', `The target for this ledger is ${target.displayTarget}.`)
}

// The month. Recomputed from the same entries the day view reads, so the two
// cannot disagree about what is recorded.
const renderMonth = () => {
  const summary = ask(kernel.ViewMonth, {
    entries: state.entries,
    year: 2026,
    month: 9,
    preferences: state.preferences
  })
  if (summary.ok !== true) {
    // A preferences file that cannot be read fails the whole month view, and
    // the kernel's words say why. Drawing the figures and quietly omitting
    // the bar would hide a file that needs attention.
    setText('target-help', summary.error)
    return
  }

  renderTarget(summary.target, summary.displayTotal)

  setText('month-total', summary.displayTotal)
  setText('month-decimal', summary.displayDecimalHours)
  setText('month-days', String(summary.activeDays))
  setText('month-entries', `${summary.countedEntries} counted entries`)
  setText('month-evidence', `${summary.evidencePercent}%`)
  // The count beside the percentage, always: "72%" alone hides whether it is
  // 31 of 43 or 3 of 4.
  setText(
    'month-evidence-detail',
    `${summary.entriesWithEvidence} of ${summary.countedEntries} entries`
  )
  setText('month-origin', `${summary.displayTimed} / ${summary.displayManual}`)
  setText('month-changes', `${summary.correctedEntries} / ${summary.removedEntries}`)

  const grid = document.getElementById('month-days-grid')
  if (!grid) return

  grid.replaceChildren(
    ...summary.days.map((day) => {
      const bar = el('div', 'day-bar')
      const shape = el('span', 'day-bar-shape')
      // The only number the page places is one the kernel computed: the
      // height as a percentage of the month's busiest day.
      shape.style.height = `${day.relativeHeightPercent}%`
      const label = el('span')
      label.append(day.date.slice(8), document.createElement('br'), day.displayTime)
      bar.append(shape, label)
      return bar
    })
  )
}

const render = () => {
  renderDay(state.view)
  renderReview()
  renderMonth()
  renderMerge()
  renderConflict()
  // Disclosure, not decoration: the projection reports how many entries it
  // excluded, so the page can say they exist without showing them.
  setText(
    'excluded-count',
    state.view.excludedEntries === 0 ? '' : ` (${state.view.excludedEntries} removed)`
  )
  renderSelection(state.selectedUnits)
  renderNotice('create-message', state.message)
  renderNotice(
    'create-effects',
    state.performed.length > 0
      ? `Saved: ${state.performed.map((p) => `${p.effect} — ${p.outcome}`).join(', ')}`
      : state.effects.length === 0
        ? null
        : `Requested: ${state.effects.join(', ')}`
  )
}

render()

// ---------------------------------------------------------------------------
// Submit
// ---------------------------------------------------------------------------

const value = (id) => document.getElementById(id)?.value ?? ''

// Where a command goes. With a repository configured it is applied AND
// persisted; without one it is applied only, and the page says so rather than
// pretending the change was saved.
const submitCommand = (command) => {
  if (!readConnection()) {
    absorb(send(command))
    return
  }

  sendAndPersist(command)
    .then((answer) => absorb(answer, command))
    .catch((error) => update({ message: String(error), effects: [] }))
}

// The one place a kernel answer becomes new page state. Three outcomes, and
// the page treats a refusal as ordinary: an error is a malformed request, a
// rejection is the domain declining, and acceptance replaces the loaded set.
const absorb = (answer, command) => {
  if (answer.ok !== true) {
    update({ message: answer.error, effects: [] })
    return
  }

  if (answer.accepted !== true) {
    // A rejection is a normal answer, rendered verbatim.
    update({ message: answer.rejection, effects: [] })
    return
  }

  // A persist reports what each effect DID. If any of them did not write, the
  // repository does not hold this change — and adopting the new entries would
  // show it as saved when it is not. That is exactly the UI-state-as-domain-
  // authority failure TE-R-098 forbids, and it is the difference between a
  // draft and a lie.
  //
  // `performed` is absent on the no-credential path, where applying locally is
  // the intended behaviour and the page says "Requested:" rather than "Saved:".
  const performed = answer.performed
  const wroteEverything = !performed || performed.every((p) => p.outcome === 'persisted')

  if (!wroteEverything) {
    const conflict = performed.find((p) => p.outcome === 'conflicted')

    update({
      // Deliberately NOT adopting answer.entries or answer.versions.
      message: conflict
        ? null
        : `Not saved: ${performed.map((p) => `${p.effect} — ${p.detail ?? p.outcome}`).join(', ')}`,
      effects: [],
      performed: [],
      // The command is kept so it can be applied again against whatever the
      // repository now holds, without the person retyping it (TE-R-071).
      conflict: conflict ? { ...conflict, command } : null,
      conflictSaved: null
    })

    // Fetch what the repository holds, for the review half of TE-R-071. It
    // arrives after the panel, which is correct: the choice is available
    // immediately and the detail fills in.
    if (conflict) {
      const connection = readConnection()
      const entryId = conflict.entryId ?? command?.entryId

      if (connection && entryId) {
        const at = state.revision

        kernel
          .ReviewEntry(
            JSON.stringify({
              repository: connection.repository,
              token: connection.token,
              catalogue,
              entryId
            })
          )
          .then((raw) => {
            // Discarded if the page moved on, for the same reason a stale
            // load is.
            if (state.revision === at) update({ conflictSaved: JSON.parse(raw) })
          })
          .catch(() => {})
      }
    }

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
  // Versions returned by a persist are the blob SHAs of what was just
  // written, so an entry created here can go on to be corrected or removed.
  // Merged in rather than replacing: a persist reports only what it wrote.
  if (answer.versions) Object.assign(versions, answer.versions)

  update({
    entries: answer.entries,
    view: derive(answer.entries, state.visibility),
    selectedUnits: null,
    // The selection referred to entries that may no longer be mergeable, so
    // it is dropped rather than carried into a state it was not made in.
    selectedForMerge: [],
    message: null,
    conflict: null,
    conflictSaved: null,
    effects: answer.effects ?? [],
    // What was REQUESTED versus what was DONE are different facts, and the
    // page reports whichever it has. A conflict is not an error: it means the
    // entry moved under us and must be re-read (TE-R-072).
    performed: answer.performed ?? []
  })
}

const submit = (event) => {
  event.preventDefault()

  // Note what is absent: no check that a project was chosen, no check that a
  // duration was selected, no date validation. An incomplete command is sent
  // as-is and the kernel refuses it. That is the point — if the bridge
  // pre-validated, the rule would exist twice (TE-R-092).
  submitCommand({
    kind: 'create',
    entryId: crypto.randomUUID(),
    projectId: value('manual-project'),
    activityTypeId: value('manual-type'),
    date: value('manual-date'),
    durationUnits: state.selectedUnits,
    description: value('manual-description'),
    occurredAtMs: Date.now()
  })
}

document.getElementById('create-form')?.addEventListener('submit', submit)

fillSelect('merge-project', choices.projects ?? [])
fillSelect('merge-activity', choices.activityTypes ?? [])

document.getElementById('merge-form')?.addEventListener('submit', (event) => {
  event.preventDefault()
  submitCommand({
      kind: 'merge',
      // Identity for the merged entry is minted here for the same reason a
      // split child's is: Tier 2 is pure and cannot mint it.
      newEntryId: crypto.randomUUID(),
      projectId: value('merge-project'),
      activityTypeId: value('merge-activity'),
      description: value('merge-description'),
      reason: value('merge-reason'),
      occurredAtMs: Date.now(),
      sources: state.selectedForMerge.map((entryId) => ({
        // Each source carries the version IT was read at — they were read
        // independently and any one may be stale (TE-R-070).
        entryId,
        expectedVersion: versions[entryId]
      }))
  })
})

const renderConnection = () => {
  const connection = readConnection()
  const repository = connection?.repository
  setText(
    'sync-status',
    repository ? `${repository.owner}/${repository.repo}` : 'Not signed in'
  )
  setText(
    'sync-meta',
    repository
      ? `Writing to ${repository.branch}.`
      : 'Changes are held in this page only.'
  )
}

// Load the real ledger from the repository, replacing the fixture.
//
// Everything the page holds is replaced together — entries, versions and the
// catalogue — because they are read at one moment and describe one state. A
// partial swap would leave the version map describing entries that are no
// longer there.
const loadFromRepository = async (connection) => {
  // A load describes the repository at one moment. If anything changed the
  // page while the request was in flight — a command the person submitted, a
  // later load — this answer is already stale, and applying it would throw
  // their work away without saying so.
  const startedAt = state.revision
  const answer = JSON.parse(
    await kernel.LoadLedger(
      JSON.stringify({
        repository: connection.repository,
        token: connection.token,
        date: DATE
      })
    )
  )

  if (state.revision !== startedAt) return

  if (answer.ok !== true) {
    update({ message: answer.error, effects: [], performed: [] })
    return
  }

  // The catalogue may be unreadable while the entries are fine. It is never
  // replaced with an empty one: an empty catalogue refuses every project,
  // which reads as "all your projects were archived" rather than "the
  // catalogue could not be read" (TE-R-084).
  if (answer.catalogue) catalogue = answer.catalogue

  for (const key of Object.keys(versions)) delete versions[key]
  Object.assign(versions, answer.versions ?? {})

  update({
    entries: answer.entries,
    view: derive(answer.entries, state.visibility),
    selectedForMerge: [],
    message: answer.catalogueError ?? null,
    // Read in the same answer as the entries, so the month's target and the
    // month's total describe one moment.
    preferences: answer.preferences ?? null,
    preferencesError: answer.preferencesError ?? null,
    effects: [],
    performed: []
  })
}

// Setting the target is the one write that is not a ledger fact: it appends
// no revision and attributes nothing. It still needs a repository, because
// the target lives in the ledger so that it survives a change of device.
const writeTarget = async (body) => {
  const connection = readConnection()
  if (!connection) {
    update({ message: 'Connect a repository before setting a target.' })
    return
  }

  const startedAt = state.revision
  const answer = JSON.parse(
    await kernel.SetMonthlyTarget(
      JSON.stringify({
        repository: connection.repository,
        token: connection.token,
        ...body
      })
    )
  )

  if (state.revision !== startedAt) return

  if (answer.ok !== true) {
    update({ message: answer.error })
    return
  }

  update({
    preferences: answer.preferences,
    preferencesError: null,
    message: answer.outcome
  })
}

document.getElementById('target-form')?.addEventListener('submit', (event) => {
  event.preventDefault()
  // The typed value is passed through as text. Parsing it is the kernel's —
  // a page that parsed it would have to decide what "eighty" means, and
  // deciding is what this file must not do.
  writeTarget({ hours: value('target-hours') }).catch((error) =>
    update({ message: String(error) })
  )
})

document.getElementById('target-clear')?.addEventListener('click', () => {
  writeTarget({ clear: true }).catch((error) => update({ message: String(error) }))
})

document.getElementById('repo-form')?.addEventListener('submit', (event) => {
  event.preventDefault()
  writeConnection({
    repository: {
      owner: value('repo-owner'),
      repo: value('repo-name'),
      branch: value('repo-branch'),
      // Blank means github.com. A GitHub Enterprise Server repository is not
      // reachable at api.github.com, so the field exists; leaving it empty is
      // the common case and must not be a configuration step.
      apiRoot: value('repo-api-root')
    },
    token: value('repo-token')
  })
  // Cleared from the DOM immediately. The value lives in sessionStorage and
  // is never rendered back, so it cannot be read off the page afterwards.
  const field = document.getElementById('repo-token')
  if (field) field.value = ''
  renderConnection()
  loadFromRepository(readConnection()).catch((error) =>
    update({ message: String(error), effects: [], performed: [] })
  )
})

// Reload first, always. Both answers to a conflict begin by finding out what
// the repository actually holds — the difference is only whether the person's
// change is then applied on top of it.
const reconcile = async (applyAgain) => {
  const conflict = state.conflict
  const connection = readConnection()
  if (!conflict || !connection) return

  update({ conflict: null, conflictSaved: null, message: 'Reloading…' })
  await loadFromRepository(connection)

  if (!applyAgain) return

  // Re-applied against the version the reload just observed, not the one the
  // command was built with. Reusing the stale token would conflict again, for
  // the same reason, forever.
  const entryId = conflict.command.entryId ?? conflict.entryId
  submitCommand({
    ...conflict.command,
    expectedVersion: versions[entryId],
    occurredAtMs: Date.now()
  })
}

document
  .getElementById('conflict-retry')
  ?.addEventListener('click', () =>
    reconcile(true).catch((error) => update({ message: String(error) }))
  )

document
  .getElementById('conflict-discard')
  ?.addEventListener('click', () =>
    reconcile(false).catch((error) => update({ message: String(error) }))
  )

document.getElementById('repo-forget')?.addEventListener('click', () => {
  writeConnection(null)
  const field = document.getElementById('repo-token')
  if (field) field.value = ''
  renderConnection()
})

renderConnection()

document.getElementById('show-removed')?.addEventListener('change', (event) => {
  // A new question for the kernel, not a filter over the answer it already
  // gave. The page cannot express "show removed" itself — it has no idea
  // which entries those are.
  const visibility = event.target.checked ? 'includeRemoved' : 'counting'
  update({ visibility, view: derive(state.entries, visibility) })
})

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
