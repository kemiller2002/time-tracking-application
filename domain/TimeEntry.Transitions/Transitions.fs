/// Tier 2 — State Transition. The legal changes, as pure functions.
///
/// Every function here is total, deterministic, and free of external effects:
/// given the same loaded state and the same command it returns the same
/// `Outcome`, and it neither reads a clock nor touches a network
/// (TE-R-093). This is what lets the whole domain be verified with no browser
/// and no GitHub call.
///
/// Each transition is written as a flat `Result` pipeline of named guards
/// followed by a single construction step. Guards are shared, so "check the
/// version" and "check the capability" cannot be forgotten in one transition
/// and remembered in another.
module TimeEntry.Transitions.Transitions

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Semantic.Catalogue
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Guards

// ---------------------------------------------------------------------------
// Create (TE-R-020)
// ---------------------------------------------------------------------------

/// A new entry needs no state guard and no version guard: it does not exist
/// yet. `ExpectedVersion = None` tells the interpreter to fail the write if it
/// turns out it does.
let createEntry (catalogue: Catalogue) (request: CreateEntryRequest) : Outcome =
    match requireCatalogue catalogue request.Facts.Project request.Facts.ActivityType with
    | Error rejection -> Rejected rejection
    | Ok() ->

    let entry =
        { Id = request.NewEntryId
          State = Active
          Effective = request.Facts
          History = [ revisionOf request.Attribution Created request.Facts ]
          Version = None }

    Accepted([ entry ], [ PersistNewEntry { Entry = entry; ExpectedVersion = None } ])

// ---------------------------------------------------------------------------
// Correct (TE-R-022, TE-R-050, TE-R-051)
// ---------------------------------------------------------------------------

/// Correction is supersession, not mutation: the prior revision stays in
/// `History` and the corrected values become `Effective`. The entry keeps its
/// identity and stays `Active`, so it continues to count toward totals with
/// its new values.
let correctEntry (catalogue: Catalogue) (request: CorrectEntryRequest) (entry: TimeEntry) : Outcome =
    match
        requireReferenceMove
            catalogue
            [ entry.Effective.Project ]
            [ entry.Effective.ActivityType ]
            request.CorrectedFacts.Project
            request.CorrectedFacts.ActivityType
    with
    | Error rejection -> Rejected rejection
    | Ok() ->

    entry
    |> transition CanCorrect request.EntryId request.ExpectedVersion (fun current ->
        let revision =
            revisionOf request.Attribution (Corrected request.Reason) request.CorrectedFacts

        let corrected =
            TimeEntry.appendRevision revision Active request.CorrectedFacts current

        corrected,
        PersistCorrection
            { Entry = corrected
              ExpectedVersion = Some request.ExpectedVersion })

// ---------------------------------------------------------------------------
// Void (TE-R-024, TE-R-060, TE-R-061, TE-R-063)
// ---------------------------------------------------------------------------

/// Void excludes the entry from totals and retains the record. It is not a
/// delete: `Effective` and `History` are untouched, and only `State` changes.
let voidEntry (request: VoidEntryRequest) (entry: TimeEntry) : Outcome =
    entry
    |> transition CanVoid request.EntryId request.ExpectedVersion (fun current ->
        let revision =
            revisionOf request.Attribution (Voided request.Reason) current.Effective

        let voided =
            TimeEntry.appendRevision
                revision
                (Void(request.Reason, request.Attribution.OccurredAt))
                current.Effective
                current

        voided,
        PersistVoid
            { Entry = voided
              ExpectedVersion = Some request.ExpectedVersion })

// ---------------------------------------------------------------------------
// Restore (TE-R-025, TE-R-062)
// ---------------------------------------------------------------------------

/// Restore returns a voided entry to `Active`. The void and the restore both
/// remain in history, so the restore screen can show void reason, void date,
/// and restore reason (TE-R-062).
let restoreEntry (request: RestoreEntryRequest) (entry: TimeEntry) : Outcome =
    entry
    |> transition CanRestore request.EntryId request.ExpectedVersion (fun current ->
        let revision =
            revisionOf request.Attribution (Restored request.Reason) current.Effective

        let restored =
            TimeEntry.appendRevision revision Active current.Effective current

        restored,
        PersistRestore
            { Entry = restored
              ExpectedVersion = Some request.ExpectedVersion })

