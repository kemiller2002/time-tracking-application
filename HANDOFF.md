# Time Tracking Application handoff

## Objective

Implement the first mobile-first Business Activity Ledger UI slice from `prompts/business_activity_ledger_ui_system_agent_prompt.md`.

## Current state

- Functional dependency-light PWA prototype implemented with synthetic local records.
- Timer, manual entry, correction, void/restore, split validation, evidence, history, review/attestation, reports, conflict, and Shortcut guide are demonstrable.
- Cloudflare API boundary exists, but no backend, real authentication, or evidence upload is connected.
- Visual interpretation, architecture, decisions, state/offline/accessibility/test/runbook, and visual review are documented under `docs/`.
- The stricter execution contract has completed Capabilities 0 and 1 locally. Capability 1 has health/config/project/type contracts, schemas, a Worker-compatible handler, client methods, visible demo authority, no modal workflows, and passing unit/component/integration/accessibility-baseline/contract/build checks.
- User direction forbids modals. All active modal workflows were replaced by full in-page detail workspaces; `DEC-0003` records the constraint.

## Validation

Run:

```bash
./ros registry check
./ros validate
npm run check
```

Observed 2026-08-02: 5/5 Node tests passed; syntax check passed; ROS registries were current; ROS validation passed. Browser review at 320 and 390-class mobile widths found no content overflow or console errors. The start/pause/resume/stop journey worked. A 32 px brand-link target was fixed to 44 px.

Execution-contract validation later on 2026-08-02: 5 unit and 3 contract tests passed; typecheck, lint, build, baseline schema validation, ROS registry check, and ROS validation passed. The in-app browser verified zero active dialogs/modals and correct in-page Back focus, but blocked local `/api/v1/*` browser access; curl and contract tests confirmed HTTP 200 behavior.

## Unresolved questions

1. What exact Cloudflare request/response schemas and optimistic version contract are accepted?
2. Which authentication option and CSRF model will production use?
3. What evidence file limits, scanning, retention, privacy classification, and signed-upload flow apply?
4. How does the design perform in real iPhone Safari/VoiceOver task testing?

## Next action

Choose the Capability 2 identity boundary—recommended Cloudflare Access plus short-lived secure application session/CSRF protection—before implementing authentication. Do not advance feature UI independently.
