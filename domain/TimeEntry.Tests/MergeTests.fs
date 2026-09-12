/// Merge transition (TE-R-026, DF-TE-0006): sources superseded into one
/// entry, with the total computed rather than supplied.
module TimeEntry.Tests.MergeTests

open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Transitions
open TimeEntry.Tests.Helpers

// Merge (TE-R-026, DF-TE-0006)
// ---------------------------------------------------------------------------



[<Fact>]
let ``merge is offered on an active entry and withheld elsewhere`` () =
    // DF-TE-0006 resolved OQ-4, so merge is now a real capability.
    Assert.True(Capabilities.has CanMerge Active)
    Assert.False(Capabilities.has CanMerge (Void(reason "r", instant 1L)))
    Assert.False(Capabilities.has CanMerge (Superseded(SupersededBySplit [ entryId "c1" ])))

[<Fact>]
let ``merging two entries supersedes both into one active entry`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]

    let entries, effects = accepted (mergeEntries catalogue request [ a; b ])

    Assert.Equal(3, List.length entries)

    let target = entries |> List.find (fun e -> e.Id = entryId "m1")
    Assert.Equal(Active, target.State)
    // The merged duration is the exact integer sum: 36m + 24m = 60m.
    Assert.Equal(3600, Duration.seconds target.Effective.Duration)

    for id in [ "e1"; "e2" ] do
        let source = entries |> List.find (fun e -> e.Id = entryId id)

        match source.State with
        | Superseded(SupersededByMerge into) -> Assert.Equal(entryId "m1", into)
        | other -> failwithf "expected SupersededByMerge, got %A" other

    match List.exactlyOne effects with
    | PersistMerge(targetPersist, sourcePersists) ->
        Assert.Equal(None, targetPersist.ExpectedVersion)
        Assert.Equal(2, List.length sourcePersists)
        // Each source is written against the version its caller read.
        Assert.Equal<VersionToken option list>(
            [ Some(version "sha-a"); Some(version "sha-b") ],
            sourcePersists |> List.map (fun p -> p.ExpectedVersion)
        )
    | other -> failwithf "expected PersistMerge, got %A" other

[<Fact>]
let ``merging preserves the total and never double counts`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]
    let entries, _ = accepted (mergeEntries catalogue request [ a; b ])

    // Only the merged entry counts; the sources contribute zero.
    Assert.Equal(3600000L, entries |> List.sumBy TimeEntry.contributedMilliseconds)

[<Fact>]
let ``a merged source can never be restored back into totals`` () =
    // This is the reason DF-TE-0006 chose Superseded over Void: a restorable
    // source would return its time to totals while the merged entry still
    // carries it.
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]
    let entries, _ = accepted (mergeEntries catalogue request [ a; b ])

    let source =
        entries
        |> List.find (fun e -> e.Id = entryId "e1")
        |> fun e -> { e with Version = Some(version "sha-a2") }

    Assert.Empty(Capabilities.available source.State)

    let restore: RestoreEntryRequest =
        { EntryId = source.Id
          ExpectedVersion = version "sha-a2"
          Reason = reason "changed my mind"
          Attribution = attribution "e1-r9" }

    match rejection (restoreEntry restore source) with
    | NotPermittedInState(CanRestore, AlreadySuperseded(SupersededByMerge _)) -> ()
    | other -> failwithf "expected restore to be refused as superseded, got %A" other

[<Fact>]
let ``merge records lineage in both directions`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]
    let entries, _ = accepted (mergeEntries catalogue request [ a; b ])

    let target = entries |> List.find (fun e -> e.Id = entryId "m1")

    match (List.head target.History).Change with
    | CreatedByMergeOf sources -> Assert.Equal<EntryId list>([ entryId "e1"; entryId "e2" ], sources)
    | other -> failwithf "expected CreatedByMergeOf, got %A" other

    let source = entries |> List.find (fun e -> e.Id = entryId "e1")

    match (List.head source.History).Change with
    | MergedInto target -> Assert.Equal(entryId "m1", target)
    | other -> failwithf "expected MergedInto, got %A" other

    // The source's own original revision survives (TE-R-030).
    Assert.Equal(2, List.length source.History)

[<Fact>]
let ``merge requires a reason and marks the result as manual`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]
    let entries, _ = accepted (mergeEntries catalogue request [ a; b ])
    let target = entries |> List.find (fun e -> e.Id = entryId "m1")

    match target.Effective.Origin with
    | Manual r -> Assert.Equal("Same task split across two timer runs", Reason.value r)
    | Timed -> failwith "a merged entry is a deliberate manual act, not a timed record"

