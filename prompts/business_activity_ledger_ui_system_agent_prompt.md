# Agent Implementation Script: Mobile-First Business Activity Ledger UI

## Mission

Design and implement the user interface system for the Echelon Foundry Business Activity Ledger.

The application is a mobile-first time and business-activity tracker designed primarily for use on an iPhone. It must allow the owner to start and stop timers, record six-minute increments, classify business activities, add evidence, correct mistakes, review daily work, and inspect monthly totals.

The interface must be extremely easy to use while the underlying system preserves an append-only audit history.

All data reads and writes must go through a service running on Cloudflare. The browser must not communicate directly with GitHub and must never contain GitHub credentials.

---

# 1. Mandatory First Step: Read `.visual-engineering`

Before creating any UI, component, page, layout, design token, visual direction, interaction pattern, or implementation plan:

1. Locate the `.visual-engineering` folder.
2. Read every relevant document in that folder.
3. Inspect any:
   - README files;
   - design principles;
   - visual standards;
   - composition rules;
   - component specifications;
   - accessibility guidance;
   - typography guidance;
   - color guidance;
   - layout systems;
   - interaction guidance;
   - artist or style references;
   - design research;
   - implementation examples.
4. Summarize the applicable principles in `docs/UI-VISUAL-ENGINEERING-INTERPRETATION.md`.
5. Identify:
   - principles that should govern this application;
   - conflicts between those principles and the needs of a time-tracking interface;
   - assumptions;
   - unresolved questions;
   - accessibility implications;
   - mobile-specific adaptations.
6. Do not begin designing until this review is complete.
7. Do not merely copy an existing visual style. Translate the principles into a coherent interface appropriate for this product.

The UI must visibly reflect the repository's visual-engineering research rather than defaulting to generic dashboard design.

---

# 2. Product Context

This system records legitimate business work such as:

- research;
- software development;
- product development;
- LinkedIn marketing;
- marketing meetings;
- client meetings;
- sales;
- networking;
- writing;
- proposal preparation;
- administration;
- accounting;
- business planning;
- training;
- travel;
- general business activity.

The UI must make it easy to record activities performed from a phone, including:

- reading and research;
- writing in LinkedIn;
- commenting or engaging on LinkedIn;
- calls;
- meetings;
- short administrative tasks;
- work completed in six-minute intervals;
- longer timer-based sessions.

The UI is not merely a billing timer. It is a business activity ledger with an audit-friendly correction system.

---

# 3. Primary Design Goals

The interface must be:

- mobile first;
- fast;
- legible;
- calm;
- visually coherent;
- accessible;
- easy to use one-handed;
- resistant to accidental mistakes;
- transparent about corrections;
- easy to review later;
- suitable for frequent use throughout the day.

The UI should feel simpler than the underlying data model.

The user should not need to understand event sourcing, GitHub, immutable records, hashes, or projection engines to use the system.

---

# 4. Mobile-First Requirement

Design for iPhone first.

The smallest supported layout should work comfortably around:

```text
320 CSS pixels wide
```

Primary target:

```text
390–430 CSS pixels wide
```

Desktop and tablet layouts should enhance the experience but must not determine the mobile design.

## Mobile interaction requirements

- Primary controls must be reachable with one hand.
- Timer controls must remain easy to reach.
- Tap targets must be at least 44 × 44 CSS pixels.
- Important actions must not depend on hover.
- Swipe gestures may be offered, but every swipe action must also have a visible accessible alternative.
- Forms should avoid unnecessary typing.
- Voice input should be supported through native browser behavior where possible.
- Numeric and time inputs should invoke appropriate mobile keyboards.
- The interface must work in portrait orientation.
- Landscape support is desirable but secondary.
- Avoid dense desktop tables on mobile.
- Use timelines, cards, grouped lists, drawers, and bottom sheets appropriately.
- Do not hide critical actions behind ambiguous icons.

---

# 5. Cloudflare Service Architecture

All updates are backed by a service running on Cloudflare.

The UI must call the Cloudflare service for:

- authentication;
- timer creation;
- timer state;
- timer pause and resume;
- timer completion;
- manual activity creation;
- activity corrections;
- voiding;
- restoration;
- splitting;
- merging;
- evidence attachment;
- evidence removal;
- daily review;
- attestation;
- reports;
- configuration;
- project lists;
- activity-type lists.

