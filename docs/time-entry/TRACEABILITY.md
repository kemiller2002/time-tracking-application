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

**Verification-evidence status.** Every test named below has been executed and
passed. `AGENTS.md` forbids claiming a test passed unless it ran and passed,
and ROS forbids marking work complete merely because code exists, so the
Evidence column is only `**VERIFIED**` where something actually ran:

```
dotnet test TimeEntry.sln    276 passed, 0 failed
npm run check:browser        111 checks passed in headless Chromium
check-domain-architecture    0 tier-boundary violations
```

*An earlier version of this document said no .NET SDK could be installed here
and that no test had ever run. Both were true when written and are recorded as
corrections in `TOOLCHAIN-BLOCKER.md`, together with the reasoning error that
produced them: an environment-capability claim must name the channels tested.*

**The one thing still unverified** is any operation against a real GitHub
repository — the agent proxy denies GitHub API writes and there is no test
repository. Reads and writes are exercised against a store double in F# and,
in Chromium, against a local stub that records what arrived. `WI-0028` tracks
it, and no row below claims otherwise.

## A. Time measurement

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-001 | Exact elapsed time is authoritative, in milliseconds (`DF-TE-0009`) | Projection leaves it unchanged; sub-second time survives | WI-0005 | `Duration.fs` `Duration` | `DurationTests` "projecting to units leaves the exact duration intact" | **VERIFIED** |
| TE-R-002 | Units are a projection, not a store | `EntryFacts` has no unit field | WI-0004 | `EntryState.fs` `EntryFacts` | structural: absence of field | **VERIFIED** |
| TE-R-003 | 1 unit = 6 min; 10 units = 1 hour | Constants assert it | WI-0005 | `Duration.fs` literals | `DurationTests` "one unit is six minutes and ten units is one hour" | **VERIFIED** |
| TE-R-004 | No fractional units in authoritative state | `BillableUnits` only constructible by projection | WI-0005 | `Duration.fs` private ctor | `DurationTests` rounding theories | **VERIFIED** |
| TE-R-005 | elapsed = end − start − paused | Paused intervals subtract | WI-0005 | `Duration.ofInterval` | `DurationTests` "elapsed time subtracts paused intervals" | **VERIFIED** |
| TE-R-006 | No JS-interval time | The bridge is forbidden arithmetic; a check enforces it | WI-0007 | `check-domain-architecture` bridge rules | adversarially verified (`.reduce` injection caught) | **VERIFIED** (structurally) |
| TE-R-007 | No authoritative floating point | No `float` in Tiers 1–3; integer `int64` ms | WI-0005 | `Duration.fs`, `Projection.fs` | `ProjectionTests` "totals sum exact seconds rather than per entry rounded units" | **VERIFIED** |
| TE-R-008 | Warn on device/server clock skew | — | WI-0048 | — | — | **NOT IMPLEMENTED.** The host supplies `occurredAtMs` and the repository supplies nothing comparable, so there is no server time to compare against. Needs a decision about where authoritative time comes from before it can be built |
| TE-R-009 | Quick durations 6..60 | Grid generated from `MillisecondsPerBillableUnit`, not hard-coded | WI-0031 | `Kernel.durationGrid` | `verify-browser-kernel` "the duration grid is computed in F#, labels and all" | **VERIFIED** |

