---
id: TE-UIP-001
title: Time Entry UI Provenance
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0008]
related_documents:
  - docs/time-entry/DECISIONS.md
tags: [ui, provenance, git-history, time-entry]
---

# Time Entry UI Provenance

Evidence-based reconciliation of intended screen design, per the execution
requirement to recover screen intent from Git history before recreating any
screen.

## Method

Commands run against the full history of all branches:

```
git log --oneline --all                              # 5 commits total
git log --all --diff-filter=D --summary              # every deletion, ever
git log --all --oneline --name-status -- '*.html' '*.css'
git log --all --oneline -- static-ui-screens/
git diff 0270575 HEAD -- static-ui-screens/          # 0 lines
```

## Finding 1 — nothing was ever removed

The premise the execution script anticipated (markup temporarily removed
during implementation, needing recovery) is **false for this repository**.

`git log --all --diff-filter=D --summary` reports exactly **one** deletion in
the entire history: `tools/ros_cli.mjs`, deleted by the current HEAD commit as
part of the ROS 2.0.1 upgrade. **No HTML or CSS file has ever been deleted on
any branch.**

`git diff 0270575 HEAD -- static-ui-screens/` is empty: the screens are
byte-identical to their introducing commit.

Therefore no file recovery (`git show <commit>:<path>`) was required, and none
was performed. Acting on the recovery premise would have meant fabricating a
provenance conflict the evidence does not support.

## Finding 2 — there are two complete, different design systems

This is the real provenance question, and it is not the one the script
anticipated.

| | **Design A — app shell** | **Design B — static screens** |
|---|---|---|
| Entry point | `index.html` | `static-ui-screens/*.html` (18 files) |
| Stylesheet | `src/styles.css` (minified, 17.4 KB normalized) | `static-ui-screens/styles.css` (24.6 KB normalized) |
| Introduced | `47059b9` initial commit | `0270575` "Add static business ledger UI screens" |
| Last touched | `e896ea1` "adding roadmap" | `0270575` (never modified) |
| Structure | Single page, `data-view` sections, JS-driven | Multi-page, one file per screen, no JS |
| Layout | Mobile-first, bottom nav | `app-shell` sidebar + responsive bottom nav |
| Canvas token | `--bg: #f2efe7` | `--canvas: #e8e4dc`, `--paper: #f8f5ee` |
| Accent token | `--accent: #d36b45` (rust) | `--mineral: #215d57` (teal) + `--rust: #9a4d32` |
| Serif | Georgia | Iowan Old Style, Baskerville |
| Sans | ui-rounded / SF Pro Text | Inter |
| Screen coverage | today, track, month (+ `.detail-page` workspaces) | 18 screens |

They are **not** minified and unminified copies of one system. Token names,
token values, selector vocabulary (`.app-shell`, `.auth-page`, `.badge`,
`.record`, `.surface` exist only in B), typography, and layout model all
differ. Verified by token grep and selector-set difference.

## Finding 3 — Design B is newer and matches the required screen inventory

Design B (`0270575`) is the most recent UI commit, and its 18 screens map
almost exactly onto the required screens in
`prompts/business_activity_ledger_ui_system_agent_prompt.md` §8:

| Requirement | Design B screen |
|---|---|
| §8.1 Home / Current Activity | `index.html` |
| §8.2 Quick Start | `start.html` |
| §8.3 Stop Activity | `stop.html` |
| §8.4 Manual Entry | `manual-entry.html` |
| §8.5 Daily Timeline | `today.html` |
| §8.6 Activity Detail | `activity.html` |
| §8.7 Correction Screen | `correction.html` |
| §8.8 Void Screen | `remove.html` |
| §8.9 Restore Screen | `restore.html` |
| §8.10 Split Activity | `split.html` |
| §8.11 Merge Activities | `merge.html` |
| §8.12 Add Evidence | `evidence.html` |
| §8.13 Daily Review | `review.html` |
| §8.14 Monthly Summary | `month.html` |
| §8.15 Settings | `settings.html` |
| §19 Authentication | `sign-in.html` |
| (system states reference) | `states.html` |
| (navigation overflow) | `more.html` |

Design A covers only §8.1, §8.5, §8.14 plus generic in-page workspaces.

The commit message — "Add static business **ledger UI screens**" — and the
absence of any JavaScript in Design B are consistent with these being the
authored *design reference*, i.e. exactly the "intended screens [that] exist
only as HTML and CSS" the execution script describes.

## Chosen authority

| Concern | Authority | Rationale |
|---|---|---|
| Which screens exist | **Design B** | Newest; complete coverage of §8.1–8.15 |
| Screen structure, semantics, copy | **Design B** | Authored reference, matches requirements |
| Domain-visible states shown per screen | **Design B** | e.g. `badge-change` "Corrected once" |
| Delivery shell, PWA, service worker, manifest | **Design A** | Only A has `manifest.webmanifest`, `service-worker.js` |
| No-modal constraint (`DEC-0003`) | **Design A** | Project-owned constraint recorded in `HANDOFF.md`; Design B is already modal-free, so the two agree |
| 44px touch targets | **Both** | A fixed this (`HANDOFF.md`); B's `.button-small` must be checked against it |

Recorded as [`DF-TE-0003`](DECISIONS.md#df-te-0003).

**No screen will be recreated from imagination while a `static-ui-screens/`
counterpart exists.**

## OQ-5 — unresolved visual reconciliation

Design A and Design B are both complete and mutually inconsistent visual
languages (different palettes, typography, and layout models). Which visual
language the shipped application should use is a **design decision with no
repository evidence either way**:

- No requirements document names a palette or typeface.
- `.visual-engineering/` is cited by both prompts as the mandatory visual
  authority, and `docs/UI-VISUAL-ENGINEERING-INTERPRETATION.md` (51 lines)
  interprets it — but neither selects between A and B, because B did not
  exist when that interpretation was written (`e896ea1` precedes `0270575`).
- `HANDOFF.md` describes A as the implemented prototype but does not reject B.

This is recorded rather than silently resolved. It does **not** block domain
work: screen *inventory*, *flows*, and *domain-visible states* are taken from
Design B regardless of which palette wins, and none of the domain
requirements (TE-R-001..TE-R-113) depend on the visual language.

It **does** block final UI integration (TE-015) and should be resolved by the
user or by a `.visual-engineering` reading that postdates `0270575`.