The UI must never:

- contain GitHub credentials;
- call the GitHub API directly;
- assume that a Git commit is immediately visible;
- modify repository files itself;
- expose Cloudflare secrets;
- rely on client timestamps as the only source of truth.

Recommended architecture:

```text
Mobile Web UI
     |
     v
Cloudflare Service
     |
     +--> Authentication
     +--> Command validation
     +--> Active timer state
     +--> Idempotency
     +--> GitHub-backed persistence
     +--> Effective projections
     +--> Reports
```

---

# 6. Cloudflare API Contract Assumptions

The UI should be built against a versioned API.

Suggested base:

```text
/api/v1
```

Expected endpoints:

```text
GET    /api/v1/config
GET    /api/v1/projects
GET    /api/v1/activity-types

GET    /api/v1/timers/current
POST   /api/v1/timers/start
POST   /api/v1/timers/pause
POST   /api/v1/timers/resume
POST   /api/v1/timers/stop

GET    /api/v1/days/{date}
GET    /api/v1/months/{month}
GET    /api/v1/activities/{activityId}

POST   /api/v1/activities
POST   /api/v1/activities/{activityId}/amendments
POST   /api/v1/activities/{activityId}/void
POST   /api/v1/activities/{activityId}/restore
POST   /api/v1/activities/{activityId}/split
POST   /api/v1/activities/merge

POST   /api/v1/activities/{activityId}/evidence
DELETE /api/v1/activities/{activityId}/evidence/{evidenceLinkId}

POST   /api/v1/days/{date}/attest
GET    /api/v1/reports/daily/{date}
GET    /api/v1/reports/monthly/{month}
```

Do not silently invent backend behavior.

Create a typed client layer with:

- request DTOs;
- response DTOs;
- error DTOs;
- runtime validation;
- retries where safe;
- idempotency keys;
- cancellation;
- timeout handling;
- optimistic concurrency support.

---

# 7. UI Architecture

Use a layered UI architecture.

Recommended layers:

```text
Application Shell
Navigation
Feature Modules
Design System
API Client
State Management
Offline Queue
Validation
Accessibility Utilities
Telemetry
```

Suggested feature modules:

- timer;
- quick entry;
- daily timeline;
- activity detail;
- correction;
- evidence;
- split and merge;
- daily review;
- attestation;
- monthly summary;
- projects;
- activity types;
- settings;
- diagnostics.

Keep domain logic outside visual components.

The UI should consume effective activity records from the Cloudflare service rather than reconstructing ledger events locally unless explicitly required for offline display.

---

# 8. Required Screens

## 8.1 Home / Current Activity

Purpose:

- start work;
- view running timer;
- pause;
- resume;
- stop;
- switch activity;
- access quick entry.

Idle state:

```text
What are you working on?

[ Research ]
[ LinkedIn ]
[ Meeting ]
[ Development ]
[ Administration ]
[ More ]
```

Running state:

```text
Research
Visual Engineering

00:42:18

[ Pause ]   [ Stop ]
```

The current timer should be visually dominant but not visually aggressive.

## 8.2 Quick Start

Allow the user to save favorites:

- Research — Visual Engineering
- LinkedIn — Echelon Foundry
- Development — HelixNote
- Marketing Meeting — General
- Administration — Echelon Foundry

A favorite should begin timing with one tap.

## 8.3 Stop Activity

After stopping:

```text
42 minutes recorded
```

Required fields:

- activity type;
- project;
- description;
- business purpose.

Optional:

- outcome;
- evidence;
- tags;
- notes.

Provide:

- save;
- save and start another;
- discard with explanation.

Do not make the user fill out a long form before the timer can stop.

Stop the timer immediately, then collect metadata.

## 8.4 Manual Entry

Support:

- start and end time;
- duration-only entry;
- six-minute increments;
- past-date entry;
- reason for manual entry;
- activity;
- project;
- description;
- purpose;
- evidence.

Quick durations:

```text
6  12  18  24  30
36 42  48  54  60
```

## 8.5 Daily Timeline

Show activities chronologically.

Example:

```text
Sunday, August 2

9:00–9:42
Research
Visual Engineering
42 min
2 evidence items

10:15–10:51
LinkedIn marketing
Echelon Foundry
36 min
Corrected once

11:00–12:12
Software development
HelixNote
72 min
```

