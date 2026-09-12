namespace Ledger.Domain

open System

type Environment =
    { Projects: Map<string, ReferenceItem>
      ActivityTypes: Map<string, ReferenceItem>
      Tags: Map<string, ReferenceItem>
      NewId: unit -> string
      Clock: unit -> DateTimeOffset }

type CommandResult<'success> =
    | Success of document: LedgerDocument * result: 'success
    | Rejected of Diagnostic list
    | Conflict of currentVersion: int64 * currentActivity: Activity option * diagnostics: Diagnostic list

type CreateActivityCommand =
    { ActivityTypeId: string
      ProjectId: string
      Description: string
      BusinessPurpose: string
      Outcome: string
      TagIds: string list
      EntryMethod: EntryMethod
      ReconstructionReason: string option
      StartedAt: DateTimeOffset
      EndedAt: DateTimeOffset
      ClientTimestamp: DateTimeOffset option }

/// `ExpectedVersion = None` skips the optimistic-concurrency check entirely,
/// matching `worker/src/store.js`'s `assertVersion` (a falsy/omitted
/// `base_version` is not checked).
type AmendActivityCommand =
    { ActivityId: string; ExpectedVersion: int64 option; Changes: CreateActivityCommand; Reason: string }

type VoidActivityCommand =
    { ActivityId: string; ExpectedVersion: int64 option; Reason: string }

type RestoreActivityCommand =
    { ActivityId: string; ExpectedVersion: int64 option; Reason: string }

type SplitPart =
    { ActivityTypeId: string option
      ProjectId: string option
      Description: string option
      BusinessPurpose: string option
      Outcome: string option
      TagIds: string list option
      DurationMs: float }

type SplitActivityCommand =
    { SourceActivityId: string; ExpectedVersion: int64 option; Parts: SplitPart list; Reason: string }

/// A source id absent from `ExpectedVersions` skips its version check, mirroring
/// `worker/src/store.js`'s per-source `base_versions?.[index]` behavior.
type MergeActivitiesCommand =
    { SourceActivityIds: string list
      ExpectedVersions: Map<string, int64>
      ActivityTypeId: string
      ProjectId: string
      Description: string
      BusinessPurpose: string
      Reason: string }

type AttachEvidenceCommand =
    { ActivityId: string
      ExpectedVersion: int64 option
      Type: EvidenceType
      Uri: string option
      Note: string option
      Hash: string option
      Label: string }

type DetachEvidenceCommand =
    { ActivityId: string; ExpectedVersion: int64 option; EvidenceLinkId: string; Reason: string }

type AttestDayCommand =
    { Date: DateOnly; Statement: string; ExpectedProjectionVersion: string option }

type StartTimerCommand = { ActivityTypeId: string; ProjectId: string; Description: string }

