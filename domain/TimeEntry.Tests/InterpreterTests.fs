/// Verifies the effect interpreter (TE-R-035, TE-R-070..TE-R-074, TE-R-084,
/// TE-R-093).
///
/// The store is a record of functions, so the whole write path — including
/// optimistic concurrency and multi-file atomicity — is exercised here with no
/// network. That is the payoff of Tier 2 returning effects as data rather than
/// performing them.
module TimeEntry.Tests.InterpreterTests

open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Transitions
open TimeEntry.Persistence
open TimeEntry.GitHub.Store
open TimeEntry.GitHub.Interpreter
open TimeEntry.Tests.Helpers
open TimeEntry.Tests.FakeStore

let private run effect (fake: Fake) =
    interpret fake.Store effect |> Async.RunSynchronously

let private storedFor (entry: TimeEntry) =
    Layout.entryPath entry.Id, Serialization.write (Mapping.toDocument entry)

// ---------------------------------------------------------------------------
// Writing a new entry
// ---------------------------------------------------------------------------

[<Fact>]
let ``persisting a new entry writes it and returns its version`` () =
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 52)
          Attribution = attribution "e1-r1" }

    let entries, effects = accepted (createEntry request)
    let entry = single entries
    let fake = Fake([])

    match run (List.exactlyOne effects) fake with
    | Persisted [ (id, token) ] ->
        Assert.Equal(entryId "e1", id)
        // The returned token is the file's actual new SHA.
        Assert.Equal(fake.ShaOf(Layout.entryPath entry.Id), Some(VersionToken.value token))
    | other -> failwithf "expected Persisted, got %A" other

    Assert.Equal<string list>([ "ledger/entries/e1/e1.json" ], fake.Paths)

[<Fact>]
let ``creating an entry that already exists is a conflict, not an overwrite`` () =
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 52)
          Attribution = attribution "e1-r1" }

    let entries, effects = accepted (createEntry request)
    let entry = single entries
    // The file is already there — another device got here first.
    let fake = Fake([ storedFor entry ])

    match run (List.exactlyOne effects) fake with
    | Conflicted conflict ->
        Assert.Equal(Some(entryId "e1"), conflict.EntryId)
        Assert.Equal(None, conflict.Expected)
        Assert.True(conflict.Actual.IsSome)
    | other -> failwithf "expected Conflicted, got %A" other

    Assert.Equal(0, fake.CommitCount)

// ---------------------------------------------------------------------------
// Optimistic concurrency (TE-R-070, TE-R-072, TE-R-073)
// ---------------------------------------------------------------------------

/// An entry as the store holds it, with its version set to the real file SHA.
let private seeded (id: string) (duration: Duration) =
    let draft = { persistedEntry id duration "placeholder" with Version = None }
    let path, content = storedFor draft
    let fake = Fake([ path, content ])
    let sha = (fake.ShaOf path).Value
    { draft with Version = Some(version sha) }, fake

[<Fact>]
let ``a correction against the current version is written`` () =
    let entry, fake = seeded "e1" (minutes 52)
    let sha = VersionToken.value (Option.get entry.Version)
    let request = correctionRequest entry (minutes 46) sha
    let _, effects = accepted (correctEntry request entry)

    match run (List.exactlyOne effects) fake with
    | Persisted [ (id, token) ] ->
        Assert.Equal(entryId "e1", id)
        // A new version, because the content changed.
        Assert.NotEqual<string>(sha, VersionToken.value token)
    | other -> failwithf "expected Persisted, got %A" other

