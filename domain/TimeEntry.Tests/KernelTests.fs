/// The browser kernel's command surface, exercised without a browser.
///
/// `check:browser` proves the whole path — HTML to WASM to DOM — but it needs
/// a published bundle and a real Chromium, so it stays coarse. These tests
/// cover the part that has the most ways to be quietly wrong: the translation
/// from a JSON request into a domain command.
///
/// What they deliberately do NOT do is re-test the transitions. Whether an
/// archived project may take new time, or a stale version must be refused, is
/// settled in `TransitionTests`. Here the question is only whether a request
/// spelled a particular way reaches the right transition with the right
/// values — and whether a malformed one is refused rather than defaulted.
module TimeEntry.Tests.KernelTests

open System.Text.Json.Nodes
open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Persistence
open TimeEntry.Tests.Helpers

// ---------------------------------------------------------------------------
// Building a request the way the page builds one
// ---------------------------------------------------------------------------

/// Serialize entries the way the transport does — stored documents, with
/// version tokens carried BESIDE them rather than inside.
let private requestFor (entries: TimeEntry list) (command: string) =
    let node = JsonObject()

    node.Add("catalogue", JsonNode.Parse(Serialization.writeCatalogue (Mapping.catalogueToDocument catalogue)))

    let documents = JsonArray()

    for entry in entries do
        documents.Add(JsonNode.Parse(Serialization.write (Mapping.toDocument entry)))

    node.Add("entries", documents)

    let versions = JsonObject()

    for entry in entries do
        match entry.Version with
        | Some token -> versions.Add(EntryId.value entry.Id, JsonValue.Create<string>(VersionToken.value token))
        | None -> ()

    node.Add("versions", versions)
    node.Add("command", JsonNode.Parse command)
    node.ToJsonString()

let private answer (entries: TimeEntry list) (command: string) =
    TimeEntry.Kernel.dispatch (requestFor entries command) |> JsonNode.Parse

let private field (node: JsonNode) (name: string) = node.[name].ToString()

let private isAccepted (node: JsonNode) =
    node.["ok"].ToString() = "true" && node.["accepted"] <> null && node.["accepted"].ToString() = "true"

/// The effects the domain requested, as names. Never performed here — that is
/// the interpreter's job (TE-R-093).
let private effects (node: JsonNode) =
    match node.["effects"] with
    | :? JsonArray as items -> items |> Seq.map (fun i -> i.ToString()) |> List.ofSeq
    | _ -> []

let private storedEntries (node: JsonNode) =
    match node.["entries"] with
    | :? JsonArray as items -> items |> Seq.map (fun i -> i.ToJsonString()) |> List.ofSeq
    | _ -> []

/// The one entry every mutation test starts from: 30 minutes, persisted, so
/// it has a version and can be the target of a command.
let private existing = persistedEntry "e1" (minutes 30) "sha-1"

// ---------------------------------------------------------------------------
// create
// ---------------------------------------------------------------------------

[<Fact>]
let ``a create in billable units is converted by the kernel, not the caller`` () =
    let result =
        answer
            []
            """{ "kind": "create", "entryId": "e9", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "date": "2026-09-10",
                 "durationUnits": 5, "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted result)
    // 5 units is 30 exact minutes. The request never named a millisecond.
    Assert.Contains("\"exact_duration_ms\":1800000", List.head (storedEntries result))
    Assert.Equal<string list>([ "PersistNewEntry" ], effects result)

[<Fact>]
let ``a create with neither duration form is refused rather than defaulted`` () =
    let result =
        answer
            []
            """{ "kind": "create", "entryId": "e9", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "date": "2026-09-10",
                 "occurredAtMs": 1789000000 }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("missing 'durationMs' or 'durationUnits'", field result "error")

