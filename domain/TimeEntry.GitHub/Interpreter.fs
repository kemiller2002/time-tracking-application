/// Tier 4 — Host. Executes the effects Tier 2 requested.
///
/// Tier 2 returns effects as data and never performs them (TE-R-093). This is
/// the only place they actually happen. The interpreter is the counterpart to
/// that rule: it contains no domain decisions, only the mechanics of reading
/// and writing, plus the translation of a store-level precondition failure
/// into the conflict the domain understands.
module TimeEntry.GitHub.Interpreter

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Effects
open TimeEntry.Persistence
open TimeEntry.GitHub.Store

let private optionOf (result: Result<'a, 'e>) =
    match result with
    | Ok value -> Some value
    | Error _ -> None

/// A stored file that could not be turned back into an entry.
///
/// Collected rather than thrown so one corrupt record is one unreadable entry,
/// not a failed load (TE-R-084).
type UnreadableEntry = { Path: string; Detail: string }

/// A stale write, in the domain's terms rather than the store's.
type PersistConflict =
    { EntryId: EntryId option
      Path: string
      Expected: VersionToken option
      Actual: VersionToken option }

/// What executing one effect produced.
type EffectOutcome =
    | EntriesLoaded of entries: TimeEntry list * unreadable: UnreadableEntry list
    /// New version token per entry written, so Tier 3 can update the state it
    /// holds without re-reading.
    | Persisted of versions: (EntryId * VersionToken) list
    /// TE-R-070/TE-R-072: nothing was written, and the domain must reconcile.
    | Conflicted of PersistConflict
    | CatalogueLoaded of Catalogue
    /// The catalogue file is missing or cannot be interpreted.
    ///
    /// Distinct from `Failed` because the remedy differs: a transport failure
    /// is retryable, a corrupt catalogue needs a human. Both prevent recording
    /// time, and neither may be reported as an *empty* catalogue — an empty
    /// catalogue refuses every project, which a test in `CatalogueTests`
    /// pins.
    | CatalogueUnreadable of detail: string
    | Failed of StoreError

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

let private writeFor (request: PersistRequest) : FileWrite =
    { Path = Layout.entryPath request.Entry.Id
      Content = Serialization.write (Mapping.toDocument request.Entry)
      ExpectedSha = request.ExpectedVersion |> Option.map VersionToken.value }

/// Map a store precondition failure back into the domain's conflict language.
///
/// The store speaks in paths and SHAs; the domain speaks in entry ids and
/// opaque version tokens. Translating here is what keeps GitHub's vocabulary
/// out of the domain (TE-R-094).
let private conflictFrom (requests: PersistRequest list) (path: string) expected actual : PersistConflict =
    let entryId =
        requests
        |> List.tryFind (fun r -> Layout.entryPath r.Entry.Id = path)
        |> Option.map (fun r -> r.Entry.Id)

    let token (raw: string option) =
        raw |> Option.bind (fun value -> optionOf (VersionToken.create value))

    { EntryId = entryId
      Path = path
      Expected = token expected
      Actual = token actual }

/// Commit a group of writes as one unit.
///
/// Every persist effect goes through here, including single-entry ones, so
/// there is one write path rather than two. The commit is a compare-and-swap:
/// per-file `ExpectedSha` preconditions plus `ExpectedHeadSha` on the branch
/// ref, so a concurrent change fails the whole commit and lands nothing.
/// Split and merge depend on that — they were shaped as one grouped effect
/// precisely so the source and its children cannot land separately
/// (TE-R-035).
let private commitAll (store: GitHubStore) (message: string) (requests: PersistRequest list) =
    async {
        let! head = store.ReadHead()

        match head with
        | Error error -> return Failed error
        | Ok headSha ->
            let request =
                { Message = message
                  Writes = requests |> List.map writeFor
                  ExpectedHeadSha = headSha }

            let! committed = store.Commit request

            match committed with
            | Error(PreconditionFailed(path, expected, actual)) ->
                return Conflicted(conflictFrom requests path expected actual)
            | Error(HeadMoved(expected, actual)) ->
                // The branch moved under us. Reported as a conflict, not a
                // failure: nothing was written and the caller must re-read,
                // which is the same obligation a stale file produces.
                return
                    Conflicted
                        { EntryId = requests |> List.tryHead |> Option.map (fun r -> r.Entry.Id)
                          Path = "<branch head>"
                          Expected = optionOf (VersionToken.create expected)
                          Actual = optionOf (VersionToken.create actual) }
            | Error error -> return Failed error
            | Ok result ->
                let byPath = Map.ofList result.WrittenShas

                let versions =
                    requests
                    |> List.choose (fun r ->
                        let path = Layout.entryPath r.Entry.Id

                        Map.tryFind path byPath
                        |> Option.bind (fun sha ->
                            optionOf (VersionToken.create sha)
                            |> Option.map (fun token -> r.Entry.Id, token)))

                return Persisted versions
    }