[<Fact>]
let ``a correction against a stale version writes nothing`` () =
    // TE-R-070: never silently overwrite a newer correction.
    let entry, fake = seeded "e1" (minutes 52)
    let sha = VersionToken.value (Option.get entry.Version)
    let request = correctionRequest entry (minutes 46) sha
    let _, effects = accepted (correctEntry request entry)

    // Someone else corrects it first.
    let path = Layout.entryPath entry.Id
    let otherEntry = { entry with Effective = { entry.Effective with Duration = minutes 30 } }
    fake.ChangeBehindOurBack(path, snd (storedFor otherEntry))
    let shaAfterTheirWrite = fake.ShaOf path

    match run (List.exactlyOne effects) fake with
    | Conflicted _ -> ()
    | other -> failwithf "expected Conflicted, got %A" other

    // Their content is untouched: nothing was overwritten.
    Assert.Equal(shaAfterTheirWrite, fake.ShaOf path)
    Assert.Equal(0, fake.CommitCount)

[<Fact>]
let ``a conflict reports both versions so the UI can offer a choice`` () =
    // TE-R-071: the conflict screen shows current saved vs proposed.
    let entry, fake = seeded "e1" (minutes 52)
    let sha = VersionToken.value (Option.get entry.Version)
    let request = correctionRequest entry (minutes 46) sha
    let _, effects = accepted (correctEntry request entry)

    let path = Layout.entryPath entry.Id
    let otherEntry = { entry with Effective = { entry.Effective with Duration = minutes 30 } }
    fake.ChangeBehindOurBack(path, snd (storedFor otherEntry))

    match run (List.exactlyOne effects) fake with
    | Conflicted conflict ->
        Assert.Equal(Some(version sha), conflict.Expected)
        Assert.Equal(fake.ShaOf path, conflict.Actual |> Option.map VersionToken.value)
        Assert.NotEqual<VersionToken option>(conflict.Expected, conflict.Actual)
    | other -> failwithf "expected Conflicted, got %A" other

[<Fact>]
let ``a branch head that moves during the commit is a conflict and writes nothing`` () =
    // The genuine race: HEAD is read, then a commit is attempted against it.
    // Another writer landing in that window must lose, and this is the only
    // check that catches it — the entry's own file is untouched, so every
    // per-file precondition would pass.
    let entry, fake = seeded "e1" (minutes 52)
    let sha = VersionToken.value (Option.get entry.Version)
    let request = correctionRequest entry (minutes 46) sha
    let _, effects = accepted (correctEntry request entry)

    fake.InterfereDuringCommit(fun () -> fake.ChangeBehindOurBack("ledger/entries/zz/zz.json", "{}"))

    match run (List.exactlyOne effects) fake with
    | Conflicted conflict -> Assert.Equal("<branch head>", conflict.Path)
    | other -> failwithf "expected Conflicted on head, got %A" other

    Assert.Equal(0, fake.CommitCount)

// ---------------------------------------------------------------------------
// Atomicity of grouped writes (TE-R-035)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a split lands the source and every child in one commit`` () =
    let entry, fake = seeded "e1" (minutes 60)
    let sha = VersionToken.value (Option.get entry.Version)
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let _, effects = accepted (splitEntry (splitRequest entry sha children) entry)

    match run (List.exactlyOne effects) fake with
    | Persisted versions -> Assert.Equal(3, List.length versions)
    | other -> failwithf "expected Persisted, got %A" other

    // One commit, not three: the group cannot be half-applied.
    Assert.Equal(1, fake.CommitCount)
    Assert.Equal(3, List.length fake.Paths)

[<Fact>]
let ``a split whose source is stale writes no child at all`` () =
    // The atomicity that matters: a partially applied split would either lose
    // or duplicate recorded time.
    let entry, fake = seeded "e1" (minutes 60)
    let sha = VersionToken.value (Option.get entry.Version)
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let _, effects = accepted (splitEntry (splitRequest entry sha children) entry)

    let path = Layout.entryPath entry.Id
    let otherEntry = { entry with Effective = { entry.Effective with Duration = minutes 30 } }
    fake.ChangeBehindOurBack(path, snd (storedFor otherEntry))

    match run (List.exactlyOne effects) fake with
    | Conflicted _ -> ()
    | other -> failwithf "expected Conflicted, got %A" other

    // Still only the source file: neither child leaked through.
    Assert.Equal<string list>([ path ], fake.Paths)
    Assert.Equal(0, fake.CommitCount)

