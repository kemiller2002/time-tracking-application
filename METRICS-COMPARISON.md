# Implementation metrics: retired JavaScript vs. current F#/WASM

Per `.sde/reference/ENGINEERING-METRICS.md`'s evidence-class rule, every
figure below is labeled and never estimated where it isn't directly
measurable:

- **SELF-REPORT** — a command run against this repository (or its git
  history) during this session, reproducible by anyone with the commands
  shown.
- **NOT OBSERVABLE** — this session has no harness-telemetry access (tool
  call counts, tokens, cost, wall-clock authoring time), so these are
  reported as not observable rather than guessed.

All commands were run from the repository root. The JS baseline is measured
at commit `da9b373` (the last commit before the F# port began; it is the
full, feature-complete pre-port implementation). The F# figures are measured
against the current working tree on `claude/time-tracking-requirements-okgq9t`.

## 1. Business-logic footprint (lines of code)

| Layer | JS implementation (commit `da9b373`) | F#/WASM implementation (current) |
|---|---|---|
| Server/domain logic | `worker/src/store.js` 118 + `worker/src/handler.js` 75 + `worker/src/errors.js` 10 = **203** | `Ledger.Domain/` (Tier 1/2): Commands.fs 608, Model.fs 280, Summary.fs 118, Diagnostics.fs 90, Services.fs 39 = **1,135** |
| Application/orchestration | *(none separate — handler.js above is both routing and orchestration)* | `Ledger.Engine/` (Tier 3): Dispatch.fs 383, DocumentCodec.fs 371, Protocol.fs 161, Projections.fs 152, Session.fs 119, Reports.fs 114 = **1,300** |
| Browser/host bridge | `src/app.js` 147 + `src/api/client.js` 42 + `src/domain.js` 34 + `src/state.js` 22 + `index.html` 57 + `src/styles.css` 6 + `src/accessibility-fixes.css` 9 = **317** | `web/`: styles.css 1,894 (see §5), index.html 425, dom-bindings.js 204, wasm-engine-transport.js 31, main.js 5 = **559** excluding CSS, **2,453** including it. Wasm shim: Program.cs 17. |
| API contract | `openapi/ledger-api.yaml` **1,301** | *(none — no network API; `Protocol.fs`'s hand-rolled JSON wire shape is the entire contract, counted above in Protocol.fs's 161)* |
| **Total (excl. contract/schema docs, excl. CSS)** | **520** (203 + 317) | **2,994** (1,135 + 1,300 + 559) |

**Reading this**: the JS total is small because it was a thin CRUD layer —
most of the business rules described in `docs/DOMAIN-REQUIREMENTS.md` were
either unenforced or partially enforced (see §4, "spec gaps closed"). The F#
port is ~5.75x larger by line count for the domain+orchestration layers
alone, which reflects rules the JS version never implemented (six-minute
billing, restore re-validation, merge contiguity, the full capability
matrix, timer pause/resume, evidence lifecycle, day/month projections,
report rendering in three formats), not the same logic rewritten in a more
verbose language. Evidence class: **SELF-REPORT** (`git show <rev>:<path> |
wc -l` and `wc -l` against the working tree, both reproducible).

## 2. Test footprint

| | JS implementation | F# implementation |
|---|---|---|
| Test files | `tests/service.test.js` 205, `tests/api-client.test.js` 25, `tests/component.test.js` 26, `tests/domain.test.js` 6, `tests/accessibility.test.js` 31 = **293 lines** | `Ledger.Domain.Specs/Program.fs` 550, `Ledger.Engine.Specs/Program.fs` 229 = **779 lines** |
| Test framework | Node's built-in `node:test` (zero dependencies) | Hand-rolled `(string * (unit -> unit)) list` runner (zero dependencies) |
| Individual specs | Not machine-counted here — Node's runner nests subtests inside larger blocks not delimited the same way F#'s flat list is; a direct count would misrepresent both suites' granularity | **63** (`Ledger.Domain.Specs` 49 + `Ledger.Engine.Specs` 14), directly counted as `("<description>", fun () -> ...)` entries |
| Coverage instrumentation | None found in `package.json` (no `nyc`/`c8`/coverage script) | None (no coverage tool wired) |
| Last run result (this session) | Not re-run — the JS implementation was deleted from the working tree in commit `21d7219`; re-running would require checking out history separately | **All 63 pass** (`dotnet run --project f-sharp/tests/Ledger.Domain.Specs`, `...Ledger.Engine.Specs`), confirmed this session |

Evidence class: **SELF-REPORT**.

## 3. Dependencies

