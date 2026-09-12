/// Verifies the persistence boundary (TE-R-094, TE-R-084).
///
/// The load-bearing property is round-trip identity:
/// `fromDocument v (toDocument e) = Ok e`. If that holds for every state and
/// every revision kind, the stored form cannot silently lose or reinterpret
/// domain state — which is the entire job of an anti-corruption layer.
module TimeEntry.Tests.PersistenceTests

open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Transitions
open TimeEntry.Persistence.Documents
open TimeEntry.Persistence.Mapping
open TimeEntry.Persistence.Serialization
open TimeEntry.Persistence.Layout
open TimeEntry.Tests.Helpers

/// Domain -> document -> JSON -> document -> domain, which is the full path a
/// real load takes. A mapping-only round trip would miss serializer defects.
let private roundTrip (entry: TimeEntry) : Result<TimeEntry, string> =
    let json = write (toDocument entry)

    match read json with
    | Error e -> Error(sprintf "decode failed: %A" e)
    | Ok document ->
        match fromDocument entry.Version document with
        | Error e -> Error(sprintf "mapping failed: %A" e)
        | Ok result -> Ok result

let private assertRoundTrips (entry: TimeEntry) =
    match roundTrip entry with
    | Error detail -> failwith detail
    | Ok result -> Assert.Equal(entry, result)

// ---------------------------------------------------------------------------
// Round-trip identity across every state
// ---------------------------------------------------------------------------

[<Fact>]
let ``an active entry round-trips exactly`` () =
    assertRoundTrips (persistedEntry "e1" (minutes 52) "sha-1")

[<Fact>]
let ``a corrected entry round-trips with its full history`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let request = correctionRequest entry (minutes 46) "sha-1"
    let corrected = single (fst (accepted (correctEntry catalogue request entry)))

    // Carry a version, as a persisted entry would.
    let stored = { corrected with Version = Some(version "sha-2") }
    assertRoundTrips stored

    match roundTrip stored with
    | Ok result ->
        Assert.Equal(2, List.length result.History)
        Assert.Equal(1, TimeEntry.correctionCount result)
    | Error detail -> failwith detail

[<Fact>]
let ``a voided entry round-trips with its reason and timestamp`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    assertRoundTrips { voided with Version = Some(version "sha-2") }

[<Fact>]
let ``a restored entry round-trips with void and restore both in history`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let target = { voided with Version = Some(version "sha-2") }

    let restore: RestoreEntryRequest =
        { EntryId = target.Id
          ExpectedVersion = version "sha-2"
          Reason = reason "Not a duplicate after review"
          Attribution = attribution "e1-r3" }

    let restored = single (fst (accepted (restoreEntry restore target)))
    assertRoundTrips { restored with Version = Some(version "sha-3") }

[<Fact>]
let ``a split source and its children all round-trip`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, _ = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)

    for produced in entries do
        assertRoundTrips produced

[<Fact>]
let ``a merged entry and its superseded sources all round-trip`` () =
    let a = persistedEntry "e1" (minutes 36) "sha-a"
    let b = persistedEntry "e2" (minutes 24) "sha-b"
    let request = mergeRequest [ mergeSource "e1" "sha-a"; mergeSource "e2" "sha-b" ]
    let entries, _ = accepted (mergeEntries catalogue request [ a; b ])

    for produced in entries do
        assertRoundTrips produced

[<Fact>]
let ``an entry with evidence round-trips including labels`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let withEvidence =
        { entry with
            Effective =
                { entry.Effective with
                    Evidence =
                        [ { Uri = "https://example.invalid/a"
                            Label = Some(description "Meeting notes")
                            AttachedAt = instant 1789000000000L }
                          // A label-less item, so the null path is covered too.
                          { Uri = "https://example.invalid/b"
                            Label = None
                            AttachedAt = instant 1789000001000L } ] } }

    assertRoundTrips withEvidence

[<Fact>]
let ``a manual entry round-trips its reason and a timed entry does not invent one`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let manual =
        { entry with
            Effective =
                { entry.Effective with
                    Origin = Manual(reason "Reconstructed from calendar") } }

    assertRoundTrips manual
    assertRoundTrips entry // Timed

[<Fact>]
let ``an entry with no description round-trips as absent`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let bare = { entry with Effective = { entry.Effective with Description = None } }

    assertRoundTrips bare

    match roundTrip bare with
    | Ok result -> Assert.True(result.Effective.Description.IsNone)
    | Error detail -> failwith detail

// ---------------------------------------------------------------------------
// Precision and dates
// ---------------------------------------------------------------------------

[<Fact>]
let ``sub-second duration survives persistence`` () =
    // DF-TE-0009: the stored field is exact_duration_ms, so no truncation.
    let entry = persistedEntry "e1" (millis 1500L) "sha-1"
    assertRoundTrips entry

    let document = toDocument entry
    Assert.Equal(1500L, document.effective.exact_duration_ms)

[<Fact>]
let ``the stored date is a readable calendar date and reads back exactly`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = toDocument entry

    // Reviewable in a GitHub diff, not an opaque ordinal.
    Assert.Equal("2026-09-10", document.effective.date)
    assertRoundTrips entry

