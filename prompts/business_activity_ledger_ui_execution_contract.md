# UI Execution Contract
## Business Activity Ledger

**Contract type:** Executable engineering specification  
**Primary system:** Mobile-first Business Activity Ledger UI  
**Dependent systems:** Cloudflare service, GitHub-backed data layer, reports, validation, and workflows

## 1. Purpose

This contract directs an autonomous engineering agent to design, implement, validate, and document the Business Activity Ledger UI in dependency order.

This is not a Scrum backlog or sprint plan. Each capability is a gated engineering obligation.

A capability is complete only when it is:

1. Implemented.
2. Tested.
3. Visually reviewed.
4. Accessibility reviewed.
5. Documented.
6. Reconciled with the Cloudflare service.
7. Reconciled with the GitHub-backed data layer.
8. Reflected in schemas, contracts, and validation.
9. Recorded in the decision journal.
10. Proven not to break completed capabilities.

The agent must not advance merely because a screen appears functional.

---

## 2. Mandatory first action

Before creating or modifying UI code:

1. Locate `.visual-engineering`.
2. Read every relevant file in that directory.
3. Read repository-level instructions.
4. Read `docs/DOMAIN-REQUIREMENTS.md` — the ledger's business rules. This
   contract governs *how* to build; that document governs *what must be
   true* of the result. Where they conflict, the domain requirements win
   and this contract must be revised.
5. Read the data-layer architecture and schemas.
6. Read the Cloudflare service/API contract.
7. Inspect the current implementation, components, tests, workflows, and recent decisions.
8. Create `docs/UI-RECONNAISSANCE.md`.
9. Create `docs/UI-IMPLEMENTATION-PLAN.md`.

`UI-RECONNAISSANCE.md` must include:

- applicable `.visual-engineering` principles;
- current UI state;
- current Cloudflare state;
- current data-layer state;
- contradictions;
- missing capabilities;
- accessibility concerns;
- security concerns;
- architectural risks;
- assumptions;
- recommended implementation order.

Do not begin implementation until these documents exist.

---

## 3. Intended architecture

```text
Mobile-first web UI
        |
        v
Cloudflare service
        |
        +--> authentication
        +--> active timer state
        +--> request validation
        +--> idempotency
        +--> conflict handling
        +--> evidence upload handling
        +--> GitHub App authentication
        |
        v
GitHub-backed append-only ledger
        |
        +--> activities
        +--> amendments
        +--> voids and restorations
        +--> evidence links
        +--> reports
        +--> integrity manifests
```

The browser must never communicate directly with GitHub and must never contain GitHub credentials.

Every durable update must pass through the Cloudflare service.

---

## 4. Core product rules

The UI must be:

- mobile first;
- optimized for iPhone;
- comfortable for one-handed use;
- fast enough for repeated daily use;
- accessible;
- visibly grounded in `.visual-engineering`;
- calm, trustworthy, and visually coherent;
- easy to correct;
- transparent about history;
- resistant to accidental loss.

The system must preserve:

- exact elapsed time;
- original records;
- correction history;
- void and restoration history;
- evidence history;
- report traceability.

Six-minute controls are an input convenience. They must not silently inflate or replace exact elapsed time.

---

## 5. Mandatory execution cycle

For every capability:

```text
Read contracts
  -> inspect implementation
  -> identify UI requirements
  -> identify Cloudflare requirements
  -> identify data-layer requirements
  -> implement smallest complete slice
  -> test
  -> visual review
  -> accessibility review
  -> challenge assumptions
  -> update Cloudflare service
  -> update data layer
  -> update schemas/contracts/docs
  -> record decisions
  -> run full validation
  -> commit if authorized
  -> proceed
```

Skipping a stage requires a written justification.

---

## 6. Living data contract

After every capability, answer explicitly:

1. Does the UI require a new endpoint?
2. Does it require a changed endpoint?
3. Does it require a new command or response field?
4. Does it require a new error code?
5. Does it require a new event type?
6. Does it require a schema change?
7. Does it require new validation?
8. Does it require new projection behavior?
9. Does it require new report behavior?
10. Does it require new configuration?
11. Does it require a new security control?
12. Does it require new tests?
13. Does it require migration support?
14. Did it expose a flaw in an earlier decision?

Any “yes” must be resolved before moving forward. The frontend must not drift ahead of Cloudflare or the ledger.

---

## 7. Capability records

For every completed capability, create:

```text
docs/capability-records/CAP-###-<name>.md
```

Template:

