namespace Ledger.Domain

open System

/// Project, Activity Type, and Tag all share this shape in the JS implementation
/// (schemas/domain/{project,activity-type,tag}.schema.json) — one reference-item
/// type covers all three reference directories.
type ReferenceItem =
    { Id: string; Name: string; Active: bool; Version: string }

type Config =
    { SchemaVersion: string
      ProjectionVersion: string
      Timezone: string
      SixMinuteControls: bool
      Theme: string
      EvidenceUploads: bool
      OfflineCommands: bool
      Reports: bool }

/// A required, non-blank text field (Description, Business Purpose). Mirrors the
/// sibling repo's ExactMinutes/TimeOfDay smart-constructor pattern, but unwraps
/// back to a plain trimmed string for storage — Activity's fields are plain
/// strings, matching the JS implementation.
[<Struct>]
type NonBlankText = private NonBlankText of string

module NonBlankText =
    let create (field: string) (code: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error (Diagnostic.domain code (sprintf "%s is required." field) |> Diagnostic.forField field)
        else
            Ok (NonBlankText (value.Trim()))

    let value (NonBlankText v) = v

type EntryMethod =
    | Manual
    | Split
    | Merge
    | Timer

type ActivityStatus = Recorded | Voided | Superseded

/// Evidence is attached, never required (docs/DOMAIN-REQUIREMENTS.md §11).
type EvidenceType =
    | UrlEvidence
    | LinkedInPostEvidence
    | GitHubCommitEvidence
    | PullRequestEvidence
    | IssueEvidence
    | CalendarEventEvidence
    | DocumentEvidence
    | ScreenshotEvidence
    | OtherEvidence

type Evidence =
    { EvidenceLinkId: string
      Type: EvidenceType
      Uri: string option
      Note: string option
      Hash: string option
      Label: string
      AttachedAt: DateTimeOffset }

type AuditEvent =
    | Created of at: DateTimeOffset
    | Amended of at: DateTimeOffset * reason: string
    | Voided of at: DateTimeOffset * reason: string
    | Restored of at: DateTimeOffset * reason: string
    | Superseded of at: DateTimeOffset * reason: string
    | EvidenceAttached of at: DateTimeOffset * evidenceLinkId: string
    | EvidenceDetached of at: DateTimeOffset * evidenceLinkId: string * reason: string
    /// Named `SplitPerformed` (not `Split`) to avoid colliding with `EntryMethod.Split`.
    | SplitPerformed of at: DateTimeOffset * replacementIds: string list * reason: string
    | Merged of at: DateTimeOffset * sourceIds: string list * reason: string

module AuditEvent =
    let occurredAt =
        function
        | Created at -> at
        | Amended(at, _) -> at
        | Voided(at, _) -> at
        | Restored(at, _) -> at
        | Superseded(at, _) -> at
        | EvidenceAttached(at, _) -> at
        | EvidenceDetached(at, _, _) -> at
        | SplitPerformed(at, _, _) -> at
        | Merged(at, _, _) -> at

    let isAmendment = function Amended _ -> true | _ -> false

type Relationships =
    { MergedInto: string option
      SplitInto: string list option
      MergedFrom: string list option }

module Relationships =
    let empty = { MergedInto = None; SplitInto = None; MergedFrom = None }

type Activity =
    { ActivityId: string
      ActivityTypeId: string
      ProjectId: string
      Description: string
      BusinessPurpose: string
      Outcome: string
      TagIds: Set<string>
      EntryMethod: EntryMethod
      ReconstructionReason: string option
      StartedAt: DateTimeOffset
      EndedAt: DateTimeOffset
      ClientTimestamp: DateTimeOffset option
      ServerReceivedAt: DateTimeOffset
      Voided: bool
      Superseded: bool
      Evidence: Evidence list
      History: AuditEvent list
      Relationships: Relationships
      Version: int64
      UpdatedAt: DateTimeOffset }

module Activity =
    let status a =
        if a.Superseded then ActivityStatus.Superseded
        elif a.Voided then ActivityStatus.Voided
        else ActivityStatus.Recorded

    let exactDurationMs a = (a.EndedAt - a.StartedAt).TotalMilliseconds
    let exactMinutes a = exactDurationMs a / 60000.0
    let hasAmendment a = a.History |> List.exists AuditEvent.isAmendment

/// Calendar-date grouping for an instant, matching the JS implementation's
/// `iso(value).slice(0, 10)` — the UTC calendar date, since every timestamp
/// round-trips through `new Date(value).toISOString()`.
module DateKey =
    let ofInstant (dt: DateTimeOffset) = DateOnly.FromDateTime(dt.UtcDateTime)
    let monthOf (dt: DateTimeOffset) = dt.UtcDateTime.ToString("yyyy-MM")

/// Reporting/billing view, always derived, never persisted
/// (docs/DOMAIN-REQUIREMENTS.md §4): each activity rounds *up* to the nearest
/// six-minute increment independently — two two-minute activities bill as
/// twelve minutes combined, never as one four-minute total rounded once.
module Billing =
    let billedMinutesFor (activity: Activity) = 6.0 * ceil (Activity.exactDurationMs activity / 360000.0)
    let billedMsFor (activity: Activity) = billedMinutesFor activity * 60000.0

type ActivityCapability =
    | Amend
    | Split
    | Void
    | MergeAsSource
    | LinkEvidence
    | UnlinkEvidence
    | Restore

module Capability =
    let forActivity (activity: Activity) =
        match Activity.status activity, Activity.exactDurationMs activity with
        | ActivityStatus.Recorded, ms when ms >= 120000.0 ->
            Set.ofList
                [ Amend; Split; Void; MergeAsSource; LinkEvidence
                  if not activity.Evidence.IsEmpty then UnlinkEvidence ]
        | ActivityStatus.Recorded, _ ->
            Set.ofList
                [ Amend; Void; MergeAsSource; LinkEvidence
                  if not activity.Evidence.IsEmpty then UnlinkEvidence ]
        | ActivityStatus.Voided, _ -> Set.ofList [ Restore; LinkEvidence ]
        | ActivityStatus.Superseded, _ -> Set.empty

/// The timer is ephemeral, per-session state — not part of `LedgerDocument` —
/// until `stop` produces an Activity.
type TimerSegment = { Start: DateTimeOffset; End: DateTimeOffset option }
type TimerPhase = TimerRunning | TimerPaused

type ActiveTimer =
    { ActivityTypeId: string
      ProjectId: string
      Description: string
      PausedMs: float
      Segments: TimerSegment list
      Phase: TimerPhase }

type TimerState = NoTimer | ActiveTimerState of ActiveTimer

type StoppedTimer =
    { ActivityTypeId: string
      ProjectId: string
      Description: string
      StartedAt: DateTimeOffset
      ExactMs: float
      ExactMinutes: float
      Discarded: bool }

/// Under-thirty-second timers discard with nothing recorded
/// (docs/DOMAIN-REQUIREMENTS.md §12); at/above it, `exactMs` matches
/// `worker/src/store.js`'s `stopTimer`: `max(0, end - started - pausedMs)`
/// where `end` is the pause moment if stopped-while-paused, else now.
module Timer =
    let private closeOpenSegment (now: DateTimeOffset) (segments: TimerSegment list) =
        segments
        |> List.map (fun segment -> if segment.End = None then { segment with End = Some now } else segment)

    let start activityTypeId projectId description (now: DateTimeOffset) (state: TimerState) =
        match state with
        | ActiveTimerState _ ->
            Error (Diagnostic.domain DiagnosticCode.TimerAlreadyRunning "A timer is already running.")
        | NoTimer ->
            Ok (ActiveTimerState
                    { ActivityTypeId = activityTypeId
                      ProjectId = projectId
                      Description = description
                      PausedMs = 0.0
                      Segments = [ { Start = now; End = None } ]
                      Phase = TimerRunning })

    let pause (now: DateTimeOffset) (state: TimerState) =
        match state with
        | NoTimer -> Error (Diagnostic.domain DiagnosticCode.TimerNotFound "No timer is running.")
        | ActiveTimerState timer when timer.Phase <> TimerRunning ->
            Error (Diagnostic.domain DiagnosticCode.TimerNotRunning "The timer is not running.")
        | ActiveTimerState timer ->
            Ok (ActiveTimerState { timer with Segments = closeOpenSegment now timer.Segments; Phase = TimerPaused })

    let resume (now: DateTimeOffset) (state: TimerState) =
        match state with
        | NoTimer -> Error (Diagnostic.domain DiagnosticCode.TimerNotFound "No timer is running.")
        | ActiveTimerState timer when timer.Phase <> TimerPaused ->
            Error (Diagnostic.domain DiagnosticCode.TimerNotPaused "The timer is not paused.")
        | ActiveTimerState timer ->
            let pausedAt = (List.last timer.Segments).End.Value
            let elapsedPause = (now - pausedAt).TotalMilliseconds
            Ok (ActiveTimerState
                    { timer with
                        PausedMs = timer.PausedMs + elapsedPause
                        Segments = timer.Segments @ [ { Start = now; End = None } ]
                        Phase = TimerRunning })

    let stop (now: DateTimeOffset) (state: TimerState) =
        match state with
        | NoTimer -> Error (Diagnostic.domain DiagnosticCode.TimerNotFound "No timer is running.")
        | ActiveTimerState timer ->
            let startedAt = (List.head timer.Segments).Start
            let effectiveEnd =
                match timer.Phase with
                | TimerPaused -> (List.last timer.Segments).End.Value
                | TimerRunning -> now
            let exactMs = max 0.0 ((effectiveEnd - startedAt).TotalMilliseconds - timer.PausedMs)
            Ok
                { ActivityTypeId = timer.ActivityTypeId
                  ProjectId = timer.ProjectId
                  Description = timer.Description
                  StartedAt = startedAt
                  ExactMs = exactMs
                  ExactMinutes = Math.Round(exactMs / 60000.0, 4)
                  Discarded = exactMs < 30000.0 }

type DailyAttestation =
    { AttestationId: string
      Date: DateOnly
      Statement: string
      AttestedSequence: int64
      AttestedAt: DateTimeOffset }

/// Flat, unbounded activity list — unlike the sibling's per-week WeekDocument,
/// this app has no week-scoping, matching worker/src/store.js's single global
/// event log.
type LedgerDocument =
    { SchemaVersion: int
      Activities: Activity list
      Attestations: DailyAttestation list
      EventSequence: int64 }

module LedgerDocument =
    let empty = { SchemaVersion = 1; Activities = []; Attestations = []; EventSequence = 0L }