// ---------------------------------------------------------------------------
// Attach evidence (TE-R-033)
// ---------------------------------------------------------------------------

let attachEvidence (request: AttachEvidenceRequest) (entry: TimeEntry) : Outcome =
    entry
    |> transition CanAttachEvidence request.EntryId request.ExpectedVersion (fun current ->
        let facts =
            { current.Effective with
                Evidence = current.Effective.Evidence @ [ request.Evidence ] }

        let revision =
            revisionOf request.Attribution (EvidenceAttached request.Evidence) facts

        let updated = TimeEntry.appendRevision revision current.State facts current

        updated,
        PersistEvidenceAttachment
            { Entry = updated
              ExpectedVersion = Some request.ExpectedVersion })

// ---------------------------------------------------------------------------
// Split (TE-R-023, TE-R-040, TE-R-043, TE-R-046)
// ---------------------------------------------------------------------------

/// Splitting supersedes the source and creates the children.
///
/// The source becomes `Superseded`, not `Void` and not `Active`: its time now
/// lives in the children, so leaving it `Active` would double-count and
/// marking it `Void` would misreport why it stopped counting.
///
/// Has its own guard pipeline rather than reusing `transition` because it
/// yields many entries and one grouped effect.
/// A split redistributes time that already exists, so a child keeping the
/// source's project or activity type is retaining a reference, not moving
/// time onto one.
let private requireChildrenCatalogue
    (catalogue: Catalogue)
    (source: EntryFacts)
    (children: SplitChild list)
    =
    children
    |> List.tryPick (fun child ->
        match
            requireReferenceMove
                catalogue
                [ source.Project ]
                [ source.ActivityType ]
                child.Project
                child.ActivityType
        with
        | Error rejection -> Some rejection
        | Ok() -> None)
    |> function
        | Some rejection -> Error rejection
        | None -> Ok()

let splitEntry (catalogue: Catalogue) (request: SplitEntryRequest) (entry: TimeEntry) : Outcome =
    let validated =
        requireChildrenCatalogue catalogue entry.Effective request.Children
        |> Result.bind (fun () -> requireSameEntry request.EntryId entry)
        |> Result.bind (fun () -> requireCapability CanSplit entry)
        |> Result.bind (fun () -> requireVersion request.ExpectedVersion entry)
        |> Result.bind (fun () -> requireAtLeastTwoChildren request.Children)
        |> Result.bind (fun () -> requireUniqueIdentities entry.Id request.Children)
        |> Result.bind (fun () -> requireReassignedEvidenceExists entry.Effective request.Children)
        |> Result.bind (fun () -> requireTotalPreserved entry.Effective.Duration request.Children)

    match validated with
    | Error rejection -> Rejected rejection
    | Ok() ->
        let childIds = request.Children |> List.map (fun c -> c.NewEntryId)

        let sourceRevision =
            revisionOf request.Attribution (SplitInto childIds) entry.Effective

        let supersededSource =
            TimeEntry.appendRevision
                sourceRevision
                (Superseded(SupersededBySplit childIds))
                entry.Effective
                entry

        let toChildEntry (child: SplitChild) =
            let facts =
                { entry.Effective with
                    Project = child.Project
                    ActivityType = child.ActivityType
                    Duration = child.Duration
                    Description = child.Description
                    Evidence = child.ReassignedEvidence }

            let revision =
                { Id = child.NewRevisionId
                  Change = CreatedBySplitOf entry.Id
                  Facts = facts
                  RecordedAt = request.Attribution.OccurredAt
                  RecordedBy = request.Attribution.Actor
                  Device = request.Attribution.Device }

            { Id = child.NewEntryId
              State = Active
              Effective = facts
              History = [ revision ]
              Version = None }

        let children = request.Children |> List.map toChildEntry

        let effect =
            PersistSplit(
                { Entry = supersededSource
                  ExpectedVersion = Some request.ExpectedVersion },
                children |> List.map (fun c -> { Entry = c; ExpectedVersion = None })
            )

        Accepted(supersededSource :: children, [ effect ])


