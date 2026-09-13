# CHR-INT-001/015 — Current Startup Behavior, and Where Reconciliation Slots In

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

## Where observation reconciliation slots in (CHR-INT-015 — wired)

As of CHR-INT-015, reconciliation actually runs. It is seeded the moment
`reference.json` resolves — not as a sibling effect requested alongside
it, but chained off its own result (`Dispatch.fs`'s
`beginObservationReconciliation`, called from `handleEffectResult`'s
"github-reference-pull" success/404 cases) — precisely so the very first
inbox listing it builds is always based on the just-pulled project list,
never a stale one an effect built earlier in the same `handle` call would
have captured:

```
identity resolves
    ├── pull settings.json      (existing)
    ├── pull ledger.json        (existing)
    └── pull reference.json     (existing)
            └── seed reconciliation over every known project, then:
                list inbox -> for each observation:
                    read it -> check receipt -> check candidate ->
                    (write candidate ->) write receipt -> next observation
                (then advance to the next project once one's queue is empty)
```

Every step is exactly one `HttpEffect` in flight at a time, one step per
`Dispatch.handle` call — mirroring the existing atomic-commit chain
(`buildRefGetEffect` through `buildRefUpdateEffect`) rather than an
in-process loop, since that is the WASM boundary's actual constraint.
`Session.IntegrationReconciliationStep` names exactly which response is
outstanding at each point; `handleEffectResult` decides state transitions
(deserializing responses, calling `ObservationReconciliation.decide`/
`reconcileRaw`, advancing the step or the observation/project queue), and
`handleMessage`'s `observationReconciliationEffects` closure reads the
already-advanced step to build the one next `GitHubSync.build*Effect`
request — see `Dispatch.fs`'s "CHR-INT-015: startup observation
reconciliation" section for the full case-by-case reasoning (in
particular why a candidate write's 409/422 is treated as success-
equivalent, unlike a ledger push's, and why any other non-success at any
step just skips the one observation or project in progress rather than
surfacing a user-visible error — everything is safely retried, from
scratch, on the next startup).

A project with nothing to reconcile (an empty or 404 inbox) advances
straight to the next project; once every known project's inbox has been
exhausted, `Session.State.IntegrationReconciliation` reverts to `None`
until the next `reference.json` pull re-seeds it. Verified by 9
`Ledger.Engine.Specs` driving `Dispatch.handle` directly through a full
pass (including the receipt-repair and unrecognized-project-rejection
cases) and, live, in a browser via Playwright with GitHub's API mocked at
the network layer.

### The building blocks `Dispatch.fs` now drives (`GitHubSync.fs`)

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
candidate-then-receipt in that order per §22) lives entirely in
`Dispatch.fs`, per the "CHR-INT-015 — wired" section above.

Per section 20/72, reconciliation must never block the first render, and
per the accepted V1 tradeoff (section 72), it only runs when Chrona's
WASM starts — there is no background service, no polling, no push
notification from GitHub when ROS writes a new observation.
