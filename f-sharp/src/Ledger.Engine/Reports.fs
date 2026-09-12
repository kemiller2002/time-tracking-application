namespace Ledger.Engine

open System
open System.Text.Json.Nodes
open Ledger.Domain

/// Report rendering (markdown/csv/json string-building) lives here, not in the
/// browser bridge: it is pure formatting over an already-computed summary, but
/// formatting is still application meaning ("no business logic outside the
/// bridge") — the bridge only ever receives a finished string to display.
module Reports =

    let private csvField (value: string) =
        if value |> Seq.exists (fun c -> c = ',' || c = '"' || c = '\n') then
            "\"" + value.Replace("\"", "\"\"") + "\""
        else
            value

    let private csvRow (a: Activity) =
        [ a.ActivityId; a.ActivityTypeId; a.ProjectId; a.Description; a.BusinessPurpose
          a.StartedAt.ToString "O"; a.EndedAt.ToString "O"; string (Activity.exactDurationMs a); string a.Voided ]
        |> List.map csvField
        |> String.concat ","

    /// Matches `worker/src/handler.js`'s `toCsv`/`REPORT_COLUMNS` exactly.
    let private csvOf (activities: Activity list) =
        let header = "activity_id,activity_type_id,project_id,description,business_purpose,started_at,ended_at,exact_duration_ms,voided"
        header :: (activities |> List.map csvRow) |> String.concat "\n"

    /// Matches `worker/src/handler.js`'s `toMarkdown`.
    let private markdownOf (identity: string) (totalMinutes: float) (decimalHours: float) (activities: Activity list) =
        let rows =
            activities
            |> List.map (fun a ->
                sprintf "| %s | %s | %s | %d |" a.ActivityTypeId a.ProjectId (a.Description.Replace("|", "\\|"))
                    (int (Math.Round(Activity.exactDurationMs a / 60000.0))))
        [ sprintf "# Report for %s" identity
          ""
          sprintf "Total: %s minutes (%s hours)" (string totalMinutes) (string decimalHours)
          ""
          "| Activity type | Project | Description | Duration (min) |"
          "| --- | --- | --- | --- |" ]
        @ rows
        |> String.concat "\n"

    let private activityJson (a: Activity) : JsonNode =
        let o = JsonObject()
        o.["activity_id"] <- JsonValue.Create(a.ActivityId)
        o.["activity_type_id"] <- JsonValue.Create(a.ActivityTypeId)
        o.["project_id"] <- JsonValue.Create(a.ProjectId)
        o.["description"] <- JsonValue.Create(a.Description)
        o.["business_purpose"] <- JsonValue.Create(a.BusinessPurpose)
        o.["started_at"] <- JsonValue.Create(a.StartedAt.ToString "O")
        o.["ended_at"] <- JsonValue.Create(a.EndedAt.ToString "O")
        o.["exact_duration_ms"] <- JsonValue.Create(Activity.exactDurationMs a)
        o.["billed_minutes"] <- JsonValue.Create(Billing.billedMinutesFor a)
        o.["voided"] <- JsonValue.Create(a.Voided)
        o :> JsonNode

    let private breakdownJson (items: ActivityBreakdown list) =
        let array = JsonArray()
        for item in items do
            let o = JsonObject()
            o.["key"] <- JsonValue.Create(item.Key)
            o.["exact_ms"] <- JsonValue.Create(item.ExactMs)
            array.Add(o)
        array :> JsonNode

    let private figuresJson (o: JsonObject) totalExactMs totalBilledMinutes decimalHours byActivityType byProject manualCount correctionCount voidCount evidenceCoverage =
        o.["total_exact_ms"] <- JsonValue.Create(totalExactMs: float)
        o.["total_billed_minutes"] <- JsonValue.Create(totalBilledMinutes: float)
        o.["decimal_hours"] <- JsonValue.Create(decimalHours: float)
        o.["by_activity_type"] <- breakdownJson byActivityType
        o.["by_project"] <- breakdownJson byProject
        o.["manual_count"] <- JsonValue.Create(manualCount: int)
        o.["correction_count"] <- JsonValue.Create(correctionCount: int)
        o.["void_count"] <- JsonValue.Create(voidCount: int)
        o.["evidence_coverage"] <- JsonValue.Create(evidenceCoverage: float)

    let private jsonOfDay (summary: DaySummary) =
        let o = JsonObject()
        o.["date"] <- JsonValue.Create(summary.Date.ToString "yyyy-MM-dd")
        o.["projection_version"] <- JsonValue.Create(summary.ProjectionVersion)
        let activities = JsonArray()
        for a in summary.IncludedActivities do
            activities.Add(activityJson a)
        o.["activities"] <- activities
        figuresJson o summary.TotalExactMs summary.TotalBilledMinutes summary.DecimalHours summary.ByActivityType summary.ByProject
            summary.ManualCount summary.CorrectionCount summary.VoidCount summary.EvidenceCoverage
        o.ToJsonString()

    let private jsonOfMonth (summary: MonthSummary) =
        let o = JsonObject()
        o.["month"] <- JsonValue.Create(summary.Month)
        o.["projection_version"] <- JsonValue.Create(summary.ProjectionVersion)
        let activities = JsonArray()
        for a in summary.IncludedActivities do
            activities.Add(activityJson a)
        o.["activities"] <- activities
        figuresJson o summary.TotalExactMs summary.TotalBilledMinutes summary.DecimalHours summary.ByActivityType summary.ByProject
            summary.ManualCount summary.CorrectionCount summary.VoidCount summary.EvidenceCoverage
        o.ToJsonString()

    let renderDay (format: string) (summary: DaySummary) : string =
        match format with
        | "markdown" -> markdownOf (summary.Date.ToString "yyyy-MM-dd") summary.TotalBilledMinutes summary.DecimalHours summary.IncludedActivities
        | "csv" -> csvOf summary.IncludedActivities
        | _ -> jsonOfDay summary

    let renderMonth (format: string) (summary: MonthSummary) : string =
        match format with
        | "markdown" -> markdownOf summary.Month summary.TotalBilledMinutes summary.DecimalHours summary.IncludedActivities
        | "csv" -> csvOf summary.IncludedActivities
        | _ -> jsonOfMonth summary
