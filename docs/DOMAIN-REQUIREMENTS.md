# Shared foundation inheritance

The source requirements in this file are supplemented by `docs/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`. That document preserves the mandatory Aegis, Forma, and conditional Folio obligations that must migrate into Chrona with the rest of this requirement corpus.

---

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
  every mutating command auto-pushes the whole document (see below for how
  `ledger.json`/`metadata.json` are committed together); "Pull latest" and
  "Sync now" trigger the same push on demand, and the ledger is also
  pulled automatically as soon as identity resolves (see below — no
  explicit pull is needed just to see what's already on GitHub). This is a
  deliberate, explicitly-chosen architecture decision, not a default: the
  token is entered by the user and sent straight from the browser to
  `api.github.com` — **there is no server component**, matching this
  app's browser-embedded design. The token is kept in its own
  `localStorage` key, separate from the synced document, and is never
  echoed back into the rendered view.
- The configured GitHub repository is never assumed to belong to this app
  alone — it may hold unrelated content the user already has there. The
  ledger's data is therefore always confined to one folder inside it,
  never placed at the repo root or at a user-chosen filename: the file
  path is always `<folder>/<login>/ledger.json`
  (`GitHubSync.dataFilePath`), where `<folder>` defaults to
  `time-tracking-data` when left blank. Neither the folder default nor the
  fixed filenames (`ledger.json`, `metadata.json`) are user-overridable
  beyond choosing the folder's name, precisely so this app cannot be
  pointed at an existing, unrelated file.
- That same folder is also never assumed to belong to one person alone —
  several people can point the same repository and folder at this app and
  each still gets their own `<login>` subfolder they write to, never one
  shared file several people's browsers race to overwrite. `<login>` is
  GitHub's own account login, resolved once per saved token via a `GET
  /user` call right after `SaveGitHubConfig` (never a name the user
  types), so it can't collide or be mistyped the way a free-text name
  could. Pull/Push are blocked, with a clear message, until that lookup
  resolves.
- Alongside `ledger.json`, this app also writes `metadata.json` into the
  same per-person folder — `{login, displayName, lastSyncedAt}`
  (`GitHubSync.buildMetadataJson`), refreshed on every push. It exists so
  a human (or other tooling) browsing a shared repository's
  `<folder>/<login>/` entries can tell whose folder is whose; nothing
  in-app ever reads it back.
- `ledger.json` and `metadata.json` are committed **atomically**, as a
  single commit, via GitHub's Git Data API rather than as two independent
  Contents API PUTs: `GitHubSync.buildRefGetEffect` through
  `buildRefUpdateEffect` read the branch's current commit and tree, build
  a new tree replacing just those two blobs, create a new commit on it,
  then move the branch ref onto that commit — so the two files' histories
  always move together; one can never land while the other is dropped or
  lags behind. `settings.json` is deliberately excluded from this commit:
  it is never written at the same moment as the ledger (a different
  trigger — `SaveSettings`/`SelectReportFormat` — fires it), so a plain
  Contents API PUT is already atomic for it on its own.
- The commit chain's optimistic concurrency happens at the branch level:
  moving the ref is a non-fast-forward move (someone/something else
  advanced the branch since the chain read it) that GitHub rejects with
  422, treated exactly like the Contents API's own 409 — both mean "this
  write raced another one," and both are what auto-merge (below) resolves.
- A pull's or push's outcome is always one of found/not-found(404)/known-
  failure/invalid-document, or confirmed-success/confirmed-failure/
  conflict/**unknown** exactly as this contract requires — a dropped
  connection or timed-out request is reported as `unknown`, never
  collapsed into success or failure (see `Dispatch.fs`'s `"github-push"`
  handling and `web/dom-bindings.js`'s `AbortController`-based timeout).
- A push conflict (409/422) is resolved automatically, not by asking the
  user to pull and redo their edit: the remote ledger is fetched and
  merged into the local document (`LedgerDocument.merge`, a pure Domain
  function — per-activity last-write-wins by `Version`, attestations
  unioned by `AttestationId`), then the push is retried with the merged
  result. If the retry conflicts again, the same two steps repeat.
- An `unknown` push outcome is reconciled automatically rather than left
  for the user to sort out by hand: the ledger is re-fetched and compared
  against what was attempted, classified per
  `Ledger.Domain/Services.fs`'s dormant `ReconciliationStatus` vocabulary
  — `Applied` (the fetched document already matches the attempt — nothing
  left to do) or `NotApplied`/`ReconciliationConflict` (either way,
  resolved the same way `LedgerDocument.merge` resolves a conflict: merge
  and retry). Only a failure of the reconciliation fetch itself stays
  `StillUnknown`, surfaced to the user as "pull latest to check."
- The ledger (like `settings.json`) is pulled automatically once identity
  resolves — a cached `Login` found on load, or a fresh `GET /user`
  success — not only on an explicit "Pull latest," so a person's existing
  GitHub-stored data appears without an extra click.
- `Ledger.Domain/Services.fs`'s `LedgerStore` — an `Async`-shaped port
  imagined for a future in-process backend adapter — stays an unused,
  documented extension point rather than becoming load-bearing: the actual
  WASM↔browser boundary is a single synchronous round-trip per message
  (`Dispatch.handle`), so GitHub sync is built through the same
  effect-request/effect-result mechanism as `Storage` instead, in
  `GitHubSync.fs` + `Dispatch.fs`'s `"github-pull"`/`"github-push"` cases.
  `LedgerStore`'s outcome vocabulary is still honored in spirit: it's
  mirrored 1:1 by how a GitHub response status maps to a sync outcome.
- Alongside `ledger.json`/`metadata.json`, this app also writes and reads
  back `settings.json` — `{reportFormat, timezone}`
  (`GitHubSync.buildSettingsJson`/`parseSettingsJson`), the user
  preferences that exist today. Unlike `metadata.json`, this file *is*
  round-tripped: it's pulled and applied as soon as identity resolves
  (fresh from `SaveGitHubConfig`, or from a cached config on reload — see
  below), and pushed again whenever `SelectReportFormat` or `SaveSettings`
  changes a preference, so a preference set on one device follows the
  person to another. A 404 here means "nothing saved yet," not a failure,
  same as for the ledger.
- Projects, activity types, and tags are data-driven from a shared,
  repo-level `reference.json` (`{projects, activityTypes, tags}`, each a
  list of `{id, name, active}`) once GitHub sync identity resolves —
  `GitHubSync.buildReferenceGetEffect`/`parseReferenceJson`,
  `Dispatch.fs`'s `"github-reference-pull"` case. Unlike `ledger.json`/
  `metadata.json`, it lives at the folder root
  (`GitHubSync.referenceFilePath`), not under any one person's subfolder —
  the catalog is shared by everyone pointing their config at that
  repo/folder, and any identified person's edit writes the same file
  (`buildReferencePutEffect`, correlation id `"github-reference-push"`) —
  there is no per-writer segregation the way `ledger.json` has. A
  successful pull replaces `Environment.Projects`/`ActivityTypes`/`Tags`
  wholesale (keeping `NewId`/`Clock`); a 404 means "no shared catalog
  published yet," not an error, and the built-in fixture defaults
  (`Session.fs`'s `fixtureEnvironment` — a single neutral "Not defined"
  placeholder per category, no assumption about who is using the app)
  keep serving as the active/inactive lists. The More screen's "Manage
  projects, activity types & tags" section is the admin surface: adding an
  item (`ReferenceCatalog.add`, `Model.fs`) slugifies its name into a
  unique id, and toggling one off (`ReferenceCatalog.setActive`) removes
  it from the choosable options without touching past activities that
  already reference it (`lookupName` falls back to the raw id, so a
  deactivated or since-renamed reference still displays); every edit
  applies locally first and, once GitHub sync is identified, pushes the
  updated catalog (`AddProject`/`AddActivityType`/`AddTag`/
  `SetProjectActive`/`SetActivityTypeActive`/`SetTagActive` in
  `Dispatch.fs`, sharing one `"admin"` error key). Only
  active items appear as choosable options (`Projections.fs`'s
  `projectOptions`/`activityTypeOptions`/`tagOptions`, rendered by
  `dom-bindings.js`'s `data-options` binding; the admin page instead reads
  `adminProjects`/`adminActivityTypes`/`adminTags`, which include inactive
  items too) — an activity that already
  references an archived item still displays correctly via `lookupName`,
  it just can't be chosen for new work.
- The GitHub sync settings themselves (owner/repo/folder/branch/token,
  plus the resolved login/display name) are cached in their own
  `localStorage` key (`GitHubSync.encodeConfig`/`decodeConfig`), separate
  from the ledger's own cache key, so a reload doesn't force re-entering
  them or re-running the identity lookup: a cached config missing `Login`
  (an older save, or the lookup never finished) re-triggers `GET /user`;
  one that already has a `Login` goes straight to pulling `settings.json`.
  A successful identity lookup re-caches the config (now including
  `Login`/`DisplayName`) so the next reload skips the lookup too.
- An explicit "Pull latest" still replaces the in-memory document outright
  (no merge) — automatic merging is specifically the push-conflict and
  reconciliation paths above, where the alternative is data loss, not a
  deliberate user action that already means "I want what's on GitHub."
- An automated architecture/boundary check (`tools/check-architecture.mjs`,
  run by `npm test` and in CI) verifies Tier 1/2 purity (`Ledger.Domain`
  never references JSON/WASM/browser/HTTP APIs) and that every
  `data-event` in `web/index.html` has a matching case in `Dispatch.fs` —
  independent of `Ledger.Engine.Specs`'s behavior tests, which could in
  principle stay green even if the two drifted apart.
