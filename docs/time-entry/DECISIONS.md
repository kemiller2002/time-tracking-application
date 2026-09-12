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

A catalogue entry — project or activity type — has status `Available` or
`Archived`, mapping onto the `active: boolean` that
`schemas/domain/project.schema.json` and
`schemas/domain/activity-type.schema.json` already define.

The rule, stated precisely: **time may not be *moved onto* an archived
reference. An archived reference that is merely *retained* is never
re-validated.**

| Command | Checked against the catalogue? |
|---|---|
| Create | Yes — no prior reference exists, so this is new time |
| Correct | Only if the project or activity type *changes* |
| Split | Only for a child whose reference differs from the source's |
| Merge | Only if the target's reference is held by no source |
| Void, restore, attach evidence | No — none of them changes which project time belongs to |

### Rationale

Allowing new time against an archived project makes "archived" mean nothing.
Invalidating or freezing existing entries would destroy or strand recorded
history, which TE-R-030 forbids.

The retention clause is what makes those two compatible, and its absence is a
real defect rather than a nicety. An earlier implementation guarded the whole
command, which meant an entry recorded *before* its project was archived could
no longer have its hours corrected at all — the user could not fix a forgotten
timer, and could not even move the entry to a live project without first
passing a check the entry already failed. That is the opposite of preserving
history. It was caught by a test written to document the behaviour, which is
why the test now asserts the corrected rule.

Splitting deserves the same treatment for the same reason: a split
redistributes time that already exists rather than recording new work, so a
child keeping the source's project is retaining a reference, not creating an
obligation.

### Corroboration

This was not invented. The repository's own schemas already carry
`active: boolean` on both catalogue types, so the Available/Archived
distinction was implied by the documented contract before this decision named
it. The schemas also pin ids to `^[a-z0-9-]+$`, which `ProjectId` and
`ActivityTypeId` now enforce — accepting ids the contract rejects would let
the domain build records that fail schema validation downstream.

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

---

## DF-TE-0011

**Title:** The browser authenticates with a token, behind a port that lets the mechanism change

**Status:** accepted · **Resolves:** `OQ-8` · **Source:** explicit user instruction

### Context

`OQ-8` recorded that the instruction fixes GitHub as the storage and
integration mechanism but says nothing about how a browser proves its right to
act on the repository. Three mechanisms were plausible and they differ in ways
that reach the transport:

| Mechanism | Lifetime | Header |
|---|---|---|
| Token supplied to the page | fixed until replaced | `Authorization: Bearer …` |
| OAuth device flow | expires, refreshes | the same, but not the same value twice |
| GitHub App installation token | ~1 hour, re-minted | the same, but not the same value twice |
| Same-origin proxy holding the session | n/a | **none** — a cookie travels instead |

The user has now settled the question: **the browser supplies a token**, and
stated that this may change.

### Decision

Two parts, and the second is the substantive one.

1. The browser authenticates by supplying a token, sent as
   `Authorization: Bearer`.

2. That is *one implementation of a port*, not the shape the rest of the
   system is written against. `TimeEntry.GitHub.Credential` defines
   `CredentialSource`, and `Credential.token` is today's implementation
   alongside `Credential.ambient` and the general `Credential.ofAsync`.

### Why the port is shaped the way it is

Three choices, each of which a token alone would not have forced:

**Asked per request, not once per session.** A fixed token could be set as a
default header when the client is built — which is exactly what this code did
before. A device-flow or installation token cannot: it expires, and the
refresh has to happen somewhere. Acquiring per request costs one function call
and is the only shape that admits both. A test (`a credential is acquired per
request, not once per client`) fails if the header is ever fixed on the client
again; it was confirmed to fail against precisely that change.

**"No header" is a case, not an empty string.** `AmbientAuthority` is what a
same-origin proxy needs: the request must arrive with no `Authorization` at
all. Representing it as an empty token would send `Authorization: Bearer ` and
earn a confusing 401.

**Failing to obtain a credential is its own outcome.** `CredentialMissing` is
a client-side fact discovered before any request is sent;
`StoreError.Unauthorized` is what a server said. "You are not signed in" and
"your access was refused" call for different responses, and collapsing them
would both lose that distinction and spend a round trip to learn something the
client already knew. `retryDelaySeconds` never retries `CredentialMissing`,
because no number of attempts makes a credential appear.

### Consequences

- Changing mechanism is a change to one function and the host's construction
  of it. `HttpStore`, `Interpreter`, and every tier below are untouched — a
  claim four tests exercise rather than assert.
- `CredentialSource.Describe` carries a short non-secret name so a diagnostic
  can report which mechanism is in use without anything being tempted to log
  the credential in order to answer.
