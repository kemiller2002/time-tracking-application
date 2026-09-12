namespace Ledger.Engine

open System
open Ledger.Domain
open Ledger.Engine.Protocol

/// Builds a flat view-model (never a raw domain graph, never `VItems` nested
/// inside `VItems`) from session state. The billed (six-minute-rounded)
/// figure is always shown alongside the exact one, never in its place.
module Projections =

    let private lookupName (directory: Map<string, ReferenceItem>) (id: string) =
        directory |> Map.tryFind id |> Option.map (fun item -> item.Name) |> Option.defaultValue id

    let private formatInstant (dt: DateTimeOffset) = dt.ToString "HH:mm"
    let private formatMinutes (totalMinutes: float) = sprintf "%.1f min" totalMinutes

    let private statusLabel =
        function
        | ActivityStatus.Recorded -> "Recorded"
        | ActivityStatus.Voided -> "Voided"
        | ActivityStatus.Superseded -> "Superseded"

    let private entryMethodLabel =
        function
        | Manual -> "Manual"
        | EntryMethod.Split -> "Split"
        | EntryMethod.Merge -> "Merge"
        | EntryMethod.Timer -> "Timer"

    let private capabilityFlags (activity: Activity) =
        let capabilities = Capability.forActivity activity
        [ "canAmend", VBool(capabilities.Contains Amend)
          "canSplit", VBool(capabilities.Contains Split)
          "canVoid", VBool(capabilities.Contains Void)
          "canMergeAsSource", VBool(capabilities.Contains MergeAsSource)
          "canLinkEvidence", VBool(capabilities.Contains LinkEvidence)
          "canUnlinkEvidence", VBool(capabilities.Contains UnlinkEvidence)
          "canRestore", VBool(capabilities.Contains Restore) ]

    let private activityItem (environment: Environment) (activity: Activity) : Map<string, ViewValue> =
        let tagLabels = activity.TagIds |> Set.toList |> List.map (lookupName environment.Tags) |> String.concat ", "
        [ "id", VString activity.ActivityId
          "timeLabel", VString(sprintf "%s-%s" (formatInstant activity.StartedAt) (formatInstant activity.EndedAt))
          "activityTypeLabel", VString(lookupName environment.ActivityTypes activity.ActivityTypeId)
          "projectLabel", VString(lookupName environment.Projects activity.ProjectId)
          "description", VString activity.Description
          "businessPurpose", VString activity.BusinessPurpose
          "exactDurationLabel", VString(formatMinutes (Activity.exactMinutes activity))
          "billedLabel", VString(formatMinutes (Billing.billedMinutesFor activity))
          "statusLabel", VString(statusLabel (Activity.status activity))
          "entryMethodLabel", VString(entryMethodLabel activity.EntryMethod)
          "evidenceCount", VNumber(float activity.Evidence.Length)
          "tagLabels", VString tagLabels ]
        @ capabilityFlags activity
        |> Map.ofList

    let private evidenceItem (activity: Activity) (evidence: Evidence) : Map<string, ViewValue> =
        Map.ofList
            [ "activityId", VString activity.ActivityId
              "evidenceLinkId", VString evidence.EvidenceLinkId
              "label", VString evidence.Label
              "uri", VString(evidence.Uri |> Option.defaultValue "") ]

    let private breakdownItems (items: ActivityBreakdown list) =
        items |> List.map (fun b -> Map.ofList [ "key", VString b.Key; "exactMinutesLabel", VString(formatMinutes (b.ExactMs / 60000.0)) ])

    /// Common day/month summary figures, flattened under `prefix` — `ViewValue`
    /// has no object variant, so a summary's fields live at the top level
    /// rather than as a one-row `VItems` (which would misrepresent a single
    /// object as a repeating list to the DOM binding layer).
    let private summaryFields (prefix: string) totalExactMs totalBilledMinutes decimalHours manualCount correctionCount voidCount evidenceCoverage byActivityType byProject =
        [ prefix + "TotalExactLabel", VString(formatMinutes (totalExactMs / 60000.0))
          prefix + "TotalBilledLabel", VString(formatMinutes totalBilledMinutes)
          prefix + "DecimalHoursLabel", VString(sprintf "%.2f h" (decimalHours: float))
          prefix + "ManualCount", VNumber(float (manualCount: int))
          prefix + "CorrectionCount", VNumber(float (correctionCount: int))
          prefix + "VoidCount", VNumber(float (voidCount: int))
          prefix + "EvidenceCoveragePctLabel", VString(sprintf "%.0f%%" ((evidenceCoverage: float) * 100.0))
          prefix + "ByActivityType", VItems(breakdownItems byActivityType)
          prefix + "ByProject", VItems(breakdownItems byProject) ]

    let private timerFields (environment: Environment) (timerState: TimerState) =
        match timerState with
        | NoTimer ->
            [ "timerPhase", VString "none"; "timerElapsedLabel", VString ""
              "timerActivityTypeId", VString ""; "timerProjectId", VString ""; "timerDescription", VString "" ]
        | ActiveTimerState timer ->
            let now = environment.Clock()
            let effectiveEnd = match timer.Phase with TimerPaused -> (List.last timer.Segments).End.Value | TimerRunning -> now
            let startedAt = (List.head timer.Segments).Start
            let elapsedMs = max 0.0 ((effectiveEnd - startedAt).TotalMilliseconds - timer.PausedMs)
            [ "timerPhase", VString(match timer.Phase with TimerRunning -> "running" | TimerPaused -> "paused")
              "timerElapsedLabel", VString(formatMinutes (elapsedMs / 60000.0))
              "timerActivityTypeId", VString timer.ActivityTypeId
              "timerProjectId", VString timer.ProjectId
              "timerDescription", VString timer.Description ]

    let private errorFields (errors: Map<string, string>) =
        [ "create"; "amend"; "void"; "restore"; "split"; "merge"; "evidence"; "timer"; "attest" ]
        |> List.map (fun key -> key + "Error", VString(errors |> Map.tryFind key |> Option.defaultValue ""))

    let build (state: Session.State) : Map<string, ViewValue> =
        let daySummary = Summary.forDate state.Document state.SelectedDate
        let monthSummary = Summary.forMonth state.Document state.SelectedMonth
        let latestAttestation = Summary.latestAttestation state.Document state.SelectedDate
        let warnings = Summary.reviewWarnings state.Document state.SelectedDate latestAttestation
        let attestations = state.Document.Attestations |> List.filter (fun a -> a.Date = state.SelectedDate)
        let activeActivity = state.ActiveActivityId |> Option.bind (fun id -> state.Document.Activities |> List.tryFind (fun a -> a.ActivityId = id))

        [ "selectedDateLabel", VString(state.SelectedDate.ToString "yyyy-MM-dd")
          "selectedMonthLabel", VString state.SelectedMonth
          "dayActivities", VItems(daySummary.IncludedActivities |> List.map (activityItem state.Environment))
          "dayReviewWarnings", VItems(warnings |> List.map (fun (activityId, code) -> Map.ofList [ "activityId", VString activityId; "code", VString code ]))
          "attestationHistory",
          VItems(attestations |> List.map (fun a -> Map.ofList [ "date", VString(a.Date.ToString "yyyy-MM-dd"); "statement", VString a.Statement; "attestedAt", VString(a.AttestedAt.ToString "O") ]))
          "monthActivities", VItems(monthSummary.IncludedActivities |> List.map (activityItem state.Environment))
          "activeActivityId", VString(state.ActiveActivityId |> Option.defaultValue "")
          "activeActivityEvidence", VItems(activeActivity |> Option.map (fun a -> a.Evidence |> List.map (evidenceItem a)) |> Option.defaultValue [])
          "reportFormat", VString state.ReportFormat
          "reportContent", VString(Reports.renderDay state.ReportFormat daySummary)
          "persistenceError", VString(state.PersistenceError |> Option.defaultValue "") ]
        @ summaryFields "day" daySummary.TotalExactMs daySummary.TotalBilledMinutes daySummary.DecimalHours daySummary.ManualCount daySummary.CorrectionCount
            daySummary.VoidCount daySummary.EvidenceCoverage daySummary.ByActivityType daySummary.ByProject
        @ summaryFields "month" monthSummary.TotalExactMs monthSummary.TotalBilledMinutes monthSummary.DecimalHours monthSummary.ManualCount monthSummary.CorrectionCount
            monthSummary.VoidCount monthSummary.EvidenceCoverage monthSummary.ByActivityType monthSummary.ByProject
        @ timerFields state.Environment state.TimerState
        @ errorFields state.Errors
        |> Map.ofList
