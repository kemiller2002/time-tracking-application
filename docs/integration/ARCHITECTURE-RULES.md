# Permanent Architecture Rules — Chrona Integration

These five rules, from the "Chrona Integration Assembly and Time
Observation Processing" specification's closing section, are permanent —
they govern every future change to inbound integration, not just the
CHR-INT-001 through -018 work that first implemented it. They are recorded
here rather than restated per-PR.

## 1. Inbound Integration Ownership

Chrona (the receiver) owns every contract describing what an external
system may submit to it. `EchelonFoundry.Chrona.Integration`
(`f-sharp/src/Chrona.Integration/`) is the single published surface a
producer builds against; no producer independently reproduces Chrona's
wire format by hand. See [`INTEGRATION-CONTRACT.md`](./INTEGRATION-CONTRACT.md)'s
"Ownership" section.

A future change to what Chrona accepts is made by changing this package
(and, if the wire shape changes incompatibly, its `ContractVersion` — see
"Versioning" below) — never by a producer inventing an undocumented field
or path convention Chrona happens to tolerate.

## 2. Immutable External Evidence

Once written, a `TimeObservation` file in a project's inbox is never
edited or deleted by Chrona. Processing outcomes are recorded in a
separate, equally immutable `ProcessingReceipt` (specification §16) —
never by mutating the source observation, and never by deleting it once
processed. This is what makes reconciliation idempotent (rule 4) and
keeps the inbox itself an audit trail: what a producer submitted, and
when, is never rewritten by the receiver's own processing of it.

## 3. Receiver Authority

An accepted `TimeObservation` becomes, at most, a `TimeCandidate`
(`Ledger.Engine/Integration.fs`) — never directly a `Ledger.Domain.Model.
Activity`. A `TimeCandidate` is not authoritative time; only a human
decision (accepting, modifying, or rejecting the candidate through
Chrona's own UI, via the existing `Ledger.Domain.Commands.create`) can
make it so. There is no auto-accept path, in V1 or otherwise, without a
deliberate, separately-reviewed change to this rule. See
`Integration.CandidateState` and `ObservationResult`'s doc comments for
why these two outcomes are kept structurally distinct.

## 4. Idempotent Reconciliation

Reconciling the same observation any number of times — across retried
delivery, repeated startups, or two clients racing — must always converge
to exactly one candidate and one receipt per `ObservationId`, never more.
This rests on three things staying true together, all proved by test
(CHR-INT-014 for the pure decision, CHR-INT-015/017 for the actual
`Dispatch.fs` wiring):

- **Deterministic candidate identity** (`candidate:<observationId>`) —
  two independent reconciliation passes over the same observation always
  compute the same candidate id, with nothing to look up first.
- **Critical write ordering** — a candidate is always persisted before
  its receipt is ever attempted; a failed or unconfirmed candidate write
  is never followed by a receipt write (see rule 2 — a receipt asserts
  something has already durably happened).
- **Create-only writes** — both the candidate and receipt paths are
  written with `sha = None`, so GitHub's Contents API itself rejects a
  second write to either path (409/422) rather than one client silently
  overwriting another's. A rejected create because the path already
  exists is treated as success-equivalent (the exact deterministic
  content that write would have produced already exists), never as an
  error to retry.

## 5. Complexity Requires Evidence

Nothing in this integration surface — a new field, a new path
convention, a new processing state — is added because it might be needed
later. Every addition in CHR-INT-001 through -018 traces to a concrete
requirement in the specification or a concrete gap found while
implementing it (documented, where relevant, in
[`INTEGRATION-CONTRACT.md`](./INTEGRATION-CONTRACT.md)'s vocabulary-
reconciliation table). A future change proposing new complexity here —
an additional contract field, a new processing state, an auto-accept
path — needs the same standard: a concrete, cited requirement, not
speculative future-proofing.
