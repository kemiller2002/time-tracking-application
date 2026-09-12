# Time Tracking Application current state

## Repository status

Newly initialized with Repository Operating System 1.0.0.

## Observed facts

- No domain evidence has been accepted.
- No vertical slice has been selected.
- No discipline-boundary claim has been tested.

## Assumptions

- A small, concrete communication problem can exercise the operating model.

## Active work

The first bounded slice is a mobile-first Business Activity Ledger UI prototype. It demonstrates timer, six-minute entry, effective-record review/correction, evidence, daily attestation, monthly summary, offline status, and stale-conflict behavior. Production Cloudflare persistence is not implemented.

The ledger's business rules are now recorded in `docs/DOMAIN-REQUIREMENTS.md`, reconciled from the `time-entry-state-machine` repository's C#/F# domain reconstructions. Before this document existed, no domain data layer or business-rule reference existed in this repository at all (see `docs/capability-records/CAP-000-architecture-validation.md`); schemas under `schemas/domain/` still model only configuration projections (Project, Activity Type), not activities, tags, evidence, or the lifecycle/validation rules governing them.

## Largest decision-relevant unknown

Whether the proposed `/api/v1` Cloudflare command and projection contract, authentication model, and evidence-upload boundary work against a real append-only backend under iPhone connectivity constraints.

## Baseline

Not yet recorded. Define how the same slice would be approached without ROS and
which comparison measures are feasible.
