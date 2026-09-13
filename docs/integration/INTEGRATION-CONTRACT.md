# CHR-INT-002 — Chrona's Receiver-Owned Integration Standard

This document defines the standard `EchelonFoundry.Chrona.Integration`
(`f-sharp/src/Chrona.Integration/`) implements, and reconciles the
"Chrona Integration Assembly and Time Observation Processing"
specification's vocabulary and illustrative datastore layout against what
already exists in this repository (see `BASELINE.md` and
`DATASTORE-CURRENT.md`).

## Ownership

Chrona (the receiver) owns every contract describing information an
external system may submit to it. An external system (ROS, a manual
import, a calendar sync, ...) constructs Chrona input using a Chrona-
published integration contract; it never independently reproduces
Chrona's data format by hand-building JSON.

`Chrona.Integration` is a **public, standalone package** —
`EchelonFoundry.Chrona.Integration` — versioned independently of Chrona's
own release cadence (see "Contract version vs. package version" below).
The producer (ROS) references this package; Chrona does not need to
reference anything ROS publishes merely to receive a submission.

## Transport independence

`Chrona.Integration` contains types, validation, and serialization only.
It does not know how a `TimeObservation` reaches Chrona — whether that is
a file written to Chrona's GitHub datastore (the only transport Chrona
uses today), an HTTP call, a message queue, or a local file import. It
therefore:

- has no GitHub API code,
- has no HTTP code,
- has no WASM code,
- has no AWS code,
- has no UI code,
- has no dependency on `Ledger.Domain`, `Ledger.Engine`, or `Ledger.Wasm`.

The dependency direction is `Chrona.Integration ← Ledger.Engine` (Chrona's
own code is free to depend on the public contract), never the reverse.

## Versioning: contract version vs. package version

These are deliberately independent:

- **`TimeObservation.ContractVersion`** (wire field `contractVersion`,
  currently `"1"`) describes the *serialized shape*. It changes only when
  the wire contract itself changes incompatibly.
- **The NuGet package version** (`EchelonFoundry.Chrona.Integration`,
  e.g. `1.4.2`) can advance for bug fixes, new helper functions, or
  additive optional fields without touching `ContractVersion` at all.

A future incompatible change to what a field *means* (not just an
additive optional field) requires a new contract version and a
side-by-side V1/V2 type, adapted to Chrona's internal model at the
boundary — never a silent reinterpretation of V1 payloads under a new
meaning.

## Compatibility

Every supported contract version's serialized shape is protected by
fixture files under `tests/Chrona.Integration.Tests/fixtures/v1/` (see
`COMPATIBILITY.md`). A field added to a supported version's contract
must be optional and must not change any existing fixture's expected
deserialization result. An incompatible meaning change requires a new
contract version, not a mutation of the existing one.

## Serialization

Chrona owns the wire representation; it is not incidental to whatever an
off-the-shelf serializer happens to produce by reflecting over the type.
`TimeObservation.serialize`/`deserialize` build and read the JSON
explicitly, field by field, using `System.Text.Json.Nodes` — the same
hand-rolled idiom `Ledger.Engine/GitHubSync.fs` and `Protocol.fs` already
use elsewhere in this codebase (chosen for consistency and because it
keeps the wire shape and the F# type deliberately decoupled, rather than
whatever an attribute-driven or reflection-based serializer would infer).

## Vocabulary reconciliation against the existing Chrona domain

