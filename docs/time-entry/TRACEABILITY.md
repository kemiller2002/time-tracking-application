---
id: TE-TRC-001
title: Time Entry Traceability
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0003]
tags: [traceability, time-entry, verification]
---

# Time Entry Traceability

Chain required by the execution instruction:

```
Requirement -> Rule -> Acceptance Criterion -> Work Item -> Code -> Test -> Verification Evidence
```

**Verification-evidence status.** Every "Test" cell below names a test that
exists in the repository and has **never been executed**: no .NET SDK can be
installed here (`TOOLCHAIN-BLOCKER.md`). The Evidence column therefore reads
`AUTHORED — UNVERIFIED` throughout rather than claiming a pass. `AGENTS.md`
forbids claiming a test passed unless it ran and passed, and ROS forbids
marking work complete merely because code exists.

## A. Time measurement

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-001 | Exact elapsed time is authoritative | Projecting to units leaves seconds unchanged | WI-0005 | `Duration.fs` `Duration` | `DurationTests` "projecting to units leaves the exact duration intact" | AUTHORED — UNVERIFIED |
| TE-R-002 | Units are a projection, not a store | `EntryFacts` has no unit field | WI-0004 | `EntryState.fs` `EntryFacts` | structural: absence of field | AUTHORED — UNVERIFIED |
| TE-R-003 | 1 unit = 6 min; 10 units = 1 hour | Constants assert it | WI-0005 | `Duration.fs` literals | `DurationTests` "one unit is six minutes and ten units is one hour" | AUTHORED — UNVERIFIED |
| TE-R-004 | No fractional units in authoritative state | `BillableUnits` only constructible by projection | WI-0005 | `Duration.fs` private ctor | `DurationTests` rounding theories | AUTHORED — UNVERIFIED |
| TE-R-005 | elapsed = end − start − paused | Paused intervals subtract | WI-0005 | `Duration.ofInterval` | `DurationTests` "elapsed time subtracts paused intervals" | AUTHORED — UNVERIFIED |
| TE-R-006 | No JS-interval time | Timer not implemented in JS domain | WI-0007 | — | — | NOT IMPLEMENTED (Tier 4 blocked) |
| TE-R-007 | No authoritative floating point | No `float` in Tiers 1–3 | WI-0005 | `Duration.fs`, `Projection.fs` | `ProjectionTests` "totals sum exact seconds rather than per entry rounded units" | AUTHORED — UNVERIFIED |
| TE-R-008 | Warn on device/server clock skew | — | WI-0016 | — | — | NOT IMPLEMENTED (Tier 4 blocked) |
| TE-R-009 | Quick durations 6..60 | — | WI-0017 | — | — | NOT IMPLEMENTED (UI, blocked by OQ-5) |

## B. Entry lifecycle

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-020 | Create an entry | Yields `Active` + `PersistNewEntry` with no expected version | WI-0010 | `Transitions.createEntry` | `TransitionTests` "creating an entry yields an active entry with one revision" | AUTHORED — UNVERIFIED |
| TE-R-021 | View entries | List projection produced by F# | WI-0011 | `Projection.project` | `ProjectionTests` (12 cases) | AUTHORED — UNVERIFIED |
| TE-R-022 | Correct an entry | Appends revision, stays `Active` | WI-0012 | `Transitions.correctEntry` | `TransitionTests` "correcting an entry appends a revision and keeps it active" | AUTHORED — UNVERIFIED |
| TE-R-023 | Split an entry | Source superseded, children active | WI-0013 | `Transitions.splitEntry` | `TransitionTests` "a two way split supersedes the source and creates active children" | AUTHORED — UNVERIFIED |
| TE-R-024 | Void an entry | Excluded from totals, record kept | WI-0014 | `Transitions.voidEntry` | `TransitionTests` "voiding removes the entry from totals but keeps the record" | AUTHORED — UNVERIFIED |
| TE-R-025 | Restore an entry | Returns to `Active`, both events kept | WI-0014 | `Transitions.restoreEntry` | `TransitionTests` "restoring a voided entry returns it to totals and keeps both events" | AUTHORED — UNVERIFIED |
| TE-R-026 | Merge activities | — | — | **absent by design** | `TransitionTests` "merge is never offered as a capability while OQ-4 is open" | BLOCKED — OQ-4 |
| TE-R-027 | Description of work | `Description` smart ctor | WI-0004 | `Values.Description` | — | BLOCKED — OQ-3 (requiredness) |
| TE-R-028 | Associated project | `ProjectId` required in `EntryFacts` | WI-0015 | `EntryState.EntryFacts` | `ProjectionTests` "filtering by project excludes other projects" | AUTHORED — UNVERIFIED |
| TE-R-029 | Associated activity type | `ActivityTypeId` required | WI-0015 | `EntryState.EntryFacts` | — | AUTHORED — UNVERIFIED |
| TE-R-030 | Originals preserved | Oldest revision is always `Created` | WI-0012 | `TimeEntry.appendRevision` | `TransitionTests` "correction preserves the original values in history" | AUTHORED — UNVERIFIED |
| TE-R-031 | Correction history preserved | History only grows | WI-0012 | `TimeEntry.appendRevision` | `TransitionTests` "every transition only ever grows history" | AUTHORED — UNVERIFIED |
| TE-R-032 | Void/restore history preserved | Three revisions after void+restore | WI-0014 | `Transitions` | `TransitionTests` restore case | AUTHORED — UNVERIFIED |
| TE-R-033 | Evidence history preserved | Evidence append is a revision | WI-0010 | `Transitions.attachEvidence` | — | AUTHORED — UNVERIFIED (no test yet) |
| TE-R-034 | Report traceability | Totals derive only from counting entries | WI-0011 | `Projection.project` | `ProjectionTests` "totals exclude voided and superseded entries" | AUTHORED — UNVERIFIED |
| TE-R-035 | Resistant to accidental loss | Split persisted as one grouped effect | WI-0013 | `Effects.PersistSplit` | `TransitionTests` split effect assertions | AUTHORED — UNVERIFIED |