// ---------------------------------------------------------------------------
// Loading
// ---------------------------------------------------------------------------

/// Read and decode one stored document.
let private loadOne (store: GitHubStore) (path: string) =
    async {
        let unreadable detail = Choice2Of2 { Path = path; Detail = detail }
        let! file = store.ReadFile path

        return
            match file with
            | Error error -> unreadable (sprintf "%A" error)
            // Listed but gone: a concurrent delete. Not fatal.
            | Ok None -> unreadable "listed in the tree but not readable"
            | Ok(Some stored) ->
                match Serialization.read stored.Content with
                | Error decodeError -> unreadable (sprintf "%A" decodeError)
                | Ok document ->
                    let version = optionOf (VersionToken.create stored.Sha)

                    match Mapping.fromDocument version document with
                    | Error documentError -> unreadable (sprintf "%A" documentError)
                    | Ok entry -> Choice1Of2 entry
    }

/// Read every entry document in the ledger.
///
/// The `forDate` on `LoadEntries` is deliberately *not* used to narrow the
/// read. Paths are keyed by entry identity, not date (see `Layout`), so the
/// interpreter cannot filter by path without interpreting a date — which would
/// put a domain decision in Tier 4. Filtering by date stays in Tier 3's
/// projection, where `EntryQuery.DateRange` already does it. The cost is one
/// recursive tree read, which is the trade `Layout` documents.
let private loadEntries (store: GitHubStore) =
    async {
        let! paths = store.ListEntryPaths()

        match paths with
        | Error error -> return Failed error
        | Ok allPaths ->
            // Sequential rather than parallel: GitHub's secondary rate limit
            // punishes bursts, and a deterministic order keeps the result
            // reproducible for a given store state.
            let! results =
                allPaths
                |> List.filter Layout.isEntryPath
                |> List.map (loadOne store)
                |> Async.Sequential

            let entries =
                results
                |> Array.choose (fun r ->
                    match r with
                    | Choice1Of2 entry -> Some entry
                    | Choice2Of2 _ -> None)
                |> List.ofArray

            let unreadable =
                results
                |> Array.choose (fun r ->
                    match r with
                    | Choice1Of2 _ -> None
                    | Choice2Of2 failure -> Some failure)
                |> List.ofArray

            return EntriesLoaded(entries, unreadable)
    }

/// Read the catalogue.
///
/// A missing or corrupt catalogue is reported as unreadable, never as an empty
/// catalogue: `Catalogue.empty` refuses every project, so returning it would
/// present "the project list failed to load" as "you have no projects".
let private loadCatalogue (store: GitHubStore) =
    async {
        let! file = store.ReadFile Layout.CataloguePath

        match file with
        | Error error -> return Failed error
        | Ok None -> return CatalogueUnreadable(Layout.CataloguePath + " is not present")
        | Ok(Some stored) ->
            match Serialization.readCatalogue stored.Content with
            | Error decodeError -> return CatalogueUnreadable(sprintf "%A" decodeError)
            | Ok document ->
                match Mapping.catalogueFromDocument document with
                | Error documentError -> return CatalogueUnreadable(sprintf "%A" documentError)
                | Ok catalogue -> return CatalogueLoaded catalogue
    }

// ---------------------------------------------------------------------------
// Dispatch
// ---------------------------------------------------------------------------

/// Execute one effect.
let interpret (store: GitHubStore) (effect: Effect) : Async<EffectOutcome> =
    match effect with
    | LoadEntries _ -> loadEntries store

    | LoadProjects -> loadCatalogue store

    | PersistNewEntry request -> commitAll store "Record time entry" [ request ]
    | PersistCorrection request -> commitAll store "Correct time entry" [ request ]
    | PersistVoid request -> commitAll store "Remove time entry from totals" [ request ]
    | PersistRestore request -> commitAll store "Restore time entry to totals" [ request ]
    | PersistEvidenceAttachment request -> commitAll store "Attach evidence to time entry" [ request ]

    | PersistSplit(source, children) ->
        // One commit: the superseded source and every child land together or
        // not at all.
        commitAll store "Split time entry" (source :: children)

    | PersistMerge(target, sources) -> commitAll store "Merge time entries" (target :: sources)
