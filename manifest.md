# Feature Manifest — `Business Activity Ledger`

This manifest routes engineers and agents to authority. Link to semantic rules;
do not restate or independently implement them here.

## Purpose

Record, correct, and report on the owner's business activities: timer and
manual entry, amendment, void/restore, split/merge, evidence, daily
attestation, and daily/monthly reports — per
[`docs/DOMAIN-REQUIREMENTS.md`](docs/DOMAIN-REQUIREMENTS.md), the business-rule
authority this feature implements.

This feature is structured on
[`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`](.sde/architecture/FOUR-TIER-ARCHITECTURE.md).
Dependencies point downward only (Tier 4 → 3 → 2 → 1); enforced today by
`.fsproj` `ProjectReference` direction (`Ledger.Domain` has none, so Tier 1/2
cannot import Tier 3/4 even by accident).

## Ownership

- **Tier 1 — Semantic Model** (pure values; must not know about JSON, WASM,
  browser, or storage): `f-sharp/src/Ledger.Domain/Diagnostics.fs` (the
  stable failure-reason codes), `Model.fs` (entities, billing, capabilities,
  the timer state machine).
- **Tier 2 — State Transition / Domain Execution** (legal transitions,
  guards, invariants; pure `Environment -> LedgerDocument -> Command ->
  CommandResult`, never performs I/O itself): `f-sharp/src/Ledger.Domain/Commands.fs`
  (create/amend/void/restore/split/merge/evidence/attest, behind one shared
  `validateFields` authority), `Summary.fs` (day/month aggregation — a pure
  derived query, not a transition, but equally free of I/O).
- **Transitions / commands / messages**: `Commands.fs`'s public functions;
  wire-level command routing in `f-sharp/src/Ledger.Engine/Dispatch.fs`'s
  `handleEvent`.
- **Invariants and guards**: `Commands.fs`'s private `validateFields`,
  `checkReference`, `checkTags`, `findOverlap`; `Model.fs`'s `Timer` module
  (30-second discard threshold) and `Capability` module.
- **Capabilities / authority**: `Model.fs`'s `Capability.forActivity`.
- **Important effects and effect contracts**: `f-sharp/src/Ledger.Engine/Protocol.fs`'s
  `EffectRequest`/`EffectResult` (the only effect today is `StorageEffect` —
  `HttpEffect` is defined but not yet emitted by anything); `f-sharp/src/Ledger.Domain/Services.fs`'s
  `LedgerStore` (a named, unimplemented port for a future real backend —
  not on the live path; see Known gaps).
- **Presentation state**: `f-sharp/src/Ledger.Engine/Session.fs`'s `Draft` (the
  only presentation-shaped state, and it still lives in Tier 3, not the
  browser — the browser (Tier 4) holds no state of its own beyond what
  `web/dom-bindings.js` needs to track already-rendered DOM rows for diffing).

## Interfaces

- **Inbound**: `f-sharp/src/Ledger.Wasm/Program.cs`'s single `[JSExport]`
  `LedgerWasm.Dispatch(messageJson: string): string`, calling
  `Ledger.Engine.Dispatch.handle` — the sole Tier 3/4 crossing point.
  `Dispatch.handle`'s own `SemanticEvent.Name` routing table is documented in
  `Dispatch.fs`'s `handleEvent`/`applyDraftField`.
- **Outbound**: `Protocol.fs`'s `EngineToBrowserMessage` (`View` + `Effects` +
  `Cancellations`), rendered/dispatched by `web/dom-bindings.js` and fulfilled
  by `web/wasm-engine-transport.js` (WASM runtime loading) and
  `dom-bindings.js` (the `Storage` effect, via `window.localStorage`).

## Tests and verification

- **Local behavior tests**: `dotnet run --project f-sharp/tests/Ledger.Domain.Specs`
  (Tier 1/2, calling `Commands`/`Summary`/`Billing`/`Timer` directly).
- **Boundary/contract tests**: `dotnet run --project f-sharp/tests/Ledger.Engine.Specs`
  (calls only `Dispatch.handle` with raw JSON strings — the closest thing
  this repository has to a Tier 3/4 boundary/contract test; it is a behavior
  test, not a standalone wire-agreement check).
- **Integration/live verification**: `npm run build:wasm && npm run serve`,
  then a real-browser check (this repository's own verification used
  Playwright against `http://localhost:4321/web/index.html` — create,
  reload-persistence, overlap rejection, split, merge, evidence
  attach/detach, attest + amended-after-review, all three report formats).

## Dependencies

- **Allowed direct dependencies**: `Ledger.Wasm` → `Ledger.Engine` →
  `Ledger.Domain` only (enforced by `ProjectReference` direction);
  `web/*.js` → the WASM export only, never `Ledger.Domain`/`Ledger.Engine`
  symbols directly (there are none to import — JS never sees F# types, only
  the JSON wire contract).
- **Required composition context**: `web/main.js` composes
  `WasmEngineTransport` + `DomBindings`; `Ledger.Engine/Session.fs`'s
  `fixtureEnvironment` composes the seed reference data (projects/activity
  types/tags) every command function needs as its `Environment`.

## Modification boundaries

- **Normal**: any single-tier change that doesn't alter `Protocol.fs`'s wire
  shape or `Commands.fs`'s public command records.
- **Escalation required**: a change to `Protocol.fs`'s `BrowserToEngineMessage`/
  `EngineToBrowserMessage`/`EffectRequest`/`EffectResult`, or to
  `Commands.fs`'s command input/result shapes — these cross the Tier 3/4
  boundary or redefine the Tier 2 semantic contract, and both
  `Dispatch.fs`'s routing and `web/index.html`'s `data-event`/`data-key-from`
  wiring must be updated together in the same change (a data-* attribute
  naming a `SemanticEvent.Name` that `Dispatch.fs` no longer recognizes is a
  live Uncoordinated Duplication per
  [`.sde/architecture/BOUNDARY-PRESERVATION.md`](.sde/architecture/BOUNDARY-PRESERVATION.md)).

## Local agent instructions

- None — this manifest and `.sde/README.md` are the routing entry points; no
  nested per-directory agent instruction file exists yet.

## Maintenance

- Owner: repository owner (single-owner application; see
  `docs/DOMAIN-REQUIREMENTS.md`'s Scope and terminology section).
- Last checked against implementation: 2026-09-12.
- Known gaps:
  - No automated architecture check (an equivalent of
    `check-semantic-architecture.sh`) enforces the Tier 1→4 dependency
    direction beyond `ProjectReference` structure; a Tier 1/2 file could
    still `open System.Text.Json` or similar without any check failing today.
  - No automated boundary/contract agreement check exists independent of
    `Ledger.Engine.Specs`'s behavior tests (e.g., nothing mechanically
    checks that every `data-event` name in `web/index.html` has a matching
    case in `Dispatch.fs`'s `handleEvent`).
  - `Services.fs`'s `LedgerStore` port (the named GitHub-backed-persistence
    extension point) is not wired to anything — `Protocol.fs`'s
    `StorageOutcome` now has a third `StorageUnknown` case for SDE Tier-4/
    persistence-contract conformance, but today's synchronous `localStorage`
    host in `web/dom-bindings.js` cannot actually produce it; it exists for
    the future network-backed host.
  - `EffectRequest.HttpEffect` is defined in `Protocol.fs` but never emitted
    by `Dispatch.fs` and never fulfilled by `dom-bindings.js` — it is where a
    future GitHub-backed save would land, not yet built.