```markdown
# Capability

## Goal
## Preconditions
## UI Work Completed
## Cloudflare Changes
## Data-Layer Changes
## Schema Changes
## Validation Changes
## Tests Added
## Accessibility Review
## Visual Review
## Decisions Recorded
## Known Limitations
## Risks
## Evidence of Completion
## Next Capability
```

---

## 8. Decision journal

Create `decisions/` and record significant decisions.

Examples:

```text
DEC-0001-ui-framework.md
DEC-0002-navigation.md
DEC-0003-mobile-layout.md
DEC-0004-timer-reconciliation.md
DEC-0005-offline-queue.md
DEC-0006-conflict-resolution.md
```

Template:

```markdown
# Decision

## Status
Proposed | Accepted | Superseded | Rejected

## Context
## Decision
## Alternatives Considered
## Evidence
## Assumptions
## Consequences
## Risks
## Revisit When
## Related Capabilities
```

Do not hide consequential decisions in code alone.

---

# Capability Order

## Capability 0 — Architecture validation

### Goal
Understand the current system before changing it.

### Required work

- Read `.visual-engineering`.
- Inspect UI, Cloudflare service, data layer, schemas, workflows, and tests.
- Identify mismatches.
- Produce reconnaissance and implementation plan documents.

### Completion gate

- architecture is documented;
- contradictions are identified;
- implementation order is justified;
- unresolved assumptions are recorded.

---

## Capability 1 — UI foundation

### Goal
Create the mobile-first application foundation.

### UI work

- application shell;
- routing;
- navigation;
- responsive layout down to 320 CSS pixels;
- design tokens;
- typography;
- spacing;
- color;
- focus states;
- loading, empty, error, and offline states;
- light/dark/system modes;
- accessibility baseline.

### `.visual-engineering` obligation

Explain how the repository influenced composition, hierarchy, typography, density, spacing, motion, and interaction.

### Cloudflare reconciliation

Confirm environment configuration, API base URLs, authentication bootstrap, health endpoint, configuration endpoint, and error format.

### Data-layer reconciliation

Confirm configuration, project, activity-type, timezone, and version contracts. The UI must not access the ledger directly.

### Completion gate

- shell works at 320 pixels;
- navigation works;
- baseline accessibility checks pass;
- design-system documentation exists;
- API client connects to mocks or service;
- no GitHub credential exists in browser code.

---

## Capability 2 — Authentication and sessions

### Goal
Secure the single-owner application.

### UI work

- sign-in;
- sign-out;
- session expiration;
- unauthorized state;
- secure redirects;
- reauthentication where needed.

### Cloudflare reconciliation

Confirm secure session cookies, CSRF controls, session refresh, logout, and authentication errors.

### Completion gate

- unauthenticated access is blocked;
- session expiration is recoverable;
- no long-lived secret is stored in localStorage;
- authentication is documented.

---

## Capability 3 — Configuration and preferences

### Goal
Load projects, activity types, favorites, and preferences.

### UI work

- activity selector;
- project selector;
- favorites;
- default project;
- timezone display;
- six-minute preferences;
- appearance settings;
- recent selections.

### Cloudflare contract

```text
GET /api/v1/config
GET /api/v1/projects
GET /api/v1/activity-types
GET /api/v1/preferences
PUT /api/v1/preferences
```

### Data-layer reconciliation

Separate durable GitHub configuration, Cloudflare operational preferences, and browser-only preferences. Do not commit transient UI preferences to the ledger.

---

## Capability 4 — Active timer

### Goal
Provide reliable, one-handed timer operation.

### UI work

- start;
- pause;
- resume;
- stop;
- persistent compact timer;
- elapsed-time display using tabular numerals;
- favorite one-tap starts;
- reconciliation states;
- device/server-time warning;
- reload and background recovery.

### Rules

- Do not calculate authoritative time solely from a JavaScript interval.
- Do not announce every second to screen readers.
- Use server-confirmed timestamps.

### Cloudflare contract

```text
GET  /api/v1/timers/current
POST /api/v1/timers/start
POST /api/v1/timers/pause
POST /api/v1/timers/resume
POST /api/v1/timers/stop
```

Backend requirements:

- one-active-timer rule;
- idempotency;
- concurrency handling;
- pause segments;
- duplicate-stop protection;
- authoritative server timestamps.

### Data-layer reconciliation

Active timer state remains operational in Cloudflare. A completed timer becomes an immutable activity record.

### Completion gate

