# Business Activity Ledger UI implementation plan

## Objective

Execute `business_activity_ledger_ui_execution_contract.md` in capability order without allowing the UI, Cloudflare service, and append-only ledger contracts to drift.

## Completion policy

A capability is `complete` only when UI, service, event/schema/projection behavior, tests, accessibility and visual evidence, documentation, decision record, regression validation, and capability record all pass. `Prototype present` is not equivalent to complete. Missing credentials may block production deployment but must not block deterministic local contract tests.

## Phase A — Contract foundation

### Capability 0: architecture validation

Deliver reconnaissance, this plan, `CAP-000`, and initial decision records. Validate repository state. Gate: complete when contradictions, assumptions, dependencies, and order are explicit.

### Capability 1: UI/service foundation

Define versioned OpenAPI foundations for health, config, standard success/error envelopes, request IDs, projection versions, and authentication bootstrap. Define domain configuration/project/activity-type schemas. Add a Worker-compatible handler with deterministic in-memory bindings for tests. Add endpoint-specific API client methods and runtime validators. Wire UI bootstrap states without yet enabling durable record commands.

Gate: 320 px shell and navigation; health/config mock contract tests; loading/empty/error/offline states; no secrets/direct GitHub calls; unit, integration, accessibility, syntax, schema, and production asset checks recorded.

### Capability 2: authentication and sessions

Define secure cookie, CSRF, refresh, logout, unauthorized, expired, and reauthentication contracts. Implement protected route/bootstrap UI and contract tests. Do not use localStorage for secrets.

### Capability 3: configuration and preferences

Separate GitHub-durable domain configuration, Cloudflare operational preferences, and browser-only appearance preferences. Implement selectors/favorites/timezone with server versions and validation.

## Phase B — Recording core

### Capability 4: active timer

Use a serialized Cloudflare operational-state abstraction, proposed as a single-owner Durable Object subject to decision review. Implement idempotent start/pause/resume/stop, authoritative timestamps, base versions, duplicate-stop protection, reload/background reconciliation, clock-skew warnings, and two-client tests. No GitHub event is written until timer completion.

### Capability 5: stopped activity completion

Define recoverable stopped-but-incomplete operational drafts, finalization command, immutable completion event, and “save and start another.” Never discard stopped time.

### Capability 6: manual entry

Define exact-duration/reconstructed-entry schema, timezone and overlap validation, reason rules, and entry-method projection. Six-minute buttons set exact values and never round.

## Phase C — Trustworthy projections and corrections

Implement Capability 7 day projection before Capability 8 detail/history. Then implement amendment, void/restore, split/merge, and evidence events in Capabilities 9–12. Every write includes request ID and base projection hash. Every transformation has duration/no-double-counting/cycle/evidence invariants and report regression tests.

## Phase D — Review, reports, and reliability

Capability 13 adds projection-bound attestation and supersession. Capability 14 adds exact monthly projections with rule/source/integrity metadata. Capability 15 adds generated report history and iPhone-safe JSON/Markdown/CSV downloads. Capability 16 adds the persisted command queue only after all write contracts are idempotent, versioned, and conflict-safe.

## Phase E — production quality

Capability 17 completes automated/manual accessibility and visual review across specified widths, scaling, color modes, VoiceOver, keyboard, and browsers. Capability 18 adds environment/deployment configuration, monitoring, privacy telemetry, CSP/security review, GitHub App/R2 production bindings, recovery/rollback, deployment validation, and full-system tests.

## Proposed repository structure

```text
openapi/ledger-api.yaml
schemas/domain/*.schema.json
schemas/api/*.schema.json
src/api/{client,contracts,validation}.js
src/features/*
src/offline/*
worker/src/{handler,auth,timer,commands,projections,reports}.js
worker/test/*
ledger/{events,projections,integrity}.js
tests/{unit,contract,integration,accessibility,e2e}/*
decisions/DEC-*.md
docs/capability-records/CAP-*.md
```

## Validation commands to establish

- `npm run test:unit`
- `npm run test:contract`
- `npm run test:integration`
- `npm run test:accessibility`
- `npm run test:e2e`
- `npm run typecheck`
- `npm run lint`
- `npm run build`
- `npm run validate:schemas`
- `npm run validate:all`
- `./ros registry check && ./ros validate`

Until those scripts exist, each capability record must explicitly mark the check unavailable rather than infer success.

## First execution slice

The first slice after Capability 0 is Capability 1: health/config/error contracts, schemas, a Worker-compatible in-memory adapter, client methods, and UI bootstrap. It deliberately excludes authentication completion and all durable activity writes.

Current status: complete for the local contract foundation. Unit, component, integration, accessibility-baseline, contract, syntax, lint, build, schema, and repository checks pass. The in-app test browser blocks localhost service fetches; this environment limitation is recorded, while the actual client/handler path passes contract tests. Capability 2 requires an accepted identity/session strategy.

## Production authority required later

The user or system owner must provide or approve the Cloudflare account/project, domains/environments, GitHub App and target repository, session identity provider, evidence storage/retention policy, and production deployment authority. These inputs are not required for local contract implementation but are required for Capability 18 completion.
