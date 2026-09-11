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