[<Fact>]
let ``a split whose child path already exists writes nothing`` () =
    let entry, fake0 = seeded "e1" (minutes 60)
    let sha = VersionToken.value (Option.get entry.Version)
    let children = [ splitChild "c1" (minutes 36); splitChild "c2" (minutes 24) ]
    let _, effects = accepted (splitEntry (splitRequest entry sha children) entry)

    // Rebuild the store with c2 already present, keeping the source's SHA
    // valid so only the child's precondition can fail.
    let sourcePath, sourceContent = storedFor { entry with Version = None }
    let fake = Fake([ sourcePath, sourceContent; "ledger/entries/c2/c2.json", "{}" ])
    ignore fake0

    match run (List.exactlyOne effects) fake with
    | Conflicted conflict -> Assert.Equal("ledger/entries/c2/c2.json", conflict.Path)
    | other -> failwithf "expected Conflicted on the child, got %A" other

    Assert.Equal(0, fake.CommitCount)
    // c1 was never created.
    Assert.DoesNotContain("ledger/entries/c1/c1.json", fake.Paths)

[<Fact>]
let ``a merge lands the target and every source in one commit`` () =
    let a = { persistedEntry "e1" (minutes 36) "x" with Version = None }
    let b = { persistedEntry "e2" (minutes 24) "x" with Version = None }
    let fake = Fake([ storedFor a; storedFor b ])

    let withVersion (entry: TimeEntry) =
        { entry with Version = Some(version (fake.ShaOf(Layout.entryPath entry.Id)).Value) }

    let a, b = withVersion a, withVersion b

    let request =
        mergeRequest
            [ mergeSource "e1" (VersionToken.value (Option.get a.Version))
              mergeSource "e2" (VersionToken.value (Option.get b.Version)) ]

    let _, effects = accepted (mergeEntries request [ a; b ])

    match run (List.exactlyOne effects) fake with
    | Persisted versions -> Assert.Equal(3, List.length versions)
    | other -> failwithf "expected Persisted, got %A" other

    Assert.Equal(1, fake.CommitCount)
    Assert.Equal(3, List.length fake.Paths)

// ---------------------------------------------------------------------------
// Loading
// ---------------------------------------------------------------------------

[<Fact>]
let ``loading returns entries carrying the file sha as their version`` () =
    let a = { persistedEntry "e1" (minutes 30) "x" with Version = None }
    let b = { persistedEntry "e2" (minutes 60) "x" with Version = None }
    let fake = Fake([ storedFor a; storedFor b ])

    match run (LoadEntries defaultDate) fake with
    | EntriesLoaded(entries, unreadable) ->
        Assert.Equal(2, List.length entries)
        Assert.Empty(unreadable)

        for entry in entries do
            Assert.Equal(fake.ShaOf(Layout.entryPath entry.Id), entry.Version |> Option.map VersionToken.value)
    | other -> failwithf "expected EntriesLoaded, got %A" other

[<Fact>]
let ``a persisted entry loads back identically`` () =
    // The full write-then-read cycle, which is what a second device does.
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 52)
          Attribution = attribution "e1-r1" }

    let entries, effects = accepted (createEntry request)
    let original = single entries
    let fake = Fake([])

    match run (List.exactlyOne effects) fake with
    | Persisted [ (_, token) ] ->
        match run (LoadEntries defaultDate) fake with
        | EntriesLoaded([ loaded ], []) ->
            // Identical apart from the version, which only exists once stored.
            Assert.Equal({ original with Version = Some token }, loaded)
        | other -> failwithf "expected one loaded entry, got %A" other
    | other -> failwithf "expected Persisted, got %A" other