[<Fact>]
let ``a merge needs at least two sources`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"

    match rejection (mergeEntries catalogue (mergeRequest [ mergeSource "e1" "sha-a" ]) [ a ]) with
    | MergeNeedsAtLeastTwoSources 1 -> ()
    | other -> failwithf "expected MergeNeedsAtLeastTwoSources, got %A" other

[<Fact>]
let ``merge sources must have distinct identities`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"

    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e1" "sha-a" ]

    match rejection (mergeEntries catalogue request [ a ]) with
    | MergeSourceIdentityNotUnique duplicated -> Assert.Equal(entryId "e1", duplicated)
    | other -> failwithf "expected MergeSourceIdentityNotUnique, got %A" other

[<Fact>]
let ``the merged entry may not reuse a source identity`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "m1" (minutes 24) "sha-b"

    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "m1" "sha-b" ]

    match rejection (mergeEntries catalogue request [ a; b ]) with
    | MergeSourceIdentityNotUnique duplicated -> Assert.Equal(entryId "m1", duplicated)
    | other -> failwithf "expected MergeSourceIdentityNotUnique, got %A" other

[<Fact>]
let ``a merge with one stale source is refused entirely`` () =
    // TE-R-074 "offline edits overlap": each source is version-checked, and one
    // stale source rejects the whole merge rather than merging the rest.
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b-newer"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]

    match mergeEntries catalogue request [ a; b ] with
    | Rejected(VersionConflict(expected, actual)) ->
        Assert.Equal("sha-b", VersionToken.value expected)
        Assert.Equal("sha-b-newer", VersionToken.value (Option.get actual))
    | other -> failwithf "expected VersionConflict, got %A" other

[<Fact>]
let ``a voided source cannot be merged`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"

    let b =
        { persistedEntry "e2" (minutes 24) "sha-b" with State = Void(reason "dup", instant 1L) }

    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]

    match rejection (mergeEntries catalogue request [ a; b ]) with
    | NotPermittedInState(CanMerge, AlreadyVoid) -> ()
    | other -> failwithf "expected NotPermittedInState(CanMerge, AlreadyVoid), got %A" other

[<Fact>]
let ``a merge may not span multiple ledger days`` () =
    // Refused rather than silently resolved: the ledger is day-oriented, so
    // this would move time between two days and change both days' totals.
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntryOn "e2" (minutes 24) "sha-b" (onDate 2026 9 11)
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]

    match rejection (mergeEntries catalogue request [ a; b ]) with
    | MergeSpansMultipleDays 2 -> ()
    | other -> failwithf "expected MergeSpansMultipleDays 2, got %A" other

[<Fact>]
let ``a merge naming an unloaded source is refused`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "missing" "sha-x" ]

    match rejection (mergeEntries catalogue request [ a ]) with
    | EntryNotLoaded id -> Assert.Equal(entryId "missing", id)
    | other -> failwithf "expected EntryNotLoaded, got %A" other

[<Fact>]
let ``merge dispatches through apply`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]

    let entries, _ = accepted (apply catalogue [ a; b ] (MergeEntries request))
    Assert.Equal(3, List.length entries)

[<Fact>]
let ``merging three entries sums all of them`` () =
    let a = persistedEntry "e1" (minutes 6) "sha-a"
    let b = persistedEntry "e2" (minutes 12) "sha-b"
    let c = persistedEntry "e3" (minutes 18) "sha-c"

    let request =
        mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b"; mergeSource "e3" "sha-c" ]

    let entries, _ = accepted (mergeEntries catalogue request [ a; b; c ])
    Assert.Equal(2160000L, entries |> List.sumBy TimeEntry.contributedMilliseconds)

[<Fact>]
let ``a superseded entry offers no capabilities`` () =
    Assert.Empty(Capabilities.available (Superseded(SupersededBySplit [ entryId "c1" ])))

[<Fact>]
let ``every transition only ever grows history`` () =
    // Adversarial: the one property that must hold across all transitions.
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let before = List.length entry.History

    let outcomes =
        [ correctEntry catalogue (correctionRequest entry (minutes 30) "sha-1") entry
          voidEntry (voidRequest entry "sha-1") entry
          splitEntry catalogue (splitRequest entry "sha-1" [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]) entry ]

    for outcome in outcomes do
        let entries, _ = accepted outcome

        let source = entries |> List.find (fun e -> e.Id = entryId "e1")

        Assert.True(
            List.length source.History > before,
            sprintf "history shrank or stalled: %d -> %d" before (List.length source.History)
        )