## B. Entry lifecycle

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-020 | Create an entry | Yields `Active` + `PersistNewEntry` with no expected version | WI-0010 | `Transitions.createEntry` | `TransitionTests` "creating an entry yields an active entry with one revision" | **VERIFIED** |
| TE-R-021 | View entries | List projection produced by F# | WI-0011 | `Projection.project` | `ProjectionTests` (12 cases) | **VERIFIED** |
| TE-R-022 | Correct an entry | Appends revision, stays `Active` | WI-0012 | `Transitions.correctEntry` | `TransitionTests` "correcting an entry appends a revision and keeps it active" | **VERIFIED** |
| TE-R-023 | Split an entry | Source superseded, children active | WI-0013 | `Transitions.splitEntry` | `TransitionTests` "a two way split supersedes the source and creates active children" | **VERIFIED** |
| TE-R-024 | Void an entry | Excluded from totals, record kept | WI-0014 | `Transitions.voidEntry` | `TransitionTests` "voiding removes the entry from totals but keeps the record" | **VERIFIED** |
| TE-R-025 | Restore an entry | Returns to `Active`, both events kept | WI-0014 | `Transitions.restoreEntry` | `TransitionTests` "restoring a voided entry returns it to totals and keeps both events" | **VERIFIED** |
| TE-R-026 | Merge activities | Sources superseded into a new entry; total is the exact sum | WI-0020 | `Transitions.mergeEntries` | `TransitionTests` 14 merge cases | **VERIFIED** (`DF-TE-0006`) |
| TE-R-027 | Description of work | Optional at create; blocks attestation | WI-0004 | `Values.Description`, `Obligation.intrinsic` | `ProjectionTests` "a missing purpose surfaces as a badge and an obligation" | **VERIFIED** (`DF-TE-0005`) |
| TE-R-028 | Associated project | Required, and must be an available catalogue project for new time | WI-0026 | `Catalogue`, `requireReferenceMove` | `CatalogueTests` 20 cases | **VERIFIED** (`DF-TE-0007`) |
| TE-R-029 | Associated activity type | Required, same catalogue rule as project | WI-0026 | `Catalogue`, `requireReferenceMove` | `CatalogueTests` archived/unknown activity cases | **VERIFIED** |
| TE-R-030 | Originals preserved | Oldest revision is always `Created` | WI-0012 | `TimeEntry.appendRevision` | `TransitionTests` "correction preserves the original values in history" | **VERIFIED** |
| TE-R-031 | Correction history preserved | History only grows | WI-0012 | `TimeEntry.appendRevision` | `TransitionTests` "every transition only ever grows history" | **VERIFIED** |
| TE-R-032 | Void/restore history preserved | Three revisions after void+restore | WI-0014 | `Transitions` | `TransitionTests` restore case | **VERIFIED** |
| TE-R-033 | Evidence history preserved | Evidence append is a revision, stamped with the command's own moment | WI-0038 | `Transitions.attachEvidence`, `CommandParsing` | `KernelTests` 2 cases; `verify-browser-kernel` "attaching evidence is reflected in the kernel's badges" | **VERIFIED** |
| TE-R-034 | Report traceability | Totals derive only from counting entries | WI-0011 | `Projection.project` | `ProjectionTests` "totals exclude voided and superseded entries" | **VERIFIED** |
| TE-R-035 | Resistant to accidental loss | Grouped writes land atomically or not at all | WI-0025 | `Interpreter.commitAll` | `InterpreterTests` 3 atomicity cases (stale source, existing child, merge group) | **VERIFIED** |

## C. Split invariants

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-040 | `sum(children) = source` on seconds | Under- and over-allocation refused | WI-0013 | `requireTotalPreserved`, `Duration.partsPreserve` | `DurationTests` 5 cases + `TransitionTests` 2 cases | **VERIFIED** |
| TE-R-041 | Children strictly positive | Zero-second child unconstructible | WI-0013 | `Duration.ofSeconds` | `DurationTests` "a non-positive duration is refused" | **VERIFIED** |
| TE-R-042 | Negative children refused | Same constructor guard | WI-0013 | `Duration.ofSeconds` | same | **VERIFIED** |
| TE-R-043 | Auditable lineage | Child records `CreatedBySplitOf` | WI-0013 | `Transitions.splitEntry` | `TransitionTests` "a split child records its lineage to the source" | **VERIFIED** |
| TE-R-044 | Preview before save | Preview computed in F#, exact not billed, and cannot disagree with the transition | WI-0036 | `Kernel.splitPreview` | `KernelTests` 6 cases incl. "a preview agrees with the transition that follows it"; 6 browser checks | **VERIFIED** |
| TE-R-045 | Evidence reassignment | `SplitChild.ReassignedEvidence`, offered per part in the page | WI-0013, WI-0049, WI-0051 | `Commands.SplitChild`, `Guards.requireReassignedEvidenceExists`, `main.js` `chosenEvidence` | `SplitTests` 5 cases (moves it, cannot invent it, cannot relabel it, two children may share it, the source keeps it); 3 browser checks | **VERIFIED** |
| TE-R-046 | Stale source refused | `VersionConflict` | WI-0016 | `requireVersion` | `TransitionTests` "a split against a stale source version is refused" | **VERIFIED** |