[<Theory>]
[<InlineData(2026, 1, 1)>]
[<InlineData(2026, 2, 28)>]
[<InlineData(2024, 2, 29)>] // leap day
[<InlineData(2026, 12, 31)>]
let ``date round-trips across boundaries`` (year: int, month: int, day: int) =
    let date = onDate year month day
    let entry = persistedEntryOn "e1" (minutes 30) "sha-1" date
    assertRoundTrips entry

    match roundTrip entry with
    | Ok result -> Assert.Equal(date, result.Effective.Date)
    | Error detail -> failwith detail

// ---------------------------------------------------------------------------
// The version token is metadata, not content
// ---------------------------------------------------------------------------

[<Fact>]
let ``the document contains no version field`` () =
    // The token is the blob SHA *of this content*; storing it inside would be
    // circular. It travels as file metadata instead.
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let json = write (toDocument entry)

    // The token itself must not appear anywhere in the content.
    Assert.DoesNotContain("sha-1", json)
    // No `"version"` key. Matched exactly, because `schema_version` is a
    // legitimate key that contains the word.
    Assert.DoesNotContain("\"version\"", json)
    Assert.Contains("\"schema_version\"", json)

[<Fact>]
let ``the version comes from the store, not the file`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = toDocument entry

    match fromDocument (Some(version "a-different-sha")) document with
    | Ok result -> Assert.Equal(Some(version "a-different-sha"), result.Version)
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``an unpersisted entry reads back with no version`` () =
    let entry = { persistedEntry "e1" (minutes 30) "sha-1" with Version = None }

    match fromDocument None (toDocument entry) with
    | Ok result -> Assert.Equal(None, result.Version)
    | Error e -> failwithf "expected Ok, got %A" e

// ---------------------------------------------------------------------------
// TE-R-084: a bad record is one unreadable entry, never an exception
// ---------------------------------------------------------------------------

[<Fact>]
let ``malformed json is reported, not thrown`` () =
    match read "{ this is not json" with
    | Error(MalformedJson _) -> ()
    | other -> failwithf "expected MalformedJson, got %A" other

[<Fact>]
let ``empty content is reported`` () =
    match read "   " with
    | Error EmptyContent -> ()
    | other -> failwithf "expected EmptyContent, got %A" other

[<Fact>]
let ``a json null document is reported rather than crashing`` () =
    match read "null" with
    | Error(MalformedJson _) -> ()
    | other -> failwithf "expected MalformedJson, got %A" other

[<Fact>]
let ``an unknown schema version is refused rather than guessed`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = { toDocument entry with schema_version = "99.0.0" }

    match fromDocument None document with
    | Error(UnsupportedSchemaVersion("99.0.0", supported)) -> Assert.Equal(CurrentSchemaVersion, supported)
    | other -> failwithf "expected UnsupportedSchemaVersion, got %A" other

[<Fact>]
let ``an unknown state kind is refused`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = { toDocument entry with state_kind = "eaten" }

    match fromDocument None document with
    | Error(UnknownStateKind "eaten") -> ()
    | other -> failwithf "expected UnknownStateKind, got %A" other

[<Fact>]
let ``an unknown change kind is refused`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = toDocument entry

    let corrupted =
        { document with
            history = document.history |> Array.map (fun r -> { r with change_kind = "invented" }) }

    match fromDocument None corrupted with
    | Error(UnknownChangeKind "invented") -> ()
    | other -> failwithf "expected UnknownChangeKind, got %A" other

[<Fact>]
let ``an empty history is refused`` () =
    // An entry always has at least its Created revision.
    let entry = persistedEntry "e1" (minutes 30) "sha-1"
    let document = { toDocument entry with history = [||] }

    match fromDocument None document with
    | Error EmptyHistory -> ()
    | other -> failwithf "expected EmptyHistory, got %A" other

[<Fact>]
let ``a void state missing its timestamp is refused`` () =
    // The 0 sentinel is only safe because it is validated against state_kind.
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let document = { toDocument voided with voided_at_ms = 0L }

    match fromDocument None document with
    | Error(StateFieldsInconsistent("void", _)) -> ()
    | other -> failwithf "expected StateFieldsInconsistent, got %A" other

