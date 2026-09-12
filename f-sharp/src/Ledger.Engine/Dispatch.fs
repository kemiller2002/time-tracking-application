namespace Ledger.Engine

open System
open Ledger.Domain
open Ledger.Engine.Protocol

/// SDE Tier 3 (Application / Projection / Orchestration): coordinates
/// commands, projections, and requested effects; must never become a second
/// semantic authority (`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`) — all
/// domain legality lives in `Ledger.Domain.Commands` (Tier 2).
///
/// The single entry point crossing the WASM boundary (via the `Ledger.Wasm`
/// C# JSExport shim, Tier 4). Owns command routing only.
module Dispatch =

    let private storageKey = "business-activity-ledger:v1"

    let private firstMessage (diagnostics: Diagnostic list) =
        diagnostics |> List.tryHead |> Option.map (fun d -> d.Message) |> Option.defaultValue "The request could not be completed."

    let private withError (key: string) (message: string) (state: Session.State) = { state with Errors = state.Errors |> Map.add key message }
    let private clearError (key: string) (state: Session.State) = { state with Errors = state.Errors |> Map.remove key }

    let private parseInstant (text: string option) =
        text |> Option.bind (fun t -> match DateTimeOffset.TryParse t with true, v -> Some v | _ -> None)

    // --- Draft accumulation -----------------------------------------------------

    let private applyDraftField (draft: Session.Draft) (event: SemanticEvent) : Session.Draft =
        match event.Name with
        | "DraftActivityTypeChanged" -> { draft with ActivityTypeId = event.Value }
        | "DraftProjectChanged" -> { draft with ProjectId = event.Value }
        | "DraftDescriptionChanged" -> { draft with Description = event.Value }
        | "DraftBusinessPurposeChanged" -> { draft with BusinessPurpose = event.Value }
        | "DraftOutcomeChanged" -> { draft with Outcome = event.Value }
        | "DraftStartedAtChanged" -> { draft with StartedAt = event.Value }
        | "DraftEndedAtChanged" -> { draft with EndedAt = event.Value }
        | "DraftReconstructionReasonChanged" -> { draft with ReconstructionReason = event.Value }
        | "DraftReasonChanged" -> { draft with Reason = event.Value }
        | "DraftAttestationStatementChanged" -> { draft with AttestationStatement = event.Value }
        | "DraftEvidenceTypeChanged" -> { draft with EvidenceType = event.Value }
        | "DraftEvidenceUriChanged" -> { draft with EvidenceUri = event.Value }
        | "DraftEvidenceNoteChanged" -> { draft with EvidenceNote = event.Value }
        | "DraftEvidenceLabelChanged" -> { draft with EvidenceLabel = event.Value }
        | "ToggleDraftTag" ->
            match event.Key, event.Value with
            | Some tagId, Some "on" -> { draft with TagIds = draft.TagIds.Add tagId }
            | Some tagId, _ -> { draft with TagIds = draft.TagIds.Remove tagId }
            | None, _ -> draft
        | "DraftSplitPartDurationChanged" ->
            match event.Key |> Option.bind (fun k -> match Int32.TryParse k with true, i -> Some i | _ -> None) with
            | Some index ->
                let part = draft.SplitParts |> Map.tryFind index |> Option.defaultValue Session.SplitPartDraft.empty
                { draft with SplitParts = draft.SplitParts |> Map.add index { part with DurationText = event.Value } }
            | None -> draft
        | "DraftSplitPartFieldChanged" ->
            match event.Key |> Option.map (fun k -> k.Split ':') with
            | Some [| indexText; fieldName |] ->
                match Int32.TryParse indexText with
                | true, index ->
                    let part = draft.SplitParts |> Map.tryFind index |> Option.defaultValue Session.SplitPartDraft.empty
                    let updated =
                        match fieldName with
                        | "activityTypeId" -> { part with ActivityTypeId = event.Value }
                        | "projectId" -> { part with ProjectId = event.Value }
                        | "description" -> { part with Description = event.Value }
                        | "businessPurpose" -> { part with BusinessPurpose = event.Value }
                        | "outcome" -> { part with Outcome = event.Value }
                        | _ -> part
                    { draft with SplitParts = draft.SplitParts |> Map.add index updated }
                | false, _ -> draft
            | _ -> draft
        | "ToggleMergeSource" ->
            match event.Key, event.Value with
            | Some id, Some "on" -> { draft with MergeSourceIds = draft.MergeSourceIds.Add id }
            | Some id, _ -> { draft with MergeSourceIds = draft.MergeSourceIds.Remove id }
            | None, _ -> draft
        | _ -> draft

    // --- Command submission -------------------------------------------------------

    let private handleCreate (state: Session.State) : Session.State =
        let d = state.Draft
        match d.ActivityTypeId, d.ProjectId, d.Description, d.BusinessPurpose, parseInstant d.StartedAt, parseInstant d.EndedAt with
        | Some activityTypeId, Some projectId, Some description, Some businessPurpose, Some startedAt, Some endedAt ->
            let command : CreateActivityCommand =
                { ActivityTypeId = activityTypeId; ProjectId = projectId; Description = description; BusinessPurpose = businessPurpose
                  Outcome = d.Outcome |> Option.defaultValue ""; TagIds = d.TagIds |> Set.toList; EntryMethod = Manual
                  ReconstructionReason = d.ReconstructionReason; StartedAt = startedAt; EndedAt = endedAt; ClientTimestamp = None }
            match Commands.create state.Environment state.Document command with
            | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "create"
            | Rejected diagnostics -> state |> withError "create" (firstMessage diagnostics)
            | Conflict(_, _, diagnostics) -> state |> withError "create" (firstMessage diagnostics)
        | _ -> state |> withError "create" "Fill in activity type, project, description, business purpose, start, and end before saving."

    let private handleAmend (state: Session.State) (activityId: string) : Session.State =
        match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
        | None -> state |> withError "amend" "Activity was not found."
        | Some current ->
            match state.Draft.Reason with
            | None | Some "" -> state |> withError "amend" "A reason is required."
            | Some reason ->
                let d = state.Draft
                let changes : CreateActivityCommand =
                    { ActivityTypeId = d.ActivityTypeId |> Option.defaultValue current.ActivityTypeId
                      ProjectId = d.ProjectId |> Option.defaultValue current.ProjectId
                      Description = d.Description |> Option.defaultValue current.Description
                      BusinessPurpose = d.BusinessPurpose |> Option.defaultValue current.BusinessPurpose
                      Outcome = d.Outcome |> Option.defaultValue current.Outcome
                      TagIds = (if d.TagIds.IsEmpty then current.TagIds else d.TagIds) |> Set.toList
                      EntryMethod = current.EntryMethod
                      ReconstructionReason = d.ReconstructionReason |> Option.orElse current.ReconstructionReason
                      StartedAt = parseInstant d.StartedAt |> Option.defaultValue current.StartedAt
                      EndedAt = parseInstant d.EndedAt |> Option.defaultValue current.EndedAt
                      ClientTimestamp = None }
                let command : AmendActivityCommand = { ActivityId = activityId; ExpectedVersion = Some current.Version; Changes = changes; Reason = reason }
                match Commands.amend state.Environment state.Document command with
                | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "amend"
                | Rejected diagnostics -> state |> withError "amend" (firstMessage diagnostics)
                | Conflict(_, _, diagnostics) -> state |> withError "amend" (firstMessage diagnostics)

    let private handleVoid (state: Session.State) (activityId: string) : Session.State =
        match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
        | None -> state |> withError "void" "Activity was not found."
        | Some current ->
            match state.Draft.Reason with
            | None | Some "" -> state |> withError "void" "A reason is required."
            | Some reason ->
                match Commands.voidActivity state.Environment state.Document { ActivityId = activityId; ExpectedVersion = Some current.Version; Reason = reason } with
                | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "void"
                | Rejected diagnostics -> state |> withError "void" (firstMessage diagnostics)
                | Conflict(_, _, diagnostics) -> state |> withError "void" (firstMessage diagnostics)

    let private handleRestore (state: Session.State) (activityId: string) : Session.State =
        match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
        | None -> state |> withError "restore" "Activity was not found."
        | Some current ->
            match state.Draft.Reason with
            | None | Some "" -> state |> withError "restore" "A reason is required."
            | Some reason ->
                match Commands.restore state.Environment state.Document { ActivityId = activityId; ExpectedVersion = Some current.Version; Reason = reason } with
                | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "restore"
                | Rejected diagnostics -> state |> withError "restore" (firstMessage diagnostics)
                | Conflict(_, _, diagnostics) -> state |> withError "restore" (firstMessage diagnostics)

    let private handleSplit (state: Session.State) (activityId: string) : Session.State =
        match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
        | None -> state |> withError "split" "Activity was not found."
        | Some current ->
            match state.Draft.Reason with
            | None | Some "" -> state |> withError "split" "A reason is required."
            | Some reason ->
                let parts =
                    state.Draft.SplitParts
                    |> Map.toList
                    |> List.sortBy fst
                    |> List.choose (fun (_, p) ->
                        p.DurationText
                        |> Option.bind (fun t -> match Double.TryParse t with true, minutes -> Some minutes | _ -> None)
                        |> Option.map (fun minutes ->
                            { ActivityTypeId = p.ActivityTypeId; ProjectId = p.ProjectId; Description = p.Description
                              BusinessPurpose = p.BusinessPurpose; Outcome = p.Outcome; TagIds = None; DurationMs = minutes * 60000.0 }))
                if parts.Length < 2 then
                    state |> withError "split" "At least two split parts, each with a duration in minutes, are required."
                else
                    let command : SplitActivityCommand = { SourceActivityId = activityId; ExpectedVersion = Some current.Version; Parts = parts; Reason = reason }
                    match Commands.split state.Environment state.Document command with
                    | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "split"
                    | Rejected diagnostics -> state |> withError "split" (firstMessage diagnostics)
                    | Conflict(_, _, diagnostics) -> state |> withError "split" (firstMessage diagnostics)

    let private handleMerge (state: Session.State) : Session.State =
        let d = state.Draft
        let sourceIds = d.MergeSourceIds |> Set.toList
        match d.ActivityTypeId, d.ProjectId, d.Description, d.BusinessPurpose, d.Reason with
        | Some activityTypeId, Some projectId, Some description, Some businessPurpose, Some reason when sourceIds.Length >= 2 && reason <> "" ->
            let command : MergeActivitiesCommand =
                { SourceActivityIds = sourceIds; ExpectedVersions = Map.empty; ActivityTypeId = activityTypeId
                  ProjectId = projectId; Description = description; BusinessPurpose = businessPurpose; Reason = reason }
            match Commands.merge state.Environment state.Document command with
            | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "merge"
            | Rejected diagnostics -> state |> withError "merge" (firstMessage diagnostics)
            | Conflict(_, _, diagnostics) -> state |> withError "merge" (firstMessage diagnostics)
        | _ ->
            state |> withError "merge" "Select at least two activities to merge, plus an activity type, project, description, business purpose, and reason."

    let private parseEvidenceType =
        function
        | "url" -> Some UrlEvidence
        | "linkedin-post" -> Some LinkedInPostEvidence
        | "github-commit" -> Some GitHubCommitEvidence
        | "pull-request" -> Some PullRequestEvidence
        | "issue" -> Some IssueEvidence
        | "calendar-event" -> Some CalendarEventEvidence
        | "document" -> Some DocumentEvidence
        | "screenshot" -> Some ScreenshotEvidence
        | "other" -> Some OtherEvidence
        | _ -> None

    let private handleAttachEvidence (state: Session.State) (activityId: string) : Session.State =
        match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
        | None -> state |> withError "evidence" "Activity was not found."
        | Some current ->
            match state.Draft.EvidenceType |> Option.bind parseEvidenceType with
            | None ->
                state
                |> withError "evidence" "Choose a valid evidence type (url, linkedin-post, github-commit, pull-request, issue, calendar-event, document, screenshot, other)."
            | Some evidenceType ->
                let command : AttachEvidenceCommand =
                    { ActivityId = activityId; ExpectedVersion = Some current.Version; Type = evidenceType
                      Uri = state.Draft.EvidenceUri; Note = state.Draft.EvidenceNote; Hash = None; Label = state.Draft.EvidenceLabel |> Option.defaultValue "" }
                match Commands.attachEvidence state.Environment state.Document command with
                | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "evidence"
                | Rejected diagnostics -> state |> withError "evidence" (firstMessage diagnostics)
                | Conflict(_, _, diagnostics) -> state |> withError "evidence" (firstMessage diagnostics)

    let private handleDetachEvidence (state: Session.State) (key: string) : Session.State =
        match key.Split ':' with
        | [| activityId; evidenceLinkId |] ->
            match state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = activityId) with
            | None -> state |> withError "evidence" "Activity was not found."
            | Some current ->
                match state.Draft.Reason with
                | None | Some "" -> state |> withError "evidence" "A reason is required."
                | Some reason ->
                    let command : DetachEvidenceCommand = { ActivityId = activityId; ExpectedVersion = Some current.Version; EvidenceLinkId = evidenceLinkId; Reason = reason }
                    match Commands.detachEvidence state.Environment state.Document command with
                    | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "evidence"
                    | Rejected diagnostics -> state |> withError "evidence" (firstMessage diagnostics)
                    | Conflict(_, _, diagnostics) -> state |> withError "evidence" (firstMessage diagnostics)
        | _ -> state |> withError "evidence" "Invalid evidence reference."

    let private handleAttestDay (state: Session.State) : Session.State =
        match state.Draft.AttestationStatement with
        | None | Some "" -> state |> withError "attest" "A statement is required."
        | Some statement ->
            let command : AttestDayCommand = { Date = state.SelectedDate; Statement = statement; ExpectedProjectionVersion = None }
            match Commands.attestDay state.Environment state.Document command with
            | Success(document, _) -> { state with Document = document; Draft = Session.Draft.empty } |> clearError "attest"
            | Rejected diagnostics -> state |> withError "attest" (firstMessage diagnostics)
            | Conflict(_, _, diagnostics) -> state |> withError "attest" (firstMessage diagnostics)

    let private handleStartTimer (state: Session.State) : Session.State =
        let d = state.Draft
        match d.ActivityTypeId, d.ProjectId with
        | Some activityTypeId, Some projectId ->
            match Timer.start activityTypeId projectId (d.Description |> Option.defaultValue "") (state.Environment.Clock()) state.TimerState with
            | Ok timerState -> { state with TimerState = timerState } |> clearError "timer"
            | Error diagnostic -> state |> withError "timer" diagnostic.Message
        | _ -> state |> withError "timer" "Choose an activity type and project before starting the timer."

    let private handlePauseTimer (state: Session.State) : Session.State =
        match Timer.pause (state.Environment.Clock()) state.TimerState with
        | Ok timerState -> { state with TimerState = timerState } |> clearError "timer"
        | Error diagnostic -> state |> withError "timer" diagnostic.Message

    let private handleResumeTimer (state: Session.State) : Session.State =
        match Timer.resume (state.Environment.Clock()) state.TimerState with
        | Ok timerState -> { state with TimerState = timerState } |> clearError "timer"
        | Error diagnostic -> state |> withError "timer" diagnostic.Message

    /// A stopped, non-discarded timer is turned into an Activity via a separate
    /// `create` call (mirrors `worker/src/store.js`: `stopTimer` never itself
    /// records an activity). If `create` fails — most commonly a missing
    /// business purpose the user hadn't filled in yet — the elapsed interval is
    /// preserved in the Draft rather than lost, so the user only needs to fill
    /// in the remaining fields and submit `CreateActivity`.
    let private handleStopTimer (state: Session.State) : Session.State =
        match Commands.stopTimer state.Environment state.TimerState with
        | Error diagnostic -> state |> withError "timer" diagnostic.Message
        | Ok stopped when stopped.Discarded -> { state with TimerState = NoTimer } |> clearError "timer"
        | Ok stopped ->
            let d = state.Draft
            let command : CreateActivityCommand =
                { ActivityTypeId = stopped.ActivityTypeId
                  ProjectId = stopped.ProjectId
                  Description = d.Description |> Option.defaultValue stopped.Description
                  BusinessPurpose = d.BusinessPurpose |> Option.defaultValue ""
                  Outcome = d.Outcome |> Option.defaultValue ""
                  TagIds = d.TagIds |> Set.toList
                  EntryMethod = EntryMethod.Timer
                  ReconstructionReason = d.ReconstructionReason
                  StartedAt = stopped.StartedAt
                  EndedAt = stopped.StartedAt.AddMilliseconds stopped.ExactMs
                  ClientTimestamp = None }
            match Commands.create state.Environment state.Document command with
            | Success(document, _) -> { state with Document = document; TimerState = NoTimer; Draft = Session.Draft.empty } |> clearError "timer"
            | Rejected diagnostics ->
                { state with
                    TimerState = NoTimer
                    Draft =
                        { d with
                            ActivityTypeId = Some stopped.ActivityTypeId
                            ProjectId = Some stopped.ProjectId
                            StartedAt = Some(stopped.StartedAt.ToString "O")
                            EndedAt = Some((stopped.StartedAt.AddMilliseconds stopped.ExactMs).ToString "O") } }
                |> withError "timer" (firstMessage diagnostics)
            | Conflict(_, _, diagnostics) -> { state with TimerState = NoTimer } |> withError "timer" (firstMessage diagnostics)

    let private handleEvent (state: Session.State) (event: SemanticEvent) : Session.State =
        match event.Name with
        | "CreateActivity" -> handleCreate state
        | "AmendActivity" -> event.Key |> Option.map (handleAmend state) |> Option.defaultValue state
        | "VoidActivity" -> event.Key |> Option.map (handleVoid state) |> Option.defaultValue state
        | "RestoreActivity" -> event.Key |> Option.map (handleRestore state) |> Option.defaultValue state
        | "SplitActivity" -> event.Key |> Option.map (handleSplit state) |> Option.defaultValue state
        | "MergeActivities" -> handleMerge state
        | "AttachEvidence" -> event.Key |> Option.map (handleAttachEvidence state) |> Option.defaultValue state
        | "DetachEvidence" -> event.Key |> Option.map (handleDetachEvidence state) |> Option.defaultValue state
        | "StartTimer" -> handleStartTimer state
        | "PauseTimer" -> handlePauseTimer state
        | "ResumeTimer" -> handleResumeTimer state
        | "StopTimer" -> handleStopTimer state
        | "AttestDay" -> handleAttestDay state
        | "ViewDay" ->
            match event.Value |> Option.bind (fun v -> match DateOnly.TryParse v with true, d -> Some d | _ -> None) with
            | Some date -> { state with SelectedDate = date; ActiveActivityId = None }
            | None -> state
        | "ViewMonth" -> event.Value |> Option.map (fun m -> { state with SelectedMonth = m }) |> Option.defaultValue state
        | "ViewActivity" -> { state with ActiveActivityId = event.Key }
        | "SelectReportFormat" -> event.Value |> Option.map (fun f -> { state with ReportFormat = f }) |> Option.defaultValue state
        | "ViewScreen" -> event.Value |> Option.map (fun s -> { state with CurrentScreen = s }) |> Option.defaultValue state
        | _ -> { state with Draft = applyDraftField state.Draft event }

    /// Never trusts a Storage outcome as silent success or silent failure —
    /// every branch either loads a validated document or leaves a
    /// PersistenceError for the UI to show.
    let private handleEffectResult (state: Session.State) (result: EffectResult) : Session.State =
        match result with
        | StorageResult("load", StorageSuccess(Some json)) ->
            match DocumentCodec.decode json with
            | Error message -> { state with PersistenceError = Some $"Saved data could not be read: {message}" }
            | Ok document ->
                match Commands.validateDocument state.Environment document with
                | [] -> { state with Document = document; PersistenceError = None }
                | diagnostics -> { state with PersistenceError = Some $"Saved data failed validation: {firstMessage diagnostics}" }
        | StorageResult("load", StorageSuccess None) -> state // nothing saved yet — first visit
        | StorageResult("load", StorageFailure reason) -> { state with PersistenceError = Some $"Could not load saved activities ({reason})." }
        | StorageResult("load", StorageUnknown reason) -> { state with PersistenceError = Some $"Could not confirm whether saved activities loaded ({reason}). Reload to retry." }
        | StorageResult("save", StorageSuccess _) -> { state with PersistenceError = None }
        | StorageResult("save", StorageFailure reason) -> { state with PersistenceError = Some $"Changes could not be saved ({reason}). They will be lost on reload." }
        /// An unknown save outcome must never collapse into success or failure
        /// (SDE Tier-4 doctrine; `docs/DOMAIN-REQUIREMENTS.md`'s persistence
        /// contract's fourth save outcome) — today's synchronous localStorage
        /// host cannot actually produce this case, but a later GitHub-backed
        /// host (a network write whose response never arrives) will, and this
        /// is where its reconciliation surfaces once built.
        | StorageResult("save", StorageUnknown reason) ->
            { state with PersistenceError = Some $"Changes may not have saved ({reason}). Do not assume they were lost — reload to check before re-entering them." }
        | StorageResult(_, _) -> state
        | HttpResult _ -> state

    /// Returns the new state and whether the document changed as a result (the
    /// only condition that should trigger a Storage.set) — Initialize and
    /// effect results never do; only a state-mutating Event does, and only when
    /// it actually succeeded.
    let private handleMessage (state: Session.State) (message: BrowserToEngineMessage) : Session.State * bool =
        match message with
        | Initialize _ -> state, false
        | Event event ->
            let previousSequence = state.Document.EventSequence
            let newState = handleEvent state event
            newState, newState.Document.EventSequence <> previousSequence
        | EffectResultMessage result -> handleEffectResult state result, false

    /// `messageJson`/return value are JSON strings matching
    /// `BrowserToEngineMessage`/`EngineToBrowserMessage` — see Protocol.fs.
    /// Persistence goes through the Storage effect only — nothing here calls
    /// `localStorage` directly: one `Storage.get` right after Initialize, one
    /// `Storage.set` after any event that actually changed the document.
    let handle (messageJson: string) : string =
        let message = Protocol.parseMessage messageJson
        let newState, documentChanged = handleMessage Session.current message
        Session.current <- newState
        let view = Projections.build Session.current

        let effects =
            match message with
            | Initialize _ -> [ StorageEffect("load", StorageGet, storageKey) ]
            | _ when documentChanged -> [ StorageEffect("save", StorageSet(DocumentCodec.encode Session.current.Document), storageKey) ]
            | _ -> []

        Protocol.serializeMessage { View = view; Effects = effects; Cancellations = [] }