- survives navigation and reload;
- handles offline transitions;
- retries do not duplicate records;
- two devices cannot silently create conflicting timers;
- start/pause/resume/stop/conflict tests pass.

---

## Capability 5 — Stop and complete activity

### Goal
Stop timing immediately, then complete the record.

### UI work

After stopping, collect:

- activity type;
- project;
- description;
- business purpose;
- optional outcome;
- optional tags;
- optional evidence;
- save;
- save and start another.

The timer must stop before the metadata form is completed.

### Cloudflare reconciliation

Define stopped-but-incomplete state and ensure it is recoverable.

### Data-layer reconciliation

Do not silently discard stopped time. Decide whether incomplete records remain operational drafts in Cloudflare until finalized.

---

## Capability 6 — Manual entry and six-minute controls

### Goal
Support short or reconstructed work.

### UI work

- duration-only entry;
- start/end entry;
- quick buttons for 6, 12, 18, 24, 30, 36, 42, 48, 54, and 60 minutes;
- manual-entry reason;
- past-date selection;
- activity, project, purpose, description, and evidence.

### Rules

- Exact entered duration remains visible.
- Manual entries are distinguishable from timer entries.
- Reconstructed entries require explanation.
- Do not silently round upward.

### Cloudflare contract

```text
POST /api/v1/activities
```

Validate timezone, duration, reason, overlaps, future timestamps, and idempotency.

### Data-layer reconciliation

Confirm entry method, reconstruction reason, client timestamp, server receipt time, exact duration, and reporting display fields.

---

## Capability 7 — Daily timeline

### Goal
Provide a trustworthy view of the day.

### UI work

- chronological list;
- running total;
- activity type;
- project;
- duration;
- evidence count;
- timer/manual label;
- corrected and voided labels;
- pending state;
- warnings;
- empty state.

### Cloudflare contract

```text
GET /api/v1/days/{date}
```

The service should return effective activities; the UI should not project ledger events locally.

### Completion gate

- readable at 320 pixels;
- totals match backend;
- no desktop table dependency;
- effective state is clear;
- history is reachable.

---

## Capability 8 — Activity detail and history

### Goal
Show current effective state and source history.

### UI work

- effective activity;
- original record;
- amendments;
- evidence;
- void/restoration;
- split/merge relationships;
- entry method;
- timestamps;
- warnings;
- optional raw record view.

### Cloudflare contract

```text
GET /api/v1/activities/{activityId}
```

### Completion gate

Every reportable value is traceable without overwhelming the default view.

---

## Capability 9 — Corrections

### Goal
Make correction feel like normal editing while preserving history.

### UI work

- normal edit form;
- correction reason;
- change preview;
- save correction;
- stale-edit conflict comparison.

User-facing explanation:

```text
The original entry remains in history.
This change creates a correction record.
```

### Cloudflare contract

```text
POST /api/v1/activities/{activityId}/amendments
```

Require base projection hash, optimistic concurrency, prior-value validation, and structured conflicts.

### Data-layer reconciliation

Confirm amendment schema, protected immutable fields, recalculated duration, and post-attestation state.

### Completion gate

- originals are never overwritten;
- stale edits are rejected;
- conflict UI is clear;
- time, project, category, description, and purpose correction tests pass.

---

## Capability 10 — Void and restore

### Goal
Remove incorrect entries from totals without deleting history.

### UI work

- void with reason;
- confirmation and total impact;
- restore with reason;
- history display.

### Cloudflare contract

```text
POST /api/v1/activities/{activityId}/void
POST /api/v1/activities/{activityId}/restore
```

### Data-layer reconciliation

Confirm void/restoration events, report exclusion, effective status, and duplicate-event behavior.

---

## Capability 11 — Split and merge

### Goal
Correct sessions containing multiple activities or duplicated fragments.

### Split UI

- divide duration;
- choose activity/project for replacements;
- assign descriptions;
- reassign evidence;
- preview;
- reason.

### Merge UI

- select sources;
- preview duration;
- choose final activity/project;
- combine descriptions;
- reassign evidence;
- reason.

### Cloudflare contract

```text
POST /api/v1/activities/{activityId}/split
POST /api/v1/activities/merge
```

### Data-layer reconciliation

Confirm relationship events, replacement records, source voiding, evidence handling, duration invariants, and cycle prevention.

### Completion gate

- totals validate;
- evidence is preserved;
- history is traceable;
- reports do not double count.

---

## Capability 12 — Evidence

### Goal
Support optional evidence without making record creation burdensome.

### UI work

