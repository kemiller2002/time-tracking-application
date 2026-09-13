# CHR-INT-001 — Baseline

Recorded before any Chrona Integration Assembly work began, per the
[Chrona Integration Assembly and Time Observation Processing](./INTEGRATION-CONTRACT.md)
specification's instruction to inspect and document the existing system
before writing code.

## Baseline commit

```
8eac7deabcbf23ba0e19f10c57553f80077ea558
```

Subject: "Reduce timer tick interval to 6 seconds, matching the display's
precision (#24)" — the tip of `main` immediately before this work started.

A local annotated tag `chrona-integration-baseline` points at this commit.
**It could not be pushed to `origin`** from this session — `git push origin
chrona-integration-baseline` failed with an HTTP 403 from this sandbox's
network policy, which appears to restrict pushing tag refs specifically
(branch pushes throughout this same session succeeded normally). Anyone
with unrestricted push access can create the durable ref with:

```
git tag -a chrona-integration-baseline -m "Baseline before Chrona.Integration work" 8eac7deabcbf23ba0e19f10c57553f80077ea558
git push origin chrona-integration-baseline
```

Until then, the commit SHA above is the authoritative baseline reference.

## Working tree state

Clean — `git status --short` produced no output immediately before this
work began.

## Test results at baseline

```
$ dotnet run --project f-sharp/tests/Ledger.Domain.Specs
All 60 F# domain specifications passed.

$ dotnet run --project f-sharp/tests/Ledger.Engine.Specs
All 56 F# engine specifications passed.

$ node tools/check-architecture.mjs
[PASS] Domain purity (Ledger.Domain has no JSON/WASM/browser/HTTP references)
[PASS] Boundary agreement (every data-event in web/index.html has a Dispatch.fs case)
```

No pre-existing failures to record — the baseline is fully green.

## Existing solution structure

The product is deployed and referred to as **Chrona** (custom domain
`chrona.echelonfoundry.com`), but its internal F# assemblies predate that
name and are called `Ledger.*`, not `Chrona.*`:

```
f-sharp/Ledger.slnx
f-sharp/src/Ledger.Domain/Ledger.Domain.fsproj       (Tier 1/2 — pure domain + commands)
f-sharp/src/Ledger.Engine/Ledger.Engine.fsproj       (Tier 3 — dispatch, projections, GitHub sync)
f-sharp/src/Ledger.Wasm/Ledger.Wasm.csproj           (Tier 3/4 boundary — the JSExport shim)
f-sharp/tests/Ledger.Domain.Specs/                   (hand-rolled spec runner, no test framework)
f-sharp/tests/Ledger.Engine.Specs/                   (hand-rolled spec runner, no test framework)
web/                                                 (Tier 4 — dom-bindings.js, wasm-engine-transport.js, index.html)
```

Per the specification's own instruction ("Where an existing Chrona
concept already satisfies a requirement, reuse it. Do not duplicate it
under a new name."), **this work does not rename `Ledger.*` to
`Chrona.*`.** That would be a pure-appearance rename touching every file
in the solution for no behavioral benefit, and the specification itself
says "Preserve the existing solution structure where possible" and "Do
not create an assembly merely for architectural appearance." The one
new, non-negotiable assembly name from the specification —
`EchelonFoundry.Chrona.Integration` — is added alongside the existing
`Ledger.*` projects without touching them.

See [`DATASTORE-CURRENT.md`](./DATASTORE-CURRENT.md) and
[`STARTUP-RECONCILIATION.md`](./STARTUP-RECONCILIATION.md) for the
existing GitHub datastore layout and WASM startup flow, and
[`INTEGRATION-CONTRACT.md`](./INTEGRATION-CONTRACT.md) for how the
specification's vocabulary maps onto what already exists (`TimeEntry` →
existing `Activity`, `organizationId` → no existing analog, etc.).
