/// Verifies DF-TE-0007 (OQ-1): an archived project rejects new time, but
/// entries already recorded against it are untouched.
///
/// The asymmetry is the whole point, and it is easy to get wrong in either
/// direction — refusing new time is useless if archiving also freezes
/// history, and preserving history is useless if archived projects keep
/// accepting work.
module TimeEntry.Tests.CatalogueTests

open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Transitions
open TimeEntry.Projection.Query
open TimeEntry.Projection.Projection
open TimeEntry.Tests.Helpers

let private factsFor (projectId: string) (activityTypeId: string) =
    { facts (minutes 30) with
        Project = ProjectId.create projectId |> expect
        ActivityType = ActivityTypeId.create activityTypeId |> expect }

let private createWith projectId activityTypeId =
    createEntry
        catalogue
        { NewEntryId = entryId "e1"
          Facts = factsFor projectId activityTypeId
          Attribution = attribution "e1-r1" }

let private rejectionOf (outcome: Outcome) =
    match outcome with
    | Rejected r -> r
    | Accepted _ -> failwith "expected Rejected, got Accepted"

// ---------------------------------------------------------------------------
// New time is refused
// ---------------------------------------------------------------------------

[<Fact>]
let ``an available project accepts new time`` () =
    match createWith "echelon-foundry" "research" with
    | Accepted _ -> ()
    | Rejected r -> failwithf "expected Accepted, got %A" r

[<Fact>]
let ``an archived project refuses new time`` () =
    match rejectionOf (createWith "retired-client" "research") with
    | CatalogueRejected(ProjectIsArchived id) -> Assert.Equal(projectId "retired-client", id)
    | other -> failwithf "expected ProjectIsArchived, got %A" other

[<Fact>]
let ``a project outside the catalogue refuses new time`` () =
    // Distinct from archived: "unknown" and "retired" need different messages.
    match rejectionOf (createWith "never-heard-of-it" "research") with
    | CatalogueRejected(ProjectNotInCatalogue id) -> Assert.Equal(projectId "never-heard-of-it", id)
    | other -> failwithf "expected ProjectNotInCatalogue, got %A" other

[<Fact>]
let ``an archived activity type refuses new time`` () =
    match rejectionOf (createWith "echelon-foundry" "retired-activity") with
    | CatalogueRejected(ActivityTypeIsArchived _) -> ()
    | other -> failwithf "expected ActivityTypeIsArchived, got %A" other

[<Fact>]
let ``an unknown activity type refuses new time`` () =
    match rejectionOf (createWith "echelon-foundry" "not-a-thing") with
    | CatalogueRejected(ActivityTypeNotInCatalogue _) -> ()
    | other -> failwithf "expected ActivityTypeNotInCatalogue, got %A" other

