namespace Ledger.Engine

open System
open Ledger.Domain
open Ledger.Engine.Protocol
open EchelonFoundry.Chrona.Integration

/// SDE Tier 3 (Application / Projection / Orchestration): coordinates
/// commands, projections, and requested effects; must never become a second
/// semantic authority (`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`) — all
/// domain legality lives in `Ledger.Domain.Commands` (Tier 2).
///
/// The single entry point crossing the WASM boundary (via the `Ledger.Wasm`
/// C# JSExport shim, Tier 4). Owns command routing only.
module Dispatch =

    let private storageKey = "business-activity-ledger:v1"
    let private githubConfigStorageKey = "business-activity-ledger:github-config:v1"

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
        | "DraftGitHubOwnerChanged" -> { draft with GitHubOwner = event.Value }
        | "DraftGitHubRepoChanged" -> { draft with GitHubRepo = event.Value }
        | "DraftGitHubFolderChanged" -> { draft with GitHubFolder = event.Value }
        | "DraftGitHubBranchChanged" -> { draft with GitHubBranch = event.Value }
        | "DraftGitHubTokenChanged" -> { draft with GitHubToken = event.Value }
        | "DraftTimezoneChanged" -> { draft with Timezone = event.Value }
        | "DraftNewProjectNameChanged" -> { draft with NewProjectName = event.Value }
        | "DraftNewActivityTypeNameChanged" -> { draft with NewActivityTypeName = event.Value }
        | "DraftNewTagNameChanged" -> { draft with NewTagName = event.Value }
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

    // --- GitHub sync ---------------------------------------------------------------

    /// `Folder` is deliberately not required here — an omitted or blank
    /// folder falls back to `GitHubSync.defaultFolder` (see `dataFilePath`),
    /// never to the repo root, since the target repository is never assumed
    /// to be dedicated to this app alone. `Login`/`DisplayName` always reset
    /// to `None` on a (re-)save — even if unchanged, GitHub is asked again
    /// via the "github-whoami" effect `handle` emits alongside this, since a
    /// re-save may mean a different token (and so a different person).
    let private handleSaveGitHubConfig (state: Session.State) : Session.State =
        let d = state.Draft
        match d.GitHubOwner, d.GitHubRepo, d.GitHubToken with
        | Some owner, Some repo, Some token when owner <> "" && repo <> "" && token <> "" ->
            let config : Session.GitHubSyncConfig =
                { Owner = owner
                  Repo = repo
                  Folder = d.GitHubFolder |> Option.filter (fun f -> f <> "") |> Option.defaultValue GitHubSync.defaultFolder
                  Branch = d.GitHubBranch |> Option.filter (fun b -> b <> "") |> Option.defaultValue "main"
                  Token = token
                  Login = None
                  DisplayName = None }
            { state with
                GitHubSync = Some config
                GitHubDocumentSha = None
                GitHubSettingsSha = None
                GitHubCommitParentSha = None
                GitHubSyncStatus = "identifying"
                Draft = Session.Draft.empty }
            |> clearError "githubConfig"
            |> clearError "githubSync"
            |> clearError "githubSettings"
        | _ -> state |> withError "githubConfig" "Owner, repository, and a token are all required."

    /// Applies the timezone preference locally regardless of GitHub sync —
    /// it's a legitimate local-only preference too. The push to GitHub (if
    /// configured and identified) is decided in `handle`, same as every
    /// other requested effect.
    let private handleSaveSettings (state: Session.State) : Session.State =
        { state with Timezone = state.Draft.Timezone |> Option.orElse state.Timezone; Draft = Session.Draft.empty }

    /// These only validate preconditions and mark the status as in-flight —
    /// the actual `HttpEffect` request is built in `handle`, which is where
    /// every other requested effect is decided too.
    let private handlePullFromGitHub (state: Session.State) : Session.State =
        match state.GitHubSync with
        | Some { Login = Some _ } -> { state with GitHubSyncStatus = "pulling" } |> clearError "githubSync"
        | Some { Login = None } -> state |> withError "githubSync" "Still identifying your GitHub account from your token — try again in a moment."
        | None -> state |> withError "githubSync" "Save your GitHub sync settings first."

    let private handlePushToGitHub (state: Session.State) : Session.State =
        match state.GitHubSync with
        | Some { Login = Some _ } -> { state with GitHubSyncStatus = "pushing" } |> clearError "githubSync"
        | Some { Login = None } -> state |> withError "githubSync" "Still identifying your GitHub account from your token — try again in a moment."
        | None -> state |> withError "githubSync" "Save your GitHub sync settings first."

    // --- Reference data admin (Projects/Activity Types/Tags) ------------------

    /// Shared "admin" error key across all six operations below — the admin
    /// page (More screen) shows one error region for the whole reference-data
    /// section, the same way the GitHub sync section shows one for its own.
    let private handleAddProject (state: Session.State) : Session.State =
        match state.Draft.NewProjectName |> Option.map (fun n -> n.Trim()) with
        | Some name when name <> "" ->
            match ReferenceCatalog.add state.Environment.Projects name with
            | Ok(projects, _) ->
                { state with Environment = { state.Environment with Projects = projects }; Draft = { state.Draft with NewProjectName = None } }
                |> clearError "admin"
            | Error diagnostic -> state |> withError "admin" diagnostic.Message
        | _ -> state |> withError "admin" "A project name is required."

    let private handleAddActivityType (state: Session.State) : Session.State =
        match state.Draft.NewActivityTypeName |> Option.map (fun n -> n.Trim()) with
        | Some name when name <> "" ->
            match ReferenceCatalog.add state.Environment.ActivityTypes name with
            | Ok(activityTypes, _) ->
                { state with Environment = { state.Environment with ActivityTypes = activityTypes }; Draft = { state.Draft with NewActivityTypeName = None } }
                |> clearError "admin"
            | Error diagnostic -> state |> withError "admin" diagnostic.Message
        | _ -> state |> withError "admin" "An activity type name is required."

    let private handleAddTag (state: Session.State) : Session.State =
        match state.Draft.NewTagName |> Option.map (fun n -> n.Trim()) with
        | Some name when name <> "" ->
            match ReferenceCatalog.add state.Environment.Tags name with
            | Ok(tags, _) ->
                { state with Environment = { state.Environment with Tags = tags }; Draft = { state.Draft with NewTagName = None } }
                |> clearError "admin"
            | Error diagnostic -> state |> withError "admin" diagnostic.Message
        | _ -> state |> withError "admin" "A tag name is required."

    let private handleSetProjectActive (state: Session.State) (id: string) (active: bool) : Session.State =
        match ReferenceCatalog.setActive state.Environment.Projects id active with
        | Ok projects -> { state with Environment = { state.Environment with Projects = projects } } |> clearError "admin"
        | Error diagnostic -> state |> withError "admin" diagnostic.Message

    let private handleSetActivityTypeActive (state: Session.State) (id: string) (active: bool) : Session.State =
        match ReferenceCatalog.setActive state.Environment.ActivityTypes id active with
        | Ok activityTypes -> { state with Environment = { state.Environment with ActivityTypes = activityTypes } } |> clearError "admin"
        | Error diagnostic -> state |> withError "admin" diagnostic.Message

    let private handleSetTagActive (state: Session.State) (id: string) (active: bool) : Session.State =
        match ReferenceCatalog.setActive state.Environment.Tags id active with
        | Ok tags -> { state with Environment = { state.Environment with Tags = tags } } |> clearError "admin"
        | Error diagnostic -> state |> withError "admin" diagnostic.Message

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
        | "SaveGitHubConfig" -> handleSaveGitHubConfig state
        | "PullFromGitHub" -> handlePullFromGitHub state
        | "PushToGitHub" -> handlePushToGitHub state
        | "SaveSettings" -> handleSaveSettings state
        | "AddProject" -> handleAddProject state
        | "AddActivityType" -> handleAddActivityType state
        | "AddTag" -> handleAddTag state
        | "SetProjectActive" ->
            match event.Key, event.Value with
            | Some id, Some v -> handleSetProjectActive state id (v = "on")
            | _ -> state
        | "SetActivityTypeActive" ->
            match event.Key, event.Value with
            | Some id, Some v -> handleSetActivityTypeActive state id (v = "on")
            | _ -> state
        | "SetTagActive" ->
            match event.Key, event.Value with
            | Some id, Some v -> handleSetTagActive state id (v = "on")
            | _ -> state
        /// A deliberate no-op: `web/dom-bindings.js` dispatches this every
        /// six seconds while a timer is running purely to force a fresh
        /// `Projections.build` — `timerFields`'s elapsed-time calculation
        /// reads `environment.Clock()` fresh on every call, but nothing
        /// re-renders between real events, so without this the elapsed
        /// label would sit frozen at whatever it read on the last click.
        /// Leaves `Document`/`TimerState` untouched, so it never triggers a
        /// Storage save or a GitHub push (both gated on those changing).
        | "Tick" -> state
        | _ -> { state with Draft = applyDraftField state.Draft event }

    // --- CHR-INT-015: startup observation reconciliation --------------------
    //
    // See docs/integration/STARTUP-RECONCILIATION.md and
    // Session.IntegrationReconciliation's doc comment. Every step below
    // follows the same shape: on a usable success, advance `Step` to
    // whatever comes next; on anything else (a 404 that means "nothing
    // here", a genuine error, a cancellation, an unreadable body), give up
    // on just the one observation (or, at the listing step, the one
    // project) currently in progress and move on — nothing here is
    // surfaced as a user-visible error, matching the specification's own
    // "never block the first render" concern applying just as much to a
    // background reconciliation pass. A skipped observation is simply
    // retried, from scratch, on the next startup — always safe, since
    // nothing is ever written except by the create-only candidate/receipt
    // writes below, which are themselves idempotent across retries.

    /// Moves to the next unit of work: the next observation already
    /// queued for the current project, or (once that queue is empty) the
    /// next project's inbox listing, or `None` once both are exhausted —
    /// reconciliation is then simply not running again until the next
    /// `reference.json` pull re-seeds it (`beginObservationReconciliation`,
    /// below).
    let private advanceReconciliation (reconciliation: Session.IntegrationReconciliation) : Session.IntegrationReconciliation option =
        match reconciliation.RemainingObservations with
        | next :: rest -> Some { reconciliation with RemainingObservations = rest; Step = Session.AwaitingObservationBody next }
        | [] ->
            match reconciliation.RemainingProjectIds with
            | nextProjectId :: restProjectIds ->
                Some
                    { CurrentProjectId = nextProjectId
                      RemainingProjectIds = restProjectIds
                      RemainingObservations = []
                      Step = Session.AwaitingInboxListing }
            | [] -> None

    /// Seeds reconciliation for every currently-known project, always
    /// built from the environment as it exists right after `reference.json`
    /// resolves (see the "github-reference-pull" cases below) — never from
    /// whatever was captured in an effect built before that pull ran. A
    /// no-op if reconciliation is already mid-flight (defensive: nothing
    /// in this codebase re-pulls `reference.json` mid-session today, but
    /// restarting over an in-progress pass would abandon it silently).
    let private beginObservationReconciliation (state: Session.State) : Session.State =
        match state.IntegrationReconciliation with
        | Some _ -> state
        | None ->
            match state.Environment.Projects |> Map.toList |> List.map fst with
            | [] -> state
            | firstProjectId :: restProjectIds ->
                { state with
                    IntegrationReconciliation =
                        Some
                            { CurrentProjectId = firstProjectId
                              RemainingProjectIds = restProjectIds
                              RemainingObservations = []
                              Step = Session.AwaitingInboxListing } }

    /// A freshly-decided processing receipt for `observationId`, timestamped now.
    let private receiptFor (state: Session.State) (observationId: string) (result: Integration.ObservationResult) : Integration.ProcessingReceipt =
        { ReceiptVersion = Integration.ProcessingReceipt.CurrentReceiptVersion
          ObservationId = observationId
          ProcessedAt = state.Environment.Clock()
          Result = result }

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

        /// A cache-read failure here is silent rather than surfaced as an
        /// error: unlike the ledger's own Storage load, this is an optional
        /// convenience (remembering GitHub sync settings across a reload) —
        /// the user can always just re-enter them, so a scary banner on
        /// every page load would cost more than it protects.
        | StorageResult("github-config-load", StorageSuccess(Some json)) ->
            match GitHubSync.decodeConfig json with
            | Ok config -> { state with GitHubSync = Some config; GitHubSyncStatus = (if config.Login.IsSome then "idle" else "identifying") }
            | Error _ -> state
        | StorageResult("github-config-load", StorageSuccess None) -> state
        | StorageResult("github-config-load", StorageFailure _) -> state
        | StorageResult("github-config-load", StorageUnknown _) -> state

        | StorageResult(_, _) -> state

        | HttpResult("github-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseGetResponse body with
            | Error message -> { state with GitHubSyncStatus = "error" } |> withError "githubSync" message
            | Ok parsed ->
                match DocumentCodec.decode parsed.DocumentJson with
                | Error message -> { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"GitHub's stored ledger could not be read: {message}"
                | Ok document ->
                    match Commands.validateDocument state.Environment document with
                    | [] ->
                        { state with
                            Document = document
                            GitHubDocumentSha = Some parsed.Sha
                            GitHubSyncStatus = "synced"
                            GitHubLastSyncedAt = Some(state.Environment.Clock()) }
                        |> clearError "githubSync"
                    | diagnostics ->
                        { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"GitHub's stored ledger failed validation: {firstMessage diagnostics}"
        | HttpResult("github-pull", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" "GitHub's response had no content."
        | HttpResult("github-pull", OutcomeSuccess(404, _)) ->
            { state with GitHubSyncStatus = "idle" } |> withError "githubSync" "No ledger file exists yet at that path — use Sync now to create it."
        | HttpResult("github-pull", OutcomeSuccess(status, bodyOpt)) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"GitHub returned {status}: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-pull", OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"Could not reach GitHub ({reason})."
        | HttpResult("github-pull", OutcomeCancelled) -> { state with GitHubSyncStatus = "idle" } |> clearError "githubSync"
        | HttpResult("github-pull", OutcomeUnknown reason) ->
            { state with GitHubSyncStatus = "unknown" } |> withError "githubSync" $"Could not confirm whether the pull from GitHub succeeded ({reason})."

        | HttpResult("github-push", OutcomeSuccess(status, Some body)) when status = 200 || status = 201 ->
            match GitHubSync.parsePutResponse body with
            | Ok sha ->
                { state with GitHubDocumentSha = Some sha; GitHubSyncStatus = "synced"; GitHubLastSyncedAt = Some(state.Environment.Clock()) }
                |> clearError "githubSync"
            | Error message -> { state with GitHubSyncStatus = "error" } |> withError "githubSync" message
        | HttpResult("github-push", OutcomeSuccess(status, None)) when status = 200 || status = 201 ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" "GitHub's response had no content."
        /// A stale write — GitHub's 409 (the old single-file Contents API
        /// PUT's own conflict status) or 422 (the atomic commit's ref-update
        /// step rejecting a non-fast-forward move — someone/something else
        /// advanced the branch since step 1 read it) — no longer forces a
        /// manual pull-then-redo: the remote ledger is fetched
        /// (`"github-push-conflict-pull"`, below) and merged with the local
        /// document automatically.
        | HttpResult("github-push", OutcomeSuccess((409 | 422), _)) -> { state with GitHubSyncStatus = "merging" } |> clearError "githubSync"
        | HttpResult("github-push", OutcomeSuccess(status, bodyOpt)) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"GitHub returned {status}: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-push", OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"Could not reach GitHub ({reason})."
        | HttpResult("github-push", OutcomeCancelled) -> { state with GitHubSyncStatus = "idle" } |> clearError "githubSync"
        /// An unknown push outcome must never collapse into success or failure —
        /// the same doctrine `StorageResult("save", StorageUnknown _)` already
        /// applies above — because the write may or may not have reached
        /// GitHub. Rather than leaving that to the user to sort out by hand,
        /// it is reconciled automatically: the ledger is re-fetched and
        /// classified (see `"github-push-reconcile-pull"`, below) per
        /// `Ledger.Domain.Services.ReconciliationStatus`'s dormant vocabulary
        /// — the first place that type's shape actually gets used, even
        /// though `Services.LedgerStore` itself stays unwired (see
        /// manifest.md).
        | HttpResult("github-push", OutcomeUnknown _) -> { state with GitHubSyncStatus = "reconciling" } |> clearError "githubSync"

        /// The remote ledger fetched after a push conflict. On success this
        /// merges it with the local document (`LedgerDocument.merge`) and
        /// hands the merged result back to `handleMessage` (via
        /// `GitHubSyncStatus = "pushing"`) to retry the push with the fetched
        /// `sha` — see `handleMessage`'s `conflictEffects`. Any failure here
        /// falls back to the same manual "pull latest and resolve" guidance
        /// the conflict used to give unconditionally.
        | HttpResult("github-push-conflict-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseGetResponse body with
            | Error message ->
                { state with GitHubSyncStatus = "conflict" } |> withError "githubSync" $"Could not automatically merge ({message}). Pull latest and resolve manually."
            | Ok parsed ->
                match DocumentCodec.decode parsed.DocumentJson with
                | Error message ->
                    { state with GitHubSyncStatus = "conflict" }
                    |> withError "githubSync" $"Could not automatically merge — GitHub's stored ledger could not be read ({message}). Pull latest and resolve manually."
                | Ok remoteDocument ->
                    let merged = LedgerDocument.merge state.Document remoteDocument
                    match Commands.validateDocument state.Environment merged with
                    | [] ->
                        { state with Document = merged; GitHubDocumentSha = Some parsed.Sha; GitHubSyncStatus = "pushing" }
                        |> clearError "githubSync"
                    | diagnostics ->
                        { state with GitHubSyncStatus = "conflict" }
                        |> withError "githubSync" $"Automatic merge failed validation ({firstMessage diagnostics}). Pull latest and resolve manually."
        | HttpResult("github-push-conflict-pull", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            { state with GitHubSyncStatus = "conflict" } |> withError "githubSync" "Could not automatically merge — GitHub's response had no content. Pull latest and resolve manually."
        | HttpResult("github-push-conflict-pull", OutcomeSuccess(status, bodyOpt)) ->
            { state with GitHubSyncStatus = "conflict" }
            |> withError "githubSync" $"Could not automatically merge (GitHub returned {status}: {GitHubSync.errorMessage bodyOpt}). Pull latest and resolve manually."
        | HttpResult("github-push-conflict-pull", OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "conflict" }
            |> withError "githubSync" $"Could not automatically merge — could not reach GitHub ({reason}). Pull latest and resolve manually."
        | HttpResult("github-push-conflict-pull", OutcomeCancelled) -> { state with GitHubSyncStatus = "conflict" }
        | HttpResult("github-push-conflict-pull", OutcomeUnknown reason) ->
            { state with GitHubSyncStatus = "conflict" }
            |> withError "githubSync" $"Could not confirm whether the automatic merge fetch succeeded ({reason}). Pull latest and resolve manually."

        /// Classifies an `Unknown` push outcome by comparing the ledger this
        /// fetch actually found against what was attempted (`state.Document`,
        /// untouched since the push — only a *confirmed* push ever replaces
        /// it) and against the `sha` the push expected to overwrite
        /// (`state.GitHubDocumentSha`, likewise untouched until a push
        /// confirms): the write is `Applied` if the fetched document already
        /// matches what was sent — nothing left to do — and otherwise is
        /// `NotApplied`/`ReconciliationConflict` either way resolved the same
        /// way: merge (`LedgerDocument.merge`, safe even when nothing
        /// actually changed remotely) and retry, via `GitHubSyncStatus =
        /// "pushing"` and `handleMessage`'s `reconciliationEffects`.
        | HttpResult("github-push-reconcile-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseGetResponse body with
            | Error message ->
                { state with GitHubSyncStatus = "unknown" }
                |> withError "githubSync" $"Could not confirm whether changes reached GitHub ({message}). Pull latest to check before re-entering them."
            | Ok parsed ->
                match DocumentCodec.decode parsed.DocumentJson with
                | Error message ->
                    { state with GitHubSyncStatus = "unknown" }
                    |> withError "githubSync" $"Could not confirm whether changes reached GitHub — the response could not be read ({message}). Pull latest to check before re-entering them."
                | Ok remoteDocument when remoteDocument = state.Document ->
                    // Applied: the write we couldn't confirm did land after all.
                    { state with GitHubDocumentSha = Some parsed.Sha; GitHubSyncStatus = "synced"; GitHubLastSyncedAt = Some(state.Environment.Clock()) }
                    |> clearError "githubSync"
                | Ok remoteDocument ->
                    // NotApplied (remote unchanged from the pre-push baseline) or
                    // ReconciliationConflict (something else landed meanwhile) —
                    // both resolved the same way: merge and retry the push.
                    let merged = LedgerDocument.merge state.Document remoteDocument
                    match Commands.validateDocument state.Environment merged with
                    | [] ->
                        { state with Document = merged; GitHubDocumentSha = Some parsed.Sha; GitHubSyncStatus = "pushing" }
                        |> clearError "githubSync"
                    | diagnostics ->
                        { state with GitHubSyncStatus = "unknown" }
                        |> withError "githubSync" $"Could not reconcile automatically ({firstMessage diagnostics}). Pull latest and resolve manually."
        | HttpResult("github-push-reconcile-pull", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            { state with GitHubSyncStatus = "unknown" }
            |> withError "githubSync" "Could not confirm whether changes reached GitHub — the response had no content. Pull latest to check before re-entering them."
        | HttpResult("github-push-reconcile-pull", OutcomeSuccess(status, bodyOpt)) ->
            { state with GitHubSyncStatus = "unknown" }
            |> withError "githubSync" $"Could not confirm whether changes reached GitHub (GitHub returned {status}: {GitHubSync.errorMessage bodyOpt}). Pull latest to check before re-entering them."
        | HttpResult("github-push-reconcile-pull", OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "unknown" }
            |> withError "githubSync" $"Could not confirm whether changes reached GitHub ({reason}). Pull latest to check before re-entering them."
        | HttpResult("github-push-reconcile-pull", OutcomeCancelled) -> { state with GitHubSyncStatus = "unknown" }
        | HttpResult("github-push-reconcile-pull", OutcomeUnknown reason) ->
            { state with GitHubSyncStatus = "unknown" }
            |> withError "githubSync" $"Still could not confirm whether changes reached GitHub ({reason}). Pull latest to check before re-entering them."

        /// Resolves who the saved token belongs to, never trusting a typed
        /// name — see `Session.GitHubSyncConfig.Login`'s doc comment. Every
        /// per-person path (`GitHubSync.dataFilePath`/`metadataFilePath`)
        /// depends on this having resolved.
        | HttpResult("github-whoami", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseWhoAmIResponse body with
            | Ok identity ->
                { state with
                    GitHubSync = state.GitHubSync |> Option.map (fun c -> { c with Login = Some identity.Login; DisplayName = identity.Name })
                    GitHubSyncStatus = "idle" }
                |> clearError "githubSync"
            | Error message -> { state with GitHubSyncStatus = "error" } |> withError "githubSync" message
        | HttpResult("github-whoami", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" "GitHub's response had no content."
        | HttpResult("github-whoami", OutcomeSuccess(status, bodyOpt)) ->
            { state with GitHubSyncStatus = "error" }
            |> withError "githubSync" $"Could not identify your GitHub account (GitHub returned {status}: {GitHubSync.errorMessage bodyOpt})."
        | HttpResult("github-whoami", OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"Could not identify your GitHub account ({reason})."
        | HttpResult("github-whoami", OutcomeCancelled) -> state
        | HttpResult("github-whoami", OutcomeUnknown reason) ->
            { state with GitHubSyncStatus = "unknown" }
            |> withError "githubSync" $"Could not confirm your GitHub identity ({reason}). Save your settings again to retry."

        /// Any non-success outcome for one of the atomic commit's four setup
        /// steps (ref/base/tree/create — everything before the ref-update
        /// that actually publishes the change) aborts the chain safely:
        /// nothing on GitHub has changed yet at any of those points (a
        /// dangling, unreferenced commit or tree object from a lost
        /// "create" response is harmless — nothing ever points to it), so
        /// there is no conflict or reconciliation to run, just an error to
        /// surface and a fresh retry (from step 1) to let the user request.
        | HttpResult(("github-commit-ref" | "github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeCancelled) ->
            { state with GitHubSyncStatus = "idle" }
        | HttpResult(("github-commit-ref" | "github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeFailure reason) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"Could not commit to GitHub — nothing was written yet ({reason})."
        | HttpResult(("github-commit-ref" | "github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeUnknown reason) ->
            { state with GitHubSyncStatus = "error" }
            |> withError "githubSync" $"Could not confirm a commit step reached GitHub — nothing was written yet ({reason}). Try again."
        | HttpResult(("github-commit-ref" | "github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeSuccess(status, bodyOpt)) when
            status < 200 || status >= 300
            ->
            { state with GitHubSyncStatus = "error" }
            |> withError "githubSync" $"GitHub returned {status} while committing — nothing was written yet: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult(("github-commit-ref" | "github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeSuccess(_, None)) ->
            { state with GitHubSyncStatus = "error" } |> withError "githubSync" "GitHub's response while committing had no content."

        /// Step 1 of the atomic multi-file commit (`GitHubSync.buildRefGetEffect`):
        /// the branch's current commit, needed again at step 4 (the new
        /// commit's `parents`) — a later, separate `handle` call, so it is
        /// persisted on `state` rather than threaded through one function.
        | HttpResult("github-commit-ref", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseRefResponse body with
            | Ok commitSha -> { state with GitHubCommitParentSha = Some commitSha; GitHubSyncStatus = "pushing" } |> clearError "githubSync"
            | Error message -> { state with GitHubSyncStatus = "error" } |> withError "githubSync" $"Could not read the branch's current commit ({message})."
        /// Steps 2-4 need nothing stored on `state` — `handleMessage`'s
        /// `commitChainEffects` reads each success's own body directly to
        /// build the next request, since that always happens in this same
        /// `handle` call, not a later one.
        | HttpResult(("github-commit-base" | "github-commit-tree" | "github-commit-create"), OutcomeSuccess(status, Some _)) when status >= 200 && status < 300 ->
            state |> clearError "githubSync"

        /// Unlike `metadata.json`, `settings.json` is round-tripped: applying
        /// it here is what makes a preference set on one device follow the
        /// person to another (their own error key, `githubSettings`, for the
        /// same reason `githubMetadata` is separate from `githubSync`).
        | HttpResult("github-settings-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseGetResponse body with
            | Error message -> state |> withError "githubSettings" message
            | Ok parsed ->
                match GitHubSync.parseSettingsJson parsed.DocumentJson with
                | Error message -> state |> withError "githubSettings" message
                | Ok settings ->
                    { state with
                        ReportFormat = settings.ReportFormat |> Option.defaultValue state.ReportFormat
                        Timezone = settings.Timezone |> Option.orElse state.Timezone
                        GitHubSettingsSha = Some parsed.Sha }
                    |> clearError "githubSettings"
        | HttpResult("github-settings-pull", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            state |> withError "githubSettings" "GitHub's response had no content."
        | HttpResult("github-settings-pull", OutcomeSuccess(404, _)) -> state |> clearError "githubSettings" // no settings saved yet — not an error
        | HttpResult("github-settings-pull", OutcomeSuccess(status, bodyOpt)) ->
            state |> withError "githubSettings" $"GitHub returned {status} loading settings: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-settings-pull", OutcomeFailure reason) -> state |> withError "githubSettings" $"Could not load settings from GitHub ({reason})."
        | HttpResult("github-settings-pull", OutcomeCancelled) -> state
        | HttpResult("github-settings-pull", OutcomeUnknown reason) ->
            state |> withError "githubSettings" $"Could not confirm whether settings loaded from GitHub ({reason})."

        | HttpResult("github-settings-push", OutcomeSuccess(status, Some body)) when status = 200 || status = 201 ->
            match GitHubSync.parsePutResponse body with
            | Ok sha -> { state with GitHubSettingsSha = Some sha } |> clearError "githubSettings"
            | Error message -> state |> withError "githubSettings" message
        | HttpResult("github-settings-push", OutcomeSuccess(status, None)) when status = 200 || status = 201 ->
            state |> withError "githubSettings" "GitHub's response had no content for settings."
        | HttpResult("github-settings-push", OutcomeSuccess(409, _)) ->
            state |> withError "githubSettings" "Settings on GitHub changed since these were last loaded — pull latest before saving preferences again."
        | HttpResult("github-settings-push", OutcomeSuccess(status, bodyOpt)) ->
            state |> withError "githubSettings" $"GitHub returned {status} saving settings: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-settings-push", OutcomeFailure reason) -> state |> withError "githubSettings" $"Could not reach GitHub to save settings ({reason})."
        | HttpResult("github-settings-push", OutcomeCancelled) -> state
        | HttpResult("github-settings-push", OutcomeUnknown reason) ->
            state |> withError "githubSettings" $"Settings may not have saved to GitHub ({reason})."

        /// Replaces the session's reference data (Projects/ActivityTypes/Tags)
        /// wholesale, keeping `Environment.NewId`/`Clock` untouched. A missing
        /// file (404) is not an error: it just means no one has published a
        /// shared catalog into this repo/folder yet (whether by hand or via
        /// the admin page), so the fixture defaults (`Session.fs`'s
        /// `fixtureEnvironment`) keep serving as the active/inactive lists.
        ///
        /// Both the success and 404 branches also seed CHR-INT-015's
        /// startup observation reconciliation (`beginObservationReconciliation`)
        /// — always from `Environment.Projects` exactly as it stands right
        /// here, once and only once per identity resolve, so the very
        /// first inbox listing it builds is never based on a stale project
        /// list captured before this pull ran.
        | HttpResult("github-reference-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match GitHubSync.parseGetResponse body with
            | Error message -> state |> withError "githubReference" message
            | Ok parsed ->
                match GitHubSync.parseReferenceJson parsed.DocumentJson with
                | Error message -> state |> withError "githubReference" message
                | Ok reference ->
                    let toMap (items: ReferenceItem list) = items |> List.map (fun item -> item.Id, item) |> Map.ofList
                    { state with
                        Environment =
                            { state.Environment with
                                Projects = toMap reference.Projects
                                ActivityTypes = toMap reference.ActivityTypes
                                Tags = toMap reference.Tags }
                        GitHubReferenceSha = Some parsed.Sha }
                    |> clearError "githubReference"
                    |> beginObservationReconciliation
        | HttpResult("github-reference-pull", OutcomeSuccess(status, None)) when status >= 200 && status < 300 ->
            state |> withError "githubReference" "GitHub's response had no content."
        | HttpResult("github-reference-pull", OutcomeSuccess(404, _)) ->
            state |> clearError "githubReference" |> beginObservationReconciliation // no shared catalog published yet — not an error
        | HttpResult("github-reference-pull", OutcomeSuccess(status, bodyOpt)) ->
            state |> withError "githubReference" $"GitHub returned {status} loading reference data: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-reference-pull", OutcomeFailure reason) -> state |> withError "githubReference" $"Could not load reference data from GitHub ({reason})."
        | HttpResult("github-reference-pull", OutcomeCancelled) -> state
        | HttpResult("github-reference-pull", OutcomeUnknown reason) ->
            state |> withError "githubReference" $"Could not confirm whether reference data loaded from GitHub ({reason})."

        /// The admin page's write path — fired after AddProject/AddActivityType/
        /// AddTag/SetProjectActive/SetActivityTypeActive/SetTagActive in
        /// `handleMessage`, below. Shares the `githubReference` error key
        /// with the pull above (one region on the admin page shows either
        /// direction's failure) — a 409 means someone else published a
        /// newer catalog first, so the safe move is to pull latest before
        /// retrying, not to silently overwrite it.
        | HttpResult("github-reference-push", OutcomeSuccess(status, Some body)) when status = 200 || status = 201 ->
            match GitHubSync.parsePutResponse body with
            | Ok sha -> { state with GitHubReferenceSha = Some sha } |> clearError "githubReference"
            | Error message -> state |> withError "githubReference" message
        | HttpResult("github-reference-push", OutcomeSuccess(status, None)) when status = 200 || status = 201 ->
            state |> withError "githubReference" "GitHub's response had no content for the reference catalog."
        | HttpResult("github-reference-push", OutcomeSuccess(409, _)) ->
            state |> withError "githubReference" "The shared catalog on GitHub changed since it was last loaded — pull latest before editing again."
        | HttpResult("github-reference-push", OutcomeSuccess(status, bodyOpt)) ->
            state |> withError "githubReference" $"GitHub returned {status} saving the reference catalog: {GitHubSync.errorMessage bodyOpt}"
        | HttpResult("github-reference-push", OutcomeFailure reason) -> state |> withError "githubReference" $"Could not reach GitHub to save the reference catalog ({reason})."
        | HttpResult("github-reference-push", OutcomeCancelled) -> state
        | HttpResult("github-reference-push", OutcomeUnknown reason) ->
            state |> withError "githubReference" $"The reference catalog may not have saved to GitHub ({reason})."

        /// One project's observation inbox listing. A parseable success
        /// populates the queue (possibly empty, if nothing has ever been
        /// submitted there); anything else — including a 404, which just
        /// means the inbox directory itself doesn't exist yet — is
        /// treated the same as an empty inbox. Either way `advanceReconciliation`
        /// immediately pops the first entry (or, if there is none, moves
        /// on to the next project).
        | HttpResult("github-observations-list", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation ->
                let entries =
                    GitHubSync.parseObservationListing body
                    |> Result.defaultValue []
                    |> List.map (fun entry -> { Session.Name = entry.Name; Session.Path = entry.Path })
                { state with IntegrationReconciliation = advanceReconciliation { reconciliation with RemainingObservations = entries } }
        | HttpResult("github-observations-list", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation { reconciliation with RemainingObservations = [] } }

        /// One observation file's raw content. `peekObservationId` reads
        /// just enough of it to key the receipt/candidate existence
        /// checks that come next — full structural validation is deferred
        /// until (and unless) this turns out to be a genuinely new
        /// observation (see the "github-candidate-pull" 404 case, below).
        /// A payload with no readable id at all can never be safely
        /// checked or receipted, so it is skipped outright.
        | HttpResult("github-observation-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation ->
                match GitHubSync.parseGetResponse body with
                | Error _ -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
                | Ok parsed ->
                    match TimeObservation.peekObservationId parsed.DocumentJson with
                    | None -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
                    | Some observationId ->
                        { state with
                            IntegrationReconciliation =
                                Some { reconciliation with Step = Session.AwaitingReceiptCheck(observationId, parsed.DocumentJson) } }
        | HttpResult("github-observation-pull", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }

        /// specification §23's idempotency check, part 1: does a receipt
        /// already exist for this observation id? A 2xx means yes — this
        /// observation was already fully processed (in an earlier
        /// session, or by another client), so there is nothing left to
        /// do. A 404 means no — move on to checking for a candidate
        /// (§22/23's "candidate written, receipt failed" repair case)
        /// before ever deciding anything new. Anything else is treated
        /// conservatively as "could not confirm": skip this observation
        /// rather than risk minting a second candidate for one that may
        /// already be fully processed.
        | HttpResult("github-receipt-pull", OutcomeSuccess(status, _)) when status >= 200 && status < 300 ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
        | HttpResult("github-receipt-pull", OutcomeSuccess(404, _)) ->
            match state.IntegrationReconciliation with
            | Some({ Step = Session.AwaitingReceiptCheck(observationId, rawJson) } as reconciliation) ->
                { state with IntegrationReconciliation = Some { reconciliation with Step = Session.AwaitingCandidateCheck(observationId, rawJson) } }
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
            | None -> state
        | HttpResult("github-receipt-pull", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }

        /// specification §23's idempotency check, part 2 (only reached
        /// once part 1 confirmed no receipt exists). A 2xx means a
        /// candidate already exists with no receipt — repair *only* the
        /// missing receipt, for this exact candidate, never mint a second
        /// one (§22's core recovery case, proved by CHR-INT-014's "Test
        /// D"). A 404 means this is a genuinely new observation: only now
        /// — with both checks confirmed empty — is it worth fully
        /// validating and applying Chrona's domain policy, via the same
        /// pure `ObservationReconciliation.reconcileRaw` CHR-INT-010
        /// proved and CHR-INT-014 exercised.
        | HttpResult("github-candidate-pull", OutcomeSuccess(status, Some body)) when status >= 200 && status < 300 ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation ->
                match GitHubSync.parseGetResponse body with
                | Error _ -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
                | Ok parsed ->
                    match Integration.TimeCandidate.deserialize parsed.DocumentJson with
                    | Error _ -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
                    | Ok candidate ->
                        let receipt = receiptFor state candidate.SourceObservationId (Integration.CandidateCreated candidate.CandidateId)
                        { state with IntegrationReconciliation = Some { reconciliation with Step = Session.AwaitingReceiptWrite receipt } }
        | HttpResult("github-candidate-pull", OutcomeSuccess(404, _)) ->
            match state.IntegrationReconciliation with
            | Some({ Step = Session.AwaitingCandidateCheck(observationId, rawJson) } as reconciliation) ->
                match Integration.ObservationReconciliation.reconcileRaw state.Environment false None rawJson with
                | Integration.ObservationReconciliation.CandidateCreated candidate ->
                    { state with IntegrationReconciliation = Some { reconciliation with Step = Session.AwaitingCandidateWrite candidate } }
                | Integration.ObservationReconciliation.Rejected reason ->
                    let receipt = receiptFor state observationId (Integration.ObservationResult.Rejected reason)
                    { state with IntegrationReconciliation = Some { reconciliation with Step = Session.AwaitingReceiptWrite receipt } }
                | Integration.ObservationReconciliation.AlreadyProcessed
                | Integration.ObservationReconciliation.ReceiptRepairNeeded _ ->
                    // Unreachable with existingReceipt=false/existingCandidate=None,
                    // both hardcoded just above — handled defensively rather
                    // than assumed away.
                    { state with IntegrationReconciliation = advanceReconciliation reconciliation }
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
            | None -> state
        | HttpResult("github-candidate-pull", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }

        /// The candidate write. A 409/422 means another client already
        /// created it first — safe to treat as equivalent to success here
        /// (unlike a ledger push's 409/422, which means a real conflict to
        /// merge): `CandidateId` is a pure deterministic function of the
        /// observation id (`Integration.TimeCandidate.candidateId`), so
        /// whichever client won the race, the id this receipt references
        /// is exactly the one that now exists. Anything else — a genuine
        /// failure, or an unconfirmed outcome — must NOT be followed by a
        /// receipt write (specification §22's critical ordering: writing
        /// a receipt after a failed/unknown candidate write would
        /// permanently mark an unprocessed observation "done" with
        /// nothing to show for it); this observation is simply retried,
        /// from scratch, on the next startup.
        | HttpResult("github-candidate-push", OutcomeSuccess((200 | 201 | 409 | 422), _)) ->
            match state.IntegrationReconciliation with
            | Some({ Step = Session.AwaitingCandidateWrite candidate } as reconciliation) ->
                let receipt = receiptFor state candidate.SourceObservationId (Integration.CandidateCreated candidate.CandidateId)
                { state with IntegrationReconciliation = Some { reconciliation with Step = Session.AwaitingReceiptWrite receipt } }
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }
            | None -> state
        | HttpResult("github-candidate-push", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }

        /// The receipt write — the last step for one observation,
        /// regardless of its outcome. A 409/422 (someone else already
        /// wrote the same deterministic receipt) is just as final as a
        /// confirmed success; a genuine failure or unconfirmed outcome
        /// leaves the observation exactly where it was (receipt still
        /// missing, candidate already in place), which the next
        /// startup's receipt-then-candidate check safely repairs
        /// (CHR-INT-014's proved recovery path) — never a reason to hold
        /// up this session's pass over the rest of the queue.
        | HttpResult("github-receipt-push", _) ->
            match state.IntegrationReconciliation with
            | None -> state
            | Some reconciliation -> { state with IntegrationReconciliation = advanceReconciliation reconciliation }

        | HttpResult(_, _) -> state

    /// Returns the new state and the effects it requests. LocalStorage stays
    /// the fast/offline cache (unchanged from before GitHub sync existed): one
    /// `Storage.get` right after Initialize, one `Storage.set` after any event
    /// that actually changed the document, and again after a GitHub pull
    /// replaces the document — so the cache never goes stale relative to
    /// whichever source last won. A second, independent `Storage.get`
    /// (`"github-config-load"`) also fires on Initialize, caching whatever
    /// GitHub sync settings were last saved (see `GitHubSync.encodeConfig`).
    /// GitHub itself is only ever reached through `SaveGitHubConfig`
    /// (identity lookup), an identity resolving — either a cached `Login`
    /// found on load or a fresh `"github-whoami"` success — which now always
    /// follows up with both a settings pull and a ledger pull (no more
    /// explicit "Pull latest" needed right after signing in), explicit
    /// `PullFromGitHub`/`PushToGitHub`/`SaveSettings`/`SelectReportFormat`
    /// events, or an auto-push immediately following a mutating command.
    /// Every effect list `dom-bindings.js` receives is run sequentially,
    /// awaiting each request's result before starting the next, so at most
    /// one GitHub request is ever in flight at a time even when a single
    /// trigger (like identity resolving) queues more than one. A ledger push
    /// commits `ledger.json` and `metadata.json` together as one atomic
    /// write via GitHub's Git Data API (`GitHubSync.buildRefGetEffect`
    /// through `buildRefUpdateEffect`) — a five-step chain kicked off here
    /// with just its first step and driven the rest of the way by
    /// `commitChainEffects`, below, each step's request built from the
    /// previous step's result.
    let private handleMessage (state: Session.State) (message: BrowserToEngineMessage) : Session.State * EffectRequest list =
        match message with
        | Initialize _ ->
            state, [ StorageEffect("load", StorageGet, storageKey); StorageEffect("github-config-load", StorageGet, githubConfigStorageKey) ]
        | Event event ->
            let previousSequence = state.Document.EventSequence
            let newState = handleEvent state event
            let documentChanged = newState.Document.EventSequence <> previousSequence
            let cacheEffects =
                (if documentChanged then [ StorageEffect("save", StorageSet(DocumentCodec.encode newState.Document), storageKey) ] else [])
                @ (match event.Name, newState.GitHubSync with
                   | "SaveGitHubConfig", Some config -> [ StorageEffect("github-config-save", StorageSet(GitHubSync.encodeConfig config), githubConfigStorageKey) ]
                   | _ -> [])
            let settingsPushEffect (config: Session.GitHubSyncConfig) (login: string) =
                let settingsJson = GitHubSync.buildSettingsJson newState.ReportFormat newState.Timezone
                [ GitHubSync.buildSettingsPutEffect config login newState.GitHubSettingsSha settingsJson ]
            /// No `login` parameter, unlike `settingsPushEffect` — `reference.json`
            /// lives at the folder root, not under any one person's subfolder
            /// (see `GitHubSync.referenceFilePath`'s doc comment), so any
            /// identified person's edit pushes the same shared file.
            let referencePushEffect (config: Session.GitHubSyncConfig) =
                let env = newState.Environment
                let allOf (directory: Map<string, ReferenceItem>) = directory |> Map.toList |> List.map snd
                let referenceJson = GitHubSync.buildReferenceJson (allOf env.Projects) (allOf env.ActivityTypes) (allOf env.Tags)
                [ GitHubSync.buildReferencePutEffect config newState.GitHubReferenceSha referenceJson ]
            let githubEffects =
                match newState.GitHubSync with
                | None -> []
                | Some config ->
                    match event.Name, config.Login with
                    | "SaveGitHubConfig", _ -> [ GitHubSync.buildWhoAmIEffect config ]
                    | "PullFromGitHub", Some login -> [ GitHubSync.buildGetEffect config login ]
                    | "PushToGitHub", Some _ -> [ GitHubSync.buildRefGetEffect config ]
                    | ("SaveSettings" | "SelectReportFormat"), Some login -> settingsPushEffect config login
                    | ("AddProject" | "AddActivityType" | "AddTag" | "SetProjectActive" | "SetActivityTypeActive" | "SetTagActive"), Some _ when
                        not (newState.Errors.ContainsKey "admin")
                        ->
                        referencePushEffect config
                    | _, Some _ when documentChanged -> [ GitHubSync.buildRefGetEffect config ]
                    | _ -> []
            newState, cacheEffects @ githubEffects
        | EffectResultMessage result ->
            let newState = handleEffectResult state result
            let cacheEffects =
                match result with
                | HttpResult("github-pull", OutcomeSuccess(status, Some _)) when status >= 200 && status < 300 ->
                    [ StorageEffect("save", StorageSet(DocumentCodec.encode newState.Document), storageKey) ]
                /// The merged document is cached the moment a merge succeeds —
                /// whether from a push conflict or from reconciling an
                /// `Unknown` push outcome (recognized either way by the
                /// status transition to "pushing"; see `handleEffectResult`'s
                /// "github-push-conflict-pull"/"github-push-reconcile-pull"
                /// cases) — not only once the retried push confirms, so the
                /// merge survives a reload even if the retry itself never
                /// completes.
                | HttpResult(("github-push-conflict-pull" | "github-push-reconcile-pull"), OutcomeSuccess(status, Some _)) when
                    status >= 200 && status < 300 && newState.GitHubSyncStatus = "pushing"
                    ->
                    [ StorageEffect("save", StorageSet(DocumentCodec.encode newState.Document), storageKey) ]
                | _ -> []
            /// After a config resolves (fresh from `localStorage`, or just
            /// identified), pick up exactly where it's missing something: no
            /// `Login` yet re-runs the identity lookup; a `Login` already in
            /// hand goes straight to pulling that person's settings *and*
            /// their ledger (this used to be settings-only — the ledger
            /// needed an explicit "Pull latest" click even right after
            /// identity resolved, manifest.md's own documented gap, closed
            /// here), and a freshly-resolved `Login` also re-caches the
            /// config (now including it) so a future reload skips the lookup
            /// entirely.
            let identityEffects =
                match result with
                | StorageResult("github-config-load", StorageSuccess(Some _)) ->
                    match newState.GitHubSync with
                    | Some config ->
                        match config.Login with
                        | Some login -> [ GitHubSync.buildSettingsGetEffect config login; GitHubSync.buildGetEffect config login; GitHubSync.buildReferenceGetEffect config ]
                        | None -> [ GitHubSync.buildWhoAmIEffect config ]
                    | None -> []
                | HttpResult("github-whoami", OutcomeSuccess(status, Some _)) when status >= 200 && status < 300 ->
                    match newState.GitHubSync with
                    | Some config ->
                        [ StorageEffect("github-config-save", StorageSet(GitHubSync.encodeConfig config), githubConfigStorageKey) ]
                        @ (match config.Login with
                           | Some login -> [ GitHubSync.buildSettingsGetEffect config login; GitHubSync.buildGetEffect config login; GitHubSync.buildReferenceGetEffect config ]
                           | None -> [])
                    | None -> []
                | _ -> []
            /// Drives the auto-merge round trip: a push conflict fetches the
            /// remote ledger (`buildConflictPullEffect`); once that fetch has
            /// been merged into the local document (status "pushing"), the
            /// merged result is retried by restarting the atomic commit
            /// chain from step 1 (`buildRefGetEffect`) — it will build a
            /// fresh commit on top of whatever the branch now points at,
            /// which is exactly what just-fetched-and-merged calls for. If
            /// that retry conflicts again (another writer raced this one),
            /// the same two steps repeat — self-healing rather than bounded,
            /// but each round only proceeds on a genuine new conflict, never
            /// a busy-loop.
            let conflictEffects =
                match result, newState.GitHubSync with
                | HttpResult("github-push", OutcomeSuccess((409 | 422), _)), Some ({ Login = Some login } as config) ->
                    [ GitHubSync.buildConflictPullEffect config login ]
                | HttpResult("github-push-conflict-pull", OutcomeSuccess(status, Some _)), Some { Login = Some _ } when
                    status >= 200 && status < 300 && newState.GitHubSyncStatus = "pushing"
                    ->
                    [ GitHubSync.buildRefGetEffect newState.GitHubSync.Value ]
                | _ -> []
            /// Drives the same self-healing round trip as `conflictEffects`,
            /// but for a push whose outcome came back `Unknown` rather than a
            /// confirmed conflict: fetch the ledger to classify what happened
            /// (`buildReconciliationPullEffect`), then — unless that
            /// classified as `Applied` (`GitHubSyncStatus` already "synced",
            /// nothing left to retry) — retry by restarting the commit chain,
            /// same as `conflictEffects`.
            let reconciliationEffects =
                match result, newState.GitHubSync with
                | HttpResult("github-push", OutcomeUnknown _), Some ({ Login = Some login } as config) ->
                    [ GitHubSync.buildReconciliationPullEffect config login ]
                | HttpResult("github-push-reconcile-pull", OutcomeSuccess(status, Some _)), Some { Login = Some _ } when
                    status >= 200 && status < 300 && newState.GitHubSyncStatus = "pushing"
                    ->
                    [ GitHubSync.buildRefGetEffect newState.GitHubSync.Value ]
                | _ -> []
            /// Drives the atomic commit chain itself, one step per `handle`
            /// call: each case reads the previous step's own response body
            /// to build the next request (see each builder's doc comment in
            /// `GitHubSync.fs` for what step it is). Step 4's `parents` needs
            /// the commit sha step 1 saw — `newState.GitHubCommitParentSha`,
            /// stored by `handleEffectResult`'s "github-commit-ref" case —
            /// since that arrived in an earlier, separate `handle` call.
            let commitChainEffects =
                match result, newState.GitHubSync with
                | HttpResult("github-commit-ref", OutcomeSuccess(status, Some _)), Some { Login = Some _ } when status >= 200 && status < 300 ->
                    match newState.GitHubCommitParentSha with
                    | Some parentSha -> [ GitHubSync.buildCommitGetEffect newState.GitHubSync.Value parentSha ]
                    | None -> []
                | HttpResult("github-commit-base", OutcomeSuccess(status, Some body)), Some ({ Login = Some login } as config) when
                    status >= 200 && status < 300
                    ->
                    match GitHubSync.parseCommitBaseTreeResponse body with
                    | Ok baseTreeSha ->
                        let metadataJson = GitHubSync.buildMetadataJson login config.DisplayName (newState.Environment.Clock())
                        [ GitHubSync.buildTreeCreateEffect config login baseTreeSha (DocumentCodec.encode newState.Document) metadataJson ]
                    | Error _ -> []
                | HttpResult("github-commit-tree", OutcomeSuccess(status, Some body)), Some ({ Login = Some _ } as config) when
                    status >= 200 && status < 300
                    ->
                    match GitHubSync.parseShaResponse body, newState.GitHubCommitParentSha with
                    | Ok newTreeSha, Some parentSha -> [ GitHubSync.buildCommitCreateEffect config newTreeSha parentSha ]
                    | _ -> []
                | HttpResult("github-commit-create", OutcomeSuccess(status, Some body)), Some ({ Login = Some _ } as config) when
                    status >= 200 && status < 300
                    ->
                    match GitHubSync.parseShaResponse body with
                    | Ok newCommitSha -> [ GitHubSync.buildRefUpdateEffect config newCommitSha ]
                    | Error _ -> []
                | _ -> []
            /// Drives CHR-INT-015's startup observation reconciliation one
            /// HTTP effect at a time — the counterpart to `commitChainEffects`
            /// above, but for the chain `handleEffectResult`'s "github-
            /// reference-pull"/"github-observations-list"/"github-observation-
            /// pull"/"github-receipt-pull"/"github-candidate-pull"/"github-
            /// candidate-push"/"github-receipt-push" cases advance. Every
            /// state decision already happened there; this only reads the
            /// (already-advanced) `Step` to build the one next request —
            /// `Some config, Some reconciliation` only holds once a
            /// relevant result has actually advanced it (the very first
            /// time, via `beginObservationReconciliation` on "github-
            /// reference-pull"), so this never fires on an unrelated event.
            let observationReconciliationEffects =
                match newState.GitHubSync, newState.IntegrationReconciliation with
                | Some config, Some reconciliation ->
                    match result with
                    | HttpResult(
                        ("github-reference-pull"
                        | "github-observations-list"
                        | "github-observation-pull"
                        | "github-receipt-pull"
                        | "github-candidate-pull"
                        | "github-candidate-push"
                        | "github-receipt-push"),
                        _) ->
                        [ match reconciliation.Step with
                          | Session.AwaitingInboxListing -> GitHubSync.buildObservationsListEffect config reconciliation.CurrentProjectId
                          | Session.AwaitingObservationBody observationRef -> GitHubSync.buildObservationGetEffect config observationRef.Path
                          | Session.AwaitingReceiptCheck(observationId, _) -> GitHubSync.buildReceiptGetEffect config observationId
                          | Session.AwaitingCandidateCheck(observationId, _) ->
                              GitHubSync.buildCandidateGetEffect config (Integration.TimeCandidate.candidateId observationId)
                          | Session.AwaitingCandidateWrite candidate ->
                              GitHubSync.buildCandidatePutEffect config candidate.CandidateId (Integration.TimeCandidate.serialize candidate)
                          | Session.AwaitingReceiptWrite receipt ->
                              GitHubSync.buildReceiptPutEffect config receipt.ObservationId (Integration.ProcessingReceipt.serialize receipt) ]
                    | _ -> []
                | _ -> []
            newState,
            cacheEffects
            @ identityEffects
            @ conflictEffects
            @ reconciliationEffects
            @ commitChainEffects
            @ observationReconciliationEffects

    /// `messageJson`/return value are JSON strings matching
    /// `BrowserToEngineMessage`/`EngineToBrowserMessage` — see Protocol.fs.
    let handle (messageJson: string) : string =
        let message = Protocol.parseMessage messageJson
        let newState, effects = handleMessage Session.current message
        Session.current <- newState
        let view = Projections.build Session.current
        Protocol.serializeMessage { View = view; Effects = effects; Cancellations = [] }
