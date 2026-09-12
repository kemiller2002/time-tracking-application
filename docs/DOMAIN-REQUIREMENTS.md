# Domain requirements — Business Activity Ledger

This is the authoritative statement of the business rules the Business
Activity Ledger must enforce, independent of UI layout, endpoint shape, or
storage technology. It exists because neither the execution contract
(`prompts/business_activity_ledger_ui_execution_contract.md`, a build
*process*) nor the current schemas (`schemas/domain/*.json`, which model
only configuration projections) state the ledger's own rules anywhere in
one place — a gap this repository's own capability records
(`docs/capability-records/CAP-000-architecture-validation.md`) confirm
directly: "no Business Activity Ledger domain data layer exists."

Every capability built against the execution contract must satisfy these
rules. Where a capability's design would violate one, the rule wins unless
this document is revised first with a recorded decision
(`decisions/DEC-####-*.md`).

## Lineage

This application's domain rules are reconciled from the two implementations
reconstructed in the `time-entry-state-machine` repository
(`requirements-reconstruction/csharp-business-requirements.md` and
`fsharp-business-requirements-beyond-csharp.md`). This application follows
the richer, F#-derived rule set — exact-minute recording, business
purpose, restore, merge, evidence, and attestation are already assumed
throughout the execution contract's capability list (4–13) — adapted to
this application's own vocabulary and to being single-owner rather than
multi-person.

## Scope and terminology

| state-machine term | this application's term | notes |
| --- | --- | --- |
| Time Entry | **Activity** | matches `/api/v1/activities` |
| Correct | **Amend** | matches `/api/v1/activities/{id}/amendments`; UI may present this as "edit" |
| Label | **Tag** | optional, multiple per activity |
| — | **Activity Type** | required classification, new in this application; validated the same way Project is |
| Person | **Owner** | this application is single-owner (execution contract §2, Capability 2); there is no Person entity, no active/inactive person lifecycle, and no "referenced person" validation — every activity belongs to the one authenticated owner. Multi-user, teams, and approvals are explicitly parked (execution contract, Parking Lot → Collaboration) and out of scope for these rules until that parking-lot item is authorized. |

## Entities

- **Project**, **Activity Type**, and **Tag**: each a stable id, a name, an
  active/archived status, and a version — one shared `ReferenceItem` shape
  in `f-sharp/src/Ledger.Domain/Model.fs`.
- **Activity**: one record of the owner's time — a start time, a duration,
  a project, an activity type, a description, a business purpose, zero or
  more tags, zero or more evidence links, a lifecycle status, a revision,
  an entry method (timer or manual), and provenance recording when and how
  it was created, what it was split or merged from or into, and any
  amendment/void/restore reason.

## Time representation