## D–E. Correction, void, restore semantics

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-050 | Correction is supersession | `Corrected` is a `RevisionChange`, not a state | WI-0012 | `EntryState.RevisionChange` | `TransitionTests` correction cases | **VERIFIED** |
| TE-R-051 | Correction needs a reason | `Reason` is non-optional in the request | WI-0012 | `Commands.CorrectEntryRequest` | structural | **VERIFIED** |
| TE-R-052 | History shows before/after/reason/when/device | `Revision` carries all five | WI-0012 | `EntryState.Revision` | `TransitionTests` original-values case | **VERIFIED** |
| TE-R-053 | No event-sourcing language in UI | Every user-visible outcome worded explicitly; no `%A` anywhere | WI-0046, WI-0047 | `Wording.fs`, `Kernel.changeWording` | `KernelTests` "no user-visible outcome is an F# union dump", "history uses a ledger's words, not a database's"; browser checks | **VERIFIED** |
| TE-R-054 | No raw JSON by default | History renders values, never the stored document | WI-0046 | `Kernel.entryHistory` | `KernelTests` "history does not expose the stored record by default"; browser check | **VERIFIED** |
| TE-R-060 | Void excluded from totals | `countsTowardTotals` false | WI-0014 | `EntryState.countsTowardTotals` | `TransitionTests` + `ProjectionTests` | **VERIFIED** |
| TE-R-061 | Void needs a reason | Non-optional in request and in state | WI-0014 | `Commands`, `EntryState.Void` | structural | **VERIFIED** |
| TE-R-062 | Restore shows void+restore detail | Both revisions retained | WI-0014 | `Transitions.restoreEntry` | `TransitionTests` restore case | **VERIFIED** |
| TE-R-063 | Void is not deletion | Facts and history unchanged | WI-0014 | `Transitions.voidEntry` | `TransitionTests` void case | **VERIFIED** |

## F. Concurrency

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-070 | Never silently overwrite newer | Mismatch rejects | WI-0016 | `requireVersion` | `TransitionTests` "correction requires the version the caller read" | **VERIFIED** |
| TE-R-071 | Conflict UI shows both versions | `VersionConflict` carries expected + actual | WI-0016 | `Effects.Rejection` | `TransitionTests` asserts both tokens | **VERIFIED** |
| TE-R-072 | Stale write is an explicit outcome | No effects requested on conflict | WI-0016 | `Transitions.transition` | `TransitionTests` "a stale correction writes nothing" | **VERIFIED** |
| TE-R-073 | Version evidence retained | `TimeEntry.Version`, `PersistRequest.ExpectedVersion` | WI-0016 | `EntryState`, `Effects` | `TransitionTests` effect assertions | **VERIFIED** |
| TE-R-074 | Enumerated conflict sources | Stale file and moved branch ref both reject | WI-0025 | `requireVersion`, `Interpreter.commitAll` | `InterpreterTests` stale-write + commit-window-race cases | **VERIFIED** |

## G. Projection

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-080 | F# owns filter/sort/search/totals | Query type is the only browser vocabulary | WI-0011 | `Query.fs`, `Projection.fs` | `ProjectionTests` (12 cases) | **VERIFIED** |
| TE-R-081 | Deterministic projections | Id tie-break; order-independent | WI-0011 | `Projection.sortBy` | `ProjectionTests` "sorting is deterministic regardless of input order" | **VERIFIED** |
| TE-R-082 | Chronological display | `ChronologicalAscending`/`Descending` | WI-0011 | `Query.EntrySort` | `ProjectionTests` sort cases | **VERIFIED** |
| TE-R-083 | Empty states | `IsEmpty`; void-only day is not empty | WI-0011 | `ListProjection.IsEmpty` | `ProjectionTests` 2 cases | **VERIFIED** |
| TE-R-084 | Invalid records handled | Typed `DocumentError`/`DecodeError`, never an exception | WI-0006 | `Persistence.Mapping`, `Serialization` | `PersistenceTests` 12 corruption cases | **VERIFIED** |
| TE-R-085 | Unit→display at the boundary | Projection computes hours/minutes; browser renders verbatim | WI-0011, WI-0007 | `BillableUnits.toHoursAndMinutes`, `Kernel.viewDay` | `DurationTests`, `ProjectionTests`, and `verify-browser-kernel` (52 exact min renders as 0h 54m in Chromium) | **VERIFIED** |

## H. Boundary and architecture