- add URL;
- add note;
- upload screenshot/photo/document;
- list and preview;
- unlink;
- upload progress;
- failed-upload recovery;
- privacy warning.

### Cloudflare contract

```text
POST   /api/v1/activities/{activityId}/evidence
DELETE /api/v1/activities/{activityId}/evidence/{evidenceLinkId}
```

Cloudflare may use R2, signed upload URLs, hashes, MIME checks, and size limits.

### Data-layer reconciliation

Ledger references should preserve evidence ID, type, hash, R2 object key or external URI, metadata, and unlink history. Do not store secrets or temporary signed URLs in GitHub.

---

## Capability 13 — Daily review

### Goal
Help the user confirm that the day is complete and accurate.

### UI work

Show:

- total time;
- categories and projects;
- manual and corrected entries;
- incomplete descriptions/purpose;
- evidence coverage;
- overlap warnings;
- unsynced and pending items.

Actions:

- correct;
- add evidence;
- complete missing data;
- attest;
- defer.

### Cloudflare contract

```text
GET  /api/v1/days/{date}/review
POST /api/v1/days/{date}/attest
```

### Data-layer reconciliation

Confirm attestation event, projection hash, effective activity list, amendment-after-attestation state, and superseding attestation.

---

## Capability 14 — Monthly summary

### Goal
Show progress and totals without unsupported legal conclusions.

### UI work

- exact minutes;
- decimal hours;
- configured target;
- remaining amount;
- daily, category, and project totals;
- timer versus manual;
- evidence coverage;
- corrections, voids, and warnings.

Use:

```text
Tracking target reached: Yes
Formal program determination: Not evaluated
```

Do not display “Medicaid compliant” without an authoritative separately approved rules engine.

### Cloudflare contract

```text
GET /api/v1/months/{month}
GET /api/v1/reports/monthly/{month}
```

### Data-layer reconciliation

Confirm exact totals, rule version, target configuration, no double counting, source commit, and integrity metadata.

---

## Capability 15 — Reports and export

### Goal
Provide human- and machine-readable records.

### UI work

- daily report;
- monthly report;
- JSON, Markdown, and CSV download;
- report history;
- generation status;
- source commit and integrity information.

### Cloudflare contract

```text
GET /api/v1/reports/daily/{date}
GET /api/v1/reports/monthly/{month}
GET /api/v1/reports/{reportId}/download
```

### Completion gate

- exports work on iPhone;
- reports match source data;
- failures are visible;
- metadata is understandable.

---

## Capability 16 — Offline reliability

### Goal
Remain safe and usable on unstable mobile connections.

### UI work

- local command queue;
- visible pending state;
- retry;
- discard unsent command;
- conflict handling;
- reload recovery;
- timer continuity;
- unsynced evidence handling.

States:

```text
Local only
Sending
Saved
Needs attention
Conflict
```

### Cloudflare reconciliation

Require idempotent commands, stable request IDs, safe retries, conflicts, and server reconciliation.

### Data-layer reconciliation

Retries must never create duplicate source events.

---

## Capability 17 — Accessibility and visual refinement

### Goal
Bring the product to production-quality usability.

### Required work

- VoiceOver review;
- keyboard review;
- Dynamic Type behavior;
- contrast;
- reduced motion;
- focus management;
- tap-target review;
- one-handed use;
- mobile-width review;
- error-language review;
- screenshot review;
- final `.visual-engineering` reconciliation.

Create:

```text
docs/UI-VISUAL-REVIEW.md
docs/UI-ACCESSIBILITY-REVIEW.md
```

### Completion gate

- WCAG 2.2 AA issues resolved where applicable;
- major screens reviewed at mobile widths;
- interface does not resemble a generic admin dashboard;
- timer and correction flows remain effortless.

---

## Capability 18 — Operational readiness

### Goal
Prepare UI and dependent services for reliable production use.

### Required work

- environment configuration;
- production Cloudflare deployment;
- error monitoring;
- privacy-conscious telemetry;
- runbook;
- recovery procedures;
- deployment validation;
- rollback plan;
- browser verification;
- security review.

### Completion gate

- deployment is reproducible;
- no secrets are in client bundles;
- health and error states work;
- rollback is documented;
- full-system tests pass.

---

# Parking Lot

The following items are intentionally deferred and must not interrupt core execution unless needed for an extension seam.

## External integrations

- LinkedIn API integration;
- LinkedIn archive and analytics import;
- GitHub commit and pull-request import;
- calendar integration;
- Gmail integration;
- browser extension;
- Slack;
- Microsoft Teams;
- CRM integration.