// ---------------------------------------------------------------------------
// Merge (TE-R-026, DF-TE-0006)
// ---------------------------------------------------------------------------

let private requireAtLeastTwoSources (sources: MergeSource list) : Result<unit, Rejection> =
    let count = List.length sources

    if count >= 2 then
        Ok()
    else
        Error(MergeNeedsAtLeastTwoSources count)

let private requireUniqueMergeIdentities (target: EntryId) (sources: MergeSource list) : Result<unit, Rejection> =
    let ids = target :: (sources |> List.map (fun s -> s.EntryId))

    let duplicated =
        ids
        |> List.countBy id
        |> List.tryPick (fun (entryId, count) -> if count > 1 then Some entryId else None)

    match duplicated with
    | Some entryId -> Error(MergeSourceIdentityNotUnique entryId)
    | None -> Ok()

let private resolveSources
    (loaded: TimeEntry list)
    (sources: MergeSource list)
    : Result<(MergeSource * TimeEntry) list, Rejection> =
    let resolved =
        sources
        |> List.map (fun source ->
            match loaded |> List.tryFind (fun e -> e.Id = source.EntryId) with
            | Some entry -> Ok(source, entry)
            | None -> Error(EntryNotLoaded source.EntryId))

    let firstError =
        resolved
        |> List.tryPick (fun r ->
            match r with
            | Error rejection -> Some rejection
            | Ok _ -> None)

    match firstError with
    | Some rejection -> Error rejection
    | None ->
        Ok(
            resolved
            |> List.choose (fun r ->
                match r with
                | Ok pair -> Some pair
                | Error _ -> None)
        )

/// Every source must be mergeable in its own right: legal in its state, and
/// at the version the caller read. Checked per source because each was read
/// independently and any one may be stale (TE-R-070, TE-R-074 "offline edits
/// overlap").
let private requireAllSourcesMergeable (pairs: (MergeSource * TimeEntry) list) : Result<unit, Rejection> =
    let failure =
        pairs
        |> List.tryPick (fun (source, entry) ->
            match requireCapability CanMerge entry with
            | Error rejection -> Some rejection
            | Ok() ->
                match requireVersion source.ExpectedVersion entry with
                | Error rejection -> Some rejection
                | Ok() -> None)

    match failure with
    | Some rejection -> Error rejection
    | None -> Ok()

let private requireSingleDay (pairs: (MergeSource * TimeEntry) list) : Result<unit, Rejection> =
    let days =
        pairs
        |> List.map (fun (_, entry) -> EntryDate.dayNumber entry.Effective.Date)
        |> List.distinct

    if List.length days = 1 then
        Ok()
    else
        Error(MergeSpansMultipleDays(List.length days))