[<Fact>]
let ``a correction may not move time onto an archived project`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"

    let request: CorrectEntryRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          CorrectedFacts = factsFor "retired-client" "research"
          Reason = reason "Wrong project"
          Attribution = attribution "e1-r2" }

    match rejectionOf (correctEntry catalogue request entry) with
    | CatalogueRejected(ProjectIsArchived _) -> ()
    | other -> failwithf "expected ProjectIsArchived, got %A" other

[<Fact>]
let ``a split child may not be assigned to an archived project`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"

    let children =
        [ splitChild "c1" (minutes 36)
          { splitChild "c2" (minutes 24) with
              Project = projectId "retired-client" } ]

    match rejectionOf (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | CatalogueRejected(ProjectIsArchived _) -> ()
    | other -> failwithf "expected ProjectIsArchived, got %A" other

[<Fact>]
let ``a merge may not be assigned to an archived project`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"

    let request =
        { mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ] with
            Project = projectId "retired-client" }

    match rejectionOf (mergeEntries catalogue request [ a; b ]) with
    | CatalogueRejected(ProjectIsArchived _) -> ()
    | other -> failwithf "expected ProjectIsArchived, got %A" other

// ---------------------------------------------------------------------------
// Existing history is NOT frozen — the other half of DF-TE-0007
// ---------------------------------------------------------------------------

/// An entry recorded before its project was archived.
let private entryOnArchivedProject () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"

    { entry with
        Effective = { entry.Effective with Project = projectId "retired-client" } }

[<Fact>]
let ``an entry on an archived project still counts toward totals`` () =
    // Archiving must not retroactively change recorded time (TE-R-030).
    let entry = entryOnArchivedProject ()
    Assert.True(TimeEntry.countsTowardTotals entry)

    let result =
        Projection.project RoundUp (fun _ -> []) (EntryQuery.forDay defaultDate) [ entry ]

    Assert.Equal(3120000L, result.TotalMilliseconds)

[<Fact>]
let ``an entry on an archived project can still be voided`` () =
    // Void does not change which project time belongs to, so the catalogue
    // guard must not apply to it.
    let entry = entryOnArchivedProject ()

    match voidEntry (voidRequest entry "sha-1") entry with
    | Accepted _ -> ()
    | Rejected r -> failwithf "voiding must remain possible, got %A" r

[<Fact>]
let ``an entry on an archived project can still be restored`` () =
    let entry = entryOnArchivedProject ()
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let target = { voided with Version = Some(version "sha-2") }

    let restore: RestoreEntryRequest =
        { EntryId = target.Id
          ExpectedVersion = version "sha-2"
          Reason = reason "Included after review"
          Attribution = attribution "e1-r3" }

    match restoreEntry restore target with
    | Accepted _ -> ()
    | Rejected r -> failwithf "restoring must remain possible, got %A" r

[<Fact>]
let ``an entry on an archived project can still take evidence`` () =
    let entry = entryOnArchivedProject ()

    let request: AttachEvidenceRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          Evidence =
            { Uri = "https://example.invalid/a"
              Label = None
              AttachedAt = instant 1789000000000L }
          Attribution = attribution "e1-r2" }

    match attachEvidence request entry with
    | Accepted _ -> ()
    | Rejected r -> failwithf "attaching evidence must remain possible, got %A" r

[<Fact>]
let ``an entry on an archived project can still have its hours corrected`` () =
    // The rule is "you may not MOVE time onto an archived reference", not
    // "archived references are frozen". Guarding the whole command instead
    // would make an archived project's history uncorrectable — the opposite of
    // what DF-TE-0007 set out to preserve.
    let entry = entryOnArchivedProject ()

    let request: CorrectEntryRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          // Project retained; only the duration changes.
          CorrectedFacts = { entry.Effective with Duration = minutes 46 }
          Reason = reason "Forgot to stop timer"
          Attribution = attribution "e1-r2" }

    match correctEntry catalogue request entry with
    | Accepted(entries, _) ->
        let corrected = single entries
        Assert.Equal(2760000L, Duration.milliseconds corrected.Effective.Duration)
    | Rejected r -> failwithf "a retained reference must not be re-validated, got %A" r

[<Fact>]
let ``an entry on an archived project may be moved to a live project`` () =
    // Moving *off* an archived project is the fix a user needs most.
    let entry = entryOnArchivedProject ()

    let request: CorrectEntryRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          CorrectedFacts = { entry.Effective with Project = projectId "northline" }
          Reason = reason "Billed to the wrong client"
          Attribution = attribution "e1-r2" }

    match correctEntry catalogue request entry with
    | Accepted _ -> ()
    | Rejected r -> failwithf "expected the move to a live project to succeed, got %A" r

[<Fact>]
let ``an entry on an archived project may not be moved to a different archived project`` () =
    // Retention is per-reference, not a blanket exemption for the entry.
    let entry = persistedEntry "e1" (minutes 52) "sha-1"

    let request: CorrectEntryRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          CorrectedFacts = { entry.Effective with Project = projectId "retired-client" }
          Reason = reason "Wrong client"
          Attribution = attribution "e1-r2" }

    match rejectionOf (correctEntry catalogue request entry) with
    | CatalogueRejected(ProjectIsArchived _) -> ()
    | other -> failwithf "expected ProjectIsArchived, got %A" other

[<Fact>]
let ``an entry on an archived project can still be split`` () =
    // A split redistributes time that already exists; it does not add work,
    // so children retaining the source's project are fine.
    let entry =
        { persistedEntry "e1" (minutes 60) "sha-1" with
            Effective =
                { (persistedEntry "e1" (minutes 60) "sha-1").Effective with
                    Project = projectId "retired-client" } }

    let children =
        [ { splitChild "c1" (minutes 36) with Project = projectId "retired-client" }
          { splitChild "c2" (minutes 24) with Project = projectId "retired-client" } ]

    match splitEntry catalogue (splitRequest entry "sha-1" children) entry with
    | Accepted(entries, _) -> Assert.Equal(3, List.length entries)
    | Rejected r -> failwithf "splitting must remain possible, got %A" r

[<Fact>]
let ``merged entries may keep an archived project they already shared`` () =
    let onRetired (id: string) (duration: Duration) (sha: string) =
        let entry = persistedEntry id duration sha

        { entry with
            Effective = { entry.Effective with Project = projectId "retired-client" } }

    let a = onRetired "e1" (minutes 36) "sha-a"
    let b = onRetired "e2" (minutes 24) "sha-b"

    let request =
        { mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ] with
            Project = projectId "retired-client" }

    match mergeEntries catalogue request [ a; b ] with
    | Accepted(entries, _) -> Assert.Equal(3, List.length entries)
    | Rejected r -> failwithf "merging must remain possible, got %A" r

[<Fact>]
let ``a merge may not adopt an archived project none of its sources held`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"

    let request =
        { mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ] with
            Project = projectId "retired-client" }

    match rejectionOf (mergeEntries catalogue request [ a; b ]) with
    | CatalogueRejected(ProjectIsArchived _) -> ()
    | other -> failwithf "expected ProjectIsArchived, got %A" other