## Automation

- Apple Shortcuts automation;
- iOS Share Sheet;
- automatic browser activity detection;
- automatic meeting detection;
- background evidence collection.

## AI

- smart categorization;
- natural-language entry;
- automatic summaries;
- evidence suggestions;
- missing-activity detection;
- anomaly detection;
- voice transcription.

## Platforms

- native iOS;
- Android;
- desktop application;
- Apple Watch;
- widgets.

## Collaboration

- multiple users;
- teams;
- managers;
- approvals;
- shared projects;
- workforce administration.

Parking-lot items may receive documented extension points but must not be implemented without authorization.

---

## 9. Mandatory validation after every capability

Run:

1. unit tests;
2. component tests;
3. integration tests;
4. accessibility tests;
5. type checking;
6. linting;
7. production build;
8. Cloudflare contract tests;
9. data-schema tests;
10. relevant regression tests.

Record exact commands and outcomes. Never fabricate results.

Example:

```text
npm run test:unit
Result: 143 passed, 0 failed

npm run test:e2e -- timer
Result: 18 passed, 0 failed
```

---

## 10. Architecture challenge checkpoint

After each capability, ask:

- Did implementation expose a flawed assumption?
- Is this still the simplest correct architecture?
- Is UI complexity being forced into the data layer?
- Is data-layer complexity being forced into the UI?
- Is Cloudflare holding operational state appropriately?
- Is GitHub limited to durable records?
- Is transient data being committed unnecessarily?
- Is durable data only transient?
- Is offline behavior coherent?
- Is correction history understandable?
- Are security boundaries intact?
- Is mobile usability degrading?

Record findings. If a decision changes, create a new decision and mark the old one superseded.

---

## 11. Documentation synchronization

After each capability, update affected documentation, including as relevant:

```text
README.md
docs/UI-ARCHITECTURE.md
docs/UI-COMPONENTS.md
docs/UI-STATE-MODEL.md
docs/UI-OFFLINE-BEHAVIOR.md
docs/UI-ACCESSIBILITY.md
docs/UI-CLOUDFLARE-INTEGRATION.md
docs/API.md
docs/DATA-MODEL.md
docs/CORRECTIONS.md
docs/REPORTING.md
schemas/**
openapi/**
decisions/**
```

Documentation lag is a failed completion gate.

---

## 12. Commit discipline

If authorized to commit:

- commit complete capabilities or coherent slices;
- use clear messages;
- keep unrelated changes separate;
- include dependent Cloudflare and data-layer updates in the same coherent change set or linked commits;
- never commit secrets;
- never rewrite ledger history;
- do not push without authorization.

Example:

```text
feat(timer): add mobile active timer with Cloudflare reconciliation
```

---

## 13. Final acceptance standard

Execution is complete only when:

- `.visual-engineering` visibly shaped the result;
- the application is genuinely mobile first;
- timer operation is reliable;
- six-minute entry is fast;
- manual and timer entries are distinguishable;
- corrections create amendments;
- void, restore, split, and merge work;
- evidence works;
- daily review and monthly summary work;
- exports work;
- offline behavior is safe;
- stale edits are never silently overwritten;
- all updates pass through Cloudflare;
- no GitHub credentials reach the browser;
- dependent data contracts are current;
- decision and capability records exist;
- accessibility and visual reviews are complete;
- full-system tests pass;
- limitations are documented honestly.

---

## 14. Final agent handoff

The final report must contain:

### Repository reconnaissance
What was found before implementation.

### Visual Engineering interpretation
How `.visual-engineering` shaped the interface.

### Capabilities completed
Status of every capability.

### UI changes
Screens, components, and flows.

### Cloudflare changes
Endpoints, state, authentication, idempotency, and errors.

### Data-layer changes
Events, schemas, projections, reports, and validation.

### Decisions
Accepted, rejected, and superseded decisions.

### Tests
Exact commands and results.

### Accessibility
Findings and unresolved issues.

### Visual review
Findings and corrections.

### Security
Confirmation that secrets and GitHub credentials are absent from the browser.

### Limitations
Everything incomplete, uncertain, or deferred.

### Parking lot
Deferred items without implying completion.

### Recommended next action
The smallest justified next action.

---

# Final Directive

Build the UI as a sequence of complete, validated capabilities.

Do not let the frontend, Cloudflare service, and GitHub-backed data layer evolve independently.

After every capability, reconcile all three systems before continuing.
