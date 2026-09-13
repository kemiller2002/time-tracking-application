open System
open System.IO
open EchelonFoundry.Chrona.Integration

let assertTrue condition message = if not condition then failwith message

let fixturesDir = Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "v1")
let readFixture (name: string) = File.ReadAllText(Path.Combine(fixturesDir, name))

let observedAt = DateTimeOffset.Parse "2026-09-13T14:03:00-04:00"
let startedAt = DateTimeOffset.Parse "2026-09-13T13:10:00-04:00"
let endedAt = DateTimeOffset.Parse "2026-09-13T14:02:00-04:00"

/// A minimal, always-valid duration-only observation — the baseline every
/// validation-error test perturbs one field of.
let validDurationOnly () =
    TimeObservation.create "ros:activity:test-001" "ros" "echelon-foundry" "strata" None None None None (Some 30) None [] observedAt

let expectErrors (result: Result<TimeObservation, TimeObservation.Error list>) (expected: TimeObservation.Error list) (message: string) =
    match result with
    | Error errors -> assertTrue (errors = expected) $"{message} — expected {expected}, got {errors}"
    | Ok _ -> failwith $"{message} — expected a rejection, got success"

let tests : (string * (unit -> unit)) list =
    [
      // --- Constructor validation ---------------------------------------

      "create succeeds for a duration-only observation with no optional fields", fun () ->
        match validDurationOnly () with
        | Ok observation ->
            assertTrue (observation.ContractVersion = TimeObservation.CurrentContractVersion) "ContractVersion was not stamped to the current version"
            assertTrue (observation.DurationMinutes = Some 30) "DurationMinutes was not preserved"
            assertTrue observation.StartedAt.IsNone "a duration-only observation should have no StartedAt"
        | Error errors -> failwith $"expected success, got {errors}"

      "create succeeds for a full interval observation with every optional field present", fun () ->
        let evidence = [ { Kind = "github-commit"; Reference = "abc123" } ]
        match TimeObservation.create "ros:activity:test-002" "ros" "echelon-foundry" "strata" (Some "STRATA-142") (Some "kevin") (Some startedAt) (Some endedAt) None (Some "Implemented mapping") evidence observedAt with
        | Ok observation ->
            assertTrue (observation.StartedAt = Some startedAt && observation.EndedAt = Some endedAt) "the interval was not preserved"
            assertTrue (observation.Evidence = evidence) "evidence was not preserved"
        | Error errors -> failwith $"expected success, got {errors}"

      "create rejects a blank observationId", fun () ->
        expectErrors (TimeObservation.create "" "ros" "echelon-foundry" "strata" None None None None (Some 30) None [] observedAt) [ TimeObservation.MissingObservationId ] "blank observationId"

      "create rejects a whitespace-only observationId", fun () ->
        expectErrors (TimeObservation.create "   " "ros" "echelon-foundry" "strata" None None None None (Some 30) None [] observedAt) [ TimeObservation.MissingObservationId ] "whitespace-only observationId"

      "create rejects a blank sourceSystem", fun () ->
        expectErrors (TimeObservation.create "id" "" "echelon-foundry" "strata" None None None None (Some 30) None [] observedAt) [ TimeObservation.MissingSourceSystem ] "blank sourceSystem"

      "create rejects a blank organizationId", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "" "strata" None None None None (Some 30) None [] observedAt) [ TimeObservation.MissingOrganizationId ] "blank organizationId"

      "create rejects a blank projectId", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "" None None None None (Some 30) None [] observedAt) [ TimeObservation.MissingProjectId ] "blank projectId"

      "create accumulates multiple structural errors at once", fun () ->
        expectErrors
            (TimeObservation.create "" "ros" "" "strata" None None None None (Some 30) None [] observedAt)
            [ TimeObservation.MissingObservationId; TimeObservation.MissingOrganizationId ]
            "multiple blank fields"

      "create rejects an interval where endedAt is before startedAt", fun () ->
        expectErrors
            (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None (Some endedAt) (Some startedAt) None None [] observedAt)
            [ TimeObservation.InvalidTimeRange ]
            "endedAt before startedAt"

      "create accepts a zero-length interval (endedAt equal to startedAt)", fun () ->
        match TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None (Some startedAt) (Some startedAt) None None [] observedAt with
        | Ok _ -> ()
        | Error errors -> failwith $"expected a zero-length interval to be legal, got {errors}"

      "create rejects a zero duration", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None None None (Some 0) None [] observedAt) [ TimeObservation.InvalidDuration ] "zero duration"

      "create rejects a negative duration", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None None None (Some -5) None [] observedAt) [ TimeObservation.InvalidDuration ] "negative duration"

      "create rejects both an interval and a duration given together", fun () ->
        expectErrors
            (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None (Some startedAt) (Some endedAt) (Some 90) None [] observedAt)
            [ TimeObservation.AmbiguousTimeRepresentation ]
            "interval + duration together"

      "create rejects neither an interval nor a duration", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None None None None None [] observedAt) [ TimeObservation.MissingTimeRepresentation ] "no time representation"

      "create rejects a lone startedAt with no endedAt or duration", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None (Some startedAt) None None None [] observedAt) [ TimeObservation.InvalidTimeRange ] "lone startedAt"

      "create rejects a lone endedAt with no startedAt or duration", fun () ->
        expectErrors (TimeObservation.create "id" "ros" "echelon-foundry" "strata" None None None (Some endedAt) None None [] observedAt) [ TimeObservation.InvalidTimeRange ] "lone endedAt"

      // --- Serialization ---------------------------------------------------

      "serialize then deserialize round-trips a fully-populated observation", fun () ->
        let evidence = [ { Kind = "github-commit"; Reference = "abc123" }; { Kind = "github-pr"; Reference = "42" } ]
        let original =
            TimeObservation.create "ros:activity:round-trip" "ros" "echelon-foundry" "strata" (Some "STRATA-1") (Some "kevin")
                (Some startedAt) (Some endedAt) None (Some "Description text") evidence observedAt
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match TimeObservation.deserialize (TimeObservation.serialize original) with
        | Ok roundTripped -> assertTrue (roundTripped = original) $"round trip did not preserve the value: {roundTripped} <> {original}"
        | Error errors -> failwith $"round trip failed to deserialize: {errors}"

      "serialize then deserialize round-trips an observation with every optional field absent", fun () ->
        let original = validDurationOnly () |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match TimeObservation.deserialize (TimeObservation.serialize original) with
        | Ok roundTripped -> assertTrue (roundTripped = original) "round trip did not preserve an all-optional-fields-absent value"
        | Error errors -> failwith $"round trip failed to deserialize: {errors}"

      "deserialize ignores an unrecognized extra JSON field", fun () ->
        let json = """{"contractVersion":"1","observationId":"id","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"strata","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[],"futureField":"ignored, not yet part of any contract"}"""
        match TimeObservation.deserialize json with
        | Ok observation -> assertTrue (observation.DurationMinutes = Some 30) "the known fields were not read correctly alongside the unrecognized one"
        | Error errors -> failwith $"an unrecognized extra field should not fail deserialization, got {errors}"

      "deserialize of an unsupported contract version is refused outright, not guessed at", fun () ->
        let json = """{"contractVersion":"2","observationId":"id","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"strata","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[]}"""
        match TimeObservation.deserialize json with
        | Error [ TimeObservation.UnsupportedContractVersion "2" ] -> ()
        | Error errors -> failwith $"expected UnsupportedContractVersion \"2\", got {errors}"
        | Ok _ -> failwith "an unsupported contract version must never deserialize successfully"

      "deserialize of malformed JSON returns MalformedPayload rather than throwing", fun () ->
        match TimeObservation.deserialize "{ not valid json" with
        | Error [ TimeObservation.MalformedPayload _ ] -> ()
        | other -> failwith $"expected MalformedPayload, got {other}"

      "deserialize of a payload missing observedAt returns MissingObservedAt", fun () ->
        let json = """{"contractVersion":"1","observationId":"id","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"strata","durationMinutes":30,"evidence":[]}"""
        expectErrors (TimeObservation.deserialize json) [ TimeObservation.MissingObservedAt ] "missing observedAt"

      "deserialize applies the same structural validation as create", fun () ->
        let json = """{"contractVersion":"1","observationId":"","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"strata","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[]}"""
        expectErrors (TimeObservation.deserialize json) [ TimeObservation.MissingObservationId ] "blank observationId via deserialize"

      // --- Fixture compatibility (see docs/integration/COMPATIBILITY.md) --

      "fixture minimal.json deserializes and round-trips", fun () ->
        let json = readFixture "minimal.json"
        match TimeObservation.deserialize json with
        | Ok observation ->
            match TimeObservation.deserialize (TimeObservation.serialize observation) with
            | Ok roundTripped -> assertTrue (roundTripped = observation) "minimal.json did not round-trip"
            | Error errors -> failwith $"minimal.json's serialized form failed to re-deserialize: {errors}"
        | Error errors -> failwith $"minimal.json failed to deserialize: {errors}"

      "fixture interval.json deserializes and round-trips", fun () ->
        let json = readFixture "interval.json"
        match TimeObservation.deserialize json with
        | Ok observation ->
            assertTrue (observation.WorkItemId = Some "STRATA-142") "interval.json's workItemId was not read"
            assertTrue (observation.Evidence.Length = 1) "interval.json's evidence was not read"
            match TimeObservation.deserialize (TimeObservation.serialize observation) with
            | Ok roundTripped -> assertTrue (roundTripped = observation) "interval.json did not round-trip"
            | Error errors -> failwith $"interval.json's serialized form failed to re-deserialize: {errors}"
        | Error errors -> failwith $"interval.json failed to deserialize: {errors}"

      "fixture duration.json deserializes and round-trips", fun () ->
        let json = readFixture "duration.json"
        match TimeObservation.deserialize json with
        | Ok observation ->
            assertTrue (observation.DurationMinutes = Some 45) "duration.json's durationMinutes was not read"
            match TimeObservation.deserialize (TimeObservation.serialize observation) with
            | Ok roundTripped -> assertTrue (roundTripped = observation) "duration.json did not round-trip"
            | Error errors -> failwith $"duration.json's serialized form failed to re-deserialize: {errors}"
        | Error errors -> failwith $"duration.json failed to deserialize: {errors}"

      "fixture with-work-item.json deserializes and round-trips", fun () ->
        let json = readFixture "with-work-item.json"
        match TimeObservation.deserialize json with
        | Ok observation ->
            assertTrue (observation.WorkItemId = Some "STRATA-77") "with-work-item.json's workItemId was not read"
            match TimeObservation.deserialize (TimeObservation.serialize observation) with
            | Ok roundTripped -> assertTrue (roundTripped = observation) "with-work-item.json did not round-trip"
            | Error errors -> failwith $"with-work-item.json's serialized form failed to re-deserialize: {errors}"
        | Error errors -> failwith $"with-work-item.json failed to deserialize: {errors}"

      "fixture with-evidence.json deserializes and round-trips", fun () ->
        let json = readFixture "with-evidence.json"
        match TimeObservation.deserialize json with
        | Ok observation ->
            assertTrue (observation.Evidence.Length = 2) "with-evidence.json's evidence list was not fully read"
            match TimeObservation.deserialize (TimeObservation.serialize observation) with
            | Ok roundTripped -> assertTrue (roundTripped = observation) "with-evidence.json did not round-trip"
            | Error errors -> failwith $"with-evidence.json's serialized form failed to re-deserialize: {errors}"
        | Error errors -> failwith $"with-evidence.json failed to deserialize: {errors}"

      // --- StorageConvention -------------------------------------------------

      "observationInboxPath produces the expected path for straightforward inputs", fun () ->
        match StorageConvention.observationInboxPath "time-tracking-data" "strata" "ros-activity-01JABC123" with
        | Ok path -> assertTrue (path = "time-tracking-data/integration/projects/strata/observations/inbox/ros-activity-01JABC123.json") $"unexpected path: {path}"
        | Error e -> failwith $"expected success, got {e}"

      "observationInboxPath trims leading/trailing slashes from folder", fun () ->
        match StorageConvention.observationInboxPath "/time-tracking-data/" "strata" "obs-1" with
        | Ok path -> assertTrue (path = "time-tracking-data/integration/projects/strata/observations/inbox/obs-1.json") $"unexpected path: {path}"
        | Error e -> failwith $"expected success, got {e}"

      "observationInboxPath percent-encodes a '/' in observationId so it cannot escape its directory", fun () ->
        match StorageConvention.observationInboxPath "time-tracking-data" "strata" "ros:activity/../../etc/passwd" with
        | Ok path ->
            assertTrue (not (path.Contains "../")) $"a raw '../' segment survived encoding: {path}"
            assertTrue (path.StartsWith "time-tracking-data/integration/projects/strata/observations/inbox/") $"the id escaped its intended directory: {path}"
        | Error e -> failwith $"expected success (encoding, not rejection), got {e}"

      "observationInboxPath rejects a blank folder", fun () ->
        match StorageConvention.observationInboxPath "" "strata" "obs-1" with
        | Error (StorageConvention.BlankSegment "folder") -> ()
        | other -> failwith $"expected BlankSegment \"folder\", got {other}"

      "observationInboxPath rejects a blank projectId", fun () ->
        match StorageConvention.observationInboxPath "time-tracking-data" "" "obs-1" with
        | Error (StorageConvention.BlankSegment "projectId") -> ()
        | other -> failwith $"expected BlankSegment \"projectId\", got {other}"

      "observationInboxPath rejects a blank observationId", fun () ->
        match StorageConvention.observationInboxPath "time-tracking-data" "strata" "" with
        | Error (StorageConvention.BlankSegment "observationId") -> ()
        | other -> failwith $"expected BlankSegment \"observationId\", got {other}"

      "observationInboxDirectory produces the parent directory observationInboxPath's file lives under", fun () ->
        match StorageConvention.observationInboxDirectory "time-tracking-data" "strata", StorageConvention.observationInboxPath "time-tracking-data" "strata" "obs-1" with
        | Ok directory, Ok filePath -> assertTrue (filePath = $"{directory}/obs-1.json") $"observationInboxPath's file did not live directly under observationInboxDirectory: {filePath} vs {directory}"
        | other -> failwith $"expected both to succeed, got {other}"

      "safeSegment is total (never fails) and encodes unsafe characters", fun () ->
        assertTrue (StorageConvention.safeSegment "abc-123_ABC.def" = "abc-123_ABC.def") "already-safe characters should pass through unchanged"
        assertTrue (StorageConvention.safeSegment "candidate:ros:activity:001" <> "candidate:ros:activity:001") "':' should have been encoded"
        assertTrue (not ((StorageConvention.safeSegment "a/b").Contains "/")) "'/' must never survive encoding"
    ]

[<EntryPoint>]
let main _ =
    let failures =
        tests
        |> List.choose (fun (name, test) ->
            try
                test ()
                printfn $"PASS {name}"
                None
            with error ->
                Some $"FAIL {name}: {error.Message}")
    failures |> List.iter (eprintfn "%s")
    if List.isEmpty failures then
        printfn $"All {tests.Length} Chrona.Integration compatibility specifications passed."
        0
    else
        eprintfn $"{failures.Length} of {tests.Length} Chrona.Integration compatibility specifications failed."
        1
