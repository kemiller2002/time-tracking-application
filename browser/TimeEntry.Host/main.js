// The browser bridge. TE-R-092 limits this file to WASM loading, event
// forwarding, transport and DOM binding — no business rules, no validation,
// no totals, no filtering, no sorting. Every number rendered below was
// computed by the F# kernel; this file only places strings into elements.

import { dotnet } from './_framework/dotnet.js'

const { getAssemblyExports, getConfig } = await dotnet.create()
const exports = await getAssemblyExports(getConfig().mainAssemblyName)
const kernel = exports.TimeEntry.Host.Interop

// Transport: fetch the stored documents. In the deployed app these come from
// the GitHub API; here a local fixture stands in so the architectural path
// can be exercised without credentials.
const stored = await fetch('./sample-entry.json').then((r) => r.json())

// The kernel decides everything. Note what is NOT computed here: no summing,
// no rounding, no unit conversion, no filtering.
const view = JSON.parse(kernel.ViewDay(JSON.stringify({ date: '2026-09-10', entries: [stored] })))

const render = () => {
  const total = document.getElementById('daily-total')
  const count = document.getElementById('activity-count')
  const list = document.getElementById('timeline')
  if (!total || !count || !list) return

  // Display strings come straight from the projection (TE-R-085).
  total.textContent = `${view.displayHours}h ${view.displayMinutes}m`
  count.textContent = `${view.countedEntries} ${view.countedEntries === 1 ? 'activity' : 'activities'}`

  list.replaceChildren(
    ...view.entries.map((row) => {
      const li = document.createElement('li')
      li.className = 'record'
      const title = document.createElement('strong')
      title.textContent = row.description ?? 'No description'
      const time = document.createElement('em')
      time.textContent = `${row.displayHours}h ${row.displayMinutes}m`
      // Capabilities are read, never decided here (TE-R-097).
      const actions = document.createElement('span')
      actions.className = 'meta'
      actions.textContent = row.capabilities.join(' · ')
      li.append(title, time, actions)
      return li
    })
  )
}

render()

// Exposed so the headless verification can assert on the kernel's answer.
globalThis.__kernel = { exportKeys: Object.keys(kernel), view }
