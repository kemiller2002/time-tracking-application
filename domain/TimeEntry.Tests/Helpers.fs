/// Test builders. Kept in one place so a test reads as the behavior under
/// scrutiny rather than as construction noise.
module TimeEntry.Tests.Helpers

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands

/// Unwrap a smart-constructor Result in test setup. Fails loudly rather than
/// silently substituting a default, so a broken builder cannot make a test
/// vacuously pass.
let expect (result: Result<'a, 'e>) : 'a =
    match result with
    | Ok value -> value
    | Error e -> failwithf "test setup produced an invalid value: %A" e

let entryId id = EntryId.create id |> expect
let revisionId id = RevisionId.create id |> expect
let projectId id = ProjectId.create id |> expect
let activityTypeId id = ActivityTypeId.create id |> expect
let userId id = UserId.create id |> expect
let deviceLabel label = DeviceLabel.create label |> expect
let version token = VersionToken.create token |> expect
let reason text = Reason.create text |> expect
let description text = Description.create text |> expect
let seconds n = Duration.ofSeconds n |> expect
let minutes n = Duration.ofMinutes n |> expect
let onDate year month day = EntryDate.ofYearMonthDay year month day |> expect
let instant epochSeconds = Instant.ofEpochSeconds epochSeconds

let defaultDate = onDate 2026 9 10

let facts (duration: Duration) =
    { Project = projectId "echelon-foundry"
      ActivityType = activityTypeId "research"
      Date = defaultDate
      Duration = duration
      Description = Some(description "Reviewed composition evidence.")
      Origin = Timed
      Evidence = [] }

let attribution (revision: string) =
    { Actor = userId "km"
      Device = deviceLabel "iPhone"
      OccurredAt = instant 1789000000L
      NewRevisionId = revisionId revision }

/// An entry that has been persisted once, so it has a version and can be the
/// target of a mutation.
let persistedEntry (id: string) (duration: Duration) (versionToken: string) =
    let entryFacts = facts duration

    { Id = entryId id
      State = Active
      Effective = entryFacts
      History =
        [ { Id = revisionId (id + "-r1")
            Change = Created
            Facts = entryFacts
            RecordedAt = instant 1788000000L
            RecordedBy = userId "km"
            Device = deviceLabel "iPhone" } ]
      Version = Some(version versionToken) }

/// An entry on a specific ledger day, for cross-day cases.
let persistedEntryOn (id: string) (duration: Duration) (versionToken: string) (date: EntryDate) =
    let entry = persistedEntry id duration versionToken

    { entry with
        Effective = { entry.Effective with Date = date }
        History =
            entry.History
            |> List.map (fun r -> { r with Facts = { r.Facts with Date = date } }) }

let mergeSource (id: string) (versionToken: string) : MergeSource =
    { EntryId = entryId id
      ExpectedVersion = version versionToken
      NewRevisionId = revisionId (id + "-merged") }

let splitChild (id: string) (duration: Duration) =
    { NewEntryId = entryId id
      NewRevisionId = revisionId (id + "-r1")
      Duration = duration
      Project = projectId "echelon-foundry"
      ActivityType = activityTypeId "research"
      Description = Some(description "Split part.")
      ReassignedEvidence = [] }