## C. Split invariants

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-040 | `sum(children) = source` on seconds | Under- and over-allocation refused | WI-0013 | `requireTotalPreserved`, `Duration.partsPreserve` | `DurationTests` 5 cases + `TransitionTests` 2 cases | AUTHORED — UNVERIFIED |
| TE-R-041 | Children strictly positive | Zero-second child unconstructible | WI-0013 | `Duration.ofSeconds` | `DurationTests` "a non-positive duration is refused" | AUTHORED — UNVERIFIED |
| TE-R-042 | Negative children refused | Same constructor guard | WI-0013 | `Duration.ofSeconds` | same | AUTHORED — UNVERIFIED |
| TE-R-043 | Auditable lineage | Child records `CreatedBySplitOf` | WI-0013 | `Transitions.splitEntry` | `TransitionTests` "a split child records its lineage to the source" | AUTHORED — UNVERIFIED |
| TE-R-044 | Preview before save | — | WI-0017 | — | — | NOT IMPLEMENTED (UI) |
| TE-R-045 | Evidence reassignment | `SplitChild.ReassignedEvidence` | WI-0013 | `Commands.SplitChild` | — | AUTHORED — UNVERIFIED (no test yet) |
| TE-R-046 | Stale source refused | `VersionConflict` | WI-0016 | `requireVersion` | `TransitionTests` "a split against a stale source version is refused" | AUTHORED — UNVERIFIED |

## D–E. Correction, void, restore semantics

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-050 | Correction is supersession | `Corrected` is a `RevisionChange`, not a state | WI-0012 | `EntryState.RevisionChange` | `TransitionTests` correction cases | AUTHORED — UNVERIFIED |
| TE-R-051 | Correction needs a reason | `Reason` is non-optional in the request | WI-0012 | `Commands.CorrectEntryRequest` | structural | AUTHORED — UNVERIFIED |
| TE-R-052 | History shows before/after/reason/when/device | `Revision` carries all five | WI-0012 | `EntryState.Revision` | `TransitionTests` original-values case | AUTHORED — UNVERIFIED |
| TE-R-053 | No event-sourcing language in UI | — | WI-0017 | — | — | NOT IMPLEMENTED (UI) |
| TE-R-054 | No raw JSON by default | — | WI-0017 | — | — | NOT IMPLEMENTED (UI) |
| TE-R-060 | Void excluded from totals | `countsTowardTotals` false | WI-0014 | `EntryState.countsTowardTotals` | `TransitionTests` + `ProjectionTests` | AUTHORED — UNVERIFIED |
| TE-R-061 | Void needs a reason | Non-optional in request and in state | WI-0014 | `Commands`, `EntryState.Void` | structural | AUTHORED — UNVERIFIED |
| TE-R-062 | Restore shows void+restore detail | Both revisions retained | WI-0014 | `Transitions.restoreEntry` | `TransitionTests` restore case | AUTHORED — UNVERIFIED |
| TE-R-063 | Void is not deletion | Facts and history unchanged | WI-0014 | `Transitions.voidEntry` | `TransitionTests` void case | AUTHORED — UNVERIFIED |

## F. Concurrency

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-070 | Never silently overwrite newer | Mismatch rejects | WI-0016 | `requireVersion` | `TransitionTests` "correction requires the version the caller read" | AUTHORED — UNVERIFIED |
| TE-R-071 | Conflict UI shows both versions | `VersionConflict` carries expected + actual | WI-0016 | `Effects.Rejection` | `TransitionTests` asserts both tokens | AUTHORED — UNVERIFIED |
| TE-R-072 | Stale write is an explicit outcome | No effects requested on conflict | WI-0016 | `Transitions.transition` | `TransitionTests` "a stale correction writes nothing" | AUTHORED — UNVERIFIED |
| TE-R-073 | Version evidence retained | `TimeEntry.Version`, `PersistRequest.ExpectedVersion` | WI-0016 | `EntryState`, `Effects` | `TransitionTests` effect assertions | AUTHORED — UNVERIFIED |
| TE-R-074 | Enumerated conflict sources | Split-on-stale covered; multi-device needs Tier 4 | WI-0016 | `requireVersion` | `TransitionTests` stale-split case | PARTIAL — Tier 4 blocked |