Actions:

- view;
- correct;
- add evidence;
- duplicate;
- split;
- void;
- restore;
- inspect history.

## 8.6 Activity Detail

Show:

- effective activity;
- original activity;
- correction history;
- evidence;
- relationships;
- timer/manual status;
- created time;
- server receipt time;
- current report inclusion;
- warnings.

The default view should emphasize the current effective activity.

History should be available but not overwhelming.

## 8.7 Correction Screen

The correction experience should look like ordinary editing.

Required:

- editable fields;
- current values;
- correction reason;
- save correction;
- cancel.

Explain:

```text
The original entry will remain in history.
This change creates a correction record.
```

Do not use technical event-sourcing language in the main UI.

## 8.8 Void Screen

Explain:

```text
This entry will be removed from totals.
The original record will remain in history.
```

Require a reason.

## 8.9 Restore Screen

Show:

- void reason;
- void date;
- restore reason;
- effect on totals.

## 8.10 Split Activity

Example:

```text
Original
60 min Research

Split into

36 min Research
24 min LinkedIn Marketing
```

Requirements:

- total duration validation;
- optional time correction;
- explanation;
- evidence reassignment;
- preview before save.

## 8.11 Merge Activities

Requirements:

- select adjacent or related activities;
- preview merged time;
- choose final category;
- choose project;
- combine descriptions;
- choose evidence;
- require reason.

## 8.12 Add Evidence

Support:

- paste URL;
- LinkedIn post;
- GitHub link;
- calendar event reference;
- screenshot;
- photo;
- document;
- notes;
- other.

Evidence should be attachable later.

## 8.13 Daily Review

Show:

- total time;
- categories;
- projects;
- warnings;
- manual entries;
- corrections;
- missing descriptions;
- missing business purpose;
- evidence coverage.

Actions:

- correct;
- add evidence;
- attest;
- defer review.

## 8.14 Monthly Summary

Show:

- actual minutes;
- decimal hours;
- configured target;
- remaining amount;
- category totals;
- project totals;
- timer versus manual;
- corrections;
- voids;
- evidence coverage;
- overlap warnings.

Avoid calling a month “compliant.”

Use:

```text
Tracking target reached
Formal program determination not evaluated
```

## 8.15 Settings

Support:

- preferred quick activities;
- default project;
- six-minute controls;
- display format;
- timezone;
- dark/light/system mode;
- daily review preference;
- reminder preference;
- evidence defaults;
- accessibility preferences;
- diagnostics.

---

# 9. Navigation

On mobile, use a bottom navigation system with no more than five primary destinations.

Recommended:

```text
Today
Timer
Month
Projects
More
```

Alternative:

```text
Today
Track
Review
Reports
Settings
```

Choose after applying `.visual-engineering` principles.

The running timer should remain accessible from every screen through:

- persistent compact timer;
- bottom bar indicator;
- top status strip;
- or another visually coherent persistent control.

Do not obscure primary content.

---

# 10. Design System

Create a reusable design system.

Required foundations:

- color tokens;
- typography tokens;
- spacing tokens;
- radius tokens;
- elevation tokens;
- border tokens;
- icon rules;
- motion rules;
- focus rules;
- component states;
- dark mode;
- high-contrast behavior.

Required components:

- button;
- icon button;
- segmented control;
- timer display;
- activity chip;
- project chip;
- status badge;
- timeline item;
- card;
- list row;
- bottom sheet;
- modal;
- toast;
- inline alert;
- text input;
- textarea;
- select;
- date input;
- time input;
- duration picker;
- evidence card;
- history item;
- correction summary;
- warning summary;
- empty state;
- loading state;
- error state;
- offline state;
- conflict-resolution panel.

Document components in:

```text
docs/UI-COMPONENTS.md
```

---

# 11. Visual Direction

The agent must derive visual direction from `.visual-engineering`.

However, the product should generally avoid:

- generic enterprise dashboard appearance;
- excessive cards;
- unnecessary gradients;
- arbitrary bright colors;
- decorative charts without informational value;
- visual clutter;
- small gray text;
- hidden controls;
- excessive borders;
- excessive modal dialogs;
- desktop-first density;
- sterile “admin panel” aesthetics.

