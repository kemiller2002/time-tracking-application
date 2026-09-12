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
| GitHub persistence boundary | `domain/TimeEntry.Persistence/` | 4 | `domain/manifest.md` | Document shape, mapping, layout, JSON. API adapter still outstanding (WI-0025) |
| GitHub effect interpreter | `domain/TimeEntry.GitHub/` | 4 | `domain/manifest.md` | Store port, concurrency, atomic writes. HTTP impl outstanding (WI-0027) |
| F# browser kernel | `browser/TimeEntry.Kernel/` | 3/4 | `domain/manifest.md` | JSON in, view state out; all logic |
| WASM interop shim | `browser/TimeEntry.Host/` | 4 | `domain/manifest.md` | The only C#; carries `[JSExport]`, forwards strings |
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
| `docs/time-entry/DECISIONS.md` | `DF-TE-0001..0008` (all five open questions resolved) |
| `docs/time-entry/UI-PROVENANCE.md` | Git-history screen provenance |
| `docs/time-entry/TOOLCHAIN-BLOCKER.md` | Toolchain: resolved, plus a correction to its own earlier conclusion |
| `prompts/business_activity_ledger_ui_*.md` | Source requirements authority |

## Current execution state

Tiers 1–3 are implemented in F# and **verified**: `dotnet build` passes with
0 warnings under warnings-as-errors, `dotnet test` passes 67/67, and the
tier-boundary check reports 0 violations. .NET SDK 8.0.131 installs from the
Ubuntu archive (`apt-get install dotnet-sdk-8.0`); the earlier claim that no
SDK was obtainable was wrong and is corrected in
`docs/time-entry/TOOLCHAIN-BLOCKER.md`.

Tier 4 is implemented end to end. Persistence boundary, GitHub store port and
effect interpreter, HTTP transport, and the WASM browser kernel all build and
are verified — including the full architectural path in a real browser
(`npm run check:browser`).

Two claims in earlier versions of this file were wrong and are corrected in
`docs/time-entry/TOOLCHAIN-BLOCKER.md`: neither the .NET SDK nor the
`wasm-tools` workload was ever unobtainable. Both were asserted blocked from
the failure of one delivery channel without testing apt or NuGet.

Outstanding: the transport's write path is unexercised (WI-0028, needs a
write-capable token), the remaining 17 screens are not integrated (WI-0021),
and serialization is not trim-safe (WI-0030).

The JavaScript prototype in `src/` remains the only runnable application. It
is **not** the domain authority (TE-R-090) and is retained as working
software, not as a target to extend.