// ---------------------------------------------------------------------------
// Schema conformance
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("Echelon-Foundry")>] // uppercase
[<InlineData("echelon_foundry")>] // underscore
[<InlineData("echelon foundry")>] // space
[<InlineData("echelon.foundry")>] // dot
let ``a catalogue id outside the schema pattern is refused`` (raw: string) =
    // schemas/domain/project.schema.json pins ^[a-z0-9-]+$. Accepting more
    // would let the domain build records that fail schema validation later.
    match ProjectId.create raw with
    | Error(IdentifierMalformed _) -> ()
    | other -> failwithf "expected IdentifierMalformed for '%s', got %A" raw other

[<Fact>]
let ``entry ids are not constrained to the catalogue pattern`` () =
    // Only project and activity-type ids have a documented pattern; imposing
    // it on entry ids would be inventing a rule. A UUID must remain valid.
    match EntryId.create "3F2504E0-4F89-11D3-9A0C-0305E82C3301" with
    | Ok _ -> ()
    | Error e -> failwithf "entry ids must stay unconstrained, got %A" e

[<Fact>]
let ``the active flag maps both ways`` () =
    // The schemas carry `active: boolean`, so this mapping is the contract.
    Assert.Equal(Available, CatalogueStatus.ofActiveFlag true)
    Assert.Equal(Archived, CatalogueStatus.ofActiveFlag false)
    Assert.True(CatalogueStatus.toActiveFlag Available)
    Assert.False(CatalogueStatus.toActiveFlag Archived)

[<Fact>]
let ``a catalogue name is bounded at the schema's maximum`` () =
    Assert.Equal(120, MaxNameLength)

    match CatalogueName.create (String.replicate 121 "x") with
    | Error(TextTooLong(121, 120)) -> ()
    | other -> failwithf "expected TextTooLong, got %A" other

[<Fact>]
let ``an empty catalogue refuses everything`` () =
    // The failure mode if LoadProjects silently returned [] — which is why
    // the interpreter reports NotSupported instead.
    match Catalogue.acceptsNewTime (projectId "echelon-foundry") (activityTypeId "research") Catalogue.empty with
    | Error(ProjectNotInCatalogue _) -> ()
    | other -> failwithf "expected ProjectNotInCatalogue, got %A" other