The interface should communicate:

- trust;
- calm;
- clarity;
- continuity;
- evidence;
- progress;
- control.

The timer should feel active and useful, not alarming.

Corrections should feel safe and normal, not punitive.

Audit history should feel transparent, not accusatory.

---

# 12. Typography

Use typography rules from `.visual-engineering`.

At minimum:

- support Dynamic Type-like scaling;
- body text should remain readable at mobile sizes;
- timer numerals should use tabular figures;
- duration values should align consistently;
- headings should communicate hierarchy without dominating;
- labels should not rely on all caps;
- line lengths should remain comfortable;
- descriptions should support multiline display.

---

# 13. Color

Use semantic tokens:

```text
background
surface
surface-raised
text-primary
text-secondary
text-muted
border
focus
accent
success
warning
danger
information
timer-running
timer-paused
corrected
voided
manual-entry
```

Do not use color alone to indicate:

- corrected;
- voided;
- warning;
- timer state;
- completion;
- evidence status.

Every state must also use text, iconography, shape, or layout.

---

# 14. Accessibility

The UI must satisfy WCAG 2.2 AA where applicable.

Required:

- semantic HTML;
- proper labels;
- visible focus;
- keyboard support;
- screen-reader descriptions;
- no color-only meaning;
- sufficient contrast;
- large tap targets;
- reduced-motion support;
- zoom support;
- readable error messages;
- logical heading hierarchy;
- accessible bottom sheets and dialogs;
- focus restoration;
- live region for timer state changes where appropriate.

Specific timer accessibility:

- announce timer start;
- announce pause;
- announce resume;
- announce stop;
- do not announce every second;
- provide accessible elapsed-time label;
- ensure controls are reachable with VoiceOver.

---

# 15. Offline and Unreliable Connectivity

The mobile UI must tolerate weak connectivity.

Support:

- local command queue;
- request IDs generated before submission;
- visible pending state;
- retry;
- safe idempotency;
- conflict handling;
- offline timer continuity;
- recovery after browser restart;
- clear unsynced indicators.

Do not claim a record is saved until confirmed by the Cloudflare service.

States:

```text
Local only
Sending
Saved
Needs attention
Conflict
```

---

# 16. Active Timer Behavior

The timer must survive:

- navigation;
- screen lock;
- browser backgrounding;
- app reload;
- network loss;
- device restart where practical.

The UI should not increment time solely from a JavaScript interval.

Use authoritative timestamps:

```text
elapsed = current time - started_at - paused intervals
```

Reconcile with the Cloudflare service whenever connectivity returns.

Show a warning if device time and server time differ materially.

---

# 17. Conflict Resolution

Conflicts may occur when:

- an activity was corrected elsewhere;
- a stale edit is submitted;
- offline edits overlap;
- two devices stop the same timer;
- a split uses an outdated activity version.

Conflict UI:

```text
This activity changed after you opened it.

Current saved version
Your proposed version

[Review changes]
[Apply again]
[Discard mine]
```

Never silently overwrite a newer correction.

---

# 18. Error Handling

Errors must be:

- specific;
- actionable;
- calm;
- recoverable where possible.

Examples:

Weak:

```text
Something went wrong.
```

Better:

```text
The activity was saved, but the evidence photo did not upload.
Try the photo again.
```

Weak:

```text
Conflict.
```

Better:

```text
This activity was corrected on another device after you opened it.
Review the newer version before saving your change.
```

---

# 19. Authentication

The UI should authenticate against the Cloudflare service.

Preferred options:

- Cloudflare Access;
- passkey;
- secure session cookie;
- single-owner authentication.

Requirements:

- no tokens in local source files;
- no GitHub token in browser;
- no long-lived bearer token in localStorage if avoidable;
- secure, HttpOnly, SameSite cookies where applicable;
- explicit sign-out;
- session-expired handling;
- reauthentication for sensitive actions if appropriate.

---

# 20. State Management

Use the simplest state-management approach that supports:

- active timer;
- day timeline;
- form drafts;
- offline queue;
- configuration;
- authentication;
- cached reports;
- conflict state.

Avoid excessive global state.

Separate:

- server state;
- local UI state;
- offline queued commands;
- unsaved form drafts.

Use a dedicated server-state library only if justified.

---