- Duration and start time are recorded exact to the minute. Six-minute
  quick-entry buttons (execution contract, Capability 6) are an input
  convenience only — they must never round, inflate, or replace the exact
  value the owner actually enters or the timer actually measures
  (execution contract §4: "must not silently inflate or replace exact
  elapsed time").
- An activity's start time plus duration must not cross midnight into the
  next day.
- A derived, separate six-minute **billing figure** is computed per
  activity by rounding its exact duration *up* to the next multiple of six
  minutes. Billing is computed per activity, not by rounding a daily or
  project total: two independent two-minute activities bill as twelve
  minutes combined, never as one four-minute total rounded once.

## Activity lifecycle

- Status is one of: **Recorded**, **Voided**, or **Superseded**.
- A newly created activity starts at revision 1, status Recorded.
- Only a Recorded activity may be amended, split, merged (as a source), or
  voided.
- Amending or voiding increments the activity's revision and preserves its
  identity.
- Splitting or merging marks the source(s) Superseded (revision + 1,
  recording the replacement relationship) and creates new activities at
  revision 1, each recording what it was split or merged from.
- A Voided activity may be **restored** back to Recorded (see below).
  A Superseded activity has no such path — there is no "un-split" or
  "un-merge."

## Creating an activity requires

- A start time exact to the minute, or a timer-derived start/stop pair.
- A positive whole number of minutes for duration; the activity must not
  cross midnight.
- A non-blank description.
- A non-blank **business purpose** — a statement of why the work relates
  to the business, distinct from the description of what was done.
- An existing, Active project.
- An existing, Active activity type.
- Every assigned tag must exist and currently be active.
- The activity's time interval must not overlap any other Recorded
  activity on the same date.
- The entry method (timer vs. manual) is recorded and shown to the owner
  (execution contract, Capability 7: "timer/manual label").
- A manually entered activity for a past date (a reconstructed record) must
  carry an explanatory reason (execution contract, Capability 6:
  "manual-entry reason"; "Reconstructed entries require explanation").

## Amending an activity

- The caller must state the revision they expect the activity to currently
  be at; a mismatch is rejected as a conflict, never silently applied over
  an unseen change (already reflected in `docs/UI-STATE-MODEL.md`).
- Amended fields are re-validated by the same rules as creation.
- If the amendment does not change the activity's project or activity
  type, that project/activity type does not need to still be Active — an
  existing, unchanged assignment to an archived one remains valid. If the
  amendment assigns a *different* project or activity type, the new one
  must be Active.
- Only a **newly added** tag must be active. A tag the activity already
  carried remains valid even after that tag is later deactivated —
  deactivation blocks new assignment, not historical reference. (This
  deliberately follows the F# state machine's corrected rule rather than
  the C# implementation's unconditional check, which that implementation's
  own reconstruction documents as inconsistent with its own error message.)
- An optional reason may be recorded for the amendment.

## Splitting an activity

- Requires the same expected-revision check as amending.
- The source activity must be at least two minutes long.
- The split point must fall strictly inside the source's interval, not at
  either endpoint.
- The resulting activities' durations sum to exactly the source's original
  duration and together exactly cover its original time span — no gap, no
  overlap, no time created or destroyed.

## Merging activities

- Two or more Recorded activities on the same date, whose intervals are
  contiguous (each ends exactly where the next begins), can be merged into
  one new activity spanning their combined time, with a project, activity
  type, description, and business purpose supplied for the result.
- At least two source activities are required. Non-contiguous sources, or
  sources on different dates, cannot be merged.
- Unlike splitting, merging has no minimum-length requirement — the
  constraint is contiguity, not duration.

## Voiding and restoring

- Voiding requires the same expected-revision check, marks the activity
  Voided (not deleted), and increments its revision. An optional reason
  may be recorded. A voided activity no longer counts toward totals but
  remains retrievable as history.
- A Voided activity can be restored back to Recorded, with an optional
  reason. Restoring re-validates the activity against the *current* state
  the same way an amendment does, so a restore cannot silently reintroduce
  a conflict (e.g., an overlap with something recorded while it was
  voided) that did not exist when it was voided.

## Evidence

- A Recorded activity may have evidence attached: a type (URL, LinkedIn
  post, GitHub commit, pull request, issue, calendar event, document,
  screenshot, or another kind), an optional URI, when it was captured, and
  optional notes. Evidence is always optional.
- Evidence may later be unlinked from the activity it was attached to.

## Recording time with a timer

- Only one timer may be active at a time.
- Pausing and resuming produces multiple tracked working segments; time
  spent paused is excluded from what is ultimately recorded.
- Stopping the timer computes total elapsed working time, rounded to the
  nearest whole minute.
- If the timer is stopped after running for less than thirty seconds,
  nothing is recorded.
- A timer-produced activity goes through the exact same validation as a
  manually entered one — the same overlap, project, activity-type,
  description, and business-purpose rules apply uniformly.

## Daily attestation

- The owner can attest to (review and sign off on) a day's recorded
  activities: a statement, together with a snapshot of which activities
  and what total were in effect at that moment.
- An amendment made to an activity after it has been attested remains
  legal — attestation is a checkpoint, not a lock — but the system must be
  able to determine whether an activity has changed since a particular
  attestation covered it, so a later review can be told it was "amended
  after review."
- Attesting again for the same day does not erase or replace the earlier
  attestation; both remain on record, with the newer one representing the
  current reviewed state.

## Capabilities (what's currently legal for a given activity)

| Status | Duration | Amend | Split | Void | Merge (as source) | Evidence link/unlink | Restore |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Recorded | ≥ 2 min | yes | yes | yes | yes | yes | — |
| Recorded | 1 min | yes | no (too short) | yes | yes | yes | — |
| Voided | — | no | no | no | no | link only, no unlink | yes |
| Superseded | — | no | no | no | no | no | no |

## Diagnostics — business-meaningful failure reasons required

A specific, stable reason is required for each of: non-positive or
fractional duration; an activity crossing midnight; a description or
business purpose left blank; a time interval overlapping another Recorded
activity on the same date; a referenced project or activity type that
doesn't exist or is inactive/archived when required to be active; a
referenced tag that doesn't exist or is inactive when newly assigned; a
referenced activity that doesn't exist or isn't Recorded (for amend, split,
void) or isn't Voided (for restore); a supplied revision that doesn't match
the activity's actual current revision; a split point that isn't strictly
inside the source interval; a source activity too short to split;
non-contiguous or cross-date merge sources; and a manual past-date entry
missing its required reason. Generic, undifferentiated error messages do
not satisfy this requirement.

## Persistence contract

Independent of the fact that this application's durable store is a
GitHub-backed ledger, reached directly from the browser (no Cloudflare
service or any other server component sits in between — see
"Implementation" below):

- Loading a day or activity must distinguish four different outcomes:
  found successfully; not found; a known failure (e.g., connectivity); or
  the stored data being invalid/corrupted. These must not collapse into
  one generic error.
- Saving must require the caller to state the version of the data they
  last read (optimistic concurrency — already assumed by
  `docs/UI-STATE-MODEL.md`'s 409/conflict handling) and must report one
  of: confirmed success (with a new version); confirmed failure; a
  conflict (someone else's — or another device's — newer version already
  exists); or an **unknown** outcome when it cannot be determined whether
  the write actually took effect (e.g., the mobile connection dropped
  after the request was sent but before a response arrived — the exact
  condition `docs/UI-OFFLINE-BEHAVIOR.md` and execution-contract
  Capability 16 exist to handle). An unknown outcome must never be
  silently treated as either a success or a failure.
- There must be a way to later reconcile an unknown-outcome write: to find
  out whether it actually applied, didn't apply, is still unknown, or now
  conflicts with something else. The offline command queue's idempotent,
  stable request IDs (execution contract, Capability 16) are the mechanism
  for this reconciliation and must be designed to support it, not just
  safe retries.

## Implementation

This application's business logic is implemented in F#, compiled to
WebAssembly, and runs entirely in the browser — not as a server-side API.

- `f-sharp/src/Ledger.Domain/` — the pure domain: `Diagnostics.fs`
  (the stable failure-reason codes this document's Diagnostics section
  requires), `Model.fs` (entities, billing, capabilities, the timer state
  machine), `Summary.fs` (day/month aggregation), `Commands.fs` (every rule
  above — create, amend, void, restore, split, merge, evidence, attest —
  behind one shared field-validator), and `Services.fs` (the persistence
  port a future real backend will implement).
- `f-sharp/src/Ledger.Engine/` — the wire protocol, session state, view
  projection, GitHub Contents API translation (`GitHubSync.fs`), and report
  rendering that sit between the domain and the browser bridge (`Dispatch.fs`'s
  `handle` is the sole function the WASM export calls).
- `f-sharp/src/Ledger.Wasm/` — the marshalling shim compiled to
  `browser-wasm`.
- `web/` — a thin JavaScript bridge (`wasm-engine-transport.js`,
  `dom-bindings.js`) that renders the engine's view, dispatches DOM events
  back into it, and generically fulfils whatever `Storage`/`Http` effects
  the engine requests (via `localStorage`/`fetch()`); it makes no business
  decisions of its own and never inspects a GitHub request or response.

All three of this document's rules that were not yet enforced by an
earlier, since-retired server implementation — the derived six-minute
billing figure, restore re-validating against current state, and merge
requiring same-date contiguous sources — are implemented as described
above and covered by `f-sharp/tests/Ledger.Domain.Specs`.

Persistence is a browser-local `localStorage` cache backed by an optional
GitHub sync, per the Persistence contract section above:

- `localStorage` remains authoritative moment-to-moment and offline: one
  `Storage.get` right after the engine initializes, one `Storage.set` after
  any event that actually changes the document, and again after a GitHub
  pull replaces it — the cache never goes stale relative to whichever
  source last won.
- GitHub sync is opt-in, configured from the More screen (owner, repo, an
  optional folder, branch, a personal access token). Once configured,
  every mutating command auto-pushes the whole document to the GitHub
  Contents API in addition to its `localStorage` save; "Pull latest" and
  "Sync now" trigger the same requests on demand. This is a deliberate,
  explicitly-chosen architecture decision, not a default: the token is
  entered by the user and sent straight from the browser to
  `api.github.com` — **there is no server component**, matching this
  app's browser-embedded design. The token is kept in its own
  `localStorage` key, separate from the synced document, and is never
  echoed back into the rendered view.
- The configured GitHub repository is never assumed to belong to this app
  alone — it may hold unrelated content the user already has there. The
  ledger's data is therefore always confined to one folder inside it,
  never placed at the repo root or at a user-chosen filename: the file
  path is always `<folder>/ledger.json` (`GitHubSync.dataFilePath`), where
  `<folder>` defaults to `time-tracking-data` when left blank. Neither the
  folder default nor the fixed filename are user-overridable beyond
  choosing the folder's name, precisely so this app cannot be pointed at
  an existing, unrelated file.
- The Contents API's blob `sha` *is* the version this contract asks a
  caller to state on every save — GitHub's own optimistic concurrency check
  (a stale `sha` on a push returns 409, surfaced as this app's `conflict`
  sync status) needs no separate version scheme layered on top of it.
- A pull's or push's outcome is always one of found/not-found(404)/known-
  failure/invalid-document, or confirmed-success/confirmed-failure/
  conflict/**unknown** exactly as this contract requires — a dropped
  connection or timed-out request is reported as `unknown`, never
  collapsed into success or failure (see `Dispatch.fs`'s `"github-push"`
  handling and `web/dom-bindings.js`'s `AbortController`-based timeout).
- `Ledger.Domain/Services.fs`'s `LedgerStore` — an `Async`-shaped port
  imagined for a future in-process backend adapter — stays an unused,
  documented extension point rather than becoming load-bearing: the actual
  WASM↔browser boundary is a single synchronous round-trip per message
  (`Dispatch.handle`), so GitHub sync is built through the same
  effect-request/effect-result mechanism as `Storage` instead, in
  `GitHubSync.fs` + `Dispatch.fs`'s `"github-pull"`/`"github-push"` cases.
  `LedgerStore`'s outcome vocabulary is still honored in spirit: it's
  mirrored 1:1 by how a GitHub response status maps to a sync outcome.
- What this sync does **not** do, as a deliberate scope decision: no
  background/automatic pull (only an explicit "Pull latest" or a page
  reload after which `Storage.get` still serves the local cache), no merge
  of concurrent edits (a pull replaces the in-memory document outright,
  a push conflict must be resolved by pulling first), and no reconciliation
  queue for a request whose outcome came back `unknown` (the user is told
  to pull and check, not offered an automatic retry-and-confirm flow). Any
  of these would need real design work, not just wiring, and are a
  reasonable later increment rather than something this pass needed
  to build.