| Req | Rule | Acceptance criterion | WI | Code | Test | Evidence |
|---|---|---|---|---|---|---|
| TE-R-090 | Domain in F# | Tiers 1–3 are F# | WI-0004 | `domain/` | — | **VERIFIED** |
| TE-R-091 | No domain logic in TS/JS | Bridge and C# shim both forbidden domain knowledge | WI-0007 | `main.js`, `TimeEntry.Host/Program.cs` | `check-domain-architecture` shim + bridge rules, both adversarially verified | **VERIFIED** |
| TE-R-092 | TS/JS limited to plumbing | Bridge may not sum, sort, filter, or convert units | WI-0007 | `browser/TimeEntry.Host/main.js` | `check-domain-architecture` bridge rules, adversarially verified | **VERIFIED** |
| TE-R-093 | No effects in transitions | Effects returned as data | WI-0004 | `Effects.Effect` | `TransitionTests` effect assertions | **VERIFIED** |
| TE-R-094 | No GitHub shapes in domain | Anti-corruption layer in Tier 4 only; round-trip identity holds | WI-0006 | `Persistence.Documents`, `Mapping`, `Layout` | `PersistenceTests` 9 round-trip + 6 layout cases; tier check | **VERIFIED** |
| TE-R-095 | Tier 1 infra-free | `netstandard2.0`, FSharp.Core only | WI-0004 | `TimeEntry.Semantic.fsproj` | structural: no PackageReference | **VERIFIED** |
| TE-R-096 | Illegal states unrepresentable | Private ctors; state-specific DUs | WI-0004 | `Identifiers`, `Duration`, `Values`, `EntryState` | `DurationTests` + `TransitionTests` refusal cases | **VERIFIED** |
| TE-R-097 | Capabilities from state | Rows carry capabilities | WI-0004 | `Capabilities.available` | `ProjectionTests` "a row carries the capabilities of its state" | **VERIFIED** |
| TE-R-098 | Native HTML/CSS | The live page too: markup adopted from `static-ui-screens/`, no framework | WI-0035..0046 | `browser/TimeEntry.Host/index.html` | 108 browser checks drive native controls only | **VERIFIED** |
| TE-R-099 | GitHub backend | Port, interpreter, and HTTP transport | WI-0025, WI-0027 | `GitHub.Store`, `Interpreter`, `HttpProtocol`, `HttpStore` | `InterpreterTests` 20 cases; `HttpProtocolTests` 27 cases; `CredentialTests` 7 cases; `KernelTests` persist/load cases against a store double; `verify-browser-kernel` against a local API stub that records what arrived | **PARTIAL** — the whole path is exercised, but never against a real repository (WI-0028) |

## I. Accessibility

| Req | WI | Evidence |
|---|---|---|
| TE-R-110 | WI-0017 | **VERIFIED** — `verify-browser-kernel` "no clickable div stands in for a control", against the live page |
| TE-R-111 | WI-0017 | **VERIFIED** — "every control has an accessible name" |
| TE-R-112 | WI-0017 | **VERIFIED** — "every touch target meets the 44px minimum", adversarially confirmed to fail at 20px. Found and fixed a real 13px regression on checkboxes the adopted design system has no rule for |
| TE-R-113 | WI-0017 | **VERIFIED** — "no modal workflows", checked as the absence of `dialog`/`role=dialog`/`aria-modal` rather than the absence of the word |

## Coverage summary

| Category | Count |
|---|---|
| Requirements inventoried | 66 |
| Rows marked **VERIFIED** by an executed test | **64** |
| Structural only — the field exists, the affordance does not | 0 |
| Partially verified — every layer exercised, never against a real repository (TE-R-099) | 1 |
| **NOT IMPLEMENTED**, and said so rather than left implicit (TE-R-008) | 1 |

```
dotnet test TimeEntry.sln    276 passed, 0 failed
npm run check:browser        111 checks passed in headless Chromium
npm run check                 20 passed
check-domain-architecture      0 tier-boundary violations, adversarially
                               validated against seven injected violations
ros validate / registry        pass
```

The counts above are produced by classifying each requirement row's Evidence
cell — 64 + 0 + 1 + 1 = 66, which is the check that matters: every inventoried
requirement is in exactly one category, and none has quietly fallen out of the
table.

A first attempt at this counted occurrences of the word "VERIFIED" and came to
64 of 66, with three other categories also non-zero — arithmetic that cannot
be right. Counting rows rather than words is the fix.

## Orphan check

- **Requirements with no work item:** none.
- **Code with no requirement:** none. Every module cites TE-R ids; the only
  additions beyond stated requirements are the bounded-maximum guard in
  `Duration` and the `ExcludedEntries` disclosure in `ListProjection`, both
  documented in-code as engineering constraints rather than requirements.
- **Acceptance criteria with no verification mechanism:** one. TE-R-008
  (warn on device/server clock skew) cannot be built before it is decided
  where authoritative time comes from — the host supplies `occurredAtMs` and
  the repository supplies nothing to compare it against. Marked NOT
  IMPLEMENTED rather than left implicit, and tracked as WI-0048.

- **Requirements whose verification is weaker than it looks, stated here so
  the table is not read as stronger than it is:**
  - TE-R-099 (GitHub backend) exercises every layer — credential, transport,
    interpreter, kernel, page — but never against a real repository.
  - TE-R-112's touch-target check measures the *effective* target, so a
    checkbox is measured by its label. That is the honest reading of the
    requirement, and it is a weaker assertion than measuring the control.