# 21. Technology Selection

Inspect the repository before choosing technology.

Good candidates:

- Astro with islands;
- React;
- Vue;
- Svelte;
- Lit/Web Components;
- standards-based custom elements.

The choice must align with:

- `.visual-engineering`;
- existing repository conventions;
- Cloudflare deployment;
- mobile performance;
- accessibility;
- maintainability;
- offline behavior;
- future component reuse.

Do not introduce a large framework solely because it is familiar.

Document the decision in an ADR.

---

# 22. Progressive Web App

Evaluate making the UI a PWA.

Desired capabilities:

- installable;
- app icon;
- standalone display;
- offline shell;
- cached configuration;
- queued writes;
- persistent active timer;
- shortcuts;
- share target for URLs or evidence if feasible.

Do not let service-worker complexity compromise correctness.

The Cloudflare service remains authoritative.

---

# 23. Share Sheet and Evidence Capture

Investigate PWA share-target support.

Desired flow:

```text
Share article from Safari
        |
        v
Business Activity Ledger
        |
        v
Attach URL to current activity
or
Create research activity
```

For LinkedIn:

```text
Share post URL
        |
        v
Attach as LinkedIn evidence
```

If platform support is unreliable, provide:

- paste from clipboard;
- native Shortcut;
- quick evidence form.

---

# 24. iPhone Shortcut Integration

The UI system should expose or document Shortcut-friendly endpoints.

Suggested shortcuts:

- Start Research
- Start LinkedIn
- Start Meeting
- Stop Current Activity
- Add Six Minutes
- Log Marketing Meeting
- Open Today
- Review Day

The web UI should provide a setup page explaining how to install or configure shortcuts.

---

# 25. Microcopy

Use clear language.

Prefer:

```text
Correct activity
```

over:

```text
Amend event
```

Prefer:

```text
Remove from totals
```

over:

```text
Void source record
```

Prefer:

```text
Saved to your activity history
```

over:

```text
Committed to append-only ledger
```

Technical details belong in history and audit views.

---

# 26. Empty States

Examples:

## No activity today

```text
No business activity recorded yet today.

Start a timer when you begin working, or add time manually.
```

## No evidence

```text
No evidence attached.

Evidence is optional, but you can add a link, note, screenshot, or document when one naturally exists.
```

## No project

```text
This activity is not assigned to a project.
```

---

# 27. Loading and Skeleton States

Use skeletons sparingly.

For timer state, prefer immediate cached display plus reconciliation.

Never show a fake timer.

If current timer state is unknown:

```text
Checking current timer…
```

Do not allow starting another timer until state is resolved unless the backend supports multiple timers.

---

# 28. Reports and Visualizations

Use visualizations only when they improve understanding.

Appropriate:

- progress toward configured target;
- category breakdown;
- project breakdown;
- daily totals across month;
- timer versus manual entry;
- evidence coverage.

Avoid:

- decorative gauges;
- 3D charts;
- unexplained colors;
- charts that hide exact values.

Every chart must have a text or table equivalent.

---

# 29. History and Audit View

History should show:

```text
Original entry
Created Aug 2 at 9:42 AM

Correction
Aug 2 at 11:14 AM
End time changed from 9:48 AM to 9:42 AM
Reason: Forgot to stop timer
```

Provide:

- original values;
- changed values;
- reason;
- timestamp;
- source device label;
- current effective result.

Do not expose raw JSON by default.

Offer raw record view for advanced users.

---

# 30. Privacy

Do not display sensitive evidence previews on the home screen.

Provide:

- evidence visibility controls;
- safe filenames;
- redacted URL display where appropriate;
- confirmation before uploading a file;
- warning before attaching sensitive information;
- local thumbnail handling that does not leak through logs.

---

# 31. Performance Targets

Target:

- app shell interactive quickly on mobile;
- current timer visible within one second from cache;
- server reconciliation within two seconds under normal conditions;
- start timer interaction within one second;
- stop timer immediately reflected locally;
- daily timeline under one second from cache;
- no large JavaScript bundle without justification.

Measure:

- bundle size;
- interaction latency;
- layout shift;
- offline queue reliability;
- API response time;
- accessibility performance.

---

# 32. Testing

Required test categories:

## Unit