- The token still has to reach the browser, and nothing yet performs an effect
  for it to authenticate. That is `WI-0033`, which this unblocks.
- A token held in a browser is readable by anyone with the page. That is a
  property of the mechanism chosen, not of this port, and is the reason the
  port exists: the proxy option remains available without a rewrite.

---

## DF-TE-0012

**Title:** Evidence reassignment on split copies; it does not partition

**Status:** accepted · **Resolves:** `OQ-11` · **Source:** explicit user instruction

### Decision

Two split children MAY claim the same piece of evidence.

### Why this was a question

A split reassigns the source's evidence to its children. Two readings were
both defensible and the repository stated neither: reassignment as a *move*
(each item lands on exactly one child) or as a *copy* (an item may support
several). One brief plausibly covers both halves of a session; equally
plausibly the point of reassignment is to say which half each document
supports.

`Guards.requireReassignedEvidenceExists` therefore checked only that a
claimed item exists on the source — the part both readings agree on — and left
the stricter rule uninvented.

### Consequences

- The guard is unchanged. The permissive reading was already the implemented
  one *by omission*; it is now the implemented one *by decision*, which is a
  different thing and is why a test now asserts it: `two children may claim the
  same piece of evidence` fails if a uniqueness check is ever added without
  this decision being revisited.
- The whole-item match stays. Sharing an item is permitted; silently editing it
  on the way through still is not (`a split may not relabel the evidence it
  moves`).
- Evidence coverage counts entries holding evidence, not distinct documents, so
  a shared item raises coverage on both children. That follows from the
  decision rather than qualifying it: both children genuinely are supported by
  that document.

---

## DF-TE-0013

**Title:** Overlapping recorded time is not a defect

**Status:** accepted · **Resolves:** `OQ-10` · **Source:** explicit user instruction

### Decision

Overlapping recorded time is **not** a defect. `review.html`'s "No overlapping
time" row is not implemented, and no row stands in its place.

The user's reasoning, recorded because it is the substance of the decision:
time is recorded in six-minute units, so two records can legitimately cover
the same stretch of clock. Genuine double-counting is to be reconciled later,
not caught by a daily check.

### Why no row appears

A row reading "No overlapping time · Clear" would report the result of a test
that never ran. The domain models an entry's **duration**, not its interval —
entries carry no start or end — so overlap is not computable from what is
stored even if it were a defect. Emitting a permanent tick is the one outcome
worse than emitting nothing: it is an assurance with nothing behind it.

### Consequences

- `Kernel.reviewDay` emits three checks, not four, and says why in place.
- TE-R-083 is satisfied by the checks that exist rather than left partial: the
  requirement is that the review screen state the day's checks, and "no
  overlapping time" is no longer one of them.
- Reconciling double-counted time is deferred work, captured as a backlog item
  rather than left as an open question. It needs start and end times on an
  entry, which is a change to Tier 1, and nothing has asked for that yet.

---

## DF-TE-0014

**Title:** A removed entry is labelled "Removed"

**Status:** accepted · **Resolves:** `OQ-7` (partially — see `OQ-12`) · **Source:** explicit user instruction

### Decision

An entry removed from totals is labelled **"Removed"** in a list.

### What is still not stated

OQ-7 asked about a removed **or replaced** entry; the answer named one. The
superseded case keeps the kernel's existing reading, "Replaced", and the
narrower question is re-filed as `OQ-12` rather than treated as answered by
extension. The two are kept distinct because a superseded entry has a
successor to navigate to and a removed one does not — collapsing them would
lose that from the list, which is the one place a reader sees both.

### Consequences

- `badgeView` is unchanged: `VoidedBadge -> "Removed", "badge-warn"`. As with
  DF-TE-0012, what changes is that the word is now stated rather than inferred.
- `Kernel.changeWording` continues to render the fuller "Removed from totals"
  on a detail screen, where there is room for it. The badge is the list label
  this decision fixes.

---

## DF-TE-0015

**Title:** The person using the ledger sets the monthly tracking target

**Status:** accepted · **Resolves:** `OQ-9` · **Source:** explicit user instruction

### Decision

The monthly tracking target is set by the person using the ledger. There is
**no default**: a ledger with no target set reports its figures and draws no
progress bar.

`static-ui-screens/month.html` renders a bar against "80h target". 80 is not
adopted, here or anywhere: no repository document says where it comes from,
whether it varies by person or month, or what it is for. A default would be
that invented requirement wearing a plausible number.

### Where it lives, and why there

In the ledger, at `ledger/preferences.json`, beside the catalogue.

