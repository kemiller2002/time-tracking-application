---
id: TE-DEC-001
title: Time Entry Decision Records
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0002, WI-0003]
tags: [decisions, time-entry]
---

# Time Entry Decision Records

Material decisions taken during Time Entry execution. `DF-` canonically means
Decision Record (`AGENTS.md`, Core Rules).

---

## DF-TE-0001

**Title:** GitHub API supersedes Cloudflare as the Time Entry persistence backend

**Status:** accepted

### Context

Repository requirements authority describes a Cloudflare Workers backend
throughout: `prompts/business_activity_ledger_ui_system_agent_prompt.md` §5–6
("Cloudflare Service Architecture", "Cloudflare API Contract Assumptions"),
`prompts/business_activity_ledger_ui_execution_contract.md` §3 and its
per-capability "Cloudflare reconciliation" gates, `docs/UI-CLOUDFLARE-INTEGRATION.md`,
`openapi/ledger-api.yaml`, and a working implementation in `worker/src/`.

The executing user's instruction states: "The backend storage and integration
mechanism is GitHub through the GitHub API."

These conflict directly.

### Decision

GitHub via the GitHub API is the authoritative backend. The Cloudflare design
is superseded for persistence.

### Rationale

`AGENTS.md` "Authority" orders sources descending as: **explicit user
instruction**; safety/legal constraints; canonical governance; accepted
REPs/theory; accepted architecture and decision records; current
implementation; local convention. Explicit user instruction is the highest
authority in the repository's own governance, above both the prompts and the
existing `worker/` implementation.

### Consequences