## G. Projection

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-080 | F# owns filter/sort/search/totals | Query type is the only browser vocabulary | WI-0011 | `Query.fs`, `Projection.fs` | `ProjectionTests` (12 cases) | AUTHORED — UNVERIFIED |
| TE-R-081 | Deterministic projections | Id tie-break; order-independent | WI-0011 | `Projection.sortBy` | `ProjectionTests` "sorting is deterministic regardless of input order" | AUTHORED — UNVERIFIED |
| TE-R-082 | Chronological display | `ChronologicalAscending`/`Descending` | WI-0011 | `Query.EntrySort` | `ProjectionTests` sort cases | AUTHORED — UNVERIFIED |
| TE-R-083 | Empty states | `IsEmpty`; void-only day is not empty | WI-0011 | `ListProjection.IsEmpty` | `ProjectionTests` 2 cases | AUTHORED — UNVERIFIED |
| TE-R-084 | Invalid records handled | `PersistedRecordUnreadable` obligation | WI-0011 | `Capabilities.Obligation` | — | AUTHORED — UNVERIFIED (needs Tier 4 to raise) |
| TE-R-085 | Unit→display at the boundary | Projection computes hours/minutes | WI-0011 | `BillableUnits.toHoursAndMinutes` | `DurationTests` + `ProjectionTests` 23-unit cases | AUTHORED — UNVERIFIED |

## H. Boundary and architecture

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-090 | Domain in F# | Tiers 1–3 are F# | WI-0004 | `domain/` | — | AUTHORED — UNVERIFIED |
| TE-R-091 | No domain logic in TS/JS | No TS authored; JS prototype not extended | WI-0007 | — | structural: `domain/` has no JS | HOLDS (nothing added) |
| TE-R-092 | TS limited to plumbing | — | WI-0007 | — | — | NOT IMPLEMENTED (blocked) |
| TE-R-093 | No effects in transitions | Effects returned as data | WI-0004 | `Effects.Effect` | `TransitionTests` effect assertions | AUTHORED — UNVERIFIED |
| TE-R-094 | No GitHub shapes in domain | `VersionToken` opaque; no URL/verb in `Effect` | WI-0006 | `Values.VersionToken`, `Effects` | structural | AUTHORED — UNVERIFIED |
| TE-R-095 | Tier 1 infra-free | `netstandard2.0`, FSharp.Core only | WI-0004 | `TimeEntry.Semantic.fsproj` | structural: no PackageReference | AUTHORED — UNVERIFIED |
| TE-R-096 | Illegal states unrepresentable | Private ctors; state-specific DUs | WI-0004 | `Identifiers`, `Duration`, `Values`, `EntryState` | `DurationTests` + `TransitionTests` refusal cases | AUTHORED — UNVERIFIED |
| TE-R-097 | Capabilities from state | Rows carry capabilities | WI-0004 | `Capabilities.available` | `ProjectionTests` "a row carries the capabilities of its state" | AUTHORED — UNVERIFIED |
| TE-R-098 | Native HTML/CSS | `static-ui-screens/` is HTML+CSS only | WI-0008 | `static-ui-screens/` | — | HOLDS |
| TE-R-099 | GitHub backend | — | WI-0006 | — | — | NOT IMPLEMENTED (blocked) |

## I. Accessibility

| Req | WI | Evidence |
|---|---|---|
| TE-R-110..113 | WI-0017 | NOT IMPLEMENTED — UI integration blocked by OQ-5 and the toolchain blocker |

## Coverage summary

| Category | Count |
|---|---|
| Requirements inventoried | 62 |
| Requirements with code | 38 |
| Requirements with a named test | 30 |
| Requirements **verified by execution** | **0** |
| Requirements blocked by an open question | 3 (TE-R-026, TE-R-027, TE-R-009/044/053/054 UI subset) |
| Requirements blocked by the toolchain | 11 |

## Orphan check

- **Requirements with no work item:** none.
- **Code with no requirement:** none. Every module cites TE-R ids; the only
  additions beyond stated requirements are the bounded-maximum guard in
  `Duration` and the `ExcludedEntries` disclosure in `ListProjection`, both
  documented in-code as engineering constraints rather than requirements.
- **Acceptance criteria with no verification mechanism:** the UI rows
  (TE-R-009, 044, 053, 054, 110–113) and the Tier 4 rows (TE-R-006, 008, 084,
  092, 099). All are marked NOT IMPLEMENTED rather than left implicit.