- time formatting;
- six-minute controls;
- duration calculation;
- form validation;
- API error mapping;
- conflict comparison;
- offline queue;
- status labels.

## Component

- timer;
- activity card;
- correction form;
- evidence card;
- split form;
- monthly summary;
- warning panel.

## Integration

- start timer;
- pause;
- resume;
- stop;
- save activity;
- correct activity;
- void;
- restore;
- split;
- merge;
- attach evidence;
- offline retry;
- stale conflict.

## Accessibility

- automated accessibility checks;
- keyboard navigation;
- screen-reader labels;
- focus handling;
- reduced motion;
- contrast.

## Mobile

Test at:

- 320 px;
- 375 px;
- 390 px;
- 430 px;
- tablet;
- desktop.

## Browser

At minimum:

- current Safari on iPhone;
- current Chrome;
- current Firefox;
- current Edge.

---

# 33. Visual Review Process

The agent must perform a visual review after implementation.

Required:

1. Capture screenshots of all primary screens.
2. Compare them with `.visual-engineering` principles.
3. Review:
   - hierarchy;
   - density;
   - spacing;
   - typography;
   - color;
   - component consistency;
   - accessibility;
   - one-handed use;
   - error clarity.
4. Record findings in:
   - `docs/UI-VISUAL-REVIEW.md`
5. Fix major issues before completion.

Do not declare the UI visually complete solely because it is functional.

---

# 34. Required Documentation

Create:

```text
README.md
docs/UI-ARCHITECTURE.md
docs/UI-VISUAL-ENGINEERING-INTERPRETATION.md
docs/UI-COMPONENTS.md
docs/UI-STATE-MODEL.md
docs/UI-OFFLINE-BEHAVIOR.md
docs/UI-ACCESSIBILITY.md
docs/UI-CLOUDFLARE-INTEGRATION.md
docs/UI-TEST-PLAN.md
docs/UI-VISUAL-REVIEW.md
docs/UI-RUNBOOK.md
```

---

# 35. Architecture Decision Records

Create ADRs for:

1. UI framework selection.
2. Mobile-first layout strategy.
3. Design-system structure.
4. Cloudflare API client.
5. Authentication strategy.
6. Offline queue.
7. PWA decision.
8. Active timer reconciliation.
9. State-management approach.
10. Conflict-resolution approach.
11. Evidence upload flow.
12. Accessibility baseline.
13. Visual-engineering interpretation.
14. Charting approach.
15. Shortcut integration.

---

# 36. Suggested File Structure

Adapt to the chosen framework.

```text
src/
├── app/
├── routes/
├── features/
│   ├── timer/
│   ├── activities/
│   ├── corrections/
│   ├── evidence/
│   ├── review/
│   ├── reports/
│   └── settings/
├── components/
│   ├── primitives/
│   ├── patterns/
│   └── domain/
├── design-system/
│   ├── tokens/
│   ├── typography/
│   ├── color/
│   ├── spacing/
│   └── motion/
├── api/
│   ├── client/
│   ├── contracts/
│   ├── errors/
│   └── mocks/
├── state/
├── offline/
├── accessibility/
├── telemetry/
└── tests/
```

---

# 37. Cloudflare Integration Requirements

The UI must be configurable for different environments.

Example:

```text
Development
Staging
Production
```

Use environment-specific API base URLs.

Do not hard-code secrets.

Implement:

- CSRF protection as appropriate;
- authenticated fetch;
- timeout;
- retry for idempotent reads;
- retry for idempotent commands using the same request ID;
- request tracing;
- correlation IDs;
- 409 conflict handling;
- 429 rate-limit handling;
- 503 dependency handling.

The UI should display Cloudflare service status only when it affects the user.

---

# 38. Request Idempotency

Every write command must contain a request ID.

Generate before submission.

Persist until confirmation.

Example:

```json
{
  "request_id": "01K1M6K7...",
  "command": "stop-timer",
  "client_timestamp": "2026-08-02T09:42:19-04:00",
  "payload": {}
}
```

If the request is retried, reuse the same request ID.

---

# 39. Analytics and Telemetry

Collect minimal product telemetry.

Allowed examples:

- page load failure;
- API error type;
- offline queue size;
- timer command latency;
- conflict count;
- accessibility error reports.

Do not collect:

- descriptions;
- evidence contents;
- URLs;
- business-purpose text;
- sensitive form contents.

