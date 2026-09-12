# UI offline behavior

Superseded by this app's F#/WASM, browser-embedded, no-server rewrite — the
original version of this doc described an offline command queue (request
IDs, `Local only`/`Sending`/`Saved`/`Needs attention`/`Conflict` states, a
409 pausing a command) built for a since-retired Cloudflare-Worker backend
every write went through. That backend no longer exists; this describes
what actually runs today.

- **The app shell loads offline.** `web/service-worker.js` caches the
  static shell (`index.html`/`styles.css`/`main.js`/`wasm-engine-transport.js`/
  `dom-bindings.js`) on install and grows a runtime cache from real traffic
  for everything else same-origin — in particular the WASM runtime's own
  `_framework/*` files, whose exact names depend on the current
  `dotnet publish` output and are never hardcoded. A page navigation with
  nothing cached yet (only possible on a first-ever, already-offline visit)
  falls back to the cached shell rather than failing outright.
- **Every core command is already offline-first, with no queue needed at
  all.** Create/amend/void/restore/split/merge/evidence/attest/timer all
  write straight to `localStorage` (`Dispatch.fs`'s `"save"` `StorageEffect`)
  — a write that never depends on a network round trip has nothing to queue,
  retry, or report a pending state for. There is no `Local only`/`Sending`
  distinction because there is no "sending": the write is already durable
  the instant it succeeds locally, whether or not the device is online.
- **GitHub sync — the one network-dependent feature — retries itself.** A
  push that fails while offline (or for any other reason) leaves
  `gitHubSyncStatus` at `"error"`; `web/dom-bindings.js` listens for the
  browser's `online` event and re-dispatches the same `PushToGitHub` event
  the "Sync now" button fires whenever that error state is showing and sync
  is configured — no idempotency-key queue needed, since the atomic commit
  chain (`docs/DOMAIN-REQUIREMENTS.md`'s Implementation section) and its
  auto-merge/reconciliation paths already make a retried push safe to fire
  more than once.
- **Evidence stays link/metadata only** (`Model.Evidence`'s `Uri`/`Note`/
  `Hash`/`Label` fields) — there is no binary upload feature in this
  implementation at all, so the old doc's "resumable upload support" concern
  doesn't apply; nothing here needs it to exist offline or online.
