/// Split transition: the total-preservation invariant and its refusals
/// (TE-R-023, TE-R-040..TE-R-046).
module TimeEntry.Tests.SplitTests

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

// ---------------------------------------------------------------------------
// Split (TE-R-023, TE-R-040..TE-R-046)
// ---------------------------------------------------------------------------



[<Fact>]
let ``a two way split supersedes the source and creates active children`` () =
    // The worked example from system-prompt §8.10: 60 -> 36 + 24.
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, effects = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)

    Assert.Equal(3, List.length entries)

    let source = entries |> List.find (fun e -> e.Id = entryId "e1")

    match source.State with
    | Superseded(SupersededBySplit ids) -> Assert.Equal<EntryId list>([ entryId "c1"; entryId "c2" ], ids)
    | other -> failwithf "expected Superseded by split, got %A" other

    // TE-R-040 at the level that matters: no time is created or destroyed.
    let counted = entries |> List.sumBy TimeEntry.contributedMilliseconds
    Assert.Equal(3600000L, counted)
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
    let entries, _ = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)
    let child = entries |> List.find (fun e -> e.Id = entryId "c1")

    match (List.head child.History).Change with
    | CreatedBySplitOf source -> Assert.Equal(entryId "e1", source)
    | other -> failwithf "expected CreatedBySplitOf, got %A" other

[<Fact>]
let ``an under allocated split is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 23) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | SplitDoesNotPreserveTotal(source, child) ->
        Assert.Equal(3600000L, source)
        Assert.Equal(3540000L, child)
    | other -> failwithf "expected SplitDoesNotPreserveTotal, got %A" other

[<Fact>]
let ``an over allocated split is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 25) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | SplitDoesNotPreserveTotal _ -> ()
    | other -> failwithf "expected SplitDoesNotPreserveTotal, got %A" other

[<Fact>]
let ``a split needs at least two children`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 60) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | SplitNeedsAtLeastTwoChildren 1 -> ()
    | other -> failwithf "expected SplitNeedsAtLeastTwoChildren, got %A" other

[<Fact>]
let ``split children must have distinct identities`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c1" (minutes 24) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | SplitChildIdentityNotUnique duplicated -> Assert.Equal(entryId "c1", duplicated)
    | other -> failwithf "expected SplitChildIdentityNotUnique, got %A" other

[<Fact>]
let ``a split child may not reuse the source identity`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "e1" (minutes 36); splitChild "c2" (minutes 24) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | SplitChildIdentityNotUnique duplicated -> Assert.Equal(entryId "e1", duplicated)
    | other -> failwithf "expected SplitChildIdentityNotUnique, got %A" other

[<Fact>]
let ``a split against a stale source version is refused`` () =
    // TE-R-046: "a split uses an outdated activity version".
    let entry = persistedEntry "e1" (minutes 60) "sha-2"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]

    match rejection (splitEntry catalogue (splitRequest entry "sha-1" children) entry) with
    | VersionConflict _ -> ()
    | other -> failwithf "expected VersionConflict, got %A" other

[<Fact>]
let ``an already split entry cannot be split again`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, _ = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)

    let source =
        entries
        |> List.find (fun e -> e.Id = entryId "e1")
        |> fun e -> { e with Version = Some(version "sha-2") }

    let again = [ splitChild "c3" (minutes 30); splitChild "c4" (minutes 30) ]

    match rejection (splitEntry catalogue (splitRequest source "sha-2" again) source) with
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

    let entries, _ = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)
    Assert.Equal(3600000L, entries |> List.sumBy TimeEntry.contributedMilliseconds)

// ---------------------------------------------------------------------------