[<Fact>]
let ``one corrupt file does not stop the others loading`` () =
    // TE-R-084: a bad record is one unreadable entry, not a failed projection.
    let good = { persistedEntry "e1" (minutes 30) "x" with Version = None }

    let fake =
        Fake([ storedFor good; "ledger/entries/ba/bad.json", "{ not json at all" ])

    match run (LoadEntries defaultDate) fake with
    | EntriesLoaded([ loaded ], [ failure ]) ->
        Assert.Equal(entryId "e1", loaded.Id)
        Assert.Equal("ledger/entries/ba/bad.json", failure.Path)
        Assert.Contains("MalformedJson", failure.Detail)
    | other -> failwithf "expected one loaded and one unreadable, got %A" other

[<Fact>]
let ``a file with valid json but invalid content is reported with its path`` () =
    let good = { persistedEntry "e1" (minutes 30) "x" with Version = None }

    let fake =
        Fake(
            [ storedFor good
              "ledger/entries/ba/bad.json", """{ "schema_version": "1.0.0", "entry_id": "x" }""" ]
        )

    match run (LoadEntries defaultDate) fake with
    | EntriesLoaded([ _ ], [ failure ]) -> Assert.Equal("ledger/entries/ba/bad.json", failure.Path)
    | other -> failwithf "expected one loaded and one unreadable, got %A" other

[<Fact>]
let ``paths outside the ledger are ignored`` () =
    let good = { persistedEntry "e1" (minutes 30) "x" with Version = None }
    let fake = Fake([ storedFor good; "README.md", "# not an entry" ])

    match run (LoadEntries defaultDate) fake with
    | EntriesLoaded([ _ ], []) -> ()
    | other -> failwithf "expected the README to be ignored, got %A" other

// ---------------------------------------------------------------------------
// Failures and gaps
// ---------------------------------------------------------------------------

[<Fact>]
let ``a transport failure is reported as a failure, not a conflict`` () =
    // A conflict means "re-read and decide"; a failure means "retry later".
    // Conflating them would lose that distinction.
    let fake = Fake([], TransportFailure "connection reset")

    match run (LoadEntries defaultDate) fake with
    | Failed(TransportFailure _) -> ()
    | other -> failwithf "expected Failed, got %A" other

[<Fact>]
let ``an authorization failure on a write is reported as a failure`` () =
    let request =
        { NewEntryId = entryId "e1"
          Facts = facts (minutes 52)
          Attribution = attribution "e1-r1" }

    let _, effects = accepted (createEntry request)
    let fake = Fake([], Unauthorized "bad credentials")

    match run (List.exactlyOne effects) fake with
    | Failed(Unauthorized _) -> ()
    | other -> failwithf "expected Failed, got %A" other

[<Fact>]
let ``rate limiting is reported as a failure carrying the advised wait`` () =
    let fake = Fake([], RateLimited(Some 60))

    match run (LoadEntries defaultDate) fake with
    | Failed(RateLimited(Some 60)) -> ()
    | other -> failwithf "expected Failed RateLimited, got %A" other

[<Fact>]
let ``loading projects reports that it is unimplemented rather than empty`` () =
    // Returning [] would look like "this user has no projects" and silently
    // break project validation.
    let fake = Fake([])

    match run LoadProjects fake with
    | NotSupported("LoadProjects", workItem) -> Assert.Equal("WI-0026", workItem)
    | other -> failwithf "expected NotSupported, got %A" other

[<Fact>]
let ``the interpreter performs no write while merely loading`` () =
    // TE-R-093's counterpart: reads must not mutate.
    let good = { persistedEntry "e1" (minutes 30) "x" with Version = None }
    let fake = Fake([ storedFor good ])
    let headBefore = fake.Head

    run (LoadEntries defaultDate) fake |> ignore

    Assert.Equal(headBefore, fake.Head)
    Assert.Equal(0, fake.CommitCount)
