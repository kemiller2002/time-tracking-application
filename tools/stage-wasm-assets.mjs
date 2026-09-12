#!/usr/bin/env node

// Copy the browser host's static assets into the published app bundle.
//
// This exists because `dotnet publish` does not reliably do it. MSBuild tracks
// its own up-to-date markers for `WasmExtraFilesToDeploy` rather than checking
// the destination, so a file deleted from — or older than — the bundle is not
// restored: a publish reports success and leaves the previous copy in place.
//
// That is a dangerous property for a verification harness. `check:browser`
// drives the published bundle, so a stale copy means the checks pass against
// code that is not the code in the repository. It happened: a reordering fix
// was verified as still broken, because the bundle still held the old file.
//
// Copying unconditionally costs milliseconds and removes the failure mode.

import { copyFileSync, existsSync, mkdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

const ROOT = new URL('..', import.meta.url).pathname
const HOST = join(ROOT, 'browser/TimeEntry.Host')
const BUNDLE = join(HOST, 'bin/Release/net8.0/browser-wasm/AppBundle')

// Kept in step with WasmExtraFilesToDeploy in TimeEntry.Host.csproj. A file
// listed there but not here would be published stale; one listed here but not
// there would fail the build first, which is the safer direction.
const ASSETS = [
  ['index.html', 'index.html'],
  ['main.js', 'main.js'],
  ['sample-entry.json', 'sample-entry.json'],
  ['sample-entry-2.json', 'sample-entry-2.json'],
  ['sample-entry-3.json', 'sample-entry-3.json'],
  ['sample-catalogue.json', 'sample-catalogue.json'],
  ['sample-versions.json', 'sample-versions.json'],
  ['sample-preferences.json', 'sample-preferences.json'],
  ['../../static-ui-screens/styles.css', 'styles.css']
]

if (!existsSync(BUNDLE)) {
  console.error(`no app bundle at ${BUNDLE} — run: dotnet publish browser/TimeEntry.Host -c Release`)
  process.exit(1)
}

let copied = 0

for (const [from, to] of ASSETS) {
  const source = join(HOST, from)
  const destination = join(BUNDLE, to)

  if (!existsSync(source)) {
    console.error(`missing asset: ${source}`)
    process.exit(1)
  }

  const changed =
    !existsSync(destination) ||
    readFileSync(source, 'utf8') !== readFileSync(destination, 'utf8')

  mkdirSync(dirname(destination), { recursive: true })
  copyFileSync(source, destination)
  if (changed) copied += 1
}

console.log(`staged ${ASSETS.length} assets into the app bundle (${copied} changed)`)