Telemetry should be optional and privacy-conscious.

---

# 40. Agent Execution Plan

## Stage 0: Reconnaissance

1. Read repository instructions.
2. Read `.visual-engineering`.
3. Inspect current frontend.
4. Inspect Cloudflare service contract.
5. Inspect existing components.
6. Document findings.
7. Create implementation plan.
8. Create ADR list.

Do not begin design before completing this stage.

## Stage 1: Information Architecture

1. Define primary user journeys.
2. Define screens.
3. Define navigation.
4. Define state transitions.
5. Define errors.
6. Define offline behavior.
7. Review against mobile-first requirements.

## Stage 2: Design System

1. Translate `.visual-engineering` principles.
2. Define tokens.
3. Define typography.
4. Define color.
5. Define spacing.
6. Define components.
7. Build component examples.
8. Run accessibility checks.

## Stage 3: API Client

1. Define typed contracts.
2. Add runtime validation.
3. Add auth handling.
4. Add request IDs.
5. Add retries.
6. Add errors.
7. Add conflict handling.
8. Add mocks.

## Stage 4: Timer

1. Build idle state.
2. Build running state.
3. Build pause/resume.
4. Build stop.
5. Build background reconciliation.
6. Build offline behavior.
7. Test on iPhone dimensions.

## Stage 5: Activities

1. Build stop form.
2. Build manual entry.
3. Build daily timeline.
4. Build detail.
5. Build quick entry.
6. Build six-minute controls.

## Stage 6: Corrections

1. Build correction screen.
2. Build void.
3. Build restore.
4. Build split.
5. Build merge.
6. Build history.

## Stage 7: Evidence

1. Build paste URL.
2. Build file upload.
3. Build screenshot/photo.
4. Build evidence list.
5. Build unlink.
6. Build offline evidence handling.

## Stage 8: Review and Reports

1. Build daily review.
2. Build attestation.
3. Build monthly summary.
4. Build category breakdown.
5. Build project breakdown.
6. Build export links.

## Stage 9: PWA and Shortcuts

1. Evaluate PWA.
2. Add offline shell.
3. Add install metadata.
4. Add share target if reliable.
5. Document iPhone Shortcuts.
6. Test background and reload behavior.

## Stage 10: Visual Review

1. Capture screenshots.
2. Compare with `.visual-engineering`.
3. Review mobile hierarchy.
4. Review density.
5. Review accessibility.
6. Fix issues.
7. Document results.

---

# 41. Acceptance Criteria

The UI is acceptable only when:

- `.visual-engineering` was read and documented before design;
- the system is genuinely mobile first;
- the primary timer can be used one-handed;
- six-minute entry is easy;
- current timer state survives navigation and reload;
- all writes go through Cloudflare;
- no GitHub credentials exist in the browser;
- corrections feel like normal editing;
- originals remain visible in history;
- stale conflicts are not silently overwritten;
- offline commands are queued safely;
- daily review works;
- monthly summary works;
- evidence can be added later;
- accessibility checks pass;
- screenshots demonstrate coherent visual design;
- the UI does not resemble a generic admin dashboard;
- all tests pass;
- documentation is complete.

---

# 42. Final Agent Report

Return:

## Visual Engineering Review

Explain what was learned from `.visual-engineering` and how it changed the UI.

## Implemented

List screens, components, and files.

## Cloudflare Integration

List endpoints used, authentication, idempotency, and conflict behavior.

## Mobile Review

Describe testing at mobile widths and one-handed use.

## Accessibility

List checks and outcomes.

## Offline Behavior

Show queued command and recovery behavior.

## Demonstrations

Show:

- start timer;
- pause;
- resume;
- stop;
- six-minute entry;
- correction;
- void;
- restore;
- split;
- merge;
- evidence;
- daily review;
- monthly summary;
- stale conflict;
- offline retry.

## Test Results

Provide exact commands and results.

## Limitations

Be explicit about anything incomplete or uncertain.

## Next Step

Recommend the smallest next implementation step.

---

# Final Instruction

Do not build a generic time-tracking dashboard.

Build a mobile-first business activity interface whose visual system is grounded in the repository's `.visual-engineering` research, whose interactions make frequent phone use effortless, and whose updates are securely handled by the Cloudflare service.