/// Every business rule in `worker/src/store.js`, reimplemented per
/// `docs/DOMAIN-REQUIREMENTS.md` — including the three fixes this port adds:
/// restore re-validation, merge same-date/contiguity, and (in `Summary.fs`) the
/// derived six-minute billing figure.
module Commands =

    /// Existence is always checked (a reference that no longer exists at all is
    /// corruption, not history). Only the *active* check is exempted when the
    /// value is unchanged from the activity's current one (`currentId = Some
    /// id`) — an archived project/type an activity already pointed to remains
    /// valid for that activity, matching `worker/src/store.js`'s
    /// `#assertActiveReference`.
    let private checkReference
        (notFoundCode: string)
        (notFoundMessage: string)
        (inactiveCode: string)
        (inactiveMessage: string)
        (contextKey: string)
        (directory: Map<string, ReferenceItem>)
        (id: string)
        (currentId: string option)
        : Diagnostic option =
        match Map.tryFind id directory with
        | None -> Some(Diagnostic.domain notFoundCode notFoundMessage |> Diagnostic.withContext (Map.ofList [ contextKey, id ]))
        | Some item when not item.Active && currentId <> Some id ->
            Some(Diagnostic.domain inactiveCode inactiveMessage |> Diagnostic.withContext (Map.ofList [ contextKey, id ]))
        | Some _ -> None

    let private checkTags (tags: Map<string, ReferenceItem>) (tagIds: string list) (currentTagIds: Set<string>) : Diagnostic option =
        tagIds
        |> List.tryPick (fun tagId ->
            match Map.tryFind tagId tags with
            | None ->
                Some(Diagnostic.domain DiagnosticCode.TagNotFound "Referenced tag does not exist." |> Diagnostic.withContext (Map.ofList [ "tag_id", tagId ]))
            | Some item when not item.Active && not (currentTagIds.Contains tagId) ->
                Some(Diagnostic.domain DiagnosticCode.TagInactive "Referenced tag is not active and cannot be newly assigned." |> Diagnostic.withContext (Map.ofList [ "tag_id", tagId ]))
            | Some _ -> None)

    let private findOverlap (document: LedgerDocument) (startedAt: DateTimeOffset) (endedAt: DateTimeOffset) (ignoredIds: Set<string>) =
        let dateKey = DateKey.ofInstant startedAt
        document.Activities
        |> List.tryFind (fun a ->
            not a.Voided
            && not (ignoredIds.Contains a.ActivityId)
            && DateKey.ofInstant a.StartedAt = dateKey
            && startedAt < a.EndedAt
            && a.StartedAt < endedAt)

    /// Mirrors `worker/src/store.js`'s `#validateActivityFields`: checks are
    /// evaluated in order and the first failure is returned (one diagnostic at a
    /// time), not an aggregate of every violation.
    /// `currentActivityTypeId`/`currentProjectId`/`currentTagIds` are `None`/empty
    /// on create and each split part/merge (every reference must currently be
    /// active); on amend/restore they carry the activity's own current values, so
    /// an unchanged reference is exempt even if it has since been archived.
    let private validateFields
        (environment: Environment)
        (document: LedgerDocument)
        (startedAt: DateTimeOffset)
        (endedAt: DateTimeOffset)
        (activityTypeId: string)
        (projectId: string)
        (description: string)
        (businessPurpose: string)
        (entryMethod: EntryMethod)
        (reconstructionReason: string option)
        (tagIds: string list)
        (ignoredIds: Set<string>)
        (currentActivityTypeId: string option)
        (currentProjectId: string option)
        (currentTagIds: Set<string>)
        (now: DateTimeOffset)
        : Diagnostic list =
        let checks : (unit -> Diagnostic option) list =
            [ (fun () ->
                if String.IsNullOrWhiteSpace activityTypeId then
                    Some(Diagnostic.domain DiagnosticCode.ActivityTypeRequired "activity_type_id is required." |> Diagnostic.forField "activity_type_id")
                else
                    None)
              (fun () ->
                if String.IsNullOrWhiteSpace projectId then
                    Some(Diagnostic.domain DiagnosticCode.ProjectRequired "project_id is required." |> Diagnostic.forField "project_id")
                else
                    None)
              (fun () ->
                match NonBlankText.create "description" DiagnosticCode.DescriptionRequired description with
                | Error d -> Some d
                | Ok _ -> None)
              (fun () ->
                match NonBlankText.create "business_purpose" DiagnosticCode.BusinessPurposeRequired businessPurpose with
                | Error d -> Some d
                | Ok _ -> None)
              (fun () ->
                if endedAt <= startedAt then
                    Some(Diagnostic.domain DiagnosticCode.InvalidTimeRange "End time must be after start time.")
                else
                    None)
              (fun () ->
                if DateKey.ofInstant startedAt <> DateKey.ofInstant endedAt then
                    Some(Diagnostic.domain DiagnosticCode.CrossesMidnight "An activity must not extend past midnight.")
                else
                    None)
              (fun () ->
                let blankReason = reconstructionReason |> Option.forall String.IsNullOrWhiteSpace
                if entryMethod = Manual && DateKey.ofInstant startedAt < DateKey.ofInstant now && blankReason then
                    Some(
                        Diagnostic.domain DiagnosticCode.ReconstructionReasonRequired "reconstruction_reason is required for a manual entry on a past date."
                        |> Diagnostic.forField "reconstruction_reason"
                    )
                else
                    None)
              (fun () ->
                findOverlap document startedAt endedAt ignoredIds
                |> Option.map (fun conflict ->
                    Diagnostic.domain DiagnosticCode.OverlappingActivity "This time interval overlaps another recorded activity."
                    |> Diagnostic.withContext (Map.ofList [ "conflicting_activity_id", conflict.ActivityId ])))
              (fun () ->
                checkReference DiagnosticCode.ProjectNotFound "Referenced project does not exist." DiagnosticCode.ProjectInactive "Referenced project is archived." "project_id"
                    environment.Projects projectId currentProjectId)
              (fun () ->
                checkReference DiagnosticCode.ActivityTypeNotFound "Referenced activity type does not exist." DiagnosticCode.ActivityTypeInactive "Referenced activity type is archived." "activity_type_id"
                    environment.ActivityTypes activityTypeId currentActivityTypeId)
              (fun () -> checkTags environment.Tags tagIds currentTagIds) ]
        checks |> List.tryPick (fun check -> check ()) |> Option.toList

    let private nextSequence (document: LedgerDocument) = document.EventSequence + 1L

    /// Applies `f` to the activity `id` refers to, then bumps its Version to the
    /// document's new EventSequence and stamps UpdatedAt — the shared tail of
    /// every mutating command once validation has passed.
    let private updateActivity (document: LedgerDocument) (id: string) (now: DateTimeOffset) (f: Activity -> Activity) : LedgerDocument =
        let sequence = nextSequence document
        { document with
            Activities = document.Activities |> List.map (fun a -> if a.ActivityId = id then { f a with Version = sequence; UpdatedAt = now } else a)
            EventSequence = sequence }

    let private checkVersion (current: Activity) (expected: int64 option) : CommandResult<'a> option =
        match expected with
        | Some v when v <> current.Version ->
            Some(Conflict(current.Version, Some current, [ Diagnostic.domain DiagnosticCode.StaleVersion "This activity changed after it was opened." ]))
        | _ -> None

    let private requireReason (reason: string) : Result<string, Diagnostic> =
        NonBlankText.create "reason" DiagnosticCode.ReasonRequired reason |> Result.map NonBlankText.value

    let create (environment: Environment) (document: LedgerDocument) (command: CreateActivityCommand) : CommandResult<Activity> =
        let now = environment.Clock()
        let diagnostics =
            validateFields environment document command.StartedAt command.EndedAt command.ActivityTypeId command.ProjectId
                command.Description command.BusinessPurpose command.EntryMethod command.ReconstructionReason command.TagIds
                Set.empty None None Set.empty now
        match diagnostics with
        | [] ->
            let sequence = nextSequence document
            let activity =
                { ActivityId = environment.NewId()
                  ActivityTypeId = command.ActivityTypeId
                  ProjectId = command.ProjectId
                  Description = command.Description.Trim()
                  BusinessPurpose = command.BusinessPurpose.Trim()
                  Outcome = command.Outcome
                  TagIds = Set.ofList command.TagIds
                  EntryMethod = command.EntryMethod
                  ReconstructionReason = command.ReconstructionReason
                  StartedAt = command.StartedAt
                  EndedAt = command.EndedAt
                  ClientTimestamp = command.ClientTimestamp
                  ServerReceivedAt = now
                  Voided = false
                  Superseded = false
                  Evidence = []
                  History = [ Created now ]
                  Relationships = Relationships.empty
                  Version = sequence
                  UpdatedAt = now }
            Success({ document with Activities = activity :: document.Activities; EventSequence = sequence }, activity)
        | diagnostics -> Rejected diagnostics

    let amend (environment: Environment) (document: LedgerDocument) (command: AmendActivityCommand) : CommandResult<Activity> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.ActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.ActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Voided then
                    Rejected
                        [ Diagnostic.domain DiagnosticCode.ActivityNotRecorded "Only a Recorded activity can be changed this way."
                          |> Diagnostic.withContext (Map.ofList [ "superseded", string current.Superseded ]) ]
                else
                    match requireReason command.Reason with
                    | Error d -> Rejected [ d ]
                    | Ok reason ->
                        let changes = command.Changes
                        let diagnostics =
                            validateFields environment document changes.StartedAt changes.EndedAt changes.ActivityTypeId changes.ProjectId
                                changes.Description changes.BusinessPurpose current.EntryMethod changes.ReconstructionReason changes.TagIds
                                (Set.singleton command.ActivityId) (Some current.ActivityTypeId) (Some current.ProjectId) current.TagIds
                                (environment.Clock())
                        match diagnostics with
                        | [] ->
                            let now = environment.Clock()
                            let newDocument =
                                updateActivity document command.ActivityId now (fun a ->
                                    { a with
                                        ActivityTypeId = changes.ActivityTypeId
                                        ProjectId = changes.ProjectId
                                        Description = changes.Description.Trim()
                                        BusinessPurpose = changes.BusinessPurpose.Trim()
                                        Outcome = changes.Outcome
                                        TagIds = Set.ofList changes.TagIds
                                        ReconstructionReason = changes.ReconstructionReason
                                        StartedAt = changes.StartedAt
                                        EndedAt = changes.EndedAt
                                        ClientTimestamp = changes.ClientTimestamp
                                        History = a.History @ [ Amended(now, reason) ] })
                            Success(newDocument, newDocument.Activities |> List.find (fun a -> a.ActivityId = command.ActivityId))
                        | diagnostics -> Rejected diagnostics

    let voidActivity (environment: Environment) (document: LedgerDocument) (command: VoidActivityCommand) : CommandResult<Activity> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.ActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.ActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Voided then
                    Rejected [ Diagnostic.domain DiagnosticCode.ActivityAlreadyVoided "Activity is already removed from totals." ]
                else
                    match requireReason command.Reason with
                    | Error d -> Rejected [ d ]
                    | Ok reason ->
                        let now = environment.Clock()
                        let newDocument = updateActivity document command.ActivityId now (fun a -> { a with Voided = true; History = a.History @ [ Voided(now, reason) ] })
                        Success(newDocument, newDocument.Activities |> List.find (fun a -> a.ActivityId = command.ActivityId))

    /// FIX: re-validates the activity against current state via `validateFields`
    /// (self excluded from the overlap check, unchanged-reference exemption
    /// applied) before flipping Voided back to false — `worker/src/store.js`'s
    /// `restoreActivity` does not do this today; it just flips the flag.
    let restore (environment: Environment) (document: LedgerDocument) (command: RestoreActivityCommand) : CommandResult<Activity> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.ActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.ActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Superseded then
                    Rejected [ Diagnostic.domain DiagnosticCode.ActivitySuperseded "A Superseded activity (replaced by a split or merge) cannot be restored." ]
                elif not current.Voided then
                    Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotVoided "Activity is already included in totals." ]
                else
                    match requireReason command.Reason with
                    | Error d -> Rejected [ d ]
                    | Ok reason ->
                        let now = environment.Clock()
                        let diagnostics =
                            validateFields environment document current.StartedAt current.EndedAt current.ActivityTypeId current.ProjectId
                                current.Description current.BusinessPurpose current.EntryMethod current.ReconstructionReason (current.TagIds |> Set.toList)
                                (Set.singleton command.ActivityId) (Some current.ActivityTypeId) (Some current.ProjectId) current.TagIds now
                        match diagnostics with
                        | [] ->
                            let newDocument = updateActivity document command.ActivityId now (fun a -> { a with Voided = false; History = a.History @ [ Restored(now, reason) ] })
                            Success(newDocument, newDocument.Activities |> List.find (fun a -> a.ActivityId = command.ActivityId))
                        | diagnostics -> Rejected diagnostics

    let split (environment: Environment) (document: LedgerDocument) (command: SplitActivityCommand) : CommandResult<Activity * Activity list> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.SourceActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.SourceActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Voided then
                    Rejected
                        [ Diagnostic.domain DiagnosticCode.ActivityNotRecorded "Only a Recorded activity can be changed this way."
                          |> Diagnostic.withContext (Map.ofList [ "superseded", string current.Superseded ]) ]
                elif List.length command.Parts < 2 then
                    Rejected [ Diagnostic.domain DiagnosticCode.SplitRequiresTwoParts "At least two split parts are required." ]
                elif Activity.exactDurationMs current < 120000.0 then
                    Rejected [ Diagnostic.domain DiagnosticCode.TooShortToSplit "Activity must be at least two minutes long to split." ]
                else
                    let total = command.Parts |> List.sumBy (fun p -> p.DurationMs)
                    let expected = Activity.exactDurationMs current
                    if total <> expected then
                        Rejected
                            [ Diagnostic.domain DiagnosticCode.DurationInvariantFailed "Split durations must equal the source duration."
                              |> Diagnostic.withContext (Map.ofList [ "expected", string expected; "actual", string total ]) ]
                    else
                        match requireReason command.Reason with
                        | Error d -> Rejected [ d ]
                        | Ok reason ->
                            let now = environment.Clock()
                            let ignoredIds = Set.singleton command.SourceActivityId
                            let rec buildParts (doc: LedgerDocument) (cursor: DateTimeOffset) (remaining: SplitPart list) (acc: Activity list) =
                                match remaining with
                                | [] -> Ok(doc, List.rev acc)
                                | part :: rest ->
                                    let next = cursor.AddMilliseconds part.DurationMs
                                    let activityTypeId = part.ActivityTypeId |> Option.defaultValue current.ActivityTypeId
                                    let projectId = part.ProjectId |> Option.defaultValue current.ProjectId
                                    let description = part.Description |> Option.defaultValue current.Description
                                    let businessPurpose = part.BusinessPurpose |> Option.defaultValue current.BusinessPurpose
                                    let outcome = part.Outcome |> Option.defaultValue current.Outcome
                                    let tagIds = part.TagIds |> Option.defaultValue (current.TagIds |> Set.toList)
                                    // Each part is independently validated as new — no inherited-reference
                                    // exemption, matching the existing JS asymmetry with amend (not one of
                                    // this port's three authorized fixes, so preserved as-is).
                                    let diagnostics =
                                        validateFields environment doc cursor next activityTypeId projectId description businessPurpose
                                            EntryMethod.Split current.ReconstructionReason tagIds ignoredIds None None Set.empty now
                                    match diagnostics with
                                    | [] ->
                                        let sequence = nextSequence doc
                                        let activity =
                                            { ActivityId = environment.NewId()
                                              ActivityTypeId = activityTypeId
                                              ProjectId = projectId
                                              Description = description.Trim()
                                              BusinessPurpose = businessPurpose.Trim()
                                              Outcome = outcome
                                              TagIds = Set.ofList tagIds
                                              EntryMethod = EntryMethod.Split
                                              ReconstructionReason = current.ReconstructionReason
                                              StartedAt = cursor
                                              EndedAt = next
                                              ClientTimestamp = None
                                              ServerReceivedAt = now
                                              Voided = false
                                              Superseded = false
                                              Evidence = []
                                              History = [ Created now ]
                                              Relationships = Relationships.empty
                                              Version = sequence
                                              UpdatedAt = now }
                                        let newDoc = { doc with Activities = activity :: doc.Activities; EventSequence = sequence }
                                        buildParts newDoc next rest (activity :: acc)
                                    | diagnostics -> Error diagnostics
                            match buildParts document current.StartedAt command.Parts [] with
                            | Error diagnostics -> Rejected diagnostics
                            | Ok(docAfterParts, replacements) ->
                                let replacementIds = replacements |> List.map (fun a -> a.ActivityId)
                                let docAfterSplitEvent =
                                    updateActivity docAfterParts command.SourceActivityId now (fun a -> { a with History = a.History @ [ SplitPerformed(now, replacementIds, reason) ] })
                                let finalDoc =
                                    updateActivity docAfterSplitEvent command.SourceActivityId now (fun a ->
                                        { a with Voided = true; Superseded = true; History = a.History @ [ Superseded(now, "Replaced by split") ] })
                                let source = finalDoc.Activities |> List.find (fun a -> a.ActivityId = command.SourceActivityId)
                                let finalReplacements = replacementIds |> List.map (fun id -> finalDoc.Activities |> List.find (fun a -> a.ActivityId = id))
                                Success(finalDoc, (source, finalReplacements))

    /// FIX: requires all sources to be on the same date and contiguous (sorted by
    /// start, each end exactly meeting the next start) before anything else is
    /// checked — `worker/src/store.js`'s `mergeActivities` does not check this
    /// today.
    let merge (environment: Environment) (document: LedgerDocument) (command: MergeActivitiesCommand) : CommandResult<Activity * Activity list> =
        if List.length command.SourceActivityIds < 2 then
            Rejected [ Diagnostic.domain DiagnosticCode.MergeRequiresTwoSources "At least two source activities are required." ]
        else
            let lookups = command.SourceActivityIds |> List.map (fun id -> id, document.Activities |> List.tryFind (fun a -> a.ActivityId = id))
            match lookups |> List.tryFind (fun (_, found) -> found.IsNone) with
            | Some(missingId, _) -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject missingId ]
            | None ->
                let sources = lookups |> List.choose snd
                let stale = sources |> List.tryFind (fun s -> command.ExpectedVersions |> Map.tryFind s.ActivityId |> Option.exists (fun v -> v <> s.Version))
                match stale with
                | Some source ->
                    Conflict(source.Version, Some source, [ Diagnostic.domain DiagnosticCode.StaleVersion "This activity changed after it was opened." |> Diagnostic.forSubject source.ActivityId ])
                | None ->
                    match sources |> List.tryFind (fun s -> s.Voided) with
                    | Some notRecorded ->
                        Rejected
                            [ Diagnostic.domain DiagnosticCode.ActivityNotRecorded "Only a Recorded activity can be changed this way."
                              |> Diagnostic.forSubject notRecorded.ActivityId
                              |> Diagnostic.withContext (Map.ofList [ "superseded", string notRecorded.Superseded ]) ]
                    | None ->
                        match requireReason command.Reason with
                        | Error d -> Rejected [ d ]
                        | Ok reason ->
                            let sorted = sources |> List.sortBy (fun s -> s.StartedAt)
                            let firstDate = DateKey.ofInstant sorted.Head.StartedAt
                            let sameDate = sorted |> List.forall (fun s -> DateKey.ofInstant s.StartedAt = firstDate)
                            let contiguous = sorted |> List.pairwise |> List.forall (fun (a, b) -> a.EndedAt = b.StartedAt)
                            if not sameDate || not contiguous then
                                Rejected [ Diagnostic.domain DiagnosticCode.MergeSourcesNotContiguous "Merge sources must be on the same date and contiguous, with no gap or overlap." ]
                            else
                                let now = environment.Clock()
                                let startedAt = sorted.Head.StartedAt
                                let endedAt = (List.last sorted).EndedAt
                                let ignoredIds = Set.ofList command.SourceActivityIds
                                let diagnostics =
                                    validateFields environment document startedAt endedAt command.ActivityTypeId command.ProjectId
                                        command.Description command.BusinessPurpose EntryMethod.Merge None [] ignoredIds None None Set.empty now
                                match diagnostics with
                                | [] ->
                                    let sequence = nextSequence document
                                    let mergedId = environment.NewId()
                                    let merged =
                                        { ActivityId = mergedId
                                          ActivityTypeId = command.ActivityTypeId
                                          ProjectId = command.ProjectId
                                          Description = command.Description.Trim()
                                          BusinessPurpose = command.BusinessPurpose.Trim()
                                          Outcome = ""
                                          TagIds = Set.empty
                                          EntryMethod = EntryMethod.Merge
                                          ReconstructionReason = None
                                          StartedAt = startedAt
                                          EndedAt = endedAt
                                          ClientTimestamp = None
                                          ServerReceivedAt = now
                                          Voided = false
                                          Superseded = false
                                          Evidence = []
                                          History = [ Created now ]
                                          Relationships = { Relationships.empty with MergedFrom = Some command.SourceActivityIds }
                                          Version = sequence
                                          UpdatedAt = now }
                                    let docWithMerged = { document with Activities = merged :: document.Activities; EventSequence = sequence }
                                    let docWithMergeEvent =
                                        updateActivity docWithMerged mergedId now (fun a -> { a with History = a.History @ [ Merged(now, command.SourceActivityIds, reason) ] })
                                    let finalDoc =
                                        command.SourceActivityIds
                                        |> List.fold
                                            (fun doc sourceId ->
                                                updateActivity doc sourceId now (fun a ->
                                                    { a with
                                                        Voided = true
                                                        Superseded = true
                                                        Relationships = { a.Relationships with MergedInto = Some mergedId }
                                                        History = a.History @ [ Superseded(now, sprintf "Merged into %s" mergedId) ] }))
                                            docWithMergeEvent
                                    let mergedFinal = finalDoc.Activities |> List.find (fun a -> a.ActivityId = mergedId)
                                    let sourcesFinal = command.SourceActivityIds |> List.map (fun id -> finalDoc.Activities |> List.find (fun a -> a.ActivityId = id))
                                    Success(finalDoc, (mergedFinal, sourcesFinal))
                                | diagnostics -> Rejected diagnostics

    let attachEvidence (environment: Environment) (document: LedgerDocument) (command: AttachEvidenceCommand) : CommandResult<Activity> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.ActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.ActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Superseded then
                    Rejected [ Diagnostic.domain DiagnosticCode.ActivitySuperseded "A Superseded activity cannot have evidence attached." ]
                else
                    let now = environment.Clock()
                    let evidence =
                        { EvidenceLinkId = environment.NewId()
                          Type = command.Type
                          Uri = command.Uri
                          Note = command.Note
                          Hash = command.Hash
                          Label = command.Label
                          AttachedAt = now }
                    let newDocument =
                        updateActivity document command.ActivityId now (fun a ->
                            { a with Evidence = a.Evidence @ [ evidence ]; History = a.History @ [ EvidenceAttached(now, evidence.EvidenceLinkId) ] })
                    Success(newDocument, newDocument.Activities |> List.find (fun a -> a.ActivityId = command.ActivityId))

    let detachEvidence (environment: Environment) (document: LedgerDocument) (command: DetachEvidenceCommand) : CommandResult<Activity> =
        match document.Activities |> List.tryFind (fun a -> a.ActivityId = command.ActivityId) with
        | None -> Rejected [ Diagnostic.domain DiagnosticCode.ActivityNotFound "Activity was not found." |> Diagnostic.forSubject command.ActivityId ]
        | Some current ->
            match checkVersion current command.ExpectedVersion with
            | Some conflict -> conflict
            | None ->
                if current.Voided then
                    Rejected
                        [ Diagnostic.domain DiagnosticCode.ActivityNotRecorded "Only a Recorded activity can be changed this way."
                          |> Diagnostic.withContext (Map.ofList [ "superseded", string current.Superseded ]) ]
                elif not (current.Evidence |> List.exists (fun e -> e.EvidenceLinkId = command.EvidenceLinkId)) then
                    Rejected [ Diagnostic.domain DiagnosticCode.EvidenceNotFound "Evidence link was not found." ]
                else
                    match requireReason command.Reason with
                    | Error d -> Rejected [ d ]
                    | Ok reason ->
                        let now = environment.Clock()
                        let newDocument =
                            updateActivity document command.ActivityId now (fun a ->
                                { a with
                                    Evidence = a.Evidence |> List.filter (fun e -> e.EvidenceLinkId <> command.EvidenceLinkId)
                                    History = a.History @ [ EvidenceDetached(now, command.EvidenceLinkId, reason) ] })
                        Success(newDocument, newDocument.Activities |> List.find (fun a -> a.ActivityId = command.ActivityId))

    let attestDay (environment: Environment) (document: LedgerDocument) (command: AttestDayCommand) : CommandResult<DailyAttestation> =
        let daySummary = Summary.forDate document command.Date
        let stale = command.ExpectedProjectionVersion |> Option.exists (fun expected -> expected <> daySummary.ProjectionVersion)
        if stale then
            Conflict(document.EventSequence, None, [ Diagnostic.domain DiagnosticCode.StaleVersion "The day changed after it was opened." ])
        else
            match NonBlankText.create "statement" DiagnosticCode.StatementRequired command.Statement with
            | Error d -> Rejected [ d ]
            | Ok statement ->
                let now = environment.Clock()
                let sequence = nextSequence document
                let attestation =
                    { AttestationId = environment.NewId()
                      Date = command.Date
                      Statement = NonBlankText.value statement
                      AttestedSequence = sequence
                      AttestedAt = now }
                Success({ document with Attestations = document.Attestations @ [ attestation ]; EventSequence = sequence }, attestation)

    /// Thin wrapper over `Timer.stop` — mirrors `worker/src/store.js`'s
    /// `stopTimer`, which does not itself create an Activity: the caller (the
    /// Engine's `Dispatch`, combining this with the accumulated draft's business
    /// purpose/tags/outcome) makes a separate `create` call when the result is
    /// not discarded.
    let stopTimer (environment: Environment) (timerState: TimerState) : Result<StoppedTimer, Diagnostic> =
        Timer.stop (environment.Clock()) timerState

    /// Re-runs `validateFields` over every Recorded activity — corrupted or
    /// hand-edited storage is rejected exactly like a bad command would be.
    let validateDocument (environment: Environment) (document: LedgerDocument) : Diagnostic list =
        let now = environment.Clock()
        document.Activities
        |> List.filter (fun a -> not a.Voided)
        |> List.collect (fun a ->
            validateFields environment document a.StartedAt a.EndedAt a.ActivityTypeId a.ProjectId a.Description a.BusinessPurpose
                a.EntryMethod a.ReconstructionReason (a.TagIds |> Set.toList) (Set.singleton a.ActivityId) (Some a.ActivityTypeId)
                (Some a.ProjectId) a.TagIds now)
