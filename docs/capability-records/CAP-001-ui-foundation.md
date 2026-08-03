# Capability 1 — UI foundation

Status: complete for the local contract foundation; production reconciliation remains Capability 18

## Goal

Create and reconcile the mobile-first application foundation with a versioned service/configuration contract.

## Preconditions

Capability 0 documents and decisions existed. No production credentials or deployment authority were required for local contract work.

## UI Work Completed

- Retained the 320 px mobile shell, routing, navigation, semantic design tokens, light/dark system themes, focus rules, reduced motion, forced colors, and 44 px controls.
- Added visible `Demo data` labeling and replaced authoritative `Saved` language with `Demo fixture`.
- Added service bootstrap status to the synchronization control.
- Replaced all active modal workflows with full in-page detail workspaces and explicit Back/focus restoration.

## Cloudflare Changes

- Added a Worker-compatible fetch handler for `GET /api/v1/health`, `/config`, `/projects`, and `/activity-types`.
- Added standard v1 response metadata and structured error responses.
- Added the handler to the local development server.
- Added endpoint-specific client methods and response-envelope validation.
- Explicitly excluded `/api/*` from service-worker caching.

This is a deterministic local adapter, not a deployed Cloudflare Worker.

## Data-Layer Changes

Added versioned configuration, project, and activity-type projection fixtures. No GitHub-backed canonical write path was created.

## Schema Changes

Added JSON Schemas for client configuration, project projection, and activity-type projection, plus an OpenAPI 3.1 foundation.

## Validation Changes

Added baseline schema validation, syntax/typecheck aliases, lint alias, production asset copy, contract-test script, and unit-test script.

## Tests Added

Three API contract tests cover health/config envelopes, versioned project/type projections, and structured missing-resource errors.

Exact results:

- `npm run test:unit` — 5 passed, 0 failed.
- `npm run test:component` — 3 passed, 0 failed.
- `npm run test:integration` — 6 passed, 0 failed.
- `npm run test:accessibility` — 4 passed, 0 failed.
- `npm run test:contract` — 3 passed, 0 failed.
- `npm run typecheck` — passed.
- `npm run lint` — passed.
- `npm run build` — passed; production assets copied to `dist/`.
- `npm run validate:schemas` — passed; 3 domain schemas parsed and passed baseline structural validation.
- `npm run check` — passed; 15 tests passed, 0 failed.
- `./ros registry check` — passed.
- `./ros validate` — passed.

Full cross-browser E2E and assistive-technology automation remain unavailable; the executable accessibility suite is a source/semantic baseline, not a WCAG conformance audit.

## Accessibility Review

At 320 px, the semantic snapshot retained landmarks/headings/navigation, no visible interactive target was below 44 px, focus moved to the in-page Back action, and no modal/dialog element existed. Automated accessibility and VoiceOver checks remain unavailable.

## Visual Review

The `Demo data` label fits the mobile masthead; chronology and hierarchy remain intact at 320 px; there was no page content overflow beyond the viewport width. The detail workflow now reads as a page rather than an overlay.

## Decisions Recorded

- `DEC-0001` dependency-first execution.
- `DEC-0002` prototype authority boundary.
- `DEC-0003` no modal workflows.

## Known Limitations

- The in-app browser blocks local service fetches, so browser-level bootstrap reports unavailable. Command-line HTTP probes and the real browser client against the Worker handler in contract tests pass. This is a test-environment limitation, not evidence of production reachability.
- Accessibility automation is baseline/static rather than a complete WCAG scanner or VoiceOver run.
- No authentication, production Worker deployment, or canonical GitHub-backed data exists.
- Existing feature demonstrations still mutate local fixtures; they are labeled non-canonical.

## Risks

The generic OpenAPI foundation is not yet complete enough for code generation or production validation. Baseline schema validation parses roots but is not a full Draft 2020-12 validator.

## Evidence of Completion

Implementation, schemas, OpenAPI file, contract tests, command output above, 320 px screenshot inspection, semantic snapshot, and no-modal workspace interaction check.

## Living Data Contract Review

1. New endpoints: yes — health, config, projects, activity types; resolved in OpenAPI/handler/client/tests.
2. Changed endpoints: no.
3. New fields: yes — v1 metadata, projection versions, display/features; resolved in schemas/contracts.
4. New error codes: yes — `method_not_allowed`, `not_found`; resolved in handler/OpenAPI foundation.
5. New event types: no.
6. Schema changes: yes — configuration/project/activity-type; resolved.
7. New validation: yes — response-envelope and baseline schema validation; resolved.
8. Projection behavior: yes — versioned configuration lists; resolved locally.
9. Report behavior: no.
10. Configuration: yes — timezone/display/features; resolved.
11. Security control: yes — `no-store` service responses and browser credential scan; authentication/CSRF belong to Capability 2.
12. Tests: yes — added and passing.
13. Migration: no canonical data exists.
14. Earlier flaw: yes — API caching and ambiguous demo authority; both corrected.

## Architecture Challenge

The dependency-first architecture remains simplest. Operational and durable state are still separated. No browser-to-GitHub path exists. Local fixtures are now visibly non-canonical. Offline behavior is not claimed complete. The major unresolved boundary is identity/session design, which belongs to Capability 2.

## Next Capability

Capability 2 — authentication and sessions. It requires an accepted identity/session strategy before implementation can be called secure.
