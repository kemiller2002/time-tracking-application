---
id: TE-MAP-001
title: Repository Semantic Map
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
tags: [sde, semantic-map, navigation]
---

# Repository Semantic Map

Routing table required by `.sde/method/FEATURE-MANIFESTS.md` (SDE-METHOD-008).
Navigation metadata only — it points at authorities, it is not one.

## Semantic areas

| Area | Lives in | Tier | Manifest | Notes |
|---|---|---|---|---|
| Time Entry semantic model | `domain/TimeEntry.Semantic/` | 1 | `domain/manifest.md` | Identity, duration, values, state, capabilities, obligations |
| Time Entry transitions | `domain/TimeEntry.Transitions/` | 2 | `domain/manifest.md` | Commands, requested effects, pure transitions |
| Time Entry projection | `domain/TimeEntry.Projection/` | 3 | `domain/manifest.md` | Queries, list projection, totals |
| Domain verification | `domain/TimeEntry.Tests/` | — | `domain/manifest.md` | Behavioural + adversarial domain tests |
| GitHub persistence boundary | *not yet implemented* | 4 | — | TE-005; blocked, see below |
| WASM / browser bridge | *not yet implemented* | 4 | — | TE-006; blocked, see below |
| Browser UI (design authority) | `static-ui-screens/` | 4 | `docs/time-entry/UI-PROVENANCE.md` | 18 screens; authoritative per `DF-TE-0003` |
| Browser UI (delivery shell) | `index.html`, `src/`, `service-worker.js` | 4 | `docs/time-entry/UI-PROVENANCE.md` | PWA shell; current JS prototype |
| Superseded backend | `worker/`, `openapi/ledger-api.yaml` | — | `docs/time-entry/DECISIONS.md` | Cloudflare; superseded by `DF-TE-0001`, retained not deleted |
| Repository governance | `AGENTS.md`, `docs/00-governance/` | — | — | ROS authority |
| Method | `.sde/` | — | — | SDE v1.1.1, installed, do not hand-edit |

## Requirements and decisions

| Document | Purpose |
|---|---|
| `docs/time-entry/REQUIREMENTS.md` | Requirement inventory, TE-R-### ids, open questions |
| `docs/time-entry/TRACEABILITY.md` | Requirement → code → test → evidence chain |
| `docs/time-entry/DECISIONS.md` | `DF-TE-0001..0003` |
| `docs/time-entry/UI-PROVENANCE.md` | Git-history screen provenance |
| `docs/time-entry/TOOLCHAIN-BLOCKER.md` | Why Tier 4 is not implemented |
| `prompts/business_activity_ledger_ui_*.md` | Source requirements authority |

## Current execution state

Tiers 1–3 are authored in F#. **They have never been compiled or run**: no
.NET SDK can be installed in this environment, so Tier 4 (GitHub persistence,
WASM bridge) is not implemented and no F# verification evidence exists. See
`docs/time-entry/TOOLCHAIN-BLOCKER.md` for the reproducible evidence and the
three ways to unblock it.

The JavaScript prototype in `src/` remains the only runnable application. It
is **not** the domain authority (TE-R-090) and is retained as working
software, not as a target to extend.