| | JS implementation | F# implementation |
|---|---|---|
| Runtime npm dependencies | **0** (`package.json` at `da9b373` has no `dependencies`/`devDependencies` key at all) | **0** npm dependencies (`package.json` only shells out to `dotnet`/`python3`) |
| .NET/NuGet dependencies | n/a | `FSharp.Core` (SDK-implicit) only; no other NuGet packages referenced in any `.fsproj`/`.csproj` |
| Project files | 1 `package.json`, plain scripts | 5 `.fsproj`/`.csproj` + 1 `.slnx` (`Ledger.Domain`, `Ledger.Engine`, `Ledger.Wasm`, `Ledger.Domain.Specs`, `Ledger.Engine.Specs`) |
| External kernel dependency avoided | n/a | `@echelon-foundry/typescript-wasm-kernel` was planned but is an unresolvable `file:../typescript-wasm-kernel` reference in the sibling repo — never added; `web/dom-bindings.js` (204 lines) is a first-party replacement instead |

Evidence class: **SELF-REPORT** (direct inspection of `package.json` at both revisions and every `.fsproj`/`.csproj` in the tree).

## 4. Business-rule coverage (spec gaps the JS implementation had, closed by the F# port)

Directly attributable to the 3 fixes authorized when this port began, plus
what test coverage now exists for each — not a subjective quality claim,
just presence/absence of the behavior and its test:

| Rule | JS implementation (`da9b373`) | F# implementation |
|---|---|---|
| Six-minute billed-minutes figure | Not implemented — no rounding logic exists in `store.js` | `Model.fs`'s `Billing` module; 3 dedicated specs in `Ledger.Domain.Specs` |
| Restore re-validates like an amendment | Not implemented — restore only flips a boolean in `store.js` | `Commands.restore` re-runs `validateFields`; 2 dedicated specs |
| Merge requires same-date, contiguous sources | Not implemented — `store.js`'s merge has no date/contiguity check | `Commands.merge` sorts and checks contiguity before merging; 3 dedicated specs |
| `Timer` as a fourth `entry_method` | JS only recognizes `manual`\|`split`\|`merge` | `Model.fs`'s `EntryMethod` has `Manual`\|`Split`\|`Merge`\|`Timer`; full timer lifecycle spec-covered |

Evidence class: **SELF-REPORT** (direct reading of `worker/src/store.js` at `da9b373` against `Commands.fs`/`Model.fs` in the current tree).

## 5. UI source and styling

| | JS implementation | F# implementation |
|---|---|---|
| UI markup | `index.html` — **57 lines**, plain unstyled form-per-endpoint page | `web/index.html` — **425 lines**, 4-screen app shell (Today/Track/Month/More) built from `static-ui-screens/*.html` |
| Stylesheet | `src/styles.css` 6 lines + `src/accessibility-fixes.css` 9 lines = **15 lines**, minimal | `web/styles.css` — **1,894 lines**, copied wholesale from the `static-ui-screens/` design system (design tokens, full component vocabulary) plus a 39-line host addendum (`[hidden]` support, native-button resets, `.field-error`) |
| Design source | Ad hoc | `static-ui-screens/*.html` + `static-ui-screens/styles.css`, the pre-existing static mockups the user pointed to explicitly |

Evidence class: **SELF-REPORT**.

## 6. Deployed/browser artifact size (F# only — the JS implementation was a server-rendered/API-backed page with no comparable bundle step)

