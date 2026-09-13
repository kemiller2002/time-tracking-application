# CHR-INT-005 — Serialization Compatibility

## Fixtures

`tests/Chrona.Integration.Tests/fixtures/v1/` holds golden JSON payloads
for `TimeObservation` contract version `"1"`:

| Fixture | Exercises |
|---|---|
| `minimal.json` | Only the required fields, no optional ones, a duration-only time representation. |
| `interval.json` | A full interval (`startedAt`/`endedAt`), matching the specification's own worked example (§12). |
| `duration.json` | A duration-only observation with a work item and actor present. |
| `with-work-item.json` | `workItemId` present, no evidence. |
| `with-evidence.json` | One or more `Evidence` entries. |

Every fixture must, forever, for as long as contract version `1` is
supported:

1. **Deserialize successfully** via `TimeObservation.deserialize`.
2. **Round-trip**: `deserialize >> serialize >> deserialize` produces an
   equal `TimeObservation` value to the first `deserialize`.
3. **Pass validation** implied by successful `deserialize` (deserialize
   internally calls the same `create` validation every hand-constructed
   observation goes through).

Adding a new optional field to the V1 contract must not require changing
any existing fixture's expected result — a fixture that predates the new
field is exactly the "old producer, newer package" case compatibility
testing exists to protect. If a fixture's expected behavior would have to
change, the change is not compatible with V1 and needs a new contract
version instead.

## What the test suite additionally covers (not fixture-based)

Beyond the golden fixtures, `Program.fs` unit-tests, without any GitHub
or WASM dependency:

- `serialize → deserialize` round-trips for a hand-constructed
  `TimeObservation` covering every optional field being present and
  every optional field being absent.
- Every structural validation error `TimeObservation.create` can produce
  (missing each required ID, an interval with `end < start`, a
  non-positive duration, both an interval and a duration given together,
  neither given, only one instant of an interval given).
- Deserializing an unsupported `contractVersion` produces
  `UnsupportedContractVersion`, not a best-effort guess at the payload's
  meaning.
- Deserializing malformed JSON produces `MalformedPayload`, not an
  unhandled exception.
- An unrecognized extra JSON field is silently ignored by `deserialize`
  (forward-compatible with a *future* contract version's additive
  fields, as long as the required V1 fields are still present and the
  declared `contractVersion` is still `"1"`).
- `StorageConvention.observationInboxPath` produces a stable, safe path
  for both ordinary and path-hostile `ObservationId`/`ProjectId` values
  (containing `/`, `..`, spaces, or other non-`[A-Za-z0-9._-]`
  characters), and rejects blank segments.

## External-consumer simulation (CHR-INT-018)

Before packing `EchelonFoundry.Chrona.Integration` 1.0.0, a throwaway
console app referencing *only* the packed `.nupkg` (via a local NuGet
feed, no project reference) confirmed the package is usable standalone:
construct → serialize → deserialize round-trips, the repo's own
`interval.json` fixture deserializes identically via the packaged
assembly, `StorageConvention.observationInboxPath` computes correctly
from outside the repo, and structural validation still rejects an
invalid observation. See `INTEGRATION-CONTRACT.md`'s CHR-INT-018 entry.

## Running

```
dotnet run --project f-sharp/tests/Chrona.Integration.Tests
```

Also runs in CI via `.github/workflows/chrona-integration-ci.yml`,
path-filtered to `src/Chrona.Integration/**` and
`tests/Chrona.Integration.Tests/**` so it never gates unrelated changes
to the rest of Chrona (and the existing `fsharp-specs.yml` workflow never
gates changes to `Chrona.Integration`, for the same reason).