/// Merge N entries into one.
///
/// The merged duration is the exact integer sum of the sources' durations, so
/// total preservation is structural: there is no supplied total that could
/// disagree with the sources, and therefore no invariant to violate.
///
/// Each source becomes `Superseded(SupersededByMerge target)` rather than
/// `Void`. `Void` would be wrong and not merely inelegant: it is restorable,
/// so restoring one source of a completed merge would return its time to
/// totals while the merged entry still carries it. `Superseded` offers no
/// capabilities, making that unrepresentable (DF-TE-0006).
let mergeEntries (catalogue: Catalogue) (request: MergeEntriesRequest) (loaded: TimeEntry list) : Outcome =
    // Resolve the sources first, so their references count as retained: a
    // merge of entries on an archived project may keep that project.
    let retained =
        request.Sources
        |> List.choose (fun source -> loaded |> List.tryFind (fun e -> e.Id = source.EntryId))

    let validated =
        requireReferenceMove
            catalogue
            (retained |> List.map (fun e -> e.Effective.Project))
            (retained |> List.map (fun e -> e.Effective.ActivityType))
            request.Project
            request.ActivityType
        |> Result.bind (fun () -> requireAtLeastTwoSources request.Sources)
        |> Result.bind (fun () -> requireUniqueMergeIdentities request.NewEntryId request.Sources)
        |> Result.bind (fun () -> resolveSources loaded request.Sources)
        |> Result.bind (fun pairs ->
            requireAllSourcesMergeable pairs
            |> Result.bind (fun () -> requireSingleDay pairs)
            |> Result.map (fun () -> pairs))

    match validated with
    | Error rejection -> Rejected rejection
    | Ok pairs ->
        let totalMilliseconds =
            pairs
            |> List.sumBy (fun (_, entry) -> Duration.milliseconds entry.Effective.Duration)

        match Duration.ofMilliseconds totalMilliseconds with
        | Error _ -> Rejected(MergeDurationOutOfRange totalMilliseconds)
        | Ok mergedDuration ->
            let _, firstEntry = List.head pairs

            let facts =
                { Project = request.Project
                  ActivityType = request.ActivityType
                  Date = firstEntry.Effective.Date
                  Duration = mergedDuration
                  Description = request.Description
                  // A merge is a deliberate manual act with a required
                  // reason, so the merged entry is not a timed record even
                  // when every source was.
                  Origin = Manual request.Reason
                  Evidence = request.Evidence }

            let sourceIds = pairs |> List.map (fun (_, entry) -> entry.Id)

            let target =
                { Id = request.NewEntryId
                  State = Active
                  Effective = facts
                  History =
                    [ { Id = request.Attribution.NewRevisionId
                        Change = CreatedByMergeOf sourceIds
                        Facts = facts
                        RecordedAt = request.Attribution.OccurredAt
                        RecordedBy = request.Attribution.Actor
                        Device = request.Attribution.Device } ]
                  Version = None }

            let supersede (source: MergeSource, entry: TimeEntry) =
                let revision =
                    { Id = source.NewRevisionId
                      Change = MergedInto request.NewEntryId
                      Facts = entry.Effective
                      RecordedAt = request.Attribution.OccurredAt
                      RecordedBy = request.Attribution.Actor
                      Device = request.Attribution.Device }

                TimeEntry.appendRevision
                    revision
                    (Superseded(SupersededByMerge request.NewEntryId))
                    entry.Effective
                    entry

            let superseded = pairs |> List.map supersede

            let effect =
                PersistMerge(
                    { Entry = target; ExpectedVersion = None },
                    superseded
                    |> List.map2
                        (fun (source: MergeSource, _) entry ->
                            { Entry = entry
                              ExpectedVersion = Some source.ExpectedVersion })
                        pairs
                )

            Accepted(target :: superseded, [ effect ])

// ---------------------------------------------------------------------------
// Dispatch
// ---------------------------------------------------------------------------

/// Apply a command against the loaded entries.
///
/// `loaded` is the authoritative state Tier 3 supplies. A command naming an
/// entry that is not loaded is rejected rather than silently creating one.
let apply (catalogue: Catalogue) (loaded: TimeEntry list) (command: Command) : Outcome =
    let against (entryId: EntryId) (run: TimeEntry -> Outcome) =
        match loaded |> List.tryFind (fun e -> e.Id = entryId) with
        | None -> Rejected(EntryNotLoaded entryId)
        | Some entry -> run entry

    match command with
    | CreateEntry request -> createEntry catalogue request
    | CorrectEntry request -> against request.EntryId (correctEntry catalogue request)
    | SplitEntry request -> against request.EntryId (splitEntry catalogue request)
    | VoidEntry request -> against request.EntryId (voidEntry request)
    | RestoreEntry request -> against request.EntryId (restoreEntry request)
    | AttachEvidence request -> against request.EntryId (attachEvidence request)
    // Merge spans many entries, so it takes the whole loaded set rather than one.
    | MergeEntries request -> mergeEntries catalogue request loaded
