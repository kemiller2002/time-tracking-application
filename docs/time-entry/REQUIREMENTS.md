---
id: TE-REQ-001
title: Time Entry Requirements Inventory
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0003]
related_documents:
  - prompts/business_activity_ledger_ui_execution_contract.md
  - prompts/business_activity_ledger_ui_system_agent_prompt.md
  - docs/time-entry/DOMAIN-MODEL.md
  - docs/time-entry/TRACEABILITY.md
tags: [requirements, time-entry, traceability]
---

# Time Entry Requirements Inventory

Stable requirement identifiers extracted from repository authority. Each
requirement cites its source. Requirements that originate from the executing
user's instruction rather than a repository document are marked
`source: user-instruction` and carry the authority precedence recorded in
[`DF-TE-0001`](DECISIONS.md#df-te-0001).

**Extraction rule applied:** implementation was treated as requirement
evidence only where it expresses intended behavior corroborated by a
requirements document. `src/domain.js` `validateSplit` is cited as
corroborating evidence for TE-R-040, not as an authority in itself.

## Authority precedence used

Per `AGENTS.md` ("Authority"), descending: explicit user instruction; safety
and legal constraints; canonical governance; accepted REPs/theory; accepted
architecture and decision records; current implementation; local convention.
Two conflicts were resolved under this rule and recorded as decisions —
see [`DECISIONS.md`](DECISIONS.md).

---

## A. Time measurement

| ID | Requirement | Source |
|---|---|---|
| TE-R-001 | Exact elapsed time MUST be preserved as authoritative. | exec-contract §4 "The system must preserve: exact elapsed time" |
| TE-R-002 | Six-minute increments are an **input convenience and billing projection**, NOT the authoritative store. | exec-contract §4 "Six-minute controls are an input convenience. They must not silently inflate or replace exact elapsed time." |
| TE-R-003 | One billing unit = six minutes; ten units = one hour. | user-instruction §2 |
| TE-R-004 | Invalid fractional billing units MUST NOT enter authoritative domain state. | user-instruction §2 |
| TE-R-005 | Elapsed time MUST derive from authoritative timestamps: `elapsed = now - started_at - paused intervals`. | system-prompt §16 |
| TE-R-006 | The UI MUST NOT increment time solely from a JavaScript interval. | system-prompt §16 |
| TE-R-007 | Totals MUST NOT use floating-point arithmetic as authoritative for billable time. | user-instruction §21 |
| TE-R-008 | A warning MUST be shown when device time and server time differ materially. | system-prompt §16 |
| TE-R-009 | Quick-duration inputs are 6,12,18,24,30,36,42,48,54,60 minutes. | system-prompt §8.4 |

> **Reconciliation note (TE-R-001 vs TE-R-002/003).** The user instruction
> describes six-minute units as the domain representation; repository
> authority requires exact elapsed time to be preserved and explicitly
> forbids six-minute controls from replacing it. These are reconciled, not
> traded off: the domain carries **both** an exact `Duration` (authoritative)
> and a derived `BillableUnits` projection. See `DF-TE-0002`.

## B. Entry lifecycle

| ID | Requirement | Source |
|---|---|---|
| TE-R-020 | The system MUST support creating a time entry. | user-instruction §2; system-prompt §8.2, §8.4 |
| TE-R-021 | The system MUST support viewing time entries. | system-prompt §8.5, §8.6 |
| TE-R-022 | The system MUST support correcting an existing entry. | system-prompt §8.7 |
| TE-R-023 | The system MUST support splitting an entry. | system-prompt §8.10 |
| TE-R-024 | The system MUST support voiding an entry. | system-prompt §8.8 |
| TE-R-025 | The system MUST support restoring a voided entry. | system-prompt §8.9 |
| TE-R-026 | The system MUST support merging activities. | system-prompt §8.11 |
| TE-R-027 | An entry MUST carry a description of work performed. | user-instruction §2; system-prompt §8.4 |
| TE-R-028 | An entry MUST be associated with a project. | user-instruction §2; system-prompt §8.4 |
| TE-R-029 | An entry MUST be associated with an activity type. | system-prompt §8.4; schemas/domain/activity-type.schema.json |
| TE-R-030 | Original records MUST be preserved. | exec-contract §4 |
| TE-R-031 | Correction history MUST be preserved. | exec-contract §4 |
| TE-R-032 | Void and restoration history MUST be preserved. | exec-contract §4 |
| TE-R-033 | Evidence history MUST be preserved. | exec-contract §4 |
| TE-R-034 | Report traceability MUST be preserved. | exec-contract §4 |
| TE-R-035 | The system MUST be resistant to accidental loss. | exec-contract §4 |

## C. Split invariants

| ID | Requirement | Source |
|---|---|---|
| TE-R-040 | A split MUST preserve the source total: `sum(children) = source`. | user-instruction §2; corroborated by `src/domain.js:validateSplit` |
| TE-R-041 | Split children MUST be strictly positive (zero-unit children rejected). | `src/domain.js:validateSplit` (`value > 0`); no repository requirement authorizes zero |
| TE-R-042 | Negative child durations MUST be rejected. | TE-R-041 corollary |
| TE-R-043 | Split children MUST retain explicit, auditable lineage to the source. | user-instruction §2 "explicit, auditable relationship" |
| TE-R-044 | A split MUST offer preview before save. | system-prompt §8.10 |
| TE-R-045 | A split MUST support evidence reassignment. | system-prompt §8.10 |
| TE-R-046 | A split against a stale source version MUST be rejected as a conflict. | system-prompt §17 "a split uses an outdated activity version" |

## D. Correction semantics

| ID | Requirement | Source |
|---|---|---|
| TE-R-050 | Correction MUST be modelled as **supersession**, not CRUD mutation: "The original entry will remain in history. This change creates a correction record." | system-prompt §8.7 |
| TE-R-051 | A correction MUST require a reason. | system-prompt §8.7 |
| TE-R-052 | History MUST show original values, changed values, reason, timestamp, source device label, and current effective result. | system-prompt §29 |
| TE-R-053 | The main UI MUST NOT use technical event-sourcing language. | system-prompt §8.7 |
| TE-R-054 | Raw JSON MUST NOT be exposed by default; a raw record view MAY be offered for advanced users. | system-prompt §29 |

## E. Void / restore semantics

| ID | Requirement | Source |
|---|---|---|
| TE-R-060 | Voiding removes an entry from totals but retains the original record in history. | system-prompt §8.8 |
| TE-R-061 | Voiding MUST require a reason. | system-prompt §8.8 |
| TE-R-062 | Restore MUST show void reason, void date, restore reason, and effect on totals. | system-prompt §8.9 |
| TE-R-063 | Void MUST NOT be physical deletion. | TE-R-030, TE-R-060 |

## F. Concurrency and conflict

| ID | Requirement | Source |
|---|---|---|
| TE-R-070 | A newer correction MUST NEVER be silently overwritten. | system-prompt §17 |
| TE-R-071 | Conflicts MUST surface current saved version vs proposed version with Review / Apply again / Discard mine. | system-prompt §17 |
| TE-R-072 | Stale writes MUST become an explicit domain outcome, not an automatic re-read-and-overwrite. | user-instruction §11 |
| TE-R-073 | Version evidence MUST be retained across load → transition → persist. | user-instruction §11 |
| TE-R-074 | Conflict sources include: corrected elsewhere, stale edit, overlapping offline edits, two devices stopping one timer, split on outdated version. | system-prompt §17 |

## G. Projection (listing, search, totals)

| ID | Requirement | Source |
|---|---|---|
| TE-R-080 | Filtering, sorting, searching, totals, and list projections MUST be owned by the F# kernel. | user-instruction §2, §17 |
| TE-R-081 | Projections MUST be deterministic for the same authoritative state and query. | user-instruction §21 |
| TE-R-082 | Activities MUST be displayable chronologically. | system-prompt §8.5 |
| TE-R-083 | Empty states MUST be handled (no activity today, no evidence, no project). | system-prompt §26 |
| TE-R-084 | Invalid persisted records MUST be handled without failing the whole projection. | user-instruction §17 |
| TE-R-085 | Unit→display conversion (e.g. 23 units = 2h 18m) happens only at the presentation boundary. | user-instruction §21 |

## H. Boundary and architecture

| ID | Requirement | Source |
|---|---|---|
| TE-R-090 | Authoritative domain state MUST be implemented in F#. | user-instruction §0, §5 |
| TE-R-091 | TypeScript/JS MUST contain no business rules, validation, policy, totals, filtering, sorting, or lifecycle. | user-instruction §13 |
| TE-R-092 | TypeScript MAY contain WASM loading, event forwarding, fetch/transport, DOM integration, boundary serialization. | user-instruction §13 |
| TE-R-093 | Pure domain transitions MUST NOT perform external effects; effects are requested as data. | user-instruction §0, §10; `.sde/architecture/FOUR-TIER-ARCHITECTURE.md` Tier 2 |
| TE-R-094 | GitHub API response shapes MUST NOT leak into the domain (anti-corruption boundary). | user-instruction §0, §12 |
| TE-R-095 | Tier 1 (Semantic Model) MUST NOT reference infrastructure, HTTP, JSON, or browser APIs. | `.sde/architecture/FOUR-TIER-ARCHITECTURE.md` |
| TE-R-096 | Illegal states MUST be structurally difficult or impossible to represent. | user-instruction §4; SDE doctrine |
| TE-R-097 | Capabilities MUST be computed from authoritative state; the UI MUST NOT independently decide legality. | user-instruction §8 |
| TE-R-098 | The browser layer SHOULD remain native HTML and CSS wherever possible. | user-instruction §0 |
| TE-R-099 | Backend storage and integration is GitHub via the GitHub API. | user-instruction §0, §11 — **supersedes** the repository's Cloudflare design, see `DF-TE-0001` |

## I. Accessibility

| ID | Requirement | Source |
|---|---|---|
| TE-R-110 | Semantic HTML MUST be preserved; clickable `div`s MUST NOT replace semantic controls. | user-instruction §24 |
| TE-R-111 | Labels, keyboard navigation, focus behavior, button semantics, form errors, and screen-reader relationships MUST be verified. | user-instruction §24; system-prompt §14 |
| TE-R-112 | Touch targets MUST meet the established 44px minimum. | `HANDOFF.md` (32px target fixed to 44px) |
| TE-R-113 | No modal workflows — full in-page detail workspaces instead. | `HANDOFF.md`; `DEC-0003` |

## J. Open questions (unresolved — NOT invented)

| ID | Question | Affected requirement | Why it is not resolvable from repository evidence |
|---|---|---|---|
| OQ-1 | May inactive/archived projects receive new time? | TE-R-028 | `schemas/domain/project.schema.json` defines project shape but no lifecycle or eligibility rule; no requirements document states one. |
| OQ-2 | Are labels a distinct concept from activity type and purpose, and do they belong to entry or project? | TE-R-029 | The user instruction names "labels"; repository requirements name "activity", "project", and "purpose". No repository document defines a `label` entity. |
| OQ-3 | Are descriptions required, and what are their length/whitespace/encoding constraints? | TE-R-027 | System-prompt §8.4 lists description as a field but states no constraint; no schema constrains it. |
| OQ-4 | Does merge (TE-R-026) produce a new entry superseding N sources, or void N sources and create one? | TE-R-026 | System-prompt §8.11 lists UI requirements only; lineage semantics unstated. |
| OQ-5 | Which of the two design systems ships? | TE-R-110..113 | **RESOLVED by DF-TE-0008** — `static-ui-screens/` is adopted. Listed here because the rest of the documentation (`DECISIONS.md`, `UI-PROVENANCE.md`, `TRACEABILITY.md`) has always numbered it OQ-5 while this table stopped at OQ-4. That gap caused a real id collision: a later question was filed as OQ-5 and had to be renumbered. |
| OQ-6 | Who is the actor on a change made in the browser? | TE-R-031, TE-R-052 | `static-ui-screens/sign-in.html` shows a sign-in screen and `settings.html` an identity chip, but no document states where the identity comes from, how it is verified, or what it is recorded as. Every history revision requires an actor, so the browser kernel currently attributes changes to the literal `browser` — a placeholder, not a decision. |
| OQ-7 | How should a removed or replaced entry be labelled in a list? | TE-R-030 | `today.html` defines the badge vocabulary — "Timer", "Manual entry", "Corrected once", "N evidence", "Purpose missing" against `badge-good`/`badge-warn`/`badge-change` — but shows no removed or superseded record, and `remove.html`/`restore.html` show only their own workflows. The kernel uses "Removed" and "Replaced" (following `remove.html`'s "Remove from totals"), which is a reading of the design, not a statement of it. |
| OQ-8 | How does the browser authenticate to the GitHub API? | TE-R-090, TE-R-070 | The instruction fixes GitHub as the storage and integration mechanism, but not the credential path from a browser. A token held client-side is readable by anyone with the page; a device-flow token still lands in browser storage; a server-side proxy would reintroduce the backend `DF-TE-0001` removed. No repository document states which. **This blocks performing effects (WI-0033), and until it is settled a command's result lives only in the page** — which is also why an entry created in the browser has no version and so cannot yet be corrected or removed. |

These are recorded per the execution rule "Do not invent missing requirements.
Do not silently resolve ambiguous requirements." They block the specific
transitions named, not the whole domain.
