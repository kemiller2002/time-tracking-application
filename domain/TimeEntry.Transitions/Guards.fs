/// Tier 2 — State Transition. Guards shared by every transition.
///
/// Extracted from `Transitions` so that "check the version", "check the
/// capability" and "check the catalogue" have exactly one definition each.
/// A guard duplicated per transition is a guard that eventually differs
/// between them.
module TimeEntry.Transitions.Guards

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Semantic.Catalogue
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects

// ---------------------------------------------------------------------------
// Guards
// ---------------------------------------------------------------------------

let requireSameEntry (expected: EntryId) (entry: TimeEntry) : Result<unit, Rejection> =
    if entry.Id = expected then Ok() else Error(EntryNotLoaded expected)

/// The attempted action must be legal in the entry's current state
/// (TE-R-097).
let requireCapability (capability: EntryCapability) (entry: TimeEntry) : Result<unit, Rejection> =
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
let requireVersion (expected: VersionToken) (entry: TimeEntry) : Result<unit, Rejection> =
    match entry.Version with
    | Some current when VersionToken.matches current expected -> Ok()
    | current -> Error(VersionConflict(expected, current))

/// Every piece of evidence a child claims must be evidence the source holds.
///
/// Matched on the whole `EvidenceRef` rather than on its URI alone. A split
/// moves evidence; it is not an opportunity to relabel it, and requiring the
/// item to match exactly means a caller can only hand back what it was given.
/// A relabelling is a different intention and should look like one.
///
/// Deliberately NOT checked here: whether two children may claim the same
/// item. The same document can genuinely support two pieces of work, and no
/// repository requirement says otherwise, so forbidding it would be inventing
/// a rule (recorded as OQ-11).
let requireReassignedEvidenceExists
    (source: EntryFacts)
    (children: SplitChild list)
    : Result<unit, Rejection> =
    let claimed = children |> List.collect (fun child -> child.ReassignedEvidence)

    match claimed |> List.tryFind (fun item -> not (List.contains item source.Evidence)) with
    | Some stranger -> Error(EvidenceNotOnSource stranger.Uri)
    | None -> Ok()

let requireAtLeastTwoChildren (children: SplitChild list) : Result<unit, Rejection> =
    let count = List.length children

    if count >= 2 then
        Ok()
    else
        Error(SplitNeedsAtLeastTwoChildren count)

let requireUniqueIdentities (source: EntryId) (children: SplitChild list) : Result<unit, Rejection> =
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
let requireTotalPreserved (source: Duration) (children: SplitChild list) : Result<unit, Rejection> =
    let childDurations = children |> List.map (fun c -> c.Duration)

    if Duration.partsPreserve source childDurations then
        Ok()
    else
        Error(SplitDoesNotPreserveTotal(Duration.milliseconds source, Duration.sum childDurations))

/// DF-TE-0007, for a command that introduces time with no prior reference.
let requireCatalogue
    (catalogue: Catalogue)
    (projectId: ProjectId)
    (activityTypeId: ActivityTypeId)
    : Result<unit, Rejection> =
    Catalogue.acceptsNewTime projectId activityTypeId catalogue
    |> Result.mapError CatalogueRejected

/// DF-TE-0007, for a command acting on time that already has a reference.
///
/// The rule is "you may not **move** time onto an archived reference", not
/// "archived references are frozen". A reference that is merely *retained* is
/// never re-validated, so an entry recorded before its project was archived
/// stays fully correctable — its hours, its description, its evidence — and
/// only a change of project or activity type is checked.
///
/// Guarding the whole command instead would make an archived project's history
/// uncorrectable, which is exactly what DF-TE-0007 set out to avoid.
let requireReferenceMove
    (catalogue: Catalogue)
    (retainedProjects: ProjectId list)
    (retainedActivityTypes: ActivityTypeId list)
    (projectId: ProjectId)
    (activityTypeId: ActivityTypeId)
    : Result<unit, Rejection> =
    let projectOk =
        if List.contains projectId retainedProjects then
            Ok()
        else
            Catalogue.projectAcceptsNewTime projectId catalogue

    let activityOk =
        if List.contains activityTypeId retainedActivityTypes then
            Ok()
        else
            Catalogue.activityTypeAcceptsNewTime activityTypeId catalogue

    projectOk
    |> Result.bind (fun () -> activityOk)
    |> Result.mapError CatalogueRejected

let revisionOf (attribution: Attribution) (change: RevisionChange) (facts: EntryFacts) =
    { Id = attribution.NewRevisionId
      Change = change
      Facts = facts
      RecordedAt = attribution.OccurredAt
      RecordedBy = attribution.Actor
      Device = attribution.Device }

/// Shared shape for the single-entry transitions: run the guards, then build.
let transition
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

