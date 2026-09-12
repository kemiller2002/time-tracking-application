# Repository Semantic Map

Keep this file small. It routes work to semantic areas and manifests; it does
not restate their rules.

| Semantic area / feature | Purpose | Location | Manifest | Notes |
|---|---|---|---|---|
| Business Activity Ledger | Record, correct, and report on the owner's business activities (timer, manual entry, amend, void/restore, split/merge, evidence, daily attestation, reports) | `f-sharp/` (Tiers 1–3), `web/` (Tier 4 browser host) | [`manifest.md`](manifest.md) | The only application feature in this repository; everything else below is governance/process scaffolding, not product code |

## Repository-wide composition

- Composition/root entry point: `web/main.js` (wires `WasmEngineTransport` + `DomBindings`, then calls `bindings.start()`)
- Shared contracts: `f-sharp/src/Ledger.Engine/Protocol.fs` (the WASM↔browser wire protocol — the only contract crossing a Tier 3/4 boundary in this repository)
- Architecture checks: none automated yet (`dotnet build` project-reference direction is the only mechanical enforcement today — `Ledger.Domain` has no `ProjectReference` to `Ledger.Engine` or `Ledger.Wasm`, so Tier 1/2 cannot import Tier 3/4 even accidentally; there is no equivalent of `check-semantic-architecture.sh` yet)
- Boundary checks: none automated yet — `f-sharp/tests/Ledger.Engine.Specs` exercises `Dispatch.handle` with raw JSON strings, which is the closest thing to a boundary/contract test today, but it is a behavior test, not a standalone agreement check
- SDE distribution: `.sde/` (installed via `npx @echelon-foundry/sde init`; do not edit directly — see `.sde/README.md`)

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| `framework/`, `.visual-engineering/`, `docs/00-governance/`, `decisions/`, `missions/`, `registries/`, `research/`, `templates/`, `ros`/`ros.json`, `tools/ros_cli.mjs` | ROS (Repository Operating System) process/governance scaffolding, unrelated to the ledger's own product semantics — not a feature of the application itself |
| `static-ui-screens/` | Static visual-design reference only (not executable, not wired to any runtime) — source material `web/index.html`/`web/styles.css` were built from, not a semantic area of its own |
| `.sde/` | Installed methodology inputs, versioned by the `@echelon-foundry/sde` package itself — not project code |