The alternative was the browser — `localStorage`, or the `sessionStorage` the
credential already uses. It was rejected for one reason: a target set on a
phone would then be invisible on a laptop, and `settings.html` describes
preferences as "Personal to this account", not personal to a device. The
ledger is the only store this application has that is neither per-device nor
per-session, and the target is small enough that putting it there costs one
file.

Stored in six-minute units rather than hours, so the file cannot express a
target the domain cannot hold, and a later "seven and a half hours" needs no
schema change. Zero encodes "no target set", which is unambiguous because
`TrackingTarget` cannot hold zero — no legal target ever writes that value.

### What each tier got, and what it did not

| Tier | Addition | Deliberately not there |
|---|---|---|
| 1 | `TrackingTarget` (units, positive, bounded), `Preferences` | any comparison against a recorded total — that mixes a preference with a projection |
| 3 | `TargetProgress.against` | fields on `PeriodSummary`: every caller would then have to supply a target, and an unset one would have to be represented as zero, which is a target and a false one |
| 4 | `PreferencesDocument`, `Layout.PreferencesPath`, `Interpreter.readPreferences` / `savePreferences` | an `Effect` case — see below |
| Kernel | `viewMonth`'s `target` node, `setMonthlyTarget` | any figure or sentence composed in the page |

**Not an `Effect`.** Tier 2's effect vocabulary describes ledger transitions.
Setting a preference appends no revision, attributes nothing to anybody, and
no capability depends on it — so giving the domain an effect case for it would
let a host's need put a non-fact into the language the ledger keeps its facts
in. It is a host operation on the interpreter, exactly like `readEntry`.

**Written with the same compare-and-swap as a ledger write**, on both the
file's blob SHA and the branch ref: it is one file two devices could write at
once. What it does *not* get is conflict *reconciliation*. A refused ledger
write becomes a reviewable conflict because the person must choose between two
versions of a record of what happened (TE-R-071); a refused preference write
needs no review, because the value is small and in front of them. The refusal
is reported as the `StoreError` it is.

### Three absences that must not collapse into one

- **No target set** — `Preferences.none`. The ordinary first state of a new
  ledger. Nothing is refused for want of a target.
- **No preferences file** — read as "no target set", not as an error. This is
  the opposite of the catalogue, where absence *is* an error, because
  `Catalogue.empty` refuses every project and returning it would present a
  failed read as "you have no projects". Nothing is refused for want of a
  preference, so absence is safe here and not there.
- **A preferences file that cannot be read** — `PreferencesUnreadable`, and
  the month view fails in words. Reporting this as "no target set" would hide
  a file that needs attention behind an invitation to set something the person
  has already set. A hand-edited negative target is a corrupt file, not an
  absent preference, and is reported as `InvalidField`.

### Consequences

- The percentage is truncated, not rounded, so it never claims more progress
  than was recorded: 79.9 hours against 80 reads 99%. A test fails against an
  injected rounding, and that was confirmed.
- `percentRecorded` is not capped; `barPercent` is. Exceeding a target is a
  fact worth reporting, and a bar wider than its track is a rendering bug.
  Separating them keeps the page from having to decide which it wanted.
- The fixture page carries a 60-hour target rather than 80, so the figure no
  document justifies does not return as a fixture.
- `settings.html`'s other preferences — appearance, larger controls, reduced
  motion, quick starts — are not implemented. `Preferences` has one field
  because one has been asked for; the record exists so the rest have somewhere
  to arrive without changing every signature that carries preferences.

---

## DF-TE-0016

**Title:** Google and Apple sign-in name the actor, and an unattributed change is refused

**Status:** accepted · **Resolves:** `OQ-6` · **Source:** explicit user instruction

### Decision

A person signs in with **Google** or **Apple**. The actor recorded on every
revision is `google:<sub>` or `apple:<sub>`. A command that carries no
identity is **refused**, not recorded.

That last sentence is the behavioural change. Until now the kernel attributed
every change to the literal string `browser`, documented as a placeholder
because OQ-6 had no answer. With an answer, a change nobody can be attributed
to is a change that should not be recorded — so `attributionOf` fails instead
of substituting.

### Why the subject and not the email

`sub` is stable and scoped to one issuer. Google's survives an email change;
Apple's is the only stable handle at all, because Apple may substitute a
per-application private relay address for the email.

Recording an email would mean a person's history stopped being theirs the day
they changed address — and worse, could make one person's old revisions read
as another person's if an address were ever reassigned. The email is kept as a
*display* fallback and nothing more.

Prefixed with the provider because `sub` is unique only within one issuer.
Without the prefix, a Google subject and an Apple subject could collide and
two people would share a history.

