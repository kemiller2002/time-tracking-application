namespace Ledger.Domain

open System

type ActivityBreakdown = { Key: string; ExactMs: float }

type DaySummary =
    { Date: DateOnly
      ProjectionVersion: string
      IncludedActivities: Activity list
      AllActivitiesForDate: Activity list
      TotalExactMs: float
      TotalBilledMinutes: float
      DecimalHours: float
      ByActivityType: ActivityBreakdown list
      ByProject: ActivityBreakdown list
      ManualCount: int
      CorrectionCount: int
      VoidCount: int
      EvidenceCoverage: float }

type MonthSummary =
    { Month: string
      ProjectionVersion: string
      IncludedActivities: Activity list
      AllActivities: Activity list
      TotalExactMs: float
      TotalBilledMinutes: float
      DecimalHours: float
      ByActivityType: ActivityBreakdown list
      ByProject: ActivityBreakdown list
      ManualCount: int
      CorrectionCount: int
      VoidCount: int
      EvidenceCoverage: float }

/// Day/month summaries are filters over `LedgerDocument.Activities` — a flat,
/// unbounded list — not a whole-document total (mirrors `worker/src/store.js`'s
/// `#summary`, applied separately by `day()`/`month()`).
module Summary =
    let private round4 (x: float) = Math.Round(x, 4)

    let private breakdown (keyOf: Activity -> string) (activities: Activity list) =
        activities
        |> List.groupBy keyOf
        |> List.map (fun (key, items) -> { Key = key; ExactMs = items |> List.sumBy Activity.exactDurationMs })

    let private projectionVersion (document: LedgerDocument) = sprintf "v%d" document.EventSequence

    let private included (activities: Activity list) =
        activities |> List.filter (fun a -> not a.Voided) |> List.sortBy (fun a -> a.StartedAt)

    let private manualCount (activities: Activity list) =
        activities |> List.filter (fun a -> a.EntryMethod = Manual) |> List.length

    let private correctionCount (activities: Activity list) =
        activities |> List.filter Activity.hasAmendment |> List.length

    let private voidCount (allForPeriod: Activity list) =
        allForPeriod |> List.filter (fun a -> a.Voided) |> List.length

    let private evidenceCoverage (activities: Activity list) =
        if activities.IsEmpty then 0.0
        else
            float (activities |> List.filter (fun a -> not a.Evidence.IsEmpty) |> List.length)
            / float activities.Length

    let forDate (document: LedgerDocument) (date: DateOnly) : DaySummary =
        let allForDate = document.Activities |> List.filter (fun a -> DateKey.ofInstant a.StartedAt = date)
        let recorded = included allForDate
        let totalExactMs = recorded |> List.sumBy Activity.exactDurationMs
        { Date = date
          ProjectionVersion = projectionVersion document
          IncludedActivities = recorded
          AllActivitiesForDate = allForDate
          TotalExactMs = totalExactMs
          TotalBilledMinutes = recorded |> List.sumBy Billing.billedMinutesFor
          DecimalHours = round4 (totalExactMs / 3600000.0)
          ByActivityType = breakdown (fun a -> a.ActivityTypeId) recorded
          ByProject = breakdown (fun a -> a.ProjectId) recorded
          ManualCount = manualCount recorded
          CorrectionCount = correctionCount recorded
          VoidCount = voidCount allForDate
          EvidenceCoverage = evidenceCoverage recorded }

    let forMonth (document: LedgerDocument) (month: string) : MonthSummary =
        let allForMonth = document.Activities |> List.filter (fun a -> DateKey.monthOf a.StartedAt = month)
        let recorded = included allForMonth
        let totalExactMs = recorded |> List.sumBy Activity.exactDurationMs
        { Month = month
          ProjectionVersion = projectionVersion document
          IncludedActivities = recorded
          AllActivities = allForMonth
          TotalExactMs = totalExactMs
          TotalBilledMinutes = recorded |> List.sumBy Billing.billedMinutesFor
          DecimalHours = round4 (totalExactMs / 3600000.0)
          ByActivityType = breakdown (fun a -> a.ActivityTypeId) recorded
          ByProject = breakdown (fun a -> a.ProjectId) recorded
          ManualCount = manualCount recorded
          CorrectionCount = correctionCount recorded
          VoidCount = voidCount allForMonth
          EvidenceCoverage = evidenceCoverage recorded }

    let latestAttestation (document: LedgerDocument) (date: DateOnly) =
        document.Attestations
        |> List.filter (fun a -> a.Date = date)
        |> List.sortBy (fun a -> a.AttestedSequence)
        |> List.tryLast

    /// `amended_after_review` warnings: an included activity whose UpdatedAt is
    /// after the latest attestation's AttestedAt for that date.
    let reviewWarnings (document: LedgerDocument) (date: DateOnly) (attestation: DailyAttestation option) =
        match attestation with
        | None -> []
        | Some attested ->
            (forDate document date).IncludedActivities
            |> List.filter (fun a -> a.UpdatedAt > attested.AttestedAt)
            |> List.map (fun a -> a.ActivityId, "amended_after_review")
