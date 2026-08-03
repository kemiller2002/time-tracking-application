# Decision — Prototype authority boundary

## Status

Accepted

## Context

The browser currently persists synthetic timer/activity data locally and presents authoritative-looking saved/report states.

## Decision

Treat all current local records as explicit demonstration fixtures. Production authority belongs only to server-confirmed Cloudflare projections backed by append-only ledger records. Browser storage may hold cached projections, drafts, timer continuity, and queued commands, but never silently become canonical.

## Alternatives Considered

Use localStorage as the ledger, let the browser write GitHub directly, or retain ambiguous dual authority. All violate the intended security/integrity boundary.

## Evidence

`src/state.js`, `src/app.js`, and the missing Worker/domain implementation documented in reconnaissance.

## Assumptions

The Cloudflare service will return projection versions, server timestamps, and confirmation states.

## Consequences

The UI adapter must be refactored before durable commands are enabled. Demonstration mode must remain distinguishable.

## Risks

Cached data can still mislead if sync state is hidden or service-worker caching is overly broad.

## Revisit When

Offline-first requirements demand a stronger local source model, or the Cloudflare service contract is accepted.

## Related Capabilities

CAP-000; Capabilities 1, 4–16.