Measured from a clean `dotnet publish -c Release` (stale multi-build hash
accumulation removed first, so this reflects one real deployment, not this
session's accumulated rebuilds):

| | Size |
|---|---|
| This app's own compiled WASM modules (uncompressed): `Ledger.Wasm.*.wasm` + `Ledger.Engine.*.wasm` + `Ledger.Domain.*.wasm` | 5,909 + 121,621 + 156,437 = **283,967 bytes (~277 KB)** |
| Full `_framework/` publish output, uncompressed (app modules + .NET runtime + BCL + ICU data) | **14 MB** |
| Full `_framework/` publish output, Brotli-compressed (what a browser actually downloads) | **~2.45 MB** (2,565,549 bytes summed across every `.br` file) |

The ~14 MB/~2.45 MB figures are almost entirely fixed .NET-WASM-runtime and
ICU-data cost, not this app's code — they would be nearly identical for a
one-line F# WASM app on the same SDK. The 277 KB figure isolates what this
port actually added. Evidence class: **SELF-REPORT** (`du -sh`,
`stat -c%s`, and a Brotli-file-size sum, all against a single clean publish
this session).

## 7. Engineering-process metrics (agent execution)

`.sde/reference/ENGINEERING-METRICS.md`'s priority measures — search
operations, repair loops, semantic/boundary decisions, manual discoveries,
build/test attempt counts *for this session's own authoring process*,
tokens, cost, and wall-clock time — are **NOT OBSERVABLE**: this session has
no harness-telemetry access to its own tool-call history, token counts, or
cost. Reporting a number for any of these would be an estimate presented as
measurement, which the evidence-class rule this document opens with
specifically prohibits. The only process facts directly observable are the
commands actually run and their outcomes, which are the SELF-REPORT figures
in §1-6 and the build/test results already shown in §2.

## Summary

The JS implementation was a thin, largely-unvalidated CRUD layer (520 lines
of business+bridge logic, 0 dependencies, an OpenAPI contract 2.5x larger
than the code it described, and at least 4 undertested or unimplemented
business rules). The F# port is substantially larger (2,994 lines across
Domain+Engine+bridge) but adds real rule coverage the JS version lacked,
runs entirely client-side with 0 npm dependencies, and its actual
application code compiles to under 280 KB of WASM — the multi-megabyte
download is .NET's fixed WASM-runtime cost, not this app's.

## Addendum: GitHub-backed sync feature (PRs #17, #18)

Added after the above was written, across a single conversation's worth of
follow-up requests: GitHub-backed persistence for the ledger, confined to a
per-person folder, with round-tripped settings and (separately) a first CI
workflow for the repo. All figures below are **SELF-REPORT** — commands run
this session against this repository's own git history — unless marked
otherwise.

### Development shape

Four incremental commits on one branch, each a direct response to a
follow-up request in the same conversation, squash-merged as PR #17:

| Commit | What it added | Files changed | Lines |
|---|---|---|---|
| `b555839` | Base sync: `Protocol.fs` `Http` shapes, `GitHubSync.fs`, config/pull/push, UI panel | 11 | +606 / -82 |
| `1d8b43f` | Folder confinement (`<folder>/ledger.json`, never repo root) | 8 | +88 / -37 |
| `208b7e8` | Per-person segregation (`<folder>/<login>/`) + `metadata.json` | 8 | +407 / -105 |
| `ca3d678` | Settings round-trip (`settings.json`) + GitHub-config `localStorage` persistence | 8 | +348 / -78 |
| **Total (PR #17, squashed)** | | **11** | **+1,238 / -91** |

`GitHubSync.fs` — the module owning all GitHub REST API translation — is
**202 lines**, written new for this feature (`f-sharp/src/Ledger.Engine/`
had 6 files before it existed).

### Test growth

| | Before (post-#16) | After (post-#17) | Change |
|---|---|---|---|
| `Ledger.Domain.Specs` | 49 | 49 | +0 (no Domain/business-rule changes — this was Tier 3/4 work only) |
| `Ledger.Engine.Specs` | 14 | 38 | **+24** |
| **Total F# specs** | 63 | 87 | **+24** |

Evidence class: **SELF-REPORT** (`dotnet run --project f-sharp/tests/...`,
re-run and passing at each of the four commits and again after the squash
merge).

### Follow-on: CI (PR #18)

A separate, smaller change once the above landed: this repository had **zero
configured CI checks** at every point checked this session
(`get_check_runs` returned `total_count: 0` on PRs #16 and #17 alike) — all
verification was manual, run by hand in whatever session made a change.
`.github/workflows/fsharp-specs.yml` (28 lines, new) runs this repo's own
documented `npm run test:fsharp` command on every push/PR. Scoped
deliberately to the two spec projects, not a full `npm run build:wasm`
(needs the Emscripten workload installed — a heavier job, not built this
pass). This is the first time this repository has had automated,
merge-blocking-capable test execution independent of a human or agent
remembering to run it by hand.

### Not measured this pass

- **WASM artifact size after the sync feature** — a clean publish was run
  repeatedly during development (to confirm the build stayed green after
  each change) but its output size was not re-recorded against the §6
  baseline above; the feature adds no new WASM-side dependencies (only
  `System.Text.Json.Nodes`, already linked in via `Protocol.fs`), so no
  material change is expected, but this is an assumption, not a
  measurement. Evidence class: **NOT OBSERVABLE** (not measured, not
  estimated).
- **Engineering-process metrics** (search operations, repair loops, tokens,
  cost, wall-clock authoring time) — unchanged from §7's original
  reasoning: this session has no harness-telemetry access to its own
  execution history. Evidence class: **NOT OBSERVABLE**.
