/// Verifies every legal transition and the important illegal ones, plus the
/// invariants that must survive an adversarial change (execution rule §23).
module TimeEntry.Tests.TransitionTests

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

let private accepted (outcome: Outcome) =
    match outcome with
    | Accepted(entries, effects) -> entries, effects
    | Rejected rejection -> failwithf "expected Accepted, got Rejected %A" rejection

let private rejection (outcome: Outcome) =
    match outcome with
    | Rejected r -> r
    | Accepted _ -> failwith "expected Rejected, got Accepted"

let private single (entries: TimeEntry list) =
    Assert.Equal(1, List.length entries)
    List.head entries

// ---------------------------------------------------------------------------
// Create (TE-R-020)
// ---------------------------------------------------------------------------

[<Fact>]
let ``creating an entry yields an active entry with one revision`` () =
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 52)
          Attribution = attribution "e1-r1" }

    let entries, effects = accepted (createEntry request)
    let entry = single entries

    Assert.Equal(Active, entry.State)
    Assert.Equal(1, List.length entry.History)
    Assert.Equal(None, entry.Version)

    match List.exactlyOne effects with
    | PersistNewEntry persist ->
        // A first write must assert the entry does not already exist.
        Assert.Equal(None, persist.ExpectedVersion)
    | other -> failwithf "expected PersistNewEntry, got %A" other

[<Fact>]
let ``a created entry counts toward totals`` () =
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 30)
          Attribution = attribution "e1-r1" }

    let entry = single (fst (accepted (createEntry request)))
    Assert.True(TimeEntry.countsTowardTotals entry)
    Assert.Equal(1800, TimeEntry.contributedSeconds entry)

// ---------------------------------------------------------------------------
// Correct (TE-R-022, TE-R-050, TE-R-051)
// ---------------------------------------------------------------------------

let private correctionRequest (entry: TimeEntry) (newDuration: Duration) (versionToken: string) =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      CorrectedFacts = { entry.Effective with Duration = newDuration }
      Reason = reason "Forgot to stop timer"
      Attribution = attribution "e1-r2" }

[<Fact>]
let ``correcting an entry appends a revision and keeps it active`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let request = correctionRequest entry (minutes 46) "sha-1"

    let corrected = single (fst (accepted (correctEntry request entry)))

    Assert.Equal(Active, corrected.State)
    Assert.Equal(2, List.length corrected.History)
    Assert.Equal(2760, Duration.seconds corrected.Effective.Duration)
    Assert.Equal(1, TimeEntry.correctionCount corrected)

[<Fact>]
let ``correction preserves the original values in history`` () =
    // TE-R-030/TE-R-052: history must still explain what existed before.
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let request = correctionRequest entry (minutes 46) "sha-1"
    let corrected = single (fst (accepted (correctEntry request entry)))

    let original = TimeEntry.originalRevision corrected |> Option.get
    Assert.Equal(3120, Duration.seconds original.Facts.Duration)

    match original.Change with
    | Created -> ()
    | other -> failwithf "the oldest revision must be Created, got %A" other

[<Fact>]
let ``correction requires the version the caller read`` () =
    // TE-R-070: a newer correction must never be silently overwritten.
    let entry = persistedEntry "e1" (minutes 52) "sha-2"
    let request = correctionRequest entry (minutes 46) "sha-1"

    match rejection (correctEntry request entry) with
    | VersionConflict(expected, actual) ->
        Assert.Equal("sha-1", VersionToken.value expected)
        Assert.Equal("sha-2", VersionToken.value (Option.get actual))
    | other -> failwithf "expected VersionConflict, got %A" other

[<Fact>]
let ``a stale correction writes nothing`` () =
    // TE-R-072: the stale write becomes an outcome, not a retry.
    let entry = persistedEntry "e1" (minutes 52) "sha-2"
    let request = correctionRequest entry (minutes 46) "sha-1"

    match correctEntry request entry with
    | Rejected _ -> ()
    | Accepted(_, effects) -> failwithf "a conflict must request no effects, got %A" effects

[<Fact>]
let ``an unpersisted entry cannot be corrected against any version`` () =
    let entry = { persistedEntry "e1" (minutes 52) "sha-1" with Version = None }
    let request = correctionRequest entry (minutes 46) "sha-1"

    match rejection (correctEntry request entry) with
    | VersionConflict(_, None) -> ()
    | other -> failwithf "expected VersionConflict with no current version, got %A" other

// ---------------------------------------------------------------------------
// Void and restore (TE-R-024, TE-R-025, TE-R-060..TE-R-063)
// ---------------------------------------------------------------------------

let private voidRequest (entry: TimeEntry) (versionToken: string) =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      Reason = reason "Duplicate of the timer entry"
      Attribution = attribution "e1-r2" }

[<Fact>]
let ``voiding removes the entry from totals but keeps the record`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))

    match voided.State with
    | Void(r, _) -> Assert.Equal("Duplicate of the timer entry", Reason.value r)
    | other -> failwithf "expected Void, got %A" other

    Assert.False(TimeEntry.countsTowardTotals voided)
    Assert.Equal(0, TimeEntry.contributedSeconds voided)
    // TE-R-063: not a delete — the facts and history survive.
    Assert.Equal(3120, Duration.seconds voided.Effective.Duration)
    Assert.Equal(2, List.length voided.History)