The specification suggests illustrative names and a datastore layout.
Per its own instruction ("where an existing Chrona concept already
satisfies a requirement, reuse it — do not duplicate it under a new
name"), here is how each maps onto what already exists in
`f-sharp/src/Ledger.Domain/Model.fs` and `f-sharp/src/Ledger.Engine/`:

| Specification term | Existing Chrona concept | Resolution |
|---|---|---|
| `TimeEntry` (authoritative) | `Ledger.Domain.Model.Activity` | Reused as-is. No new "TimeEntry" type is introduced — an accepted candidate becomes an `Activity` via the existing `Commands.create` (see the future CHR-INT-008 work). |
| `ProjectId` | `Environment.Projects` (a `Map<string, ReferenceItem>`) | Reused directly — `TimeObservation.ProjectId` is expected to eventually resolve against this same map, once Chrona-side policy validation exists. `Chrona.Integration` itself does not validate this (see "Structural vs. domain validation" below). |
| `ActorId` | *(none)* | Chrona has no user/actor registry today (it is a single-browser app; GitHub identity exists only for sync, not for "who did the work"). Carried as an optional pass-through field with no Chrona-side validation in V1 (see specification §63). |
| `WorkItemId` | *(none)* | Chrona has no work-item concept today. Carried as an optional pass-through field. |
| `OrganizationId` | *(none — see below)* | Chrona's datastore scoping unit is `<Folder>` (see `DATASTORE-CURRENT.md`), not a multi-organization hierarchy. `OrganizationId` is retained on the wire contract as a required field (a producer like ROS already has the concept, and dropping it would be a lossy, ROS-specific contract), but it does not currently select a Chrona storage path — see "Datastore path adaptation" below. |
| `organizations/<org>/projects/<project>/time/observations/inbox/` | *(no existing analog)* | Adapted — see below. |

## Structural vs. domain validation (where each lives)

Per specification §10, `Chrona.Integration` validates only what makes a
payload *structurally* legal — never Chrona's own policy:

**`Chrona.Integration` validates (this assembly):**
- Required identity fields are non-blank (`ObservationId`, `SourceSystem`,
  `OrganizationId`, `ProjectId`).
- Exactly one legal time representation is present: an interval
  (`StartedAt` + `EndedAt`, both `Some`, `DurationMinutes = None`) or a
  duration (`DurationMinutes = Some`, both instants `None`) — never both,
  never neither, never one instant alone.
- `EndedAt >= StartedAt` when an interval is given.
- `DurationMinutes > 0` when a duration is given.
- The contract version is one this package's `deserialize` recognizes.

**Chrona itself decides (future work, not this assembly):**
- Does `ProjectId` exist, and is it active?
- Is `ActorId`/`WorkItemId` recognized?
- Does this observation's time range overlap an existing entry?
- Can this observation become a time candidate at all (is the project
  closed, etc.)?

## Datastore path adaptation

The specification's illustrative layout is
`organizations/<org>/projects/<project>/time/observations/inbox/...`.
Chrona has no organization hierarchy (see the table above), so the
adapted convention — implemented by this assembly's
`StorageConvention.observationInboxPath` — is:

```
<Folder>/integration/projects/<ProjectId>/observations/inbox/<safe ObservationId>.json
```

`<Folder>` replaces the `organizations/<org>` segment (Chrona's actual
single-tenant scoping unit — see `DATASTORE-CURRENT.md`); the `projects/`
segment is retained from the specification's layout, both because it
groups an ever-growing inbox by something meaningful (§40's forward-
looking partitioning concern) and because `ProjectId` is already a
required field on every observation. `OrganizationId` remains part of the
wire contract (a producer's own identity) without selecting a path
segment — Chrona simply doesn't have more than one "organization" per
`<Folder>` to distinguish.

This convention lives in the **public** `Chrona.Integration` package
because ROS (the producer) needs to know exactly where to write an
observation. The corresponding `observation-receipts/` and `candidates/`
paths are **not** exposed here — those are entirely Chrona-owned,
internal implementation details a producer never needs (see
specification §51), and will be defined inside `Ledger.Engine` in a
future PR (CHR-INT-011 onward), free to depend on whatever internal
`Ledger.Engine`/`Ledger.Domain` conventions make sense.

## Observation filename safety

`StorageConvention` never inserts a producer-supplied ID into a path
without transforming it first: `toSafeSegment` percent-style-encodes
every character outside `[A-Za-z0-9._-]` (as `_XX` where `XX` is the
character's two-digit hex code), so an `ObservationId` containing `/`,
`..`, or any other path-meaningful character can never escape its
intended directory or collide with an unrelated file. Producers should
call `StorageConvention.observationInboxPath` themselves rather than
reimplementing this transform (specification §53).

## What this PR (CHR-INT-001 through -006) does and does not do

**Does:**
- Establish `docs/integration/` (this document and its siblings).
- Add the standalone `EchelonFoundry.Chrona.Integration` package:
  `TimeObservation` V1 (types, validated constructor, serialize/
  deserialize), `StorageConvention.observationInboxPath`, and its test
  suite with compatibility fixtures.

**Does not (deferred to follow-up PRs, tracked as CHR-INT-007 onward):**
- Any change to `Ledger.Domain`, `Ledger.Engine`, `Ledger.Wasm`, or `web/`.
- The candidate/receipt domain model, the reconciliation state machine,
  GitHub observation reading/writing, or WASM startup wiring.
- Publishing the package (CI builds and packs it as a workflow artifact
  only — see the new `.github/workflows/chrona-integration-ci.yml`).
- Any change on the ROS side (a separate repository, out of this
  session's access scope).

This keeps the addition fully inert with respect to existing Chrona
behavior, per the specification's own repeated instruction to preserve
the existing application and extend it safely rather than rewrite it.
