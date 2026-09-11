---
id: TE-MANIFEST-001
title: Time Entry Domain Feature Manifest
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
tags: [sde, feature-manifest, time-entry]
---

# Feature manifest — Time Entry domain

Per `.sde/method/FEATURE-MANIFESTS.md`. Navigation metadata; the code and the
decision records are the authorities.

## Purpose

Own the authoritative state of a business activity ledger entry: what an entry
can be, which changes are legal, what evidence each change requires, and what
the browser is allowed to see. Persistence and rendering are explicitly *not*
owned here.

## Boundary

| Tier | Project | Owns | Must not know about |
|---|---|---|---|
| 1 | `TimeEntry.Semantic` | Identity, duration, values, state, history, capabilities, obligations | HTTP, JSON, GitHub, browser, filesystem |
| 2 | `TimeEntry.Transitions` | Commands, guards, transitions, requested effects, typed rejections | Anything that performs an effect |
| 3 | `TimeEntry.Projection` | Queries, list projection, totals, view state | Serialization, transport |
| — | `TimeEntry.Tests` | Verification of the above | Network, browser |

Tier 1 targets `netstandard2.0` and references only `FSharp.Core`, so the
boundary is structural rather than conventional (TE-R-095).

## State and transitions

States: `Active`, `Void`, `Superseded`. Correction is deliberately **not** a
state — it is a `RevisionChange`, because a corrected entry still counts
toward totals with its new values. Rationale in `EntryState.fs` and
`docs/time-entry/DECISIONS.md`.

Transitions: `createEntry`, `correctEntry`, `splitEntry`, `voidEntry`,
`restoreEntry`, `attachEvidence`, `mergeEntries`.

Guards shared by every mutation: identity match, capability check, version
check. Split adds child-count, identity-uniqueness, and total-preservation.

## Invariants

1. History only grows; the oldest revision is always `Created`.
2. `sum(child durations) = source duration`, in whole seconds. The merge
   inverse is structural: the merged duration *is* the sum, never a supplied
   value that could disagree.
3. Only `Active` entries contribute to totals.
4. No mutation proceeds without a matching version token.
5. No transition performs an effect.
6. No floating-point arithmetic is authoritative.

## Contracts

- Inbound: `Command` (Tier 2).
- Outbound state: `TimeEntry list`.
- Outbound effects: `Effect list` — interpreted by Tier 4 (not implemented).
- Outbound view: `ListProjection` — a flat view state for the WASM kernel.

## Dependencies

Declared: `FSharp.Core`; `xunit` + `Microsoft.NET.Test.Sdk` in tests only.
No other package reference in any tier.

## Verification

`domain/TimeEntry.Tests` — 81 cases across duration/rounding, transitions
legal and illegal (including all six state transitions and merge), and
projection, including the adversarial cases from execution rule §23.

**Status: passing.** `dotnet test TimeEntry.sln` -> 81/81 in 89 ms;
`dotnet build` -> 0 warnings, 0 errors with warnings-as-errors;
`node tools/check-domain-architecture.mjs` -> 0 tier-boundary violations.
Every invariant above is demonstrated, not merely designed.

## Related records

`docs/time-entry/REQUIREMENTS.md`, `TRACEABILITY.md`, `DECISIONS.md`,
`UI-PROVENANCE.md`, `TOOLCHAIN-BLOCKER.md`, `SDE-MAP.md`.
