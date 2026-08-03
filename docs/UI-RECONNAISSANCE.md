# Business Activity Ledger UI reconnaissance

Date: 2026-08-02  
Contract: `prompts/business_activity_ledger_ui_execution_contract.md`  
Visual Engineering context: `0.14.1` from source commit `9722e3f749490e1b5efbc5910123b0a490c395a9`

## Scope inspected

Repository governance, Visual Engineering operational guidance and complete research index, current HTML/CSS/JavaScript implementation, service worker, API client, tests, UI documentation, provisional ADR bundle, ROS schemas/registries, repository context, Git history, and all Cloudflare/GitHub-ledger references were inspected before implementation changes.

## Applicable Visual Engineering principles

- One defensible primary path per view; the timer is the dominant action on Track and the chronological record is the dominant verification path on Today.
- Separate rapid orientation from deliberate verification. Effective state should lead while raw history progressively discloses.
- Group by meaning before adding containers. The existing chronological ledger is preferable to a generic dashboard-card grid.
- Preserve visual, semantic, reading, and focus order across responsive recomposition.
- Prefer native HTML and CSS-first composition; introduce components around durable semantics or bounded behavior.
- Keep location, timer state, synchronization state, errors, and recovery visible.
- Use text/shape/icon redundancy rather than color-only state.
- Verify narrow widths, 200% scaling, high contrast/forced colors, reduced motion, long content, keyboard order, and screen-reader semantics.

## Current UI state

The repository contains a dependency-light PWA prototype with hash routing, bottom navigation, semantic dialog sheets, responsive tokens, dark/light system themes, an active timer, favorites, six-minute entry, a daily timeline, activity detail, correction, void/restore, split demonstration, evidence demonstration, daily review/attestation, monthly summary, conflict demonstration, and an offline shell.

Domain calculations are in `src/domain.js`; client persistence is in `src/state.js`; interaction/rendering is concentrated in `src/app.js`; a generic fetch wrapper exists in `src/api/client.js`. Five Node unit tests cover duration/clock formatting, timestamp-derived elapsed time, pause behavior, and split totals.

The implementation is a demonstration, not a reconciled ledger client. Most UI actions mutate local state directly. Monthly values and some history/conflict content are static fixtures. Merge, export, authentication, real upload, server reconciliation, command replay, and complete empty/loading/error states are absent or only represented by copy.

## Current Cloudflare state

No Cloudflare Worker source, Wrangler configuration, environment bindings, Durable Object, D1/KV/R2 usage, deployment workflow, health endpoint, configuration endpoint, authentication bootstrap, CSRF protection, session refresh/logout, GitHub App authentication, API DTO schemas, or contract tests exist.

`LedgerClient` targets `/api/v1` and sets cookie credentials, request IDs, idempotency headers, cancellation, and a timeout. It does not implement endpoint methods, response DTO validation, retries, CSRF tokens, projection versions, base hashes, session recovery, clock-skew metadata, or structured error parsing. The application does not instantiate it.

## Current data-layer state

No Business Activity Ledger domain schema or implementation exists. Repository schemas are ROS governance/research artifact schemas only. There are no activity, timer-completion, amendment, void, restoration, split, merge, evidence-link, attestation, report, integrity-manifest, configuration, project, activity-type, or preference schemas.

There is no append-only event writer, GitHub App integration, source layout, projection engine, report generator, integrity hash/manifest, migration/version strategy, or domain validation suite. The current browser fixtures should not be confused with canonical records.

## Contradictions

1. Documentation says the Cloudflare service is authoritative, while runtime behavior writes durable-looking activity and timer state directly to `localStorage`.
2. The UI displays `Saved` without service confirmation.
3. Timer timestamps are device timestamps; no server-confirmed timestamp or clock-skew reconciliation exists.
4. Correction, void, split, evidence, and attestation flows change or imply state without creating append-only events.
5. The monthly report presents fixed totals without source commit, integrity metadata, rule version, or backend projection.
6. Offline documentation describes a command state machine, but no command queue executor exists.
7. The service worker uses network-first caching for all GET requests and does not distinguish API projections from shell assets.
8. The current context file still says no vertical slice was selected while also naming the UI prototype as active work.
9. The bundled ADR document is provisional and does not satisfy the execution contract’s per-decision journal structure.

