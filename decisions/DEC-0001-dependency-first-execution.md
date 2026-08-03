# Decision — Dependency-first execution

## Status

Accepted

## Context

The UI prototype is ahead of the absent Cloudflare and append-only ledger implementations. The execution contract prohibits independent evolution.

## Decision

Freeze additional feature UI work until a versioned service/error/config contract and domain schema baseline exist. Complete capabilities as reconciled vertical slices.

## Alternatives Considered

Continue polishing frontend demonstrations; invent the entire production backend implicitly; or stop permanently. Frontend-only work deepens drift, implicit production choices exceed evidence, and permanent stoppage is unnecessary because local contract foundations are implementable.

## Evidence

Repository audit documented in `docs/UI-RECONNAISSANCE.md`.

## Assumptions

Deterministic local service/ledger adapters are acceptable for contract testing before production credentials exist.

## Consequences

Capability progress will appear slower but each completed slice will have explicit authority, validation, and migration boundaries.

## Risks

The first contract choices may need revision once Cloudflare/GitHub operational evidence is available.

## Revisit When

The Cloudflare and ledger baseline exists or production constraints contradict the proposed boundaries.

## Related Capabilities

CAP-000 and Capabilities 1–18.