### What this does NOT make true, stated plainly

**Attribution is not cryptographically trustworthy, and nothing in the code
claims it is.** The ID token's signature is not verified.

Verifying it in the browser was considered and rejected as close to theatre:
the page that would do the checking is the same page that could skip it, and
it already holds a repository token that can write anything. A forged token
with the right `iss`, `aud` and `exp` would pass either way.

What the claim checks *do* buy is real but narrower: a token from another
issuer, a token minted for another application, an expired token and a token
with no subject are all refused. That is the class of failure that actually
occurs — misconfiguration, a stale session, the wrong client id — and each one
gets a message a person can act on rather than a silent wrong actor.

The verifiable authority for a change is the **commit**: GitHub authenticates
whoever wrote it, and that is recorded outside any file this application
writes. The actor inside the document is the *claimed* signed-in identity
beside it. Making the recorded actor independently verifiable needs a check
somewhere the page cannot reach — a server, or a workflow that validates the
token against the issuer's keys before accepting a commit. That is `WI-0054`,
filed rather than implied.

### Where each part lives

| Concern | Where | Why not elsewhere |
|---|---|---|
| What a provider is, what issuer it mints, what actor a subject names | Tier 1 `Identity` | these are rules, not transport |
| Whether a token's claims are acceptable | Tier 1 `Identity.accept` | a decision; the page must not make it |
| Decoding a JWT's base64url payload | browser kernel `CommandParsing` | base64 and JSON are infrastructure (TE-R-095), and a page that could pick fields out of a token could pick the wrong ones (TE-R-091) |
| Acquiring a token from Google or Apple | `main.js` | an external effect, which is exactly what the bridge is for (TE-R-092) |
| The words for a rejected sign-in | `Wording` | TE-R-053 |

`Instant` is passed into `accept` rather than read, because Tier 1 performs no
effects. There is **no leeway for clock skew**: a tolerance nobody stated
would be invented, and the device/server skew question is separately open
(TE-R-008).

### What the design screens gave, and what they did not

`static-ui-screens/sign-in.html` shows an email-and-password form with a
passkey note. It is **not** adopted: the user chose Google and Apple, so the
screen's layout carries over and its fields do not. `settings.html`'s identity
chip is where the signed-in name belongs, and the sidebar's status panel now
holds it.

One thing had to be extended: the sidebar is `height: 100vh` with no scroll of
its own, which suits eighteen screens that each carry one status panel. This
page now carries two forms in it, and on a short viewport the lower one was
clipped and its buttons unreachable. `overflow-y: auto` was added in the
page's own style block, not in `styles.css`, for the same reason as the
checkbox rule: that file is the shared design authority.

### How each provider's flow works, and the one that nearly did not

Google Identity Services returns an ID token to the browser with no client
secret, so a static page can complete the flow.

Apple was nearly a blocker. Sign in with Apple's `response_type` must be
`code` or `code id_token`, and with the `form_post` response mode its return
URL has to accept an HTTP POST — which a static page is not. The **popup**
flow is the way through: `AppleID.auth.signIn()` with `usePopup: true` resolves
in the page with `authorization.id_token`, and no server is involved. Had that
not existed, Apple sign-in would have required a server this application does
not have, and the honest answer would have been to say so rather than ship
half of it.

Both SDKs are loaded from their own providers, on demand, only when somebody
asks to sign in with that provider. Not bundled — they are versioned by the
provider and a vendored copy would be a stale one. Not loaded eagerly — a page
nobody signs in on should not call out to two companies to say so.

### Consequences

- The client ids are supplied by the person, alongside the repository. They
  are configuration rather than secrets — a client id is in the page's HTML
  the moment an SDK initialises — but nothing in this repository can hold an
  account belonging to somebody else.
- The ID token is held in `sessionStorage`, for this tab only, like the
  repository token and for the same reason: a shared device must not carry a
  bearer credential into the next person's session.
- Every existing kernel test now injects an identity, because a command
  without one is refused. The refusal itself is asserted directly, and was
  confirmed by re-introducing the `browser` placeholder and watching that one
  test fail.
- **No sign-in has ever been performed against a real Google client id or
  Apple services id.** There is no account to use and the verification harness
  has no network, so the browser checks stage what a completed sign-in leaves
  behind — in the same storage slot, in the same shape — and every code path
  after that point is the real one. This is the same gap `WI-0028` records for
  the repository, and it is `WI-0055`.
- The device label is still the literal `browser`. It was not part of OQ-6 and
  remains unstated, and it is left alone rather than filled in alongside the
  actor: one unresolved thing should not get quietly resolved on the
  coat-tails of another.
