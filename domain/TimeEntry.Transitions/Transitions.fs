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
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects

// ---------------------------------------------------------------------------
// Guards
// ---------------------------------------------------------------------------

let private requireSameEntry (expected: EntryId) (entry: TimeEntry) : Result<unit, Rejection> =
    if entry.Id = expected then Ok() else Error(EntryNotLoaded expected)

/// The attempted action must be legal in the entry's current state
/// (TE-R-097).
let private requireCapability (capability: EntryCapability) (entry: TimeEntry) : Result<unit, Rejection> =
    if Capabilities.has capability entry.State then
        Ok()
    else
        let denial =
            Capabilities.denialFor capability entry.State
            |> Option.defaultValue (BlockedByOpenQuestion "unspecified")

        Error(NotPermittedInState(capability, denial))

/// The caller must have been acting on the version we currently hold.
///
/// A mismatch writes nothing and reports both sides. There is deliberately no
/// re-read-and-retry path: TE-R-072 requires a stale write to become an
/// explicit outcome, and TE-R-070 forbids silently overwriting a newer
/// correction.
let private requireVersion (expected: VersionToken) (entry: TimeEntry) : Result<unit, Rejection> =
    match entry.Version with
    | Some current when VersionToken.matches current expected -> Ok()
    | current -> Error(VersionConflict(expected, current))

let private requireAtLeastTwoChildren (children: SplitChild list) : Result<unit, Rejection> =
    let count = List.length children

    if count >= 2 then
        Ok()
    else
        Error(SplitNeedsAtLeastTwoChildren count)

let private requireUniqueIdentities (source: EntryId) (children: SplitChild list) : Result<unit, Rejection> =
    let ids = source :: (children |> List.map (fun c -> c.NewEntryId))

    let duplicated =
        ids
        |> List.countBy id
        |> List.tryPick (fun (entryId, count) -> if count > 1 then Some entryId else None)

    match duplicated with
    | Some entryId -> Error(SplitChildIdentityNotUnique entryId)
    | None -> Ok()

/// TE-R-040. Checked on `Duration` in whole seconds because `Duration` is the
/// authoritative quantity (DF-TE-0002). Checking billable units instead would
/// let a split preserve rounded units while losing real seconds, violating
/// TE-R-001.
let private requireTotalPreserved (source: Duration) (children: SplitChild list) : Result<unit, Rejection> =
    let childDurations = children |> List.map (fun c -> c.Duration)

    if Duration.partsPreserve source childDurations then
        Ok()
    else
        Error(SplitDoesNotPreserveTotal(Duration.seconds source, Duration.sum childDurations))

let private revisionOf (attribution: Attribution) (change: RevisionChange) (facts: EntryFacts) =
    { Id = attribution.NewRevisionId
      Change = change
      Facts = facts
      RecordedAt = attribution.OccurredAt
      RecordedBy = attribution.Actor
      Device = attribution.Device }

/// Shared shape for the single-entry transitions: run the guards, then build.
let private transition
    (capability: EntryCapability)
    (entryId: EntryId)
    (expectedVersion: VersionToken)
    (build: TimeEntry -> TimeEntry * Effect)
    (entry: TimeEntry)
    : Outcome =
    let validated =
        requireSameEntry entryId entry
        |> Result.bind (fun () -> requireCapability capability entry)
        |> Result.bind (fun () -> requireVersion expectedVersion entry)

    match validated with
    | Error rejection -> Rejected rejection
    | Ok() ->
        let updated, effect = build entry
        Accepted([ updated ], [ effect ])

// ---------------------------------------------------------------------------
// Create (TE-R-020)
// ---------------------------------------------------------------------------

/// A new entry needs no state guard and no version guard: it does not exist
/// yet. `ExpectedVersion = None` tells the interpreter to fail the write if it
/// turns out it does.
let createEntry (request: CreateEntryRequest) : Outcome =
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
let correctEntry (request: CorrectEntryRequest) (entry: TimeEntry) : Outcome =
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
let splitEntry (request: SplitEntryRequest) (entry: TimeEntry) : Outcome =
    let validated =
        requireSameEntry request.EntryId entry
        |> Result.bind (fun () -> requireCapability CanSplit entry)
        |> Result.bind (fun () -> requireVersion request.ExpectedVersion entry)
        |> Result.bind (fun () -> requireAtLeastTwoChildren request.Children)
        |> Result.bind (fun () -> requireUniqueIdentities entry.Id request.Children)
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
// Dispatch
// ---------------------------------------------------------------------------

/// Apply a command against the loaded entries.
///
/// `loaded` is the authoritative state Tier 3 supplies. A command naming an
/// entry that is not loaded is rejected rather than silently creating one.
let apply (loaded: TimeEntry list) (command: Command) : Outcome =
    let against (entryId: EntryId) (run: TimeEntry -> Outcome) =
        match loaded |> List.tryFind (fun e -> e.Id = entryId) with
        | None -> Rejected(EntryNotLoaded entryId)
        | Some entry -> run entry

    match command with
    | CreateEntry request -> createEntry request
    | CorrectEntry request -> against request.EntryId (correctEntry request)
    | SplitEntry request -> against request.EntryId (splitEntry request)
    | VoidEntry request -> against request.EntryId (voidEntry request)
    | RestoreEntry request -> against request.EntryId (restoreEntry request)
    | AttachEvidence request -> against request.EntryId (attachEvidence request)