[<Fact>]
let ``voiding twice is refused`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let voidedWithVersion = { voided with Version = Some(version "sha-2") }

    match rejection (voidEntry (voidRequest voidedWithVersion "sha-2") voidedWithVersion) with
    | NotPermittedInState(CanVoid, AlreadyVoid) -> ()
    | other -> failwithf "expected NotPermittedInState(CanVoid, AlreadyVoid), got %A" other

[<Fact>]
let ``a voided entry cannot be corrected or split`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let target = { voided with Version = Some(version "sha-2") }

    match rejection (correctEntry (correctionRequest target (minutes 30) "sha-2") target) with
    | NotPermittedInState(CanCorrect, AlreadyVoid) -> ()
    | other -> failwithf "expected correction to be refused, got %A" other

    let split =
        { EntryId = target.Id
          ExpectedVersion = version "sha-2"
          Children = [ splitChild "c1" (minutes 26); splitChild "c2" (minutes 26) ]
          Attribution = attribution "e1-r3" }

    match rejection (splitEntry split target) with
    | NotPermittedInState(CanSplit, AlreadyVoid) -> ()
    | other -> failwithf "expected split to be refused, got %A" other

[<Fact>]
let ``restoring a voided entry returns it to totals and keeps both events`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let target = { voided with Version = Some(version "sha-2") }

    let restoreRequest =
        { EntryId = target.Id
          ExpectedVersion = version "sha-2"
          Reason = reason "Not a duplicate after review"
          Attribution = attribution "e1-r3" }

    let restored = single (fst (accepted (restoreEntry restoreRequest target)))

    Assert.Equal(Active, restored.State)
    Assert.True(TimeEntry.countsTowardTotals restored)
    // TE-R-062: the void and the restore must both remain explainable.
    Assert.Equal(3, List.length restored.History)

    let changes = restored.History |> List.map (fun r -> r.Change)

    Assert.True(
        changes
        |> List.exists (fun c ->
            match c with
            | Voided _ -> true
            | _ -> false)
    )

    Assert.True(
        changes
        |> List.exists (fun c ->
            match c with
            | Restored _ -> true
            | _ -> false)
    )

[<Fact>]
let ``an active entry cannot be restored`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"

    let request =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          Reason = reason "no-op"
          Attribution = attribution "e1-r2" }

    match rejection (restoreEntry request entry) with
    | NotPermittedInState(CanRestore, NotVoid) -> ()
    | other -> failwithf "expected NotPermittedInState(CanRestore, NotVoid), got %A" other

// ---------------------------------------------------------------------------
// Split (TE-R-023, TE-R-040..TE-R-046)
// ---------------------------------------------------------------------------

let private splitRequest (entry: TimeEntry) (versionToken: string) (children: SplitChild list) =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      Children = children
      Attribution = attribution "e1-r2" }

[<Fact>]
let ``a two way split supersedes the source and creates active children`` () =
    // The worked example from system-prompt §8.10: 60 -> 36 + 24.
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, effects = accepted (splitEntry (splitRequest entry "sha-1" children) entry)

    Assert.Equal(3, List.length entries)

    let source = entries |> List.find (fun e -> e.Id = entryId "e1")

    match source.State with
    | Superseded(SupersededBySplit ids) -> Assert.Equal<EntryId list>([ entryId "c1"; entryId "c2" ], ids)
    | other -> failwithf "expected Superseded by split, got %A" other

    // TE-R-040 at the level that matters: no time is created or destroyed.
    let counted = entries |> List.sumBy TimeEntry.contributedSeconds
    Assert.Equal(3600, counted)
    Assert.False(TimeEntry.countsTowardTotals source)

    match List.exactlyOne effects with
    | PersistSplit(sourcePersist, childPersists) ->
        Assert.Equal(Some(version "sha-1"), sourcePersist.ExpectedVersion)
        Assert.Equal(2, List.length childPersists)
        Assert.True(childPersists |> List.forall (fun p -> p.ExpectedVersion = None))
    | other -> failwithf "expected PersistSplit, got %A" other

[<Fact>]
let ``a split child records its lineage to the source`` () =
    // TE-R-043: no detached children, no ambiguous source.
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, _ = accepted (splitEntry (splitRequest entry "sha-1" children) entry)
    let child = entries |> List.find (fun e -> e.Id = entryId "c1")

    match (List.head child.History).Change with
    | CreatedBySplitOf source -> Assert.Equal(entryId "e1", source)
    | other -> failwithf "expected CreatedBySplitOf, got %A" other