[<Fact>]
let ``a void state missing its reason is refused`` () =
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let voided = single (fst (accepted (voidEntry (voidRequest entry "sha-1") entry)))
    let document = { toDocument voided with void_reason = null }

    match fromDocument None document with
    | Error(MissingField "void_reason") -> ()
    | other -> failwithf "expected MissingField void_reason, got %A" other

[<Fact>]
let ``a split supersession with no children is refused`` () =
    let entry = persistedEntry "e1" (minutes 60) "sha-1"
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let entries, _ = accepted (splitEntry catalogue (splitRequest entry "sha-1" children) entry)
    let source = entries |> List.find (fun e -> e.Id = entryId "e1")
    let document = { toDocument source with superseded_children = [||] }

    match fromDocument None document with
    | Error(MissingField "superseded_children") -> ()
    | other -> failwithf "expected MissingField superseded_children, got %A" other

[<Fact>]
let ``a missing required field names its path`` () =
    // The path matters: a bad record should say where it is bad.
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let document =
        { toDocument entry with
            effective = { (toDocument entry).effective with project_id = null } }

    match fromDocument None document with
    | Error(MissingField "effective.project_id") -> ()
    | other -> failwithf "expected MissingField effective.project_id, got %A" other

[<Fact>]
let ``a non positive stored duration is refused`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let document =
        { toDocument entry with
            effective = { (toDocument entry).effective with exact_duration_ms = 0L } }

    match fromDocument None document with
    | Error(InvalidField("effective.exact_duration_ms", _)) -> ()
    | other -> failwithf "expected InvalidField on duration, got %A" other

[<Fact>]
let ``a malformed stored date is refused`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let document =
        { toDocument entry with
            effective = { (toDocument entry).effective with date = "10/09/2026" } }

    match fromDocument None document with
    | Error(InvalidField("effective.date", _)) -> ()
    | other -> failwithf "expected InvalidField on date, got %A" other

[<Fact>]
let ``a manual origin without a reason is refused`` () =
    let entry = persistedEntry "e1" (minutes 30) "sha-1"

    let document =
        { toDocument entry with
            effective =
                { (toDocument entry).effective with
                    origin_kind = "manual"
                    manual_reason = null } }

    match fromDocument None document with
    | Error(MissingField "effective.manual_reason") -> ()
    | other -> failwithf "expected MissingField effective.manual_reason, got %A" other

// ---------------------------------------------------------------------------
// Layout
// ---------------------------------------------------------------------------

[<Fact>]
let ``an entry path is sharded under the ledger root`` () =
    Assert.Equal("ledger/entries/ab/abcdef.json", entryPath (entryId "abcdef"))

[<Fact>]
let ``a short entry id is padded rather than producing a one character shard`` () =
    Assert.Equal("ledger/entries/a_/a.json", entryPath (entryId "a"))

[<Fact>]
let ``an entry path does not change when the entry's date is corrected`` () =
    // The reason the layout keys on identity: a date correction must not
    // become a file rename, which cannot be done atomically.
    let entry = persistedEntry "e1" (minutes 52) "sha-1"
    let before = entryPath entry.Id

    let moved =
        { entry.Effective with
            Date = onDate 2026 12 31
            Duration = minutes 46 }

    let request: CorrectEntryRequest =
        { EntryId = entry.Id
          ExpectedVersion = version "sha-1"
          CorrectedFacts = moved
          Reason = reason "Recorded against the wrong day"
          Attribution = attribution "e1-r2" }

    let corrected = single (fst (accepted (correctEntry catalogue request entry)))

    Assert.Equal(before, entryPath corrected.Id)
    // And the corrected date really did change, so the test is not vacuous.
    Assert.Equal(onDate 2026 12 31, corrected.Effective.Date)

[<Fact>]
let ``path unsafe characters in an identifier are escaped`` () =
    // An identifier is an opaque domain string; a path is a different
    // alphabet. Nothing may climb out of the ledger root.
    let path = entryPath (entryId "../../etc/passwd")
    Assert.StartsWith("ledger/entries/", path)
    Assert.DoesNotContain("..", path)
    Assert.Equal(4, path.Split('/').Length)

[<Fact>]
let ``entry paths are recognised and other paths are not`` () =
    Assert.True(isEntryPath "ledger/entries/ab/abcdef.json")
    Assert.False(isEntryPath "ledger/entries/ab/abcdef.txt")
    Assert.False(isEntryPath "README.md")
    Assert.False(isEntryPath "ledger/entries")
    Assert.False(isEntryPath null)

[<Fact>]
let ``distinct entries never share a path`` () =
    let paths =
        [ "e1"; "e2"; "abcdef"; "abcdeg"; "a"; "b" ]
        |> List.map (fun id -> entryPath (entryId id))

    Assert.Equal(6, paths |> List.distinct |> List.length)