- `worker/src/*` and `openapi/ledger-api.yaml` become historical context, not
  the persistence target. They are **not deleted** (Core Rule: "Preserve user
  work. Inspect before modifying; do not destroy or irreversibly migrate
  without authorization.").
- The per-capability "Cloudflare reconciliation" gate in the execution
  contract is read as "backend reconciliation" and satisfied against the
  GitHub boundary instead.
- Concurrency moves from the Cloudflare optimistic-version model to GitHub
  blob/commit SHA evidence (TE-R-073).

### Revisit when

The user directs otherwise, or a newer repository decision record supersedes
this one.

---

## DF-TE-0002

**Title:** Duration is authoritative; six-minute billing units are a derived projection

**Status:** accepted

### Context

The user instruction (§2, "Time measurement") describes time as recorded in
six-minute increments, with one unit = six minutes, and says invalid
fractional units must not enter authoritative domain state.

Repository authority states the opposite emphasis. `prompts/business_activity_ledger_ui_execution_contract.md`
§4 requires the system to preserve "exact elapsed time", and states directly:

> Six-minute controls are an input convenience. They must not silently inflate
> or replace exact elapsed time.

`prompts/business_activity_ledger_ui_system_agent_prompt.md` §16 reinforces
this by defining elapsed time from authoritative timestamps
(`elapsed = current time - started_at - paused intervals`), which is not
quantised to six minutes.

### Decision

The domain carries **both**:

1. `Duration` — exact elapsed time, authoritative, stored in whole seconds.
2. `BillableUnits` — a **derived projection** of `Duration` into six-minute
   units, used for billing, totals, and quick-entry input.

`BillableUnits` is never the authoritative store of what happened. `Duration`
is never displayed as billable time without going through `BillableUnits`.

### Rationale

This is a reconciliation, not a trade-off — it satisfies both sources. The
user's constraint "invalid fractional units must not enter authoritative
domain state" is honoured because `BillableUnits` is a total function of
`Duration` and cannot be constructed fractionally. The repository's constraint
"must not silently inflate or replace exact elapsed time" is honoured because
rounding happens at projection, leaving `Duration` intact.

A single-representation model would have violated one source or the other:
storing only units loses exact elapsed time; storing only duration makes
billing totals floating-point (forbidden by user instruction §21).

### Consequences

- Split invariants (TE-R-040) are enforced on **`Duration`**, in whole seconds,
  because that is the authoritative quantity. A split that preserves billable
  units but loses seconds would violate TE-R-001.
- Totals are computed on `Duration` and projected to units at the boundary
  (TE-R-085), so no floating-point arithmetic is authoritative (TE-R-007).
- Rounding policy for `Duration → BillableUnits` is a domain policy and must
  be explicit, not implicit in a formatter.

### Revisit when

A repository requirement defines a rounding policy (round-half-up vs ceiling
vs banker's) — currently unstated. Tracked as an implementation obligation,
not an open question, because the invariant holds under any total rounding
function.

---

## DF-TE-0003

**Title:** `static-ui-screens/` is the authoritative screen design; no historical recovery is required

**Status:** accepted

**Evidence:** [`UI-PROVENANCE.md`](UI-PROVENANCE.md)

### Context

The execution instruction anticipated that intended screens might exist only
in Git history, temporarily removed during implementation, and required Git
archaeology before recreating any screen.

### Decision

No recovery is required. `static-ui-screens/` in the current tree is the
authoritative design and is byte-identical to its introducing commit.

### Rationale

Mechanically verified against the full history of all branches:

- Total history is 5 commits.
- `git log --all --diff-filter=D --summary` reports exactly one deletion in
  the repository's entire history: `tools/ros_cli.mjs`, deleted by the current
  HEAD commit as part of the ROS 2.0.1 upgrade. No HTML or CSS file has ever
  been deleted.
- `git log --all --oneline -- static-ui-screens/` returns a single commit
  (`0270575`). The 18 screens and `styles.css` were added whole and never
  modified.

The premise of the recovery instruction — markup temporarily removed during
implementation — is therefore false for this repository. Acting on it would
have meant fabricating a provenance conflict that the evidence does not
support.

### Consequences

- `static-ui-screens/` is the design authority for screen structure.
- `index.html` + `src/styles.css` (the working PWA shell, modified at
  `e896ea1`) is the *integration* target, distinct from the design authority.
- No screen may be recreated from imagination while a `static-ui-screens/`
  counterpart exists.

---

## DF-TE-0004

**Title:** No separate `Label` entity; the taxonomy is activity type + project + purpose

**Status:** accepted · **Resolves:** OQ-2

### Context

The execution instruction names "labels" as an entry concern and asks whether
they belong to entries or projects, are free-form or controlled, and
participate in filtering. No repository document defines a `label` entity.

### Evidence

Searched all 18 recovered screens for a label or tag control:

- No `name="tag*"`, no `Tags` heading, no tag placeholder anywhere.
- The taxonomy fields actually present are **activity type**, **project**, and
  **purpose/description** — "Purpose" or "Business purpose" appears in
  `today.html`, `stop.html`, `manual-entry.html`, and `correction.html`;
  "Description" in `activity.html`, `split.html`, `evidence.html`,
  `start.html`, and `states.html`.

### Decision

There is no `Label` type. "Labels where supported" is satisfied by
`ActivityTypeId`, which is the controlled taxonomy the screens and
`schemas/domain/activity-type.schema.json` actually define. Filtering by
activity type is supported (`EntryQuery.ActivityType`).

### Rationale

Adding a fourth taxonomy would mean inventing its scope, source, lifecycle,
and ownership with no requirement defining any of them — and no screen to
edit it. `EntryState.EntryFacts` therefore carries no `Labels` field, which is
the honest representation.

### Revisit when

A requirement or screen defines a label distinct from activity type.

---

## DF-TE-0005

**Title:** A description is optional at creation and required for attestation

**Status:** accepted · **Resolves:** OQ-3

### Evidence

The recovered design answers this directly rather than leaving it to
preference:

- `static-ui-screens/today.html` renders a real entry carrying a
  `badge-warn` reading **"Purpose missing"** — so an entry can exist,
  persist, and appear in the ledger without a description.
- The same screen's notice reads "The LinkedIn entry needs a clear business
  purpose **before attestation**", and `review.html` blocks the day's review
  on it.

### Decision

`Description` is `option` on `EntryFacts`. A missing description is not a
validation failure at creation; it raises the `BusinessPurposeMissing`
obligation, which blocks attestation.

Constraints: non-whitespace, trimmed, ≤ 4000 characters — an engineering bound
to prevent unbounded payloads, not a stated requirement.

### Consequence

This is what `Capabilities.Obligation.intrinsic` and the `PurposeMissing`
badge already implement, so no code change follows from resolving OQ-3 — the
implementation was already evidence-led.

---

## DF-TE-0006

**Title:** Merge supersedes its sources into a new entry

**Status:** accepted · **Resolves:** OQ-4

### Context

System-prompt §8.11 specifies merge's UI (select activities, preview merged
time, choose category/project, combine descriptions, choose evidence, require
reason) but not its lineage semantics. Two readings were possible: supersede N
sources into a new entry, or void N sources and create one.

### Decision

Merge creates a new entry and transitions every source to
`Superseded(SupersededByMerge target)`.

### Rationale

`Void` is the wrong state, and provably so rather than merely inelegantly:

1. `Void` means "excluded from totals by user judgment, with a reason", and it
   is **restorable** (TE-R-025). Restoring one source of a completed merge
   would return its time to totals while the merged entry still carries it —
   double-counting. `Superseded` offers no capabilities, so this is
   unrepresentable.
2. `Superseded` already exists and already means exactly "excluded from totals
   because this time now lives in other entries". Merge is the inverse of
   split; using the same state for both keeps one concept instead of two.
3. It makes the total-preservation invariant expressible the same way as
   split's: `merged duration = sum(source durations)`.

### Consequences

- `SupersessionCause.SupersededByMerge` is already defined and now reachable.
- `Command.MergeEntries`, `MergeEntriesRequest`, a `mergeEntries` transition,
  a `PersistMerge` effect, and `CanMerge` become implementable.
- `Capabilities.available` gains `CanMerge` for `Active`, and
  `BlockedByOpenQuestion "OQ-4"` is retired.

---

## DF-TE-0007

**Title:** Archived projects reject new time but keep existing entries

**Status:** accepted · **Resolves:** OQ-1

### Decision

A project has status `Active` or `Archived`. An `Archived` project may not be
named by a create or correct command. Entries already referencing it remain
valid, keep counting toward totals, and stay correctable in every respect
except moving *to* an archived project.

### Rationale

The two halves each follow from an existing requirement. Allowing new time
against an archived project makes "archived" mean nothing. Invalidating
existing entries would destroy recorded history, which TE-R-030 forbids and
which no requirement authorizes.

### Revisit when

A requirement defines a project lifecycle with more than two states, or states
that archiving should retroactively affect totals.

---

## DF-TE-0008

**Title:** `static-ui-screens/` is the shipping visual language; the PWA shell is retained

**Status:** accepted · **Resolves:** OQ-5

### Decision

Adopt Design B (`static-ui-screens/styles.css` and its screen structure) as
the shipping visual language. Retain from Design A only what B does not
provide: `manifest.webmanifest`, `service-worker.js`, and the PWA shell
wiring.

### Rationale

Every available signal favours B, and none favours A:

- B is the newest UI commit (`0270575`; A last changed at `e896ea1`).
- B covers all of required screens §8.1–8.15 plus sign-in; A covers three.
- B is the HTML+CSS-only design reference the execution instruction describes
  as carrying screen intent.
- B already satisfies the project's own accessibility constraints: its
  stylesheet declares `min-height: 44px` and `48px` targets, and it is
  modal-free, so `DEC-0003` (no modal workflows) and the 44px floor from
  `HANDOFF.md` both hold without modification.

### Consequences

- `index.html` must be rebuilt against B's markup and stylesheet.
- A's minified `src/styles.css` is superseded; `src/accessibility-fixes.css`
  targets A's class names (`.detail-page`, `.wordmark`) and must be re-derived
  against B rather than carried over.
- `src/app.js` is superseded by the WASM bridge (TE-R-091).

---

## DF-TE-0009

**Title:** `Duration` stores milliseconds, not whole seconds

**Status:** accepted · **Supersedes part of:** `DF-TE-0002` (representation only)

### Context

`DF-TE-0002` established that exact elapsed time is authoritative and billable
units are derived. It chose **whole seconds** as the storage unit. Inspecting
the pre-existing persistence layer — required before designing a new one —
shows that was the wrong granularity.

### Evidence

`worker/src/store.js` is the repository's only existing persistence
implementation, and its authoritative time field is **milliseconds**:

- `exact_duration_ms: end - start` on every created activity.
- `stopTimer` computes `exactMs = max(0, end - started - paused_ms)`.
- The split invariant is checked in milliseconds:
  `if (total !== current.exact_duration_ms) throw ... 'duration_invariant_failed'`.
- `exact_minutes: Number((exactMs / 60000).toFixed(4))` — a derived display
  value, exactly the projection role `BillableUnits` plays.

The existing contract therefore already separates exact time from derived
display, and already enforces the split invariant on the exact value. It
agrees with `DF-TE-0002`'s *structure* and disagrees with its *unit*.

### Decision

`Duration` stores whole **milliseconds**. `Duration.milliseconds` is the
authoritative accessor; `Duration.seconds` is retained as a derived,
explicitly lossy convenience for display. `sum` and `partsPreserve` operate on
milliseconds.

### Rationale

Whole seconds cannot represent the existing contract without loss, and the
loss is not benign:

- A 1,500 ms entry truncates to 1 s, silently discarding 500 ms of recorded
  time — a direct TE-R-001 violation.
- Worse, the split invariant becomes unsatisfiable for sub-second parts:
  splitting 1,500 ms into 750 + 750 truncates both children to 0, which
  `Duration`'s constructor rejects, so a legitimate split of real recorded
  time becomes impossible.

Milliseconds cost nothing — the arithmetic stays integer, so TE-R-007 still
holds — and remove the truncation class of defect entirely.

### Consequences

- `MillisecondsPerBillableUnit = 360_000`; unit projection is unchanged in
  behaviour.
- `Duration.ofInterval` takes epoch **milliseconds**, matching `Date.now()`
  and the existing `started_at`/`paused_ms` arithmetic.
- Serialization writes `exact_duration_ms`, matching the established field
  name, so a future import from the existing format is lossless.

---

## DF-TE-0010

**Title:** The existing implementation's void-based merge is a defect, not a precedent

**Status:** accepted · **Reinforces:** `DF-TE-0006`

### Context

`DF-TE-0006` decided that merge supersedes its sources. The pre-existing
`worker/src/store.js` does the opposite: `mergeActivities` appends
`activity.voided` for each source with reason `"Merged into <id>"`, and
`splitActivity` likewise voids its source with `"Replaced by split"`.

Under `AGENTS.md`'s authority order, current implementation ranks below
accepted decision records — but it is evidence of intended behaviour and
deserves an answer rather than silence.

### Decision

`DF-TE-0006` stands. The existing behaviour is treated as a defect.

### Rationale

The existing implementation is demonstrably unsound on its own terms, and this
is checkable directly in its source:

```js
restoreActivity(id, input, actor) {
  const current = this.assertVersion(id, input.base_version);
  if (!current.voided) throw new ServiceError('not_voided', ...);
  // ... appends activity.restored, setting a.voided = false
}
```

`restoreActivity` gates only on `voided`. A merge source *is* voided.
Therefore a source of a completed merge can be restored, which sets
`voided = false` and returns its `exact_duration_ms` to `#summary`'s total —
while the merged activity still carries that same time. The day's total
double-counts, with no error raised. The same applies to a split source.

`Superseded` makes this unrepresentable rather than merely discouraged:
`Capabilities.available (Superseded _)` is empty, so `CanRestore` is not
offered and `restoreEntry` rejects with
`NotPermittedInState(CanRestore, AlreadySuperseded _)`. A test asserts it.

### Consequences

- The distinction "excluded by user judgment, restorable" (`Void`) versus
  "excluded because the time lives elsewhere, terminal" (`Superseded`) is
  load-bearing and must survive serialization: the persisted form records
  which one applies and why, not a single `voided` boolean.
- Should the existing event log ever be imported, `activity.voided` events
  whose reason marks them as merge or split consequences must map to
  `Superseded`, not `Void`.
