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

## Where observation reconciliation will slot in (not yet implemented)

This document records the **intended integration point** for the future
work (CHR-INT-010/011/015); implementing it is out of scope for this PR
(CHR-INT-001 through -006 only — see `INTEGRATION-CONTRACT.md`).

Reconciliation is proposed to run as one more step in the same
identity-resolve chain `handleMessage`'s `identityEffects` already builds
— i.e., once GitHub sync is configured and identified, alongside (not
instead of) the existing settings/ledger/reference pulls:

```
identity resolves
    ├── pull settings.json      (existing)
    ├── pull ledger.json        (existing)
    ├── pull reference.json     (existing)
    └── list + reconcile observations/receipts   (new)
```

Reconciliation itself needs a **list** operation the current
`GitHubSync.fs` does not yet have (every existing GitHub read is "GET one
known file path," never "list a directory's contents") — the GitHub
Contents API supports listing a directory via the same endpoint
(`GET /repos/{owner}/{repo}/contents/{path}`, returning an array instead
of an object when `path` is a directory), so this fits the existing
`HttpEffect` shape without a new effect kind, but does need a new
response-parsing function in `GitHubSync.fs` and a new correlation-id
convention (proposed: `"github-observations-list"`,
`"github-receipts-list"`).

Per section 20/72, reconciliation must never block the first render, and
per the accepted V1 tradeoff (section 72), it only runs when Chrona's
WASM starts — there is no background service, no polling, no push
notification from GitHub when ROS writes a new observation.
