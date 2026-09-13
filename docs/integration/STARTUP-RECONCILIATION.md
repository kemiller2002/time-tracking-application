# CHR-INT-001 — Current Startup Behavior, and Where Reconciliation Will Slot In

## Current startup flow (as of the baseline commit)

1. `web/main.js` registers the service worker (best-effort, never blocks)
   and constructs `DomBindings(new WasmEngineTransport())`, then calls
   `bindings.start()`.
2. `DomBindings.start()` (`web/dom-bindings.js`):
   a. Binds delegated DOM listeners, the connectivity-retry listener, and
      the timer tick interval.
   b. Awaits `transport.start()` — loads and initializes the WASM module.
   c. Dispatches `{ kind: "Initialize", protocolVersion: 1, capabilities: ["Storage"] }`
      to `Ledger.Engine.Dispatch.handle`.
   d. Applies the response: renders the view **immediately** (whatever
      `Session.initial()`'s fixture state produces, since nothing has
      loaded yet), then runs the effects the response asked for.
3. `Dispatch.handle`'s `Initialize` case requests exactly two effects:
   `StorageEffect("load", StorageGet, "business-activity-ledger:v1")` and
   `StorageEffect("github-config-load", StorageGet, "business-activity-ledger:github-config:v1")`.
4. `dom-bindings.js` runs effects **sequentially**, awaiting each one's
   result before starting the next, and re-renders after every single
   result. The two `Initialize` effects above are a Storage effect
   (synchronous `localStorage` read, resolves near-instantly) — so the
   UI is showing real persisted data within one JS microtask of startup,
   well before any network activity.
5. If a cached GitHub config is found, its own effect-result handler
   chains further requests (identity lookup if `Login` is missing, else
   straight to a settings pull + a ledger pull + a reference pull) — see
   `Dispatch.fs`'s `handleMessage`, `EffectResultMessage` branch,
   `identityEffects`. Each of these is a real network round trip, run one
   at a time, each one re-rendering the view as it resolves.

**The UI is already usable before any GitHub network activity completes.**
This satisfies section 20/55 of the specification without needing new
code — `Storage` is always tried first and is synchronous, and every
subsequent GitHub effect is asynchronous, sequential, and re-renders
incrementally rather than gating the first render.

## Where observation reconciliation will slot in (building blocks done, orchestration not yet wired)

This document records the **intended integration point**. As of
CHR-INT-011/012/013, `GitHubSync.fs` has every effect-request/response
building block reconciliation needs (listing the inbox, reading one
observation, checking for and creating a candidate/receipt — see below)
and `Integration.fs` has the pure decision (`ObservationReconciliation.
decide`/`reconcileRaw`, CHR-INT-008/009/010). **None of it is called from
anywhere yet** — `Dispatch.fs`'s `handleMessage` still only ever builds
the settings/ledger/reference pull effects it already did before this
work started. Wiring these pieces together into the actual startup
sequence, including the write-ordering and idempotent-retry behavior
sections 21-26 require, is CHR-INT-015, deferred to its own PR.

Reconciliation is proposed to run as one more step in the same
identity-resolve chain `handleMessage`'s `identityEffects` already builds
— i.e., once GitHub sync is configured and identified, alongside (not
instead of) the existing settings/ledger/reference pulls:

```
identity resolves
    ├── pull settings.json      (existing)
    ├── pull ledger.json        (existing)
    ├── pull reference.json     (existing)
    └── list + reconcile observations/receipts   (CHR-INT-015, not yet wired)
```

### The building blocks now available (`GitHubSync.fs`)

- `buildObservationsListEffect config projectId` — lists a project's
  observation inbox. The GitHub Contents API returns a JSON *array*
  (rather than every other GET in this file's single object) when `path`
  names a directory — `parseObservationListing` reads that shape,
  filtering to `.json` file entries.
- `buildObservationGetEffect config path` — reads one already-located
  observation (reuses the existing `parseGetResponse`, the same
  single-file shape `reference.json`/`settings.json`/`ledger.json` already
  parse).
- `candidatePath`/`observationReceiptPath` — Chrona-internal deterministic
  paths (`<Folder>/integration/candidates/<safe id>.json`,
  `<Folder>/integration/observation-receipts/<safe id>.json`), not part of
  the public `Chrona.Integration` package (a producer never needs them —
  specification §51).
- `buildCandidateGetEffect`/`buildReceiptGetEffect` — check whether a
  candidate/receipt already exists for a given id (§23's idempotency
  check) — a 404 means it doesn't.
- `buildCandidatePutEffect`/`buildReceiptPutEffect` — **create-only**:
  always send `sha = None`. GitHub's Contents API rejects a create-with-
  no-`sha` PUT against a path that already exists, which is exactly the
  fail-safe behavior two clients racing to process the same observation
  need (§43/44) — neither can silently overwrite the other's write.

None of these functions call each other or decide *when* to call which —
that sequencing (list → for each unreceipted observation, check for an
existing candidate, decide via `ObservationReconciliation`, write
candidate-then-receipt in that order per §22) is exactly what CHR-INT-015
adds, together with the actual `EffectResult` handling in `Dispatch.fs`
that today only exists for settings/ledger/reference/whoami/push.

Per section 20/72, reconciliation must never block the first render, and
per the accepted V1 tradeoff (section 72), it only runs when Chrona's
WASM starts — there is no background service, no polling, no push
notification from GitHub when ROS writes a new observation.