[<Fact>]
let ``a create with no occurredAtMs is refused rather than stamped with zero`` () =
    // A change with no time is not a change the history can honestly record
    // (TE-R-052). Defaulting to the epoch would produce a plausible-looking
    // revision that is simply false.
    let result =
        answer
            []
            """{ "kind": "create", "entryId": "e9", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "date": "2026-09-10", "durationUnits": 5 }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("missing 'occurredAtMs'", field result "error")

[<Fact>]
let ``an entry is manual exactly when it carries a reason for being manual`` () =
    let result =
        answer
            []
            """{ "kind": "create", "entryId": "e9", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "date": "2026-09-10", "durationUnits": 5,
                 "manualReason": "Worked from notes while the timer was off.",
                 "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted result)
    let stored = List.head (storedEntries result)
    Assert.Contains("\"origin_kind\":\"manual\"", stored)
    Assert.Contains("Worked from notes", stored)

// ---------------------------------------------------------------------------
// correct
// ---------------------------------------------------------------------------

[<Fact>]
let ``a correction replaces the entry in place rather than appending a second`` () =
    // This is `mergeById`'s replace branch. Without it the page would show
    // the same entry twice and double its own total.
    let result =
        answer
            [ existing ]
            """{ "kind": "correct", "entryId": "e1", "expectedVersion": "sha-1",
                 "projectId": "northline", "activityTypeId": "research",
                 "date": "2026-09-10", "durationUnits": 10,
                 "reason": "Logged against the wrong client.",
                 "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted result)
    Assert.Equal(1, List.length (storedEntries result))
    let stored = List.head (storedEntries result)
    Assert.Contains("\"exact_duration_ms\":3600000", stored)
    Assert.Contains("\"project_id\":\"northline\"", stored)
    // The prior revision survives: correction is supersession, not mutation.
    Assert.Contains("\"change_kind\":\"created\"", stored)
    Assert.Equal<string list>([ "PersistCorrection" ], effects result)

[<Fact>]
let ``a correction without a reason is refused`` () =
    let result =
        answer
            [ existing ]
            """{ "kind": "correct", "entryId": "e1", "expectedVersion": "sha-1",
                 "projectId": "northline", "activityTypeId": "research",
                 "date": "2026-09-10", "durationUnits": 10, "occurredAtMs": 1789000000 }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("missing 'reason'", field result "error")

[<Fact>]
let ``a mutation that names no version is refused before any transition sees it`` () =
    // Not a VersionConflict — the command is malformed. A caller that cannot
    // say which version it read must not reach a transition at all
    // (TE-R-070).
    let result =
        answer
            [ existing ]
            """{ "kind": "void", "entryId": "e1", "reason": "Recorded twice.",
                 "occurredAtMs": 1789000000 }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("missing 'expectedVersion'", field result "error")

// ---------------------------------------------------------------------------
// void and restore
// ---------------------------------------------------------------------------

[<Fact>]
let ``void and restore are distinct commands against the same fields`` () =
    // `VoidEntryRequest` and `RestoreEntryRequest` have identical field sets,
    // which once cost 13 compile errors. This asserts they reach DIFFERENT
    // transitions, which a wrong annotation would silently break.
    let voided =
        answer
            [ existing ]
            """{ "kind": "void", "entryId": "e1", "expectedVersion": "sha-1",
                 "reason": "Recorded twice.", "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted voided)
    Assert.Equal<string list>([ "PersistVoid" ], effects voided)
    Assert.Contains("\"state_kind\":\"void\"", List.head (storedEntries voided))

    let restored =
        answer
            [ { existing with State = Void(reason "Recorded twice.", instant 1789000000L) } ]
            """{ "kind": "restore", "entryId": "e1", "expectedVersion": "sha-1",
                 "reason": "Removed in error.", "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted restored)
    Assert.Equal<string list>([ "PersistRestore" ], effects restored)
    Assert.Contains("\"state_kind\":\"active\"", List.head (storedEntries restored))

// ---------------------------------------------------------------------------
// split
// ---------------------------------------------------------------------------

[<Fact>]
let ``a split returns the source and every child together`` () =
    let result =
        answer
            [ existing ]
            """{ "kind": "split", "entryId": "e1", "expectedVersion": "sha-1",
                 "occurredAtMs": 1789000000,
                 "children": [
                   { "entryId": "e1a", "durationUnits": 2, "projectId": "echelon-foundry",
                     "activityTypeId": "research", "description": "First half" },
                   { "entryId": "e1b", "durationUnits": 3, "projectId": "northline",
                     "activityTypeId": "research" } ] }"""

    Assert.True(isAccepted result)
    // The source plus two children, all in one answer, because they must be
    // persisted together or not at all (TE-R-035).
    Assert.Equal(3, List.length (storedEntries result))
    Assert.Equal<string list>([ "PersistSplit" ], effects result)

[<Fact>]
let ``a split whose children lose time is refused by the domain`` () =
    // 2 + 2 units is 24 minutes against a 30-minute source. The kernel does
    // not check this; it hands the command over and the transition refuses.
    let result =
        answer
            [ existing ]
            """{ "kind": "split", "entryId": "e1", "expectedVersion": "sha-1",
                 "occurredAtMs": 1789000000,
                 "children": [
                   { "entryId": "e1a", "durationUnits": 2, "projectId": "echelon-foundry",
                     "activityTypeId": "research" },
                   { "entryId": "e1b", "durationUnits": 2, "projectId": "echelon-foundry",
                     "activityTypeId": "research" } ] }"""

    Assert.Equal("true", field result "ok")
    Assert.Equal("false", field result "accepted")
    // The rejection names both quantities — 1_800_000 ms expected against
    // 1_440_000 ms supplied — so the page can say what was lost rather than
    // only that something was.
    Assert.Contains("SplitDoesNotPreserveTotal (1800000L, 1440000L)", field result "rejection")

[<Fact>]
let ``a child that will not parse fails the whole split rather than being dropped`` () =
    // Dropping it would change the total silently, which is precisely what
    // split exists to make impossible (TE-R-040).
    let result =
        answer
            [ existing ]
            """{ "kind": "split", "entryId": "e1", "expectedVersion": "sha-1",
                 "occurredAtMs": 1789000000,
                 "children": [
                   { "entryId": "e1a", "durationUnits": 2, "projectId": "echelon-foundry",
                     "activityTypeId": "research" },
                   { "entryId": "e1b", "projectId": "echelon-foundry",
                     "activityTypeId": "research" } ] }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("missing 'durationMs' or 'durationUnits'", field result "error")

// ---------------------------------------------------------------------------
// merge
// ---------------------------------------------------------------------------

[<Fact>]
let ``a merge computes its own total from the sources`` () =
    let other = persistedEntry "e2" (minutes 12) "sha-2"

    let result =
        answer
            [ existing; other ]
            """{ "kind": "merge", "newEntryId": "m1", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "reason": "Same task, two timers.",
                 "occurredAtMs": 1789000000,
                 "sources": [
                   { "entryId": "e1", "expectedVersion": "sha-1" },
                   { "entryId": "e2", "expectedVersion": "sha-2" } ] }"""

    Assert.True(isAccepted result)
    // The request carried no duration at all. 30 + 12 minutes = 2_520_000 ms,
    // computed by the transition, so it cannot disagree with its sources.
    let merged =
        storedEntries result |> List.filter (fun s -> s.Contains "\"entry_id\":\"m1\"")

    Assert.Equal(1, List.length merged)
    Assert.Contains("\"exact_duration_ms\":2520000", List.head merged)
    Assert.Equal<string list>([ "PersistMerge" ], effects result)

[<Fact>]
let ``merge sources are superseded, so their time stops counting once`` () =
    let other = persistedEntry "e2" (minutes 12) "sha-2"

    let result =
        answer
            [ existing; other ]
            """{ "kind": "merge", "newEntryId": "m1", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "reason": "Same task, two timers.",
                 "occurredAtMs": 1789000000,
                 "sources": [
                   { "entryId": "e1", "expectedVersion": "sha-1" },
                   { "entryId": "e2", "expectedVersion": "sha-2" } ] }"""

    // Both sources and the new entry: the loaded set with the delta folded in.
    Assert.Equal(3, List.length (storedEntries result))

    let superseded =
        storedEntries result
        |> List.filter (fun s -> s.Contains "\"state_kind\":\"superseded_by_merge\"")

    // Superseded rather than void, because void is restorable — restoring a
    // merged source would return its time to the totals while the merged
    // entry still carries it (DF-TE-0006).
    Assert.Equal(2, List.length superseded)

// ---------------------------------------------------------------------------
// attach evidence
// ---------------------------------------------------------------------------

[<Fact>]
let ``attaching evidence records it against the entry`` () =
    let result =
        answer
            [ existing ]
            """{ "kind": "attachEvidence", "entryId": "e1", "expectedVersion": "sha-1",
                 "uri": "https://example.invalid/spec.pdf", "label": "The brief",
                 "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted result)
    let stored = List.head (storedEntries result)
    Assert.Contains("https://example.invalid/spec.pdf", stored)
    Assert.Contains("The brief", stored)
    Assert.Equal<string list>([ "PersistEvidenceAttachment" ], effects result)

[<Fact>]
let ``evidence is stamped with the moment the command says it occurred`` () =
    // So the evidence's timestamp and its revision's agree by construction
    // rather than by a second clock read.
    let result =
        answer
            [ existing ]
            """{ "kind": "attachEvidence", "entryId": "e1", "expectedVersion": "sha-1",
                 "uri": "https://example.invalid/spec.pdf", "occurredAtMs": 1789000000 }"""

    Assert.True(isAccepted result)
    Assert.Contains("\"attached_at_ms\":1789000000", List.head (storedEntries result))

// ---------------------------------------------------------------------------
// The vocabulary itself
// ---------------------------------------------------------------------------

[<Fact>]
let ``an unknown command kind is named rather than ignored`` () =
    // A page and a kernel that have drifted apart should say so, not silently
    // do nothing.
    let result = answer [] """{ "kind": "teleport", "occurredAtMs": 1 }"""

    Assert.Equal("false", field result "ok")
    Assert.Equal("unsupported command kind 'teleport'", field result "error")

[<Fact>]
let ``every command kind the page can send reaches a transition`` () =
    // A guard against adding a `Command` case and forgetting the wire name.
    // Each of these is well formed, so none may fail with a PARSE error; a
    // domain rejection would still be a pass for this test's question.
    let cases =
        [ """{ "kind": "create", "entryId": "e9", "projectId": "echelon-foundry",
               "activityTypeId": "research", "date": "2026-09-10", "durationUnits": 5,
               "occurredAtMs": 1789000000 }"""
          """{ "kind": "correct", "entryId": "e1", "expectedVersion": "sha-1",
               "projectId": "echelon-foundry", "activityTypeId": "research",
               "date": "2026-09-10", "durationUnits": 5, "reason": "r",
               "occurredAtMs": 1789000000 }"""
          """{ "kind": "void", "entryId": "e1", "expectedVersion": "sha-1", "reason": "r",
               "occurredAtMs": 1789000000 }"""
          """{ "kind": "restore", "entryId": "e1", "expectedVersion": "sha-1", "reason": "r",
               "occurredAtMs": 1789000000 }"""
          """{ "kind": "split", "entryId": "e1", "expectedVersion": "sha-1",
               "occurredAtMs": 1789000000,
               "children": [ { "entryId": "e1a", "durationUnits": 5,
                               "projectId": "echelon-foundry", "activityTypeId": "research" } ] }"""
          """{ "kind": "merge", "newEntryId": "m1", "projectId": "echelon-foundry",
               "activityTypeId": "research", "reason": "r", "occurredAtMs": 1789000000,
               "sources": [ { "entryId": "e1", "expectedVersion": "sha-1" } ] }"""
          """{ "kind": "attachEvidence", "entryId": "e1", "expectedVersion": "sha-1",
               "uri": "https://example.invalid/x", "occurredAtMs": 1789000000 }""" ]

    for case in cases do
        let result = answer [ existing ] case
        Assert.Equal("true", field result "ok")