[<Fact>]
let ``an under allocated split is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 23) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | SplitDoesNotPreserveTotal(source, child) ->
        Assert.Equal(3600, source)
        Assert.Equal(3540, child)
    | other -> failwithf "expected SplitDoesNotPreserveTotal, got %A" other

[<Fact>]
let ``an over allocated split is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 25) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | SplitDoesNotPreserveTotal _ -> ()
    | other -> failwithf "expected SplitDoesNotPreserveTotal, got %A" other

[<Fact>]
let ``a split needs at least two children`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 60) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | SplitNeedsAtLeastTwoChildren 1 -> ()
    | other -> failwithf "expected SplitNeedsAtLeastTwoChildren, got %A" other

[<Fact>]
let ``split children must have distinct identities`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c1" (minutes 24) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | SplitChildIdentityNotUnique duplicated -> Assert.Equal(entryId "c1", duplicated)
    | other -> failwithf "expected SplitChildIdentityNotUnique, got %A" other

[<Fact>]
let ``a split child may not reuse the source identity`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "e1" (minutes 36); splitChild "c2" (minutes 24) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | SplitChildIdentityNotUnique duplicated -> Assert.Equal(entryId "e1", duplicated)
    | other -> failwithf "expected SplitChildIdentityNotUnique, got %A" other

[<Fact>]
let ``a split against a stale source version is refused`` () =
    // TE-R-046: "a split uses an outdated activity version".
    let entry = persistedEntry "e1" (minutes 60) "sha-2"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]

    match rejection (splitEntry (splitRequest entry "sha-1" children) entry) with
    | VersionConflict _ -> ()
    | other -> failwithf "expected VersionConflict, got %A" other

[<Fact>]
let ``an already split entry cannot be split again`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, _ = accepted (splitEntry (splitRequest entry "sha-1" children) entry)

    let source =
        entries
        |> List.find (fun e -> e.Id = entryId "e1")
        |> fun e -> { e with Version = Some(version "sha-2") }

    let again = [ splitChild "c3" (minutes 30); splitChild "c4" (minutes 30) ]

    match rejection (splitEntry (splitRequest source "sha-2" again) source) with
    | NotPermittedInState(CanSplit, AlreadySuperseded _) -> ()
    | other -> failwithf "expected AlreadySuperseded, got %A" other

[<Fact>]
let ``a split of many children preserves the total`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"

    let children =
        [ splitChild "c1" (minutes 6)
          splitChild "c2" (minutes 12)
          splitChild "c3" (minutes 18)
          splitChild "c4" (minutes 24) ]

    let entries, _ = accepted (splitEntry (splitRequest entry "sha-1" children) entry)
    Assert.Equal(3600, entries |> List.sumBy TimeEntry.contributedSeconds)

// ---------------------------------------------------------------------------
// Dispatch and capability surface
// ---------------------------------------------------------------------------

[<Fact>]
let ``a command naming an unloaded entry is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"

    let request =
        { EntryId = entryId "missing"
          ExpectedVersion = version "sha-1"
          Reason = reason "x"
          Attribution = attribution "r" }

    match rejection (apply [ entry ] (VoidEntry request)) with
    | EntryNotLoaded id -> Assert.Equal(entryId "missing", id)
    | other -> failwithf "expected EntryNotLoaded, got %A" other

[<Fact>]
let ``merge is never offered as a capability while OQ-4 is open`` () =
    // Guards against a future change quietly enabling a transition whose
    // semantics no requirement defines.
    for state in
        [ Active
          Void(reason "r", instant 1L)
          Superseded(SupersededBySplit [ entryId "c1" ]) ] do
        Assert.False(Capabilities.has CanMerge state)

        match Capabilities.denialFor CanMerge state with
        | Some(BlockedByOpenQuestion "OQ-4") -> ()
        | other -> failwithf "expected BlockedByOpenQuestion OQ-4, got %A" other

[<Fact>]
let ``a superseded entry offers no capabilities`` () =
    Assert.Empty(Capabilities.available (Superseded(SupersededBySplit [ entryId "c1" ])))

[<Fact>]
let ``every transition only ever grows history`` () =
    // Adversarial: the one property that must hold across all transitions.
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let before = List.length entry.History

    let outcomes =
        [ correctEntry (correctionRequest entry (minutes 30) "sha-1") entry
          voidEntry (voidRequest entry "sha-1") entry
          splitEntry (splitRequest entry "sha-1" [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]) entry ]

    for outcome in outcomes do
        let entries, _ = accepted outcome

        let source = entries |> List.find (fun e -> e.Id = entryId "e1")

        Assert.True(
            List.length source.History > before,
            sprintf "history shrank or stalled: %d -> %d" before (List.length source.History)
        )