## Missing capabilities

- Capability 0: required reconnaissance, implementation plan, capability record, and decision journal were absent before this execution.
- Capability 1: shell exists; health/config bootstrap, typed mocks/service, complete states, and broader accessibility automation do not.
- Capability 2: authentication/session UI and backend are absent.
- Capability 3: configuration/preferences are static.
- Capabilities 4–16: visual demonstrations exist for portions, but no service/data reconciliation exists; merge/export/real queue are absent.
- Capability 17: partial visual review exists; VoiceOver, automated accessibility, 200% scaling, full contrast, and browser matrix are incomplete.
- Capability 18: Cloudflare deployment, monitoring, telemetry, security review, reproducible production build, rollback validation, and full-system tests are absent.

## Accessibility concerns

- No automated accessibility tool is configured and no VoiceOver evidence exists.
- Dynamic dialogs are built from HTML strings; validation errors are toast-only and are not associated with fields.
- The timer’s `aria-live` container is re-rendered every second, which may cause excessive announcements despite the intended rule.
- Hash routing moves focus to `main` but route announcements and history semantics need assistive-technology testing.
- 200% zoom/text scaling, light-theme contrast, forced colors, localization expansion, and keyboard dialog focus restoration are not fully evidenced.
- Disabled/loading/unknown timer state is not implemented, so a second timer can be offered before service state is known.

## Security concerns

- No GitHub credential is present in browser code, which is correct.
- No authentication or authorization exists; all local users can view/change fixtures.
- No CSRF protection, secure session lifecycle, content-security policy, Trusted Types strategy, or server-side input validation exists.
- `innerHTML` templates interpolate record values. Synthetic fixtures are trusted today, but service-derived values would create an injection boundary.
- Evidence upload validation, MIME/size checks, malware scanning, signed URL expiry, privacy classification, and retention are unresolved.
- The development server has no production hardening and must not be exposed as a service.

## Architectural risks

- Continuing frontend capability work would invent contracts and deepen drift before the authoritative service/event model exists.
- Concentrating all rendering and commands in `src/app.js` will make service reconciliation and test isolation increasingly difficult.
- Persisting full activity text in localStorage creates privacy and stale-authority risk.
- A service worker caching API GETs without version-aware policy could surface stale projections as current.
- GitHub-backed writes require careful concurrency, idempotency, integrity, and latency design; treating Git commits as immediately visible would break user feedback.
- A single “complete” capability can span UI, Cloudflare operational state, ledger events, projections, schemas, and reports; changes need vertical contract tests.

## Assumptions

- The intended first production deployment is single-owner and same-origin.
- Cloudflare may use a Worker plus Durable Object for one-active-timer serialization and R2 for evidence, but these are proposals rather than accepted choices.
- GitHub App credentials and target repository are not available in this workspace and must not be invented.
- Synthetic fixtures may remain for local contract tests if visibly labeled and separated from production adapters.
- No production deployment, credential creation, GitHub mutation, or external write is authorized by the request alone.

## Recommended implementation order

1. Freeze further feature UI work and establish the domain/API contract baseline.
2. Define OpenAPI error/config/session contracts and JSON Schemas for canonical events/projections.
3. Implement a deterministic in-memory contract service for tests plus a Worker-compatible handler boundary.
4. Implement authentication/session/CSRF contracts before exposing activity data.
5. Implement configuration and the one-active-timer operational model with idempotency and concurrency tests.
6. Connect the UI to the service through endpoint-specific methods; remove authoritative-looking direct local mutations.
7. Add timer completion/incomplete-draft and manual-entry events, projections, and contract tests.
8. Add read projections, then amendments/void/restore/split/merge/evidence/attestation/reports in dependency order.
9. Add safe offline queue behavior only after every write is idempotent and versioned.
10. Complete accessibility, visual, security, deployment, rollback, and browser validation.

The next implementable slice is Capability 1’s service-contract foundation, not another visual feature.
