/// Tier 3 — Application / Projection. Deterministic view projection.
///
/// Produces a flat `ViewState`, not DOM operations — the shape the
/// TypeScript WASM kernel expects (`@echelon-foundry/typescript-wasm-kernel`
/// README: "a flat ViewState, not DOM operations"). The browser renders this;
/// it does not compute it (TE-R-080, TE-R-091).
///
/// Every function here is deterministic for the same state and query
/// (TE-R-081) and performs no effects.
module TimeEntry.Projection.Projection

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Projection.Query

/// A domain-visible marker on a list row. Named for what it means, not how it
/// looks — the recovered design renders these as `badge-good` / `badge-warn` /
/// `badge-change` (static-ui-screens/today.html), but choosing the CSS class
/// is the browser's job.
type EntryBadge =
    | TimedEntry
    | ManualEntry
    | CorrectedBadge of times: int
    | EvidenceCount of count: int
    | PurposeMissing
    | VoidedBadge
    | SupersededBadge

/// One row of a list view. Contains no markup and no CSS class.
type EntryView =
    { Id: EntryId
      Project: ProjectId
      ActivityType: ActivityTypeId
      Date: EntryDate
      Description: string option
      /// Exact milliseconds — the authoritative quantity (DF-TE-0009), so a
      /// caller that needs precision has it.
      DurationMilliseconds: int64
      /// Billable units under the projection's rounding policy.
      BillableUnits: int
      /// TE-R-085: presentation-boundary hours/minutes, computed here so the
      /// browser never does time arithmetic.
      DisplayHours: int
      DisplayMinutes: int
      CountsTowardTotals: bool
      /// The evidence this entry holds.
      ///
      /// The badge reports the COUNT, which is all a list row needs. The
      /// items themselves are carried because a split has to offer them for
      /// reassignment, and the transition requires an exact match on the way
      /// back (TE-R-045) — so the caller must be able to hand back precisely
      /// what it was given.
      Evidence: EvidenceRef list
      Badges: EntryBadge list
      /// TE-R-097: what the user may do, decided here.
      Capabilities: EntryCapability list
      Obligations: Obligation list }

/// A complete list view with its totals.
type ListProjection =
    { Entries: EntryView list
      /// Exact integer sum of milliseconds over counting entries (TE-R-007).
      TotalMilliseconds: int64
      TotalBillableUnits: int
      TotalDisplayHours: int
      TotalDisplayMinutes: int
      CountedEntries: int
      /// How many entries matched the filter but are excluded from totals, so
      /// the UI can disclose them rather than hide them.
      ExcludedEntries: int
      /// Union of obligations across the projected entries, for the review
      /// screen (TE-R-083, static-ui-screens/review.html).
      OpenObligations: Obligation list
      /// True when nothing matched — drives the empty states (TE-R-083).
      IsEmpty: bool }

module Projection =

    let private matchesText (text: string) (entry: TimeEntry) =
        match entry.Effective.Description with
        | None -> false
        | Some description ->
            (Description.value description)
                .IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0

    let private matchesVisibility (visibility: NonCountingVisibility) (entry: TimeEntry) =
        match visibility, entry.State with
        | IncludeAll, _ -> true
        | CountingOnly, state -> EntryState.countsTowardTotals state
        | IncludeVoided, Void _ -> true
        | IncludeVoided, state -> EntryState.countsTowardTotals state

    let private matches (query: EntryQuery) (entry: TimeEntry) =
        let projectOk =
            match query.Project with
            | None -> true
            | Some p -> entry.Effective.Project = p

        let activityOk =
            match query.ActivityType with
            | None -> true
            | Some a -> entry.Effective.ActivityType = a

        let dateOk =
            match query.DateRange with
            | None -> true
            | Some range -> DateRange.contains entry.Effective.Date range

        // Whitespace-only search text is treated as no filter, so an empty
        // search box does not blank the list.
        let textOk =
            match query.Text with
            | None -> true
            | Some raw when System.String.IsNullOrWhiteSpace raw -> true
            | Some raw -> matchesText (raw.Trim()) entry

        projectOk && activityOk && dateOk && textOk

    /// Sorting is total and tie-broken by entry id, so the order is fully
    /// determined by (state, query) and never depends on input order
    /// (TE-R-081).
    let private sortBy (sort: EntrySort) (entries: TimeEntry list) =
        let byId (e: TimeEntry) = EntryId.value e.Id
        let day (e: TimeEntry) = EntryDate.dayNumber e.Effective.Date
        let ms (e: TimeEntry) = Duration.milliseconds e.Effective.Duration

        match sort with
        | ChronologicalAscending -> entries |> List.sortBy (fun e -> day e, byId e)
        | ChronologicalDescending -> entries |> List.sortBy (fun e -> -(day e), byId e)
        | LongestFirst -> entries |> List.sortBy (fun e -> -(ms e), byId e)
        | ShortestFirst -> entries |> List.sortBy (fun e -> ms e, byId e)

    let private badgesFor (entry: TimeEntry) =
        [ match entry.Effective.Origin with
          | Timed -> TimedEntry
          | Manual _ -> ManualEntry

          let corrections = TimeEntry.correctionCount entry

          if corrections > 0 then
              CorrectedBadge corrections

          let evidence = List.length entry.Effective.Evidence

          if evidence > 0 then
              EvidenceCount evidence

          if entry.Effective.Description.IsNone then
              PurposeMissing

          match entry.State with
          | Void _ -> VoidedBadge
          | Superseded _ -> SupersededBadge
          | Active -> () ]

    /// Project one entry. `extraObligations` carries obligations Tier 3 knows
    /// but Tier 1 cannot derive — unresolved project references, pending
    /// persistence, conflicts.
    let toView (policy: RoundingPolicy) (extraObligations: Obligation list) (entry: TimeEntry) : EntryView =
        let units = BillableUnits.ofDuration policy entry.Effective.Duration
        let hours, minutes = BillableUnits.toHoursAndMinutes units

        { Id = entry.Id
          Project = entry.Effective.Project
          ActivityType = entry.Effective.ActivityType
          Date = entry.Effective.Date
          Description = entry.Effective.Description |> Option.map Description.value
          DurationMilliseconds = Duration.milliseconds entry.Effective.Duration
          BillableUnits = BillableUnits.units units
          DisplayHours = hours
          DisplayMinutes = minutes
          CountsTowardTotals = TimeEntry.countsTowardTotals entry
          Evidence = entry.Effective.Evidence
          Badges = badgesFor entry
          Capabilities = Capabilities.available entry.State
          Obligations = Obligation.intrinsic entry @ extraObligations }

    /// Build a list view.
    ///
    /// Totals are summed over *counting* entries only, in whole milliseconds, and
    /// projected to units once at the end. Summing per-entry rounded units
    /// instead would drift — ten 3-minute entries are 30 minutes (5 units),
    /// not ten rounded-up units.
    let project
        (policy: RoundingPolicy)
        (obligationsFor: TimeEntry -> Obligation list)
        (query: EntryQuery)
        (entries: TimeEntry list)
        : ListProjection =

        // Selection and visibility are two separate questions, and keeping
        // them separate is what lets a day view DISCLOSE a removed entry
        // instead of hiding it (TE-R-030). `selected` is everything the query
        // asked for; `matched` is the subset the caller chose to show.
        //
        // Folding visibility into the filter — as this did — made
        // `ExcludedEntries` always zero under `CountingOnly`, so the default
        // day view could not tell the user that an entry existed and was
        // excluded. That silently contradicted this type's own documented
        // contract.
        let selected = entries |> List.filter (matches query)
        let matched = selected |> List.filter (matchesVisibility query.Visibility) |> sortBy query.Sort
        let views = matched |> List.map (fun e -> toView policy (obligationsFor e) e)
        let counting = selected |> List.filter TimeEntry.countsTowardTotals

        let totalMilliseconds =
            counting |> List.sumBy TimeEntry.contributedMilliseconds

        let totalUnits, totalHours, totalMinutes =
            match Duration.ofMilliseconds totalMilliseconds with
            | Ok duration ->
                let units = BillableUnits.ofDuration policy duration
                let h, m = BillableUnits.toHoursAndMinutes units
                BillableUnits.units units, h, m
            // A zero total is not a valid `Duration`, which is correct: zero
            // elapsed time is not a recordable span. It is still a valid
            // *total*, so it is handled here rather than by weakening
            // `Duration`.
            | Error _ -> 0, 0, 0

        { Entries = views
          TotalMilliseconds = totalMilliseconds
          TotalBillableUnits = totalUnits
          TotalDisplayHours = totalHours
          TotalDisplayMinutes = totalMinutes
          CountedEntries = List.length counting
          ExcludedEntries = List.length selected - List.length counting
          OpenObligations = views |> List.collect (fun v -> v.Obligations) |> List.distinct
          IsEmpty = List.isEmpty matched }

// ---------------------------------------------------------------------------
// Period summary
// ---------------------------------------------------------------------------

/// One day's recorded time.
type DayTotal =
    { Date: EntryDate
      TotalMilliseconds: int64
      BillableUnits: int
      DisplayHours: int
      DisplayMinutes: int
      CountedEntries: int }

/// What a range of days comes to.
///
/// Every figure here is derived from the same entries the list projection
/// reads, so a month summary and the days inside it cannot disagree.
type PeriodSummary =
    { TotalMilliseconds: int64
      TotalBillableUnits: int
      DisplayHours: int
      DisplayMinutes: int
      /// Decimal hours, as whole hours and tenths — NOT a float.
      ///
      /// A billable unit is six minutes, which is exactly one tenth of an
      /// hour, so decimal hours are exact in tenths and need no rounding at
      /// all: `units / 10` and `units % 10`. Computing this as a float would
      /// introduce error into a figure that has none, and TE-R-007 forbids
      /// floating point as authoritative for billable time.
      DecimalHoursWhole: int
      DecimalHoursTenths: int
      /// Days on which any counting time was recorded.
      ActiveDays: int
      /// One per active day, chronological.
      Days: DayTotal list
      /// Exact time captured by a running timer versus entered by hand
      /// (system-prompt §8.4). Both are counting time; the split is reported
      /// because a ledger meant for review should be able to say how much of
      /// itself was reconstructed.
      TimedMilliseconds: int64
      ManualMilliseconds: int64
      /// Counting entries carrying at least one piece of evidence, and the
      /// total they are drawn from, so a caller can present a proportion
      /// without this deciding how (TE-R-085).
      EntriesWithEvidence: int
      CountedEntries: int
      /// Entries corrected at least once, and entries currently excluded
      /// because they were removed. Both are history facts the review screen
      /// asks for (system-prompt §8.14).
      CorrectedEntries: int
      RemovedEntries: int }

module PeriodSummary =

    let private dayOf (policy: RoundingPolicy) (date: EntryDate) (entries: TimeEntry list) =
        let total = entries |> List.sumBy TimeEntry.contributedMilliseconds

        let units, hours, minutes =
            match Duration.ofMilliseconds total with
            | Ok duration ->
                let projected = BillableUnits.ofDuration policy duration
                let h, m = BillableUnits.toHoursAndMinutes projected
                BillableUnits.units projected, h, m
            | Error _ -> 0, 0, 0

        { Date = date
          TotalMilliseconds = total
          BillableUnits = units
          DisplayHours = hours
          DisplayMinutes = minutes
          CountedEntries = List.length entries }

    /// Summarise every entry in a range.
    ///
    /// Takes the entries already selected by a query rather than a query of
    /// its own, so the summary and the list a caller shows beside it are
    /// computed from exactly the same set — the alternative is two filters
    /// that drift.
    let ofEntries (policy: RoundingPolicy) (selected: TimeEntry list) : PeriodSummary =
        let counting = selected |> List.filter TimeEntry.countsTowardTotals
        let total = counting |> List.sumBy TimeEntry.contributedMilliseconds

        let units, hours, minutes =
            match Duration.ofMilliseconds total with
            | Ok duration ->
                let projected = BillableUnits.ofDuration policy duration
                let h, m = BillableUnits.toHoursAndMinutes projected
                BillableUnits.units projected, h, m
            | Error _ -> 0, 0, 0

        let days =
            counting
            |> List.groupBy (fun entry -> entry.Effective.Date)
            |> List.sortWith (fun (a, _) (b, _) -> EntryDate.compare a b)
            |> List.map (fun (date, entries) -> dayOf policy date entries)

        let byOrigin isManual =
            counting
            |> List.filter (fun entry ->
                match entry.Effective.Origin with
                | Manual _ -> isManual
                | Timed -> not isManual)
            |> List.sumBy TimeEntry.contributedMilliseconds

        { TotalMilliseconds = total
          TotalBillableUnits = units
          DisplayHours = hours
          DisplayMinutes = minutes
          DecimalHoursWhole = units / BillableUnitsPerHour
          DecimalHoursTenths = units % BillableUnitsPerHour
          ActiveDays = List.length days
          Days = days
          TimedMilliseconds = byOrigin false
          ManualMilliseconds = byOrigin true
          EntriesWithEvidence =
            counting
            |> List.filter (fun entry -> not entry.Effective.Evidence.IsEmpty)
            |> List.length
          CountedEntries = List.length counting
          CorrectedEntries =
            selected
            |> List.filter (fun entry -> TimeEntry.correctionCount entry > 0)
            |> List.length
          RemovedEntries =
            selected
            |> List.filter (fun entry ->
                match entry.State with
                | Void _ -> true
                | _ -> false)
            |> List.length }

// ---------------------------------------------------------------------------
// Progress against a tracking target
// ---------------------------------------------------------------------------

/// How a period's recorded total compares to the target set for it.
///
/// A separate projection rather than fields on `PeriodSummary`, because the
/// two answer different questions from different authorities: a summary is
/// derived entirely from the ledger, while a target is a preference somebody
/// set. Folding the target in would mean every caller of `PeriodSummary` had
/// to supply one, and a month with no target set would have to be represented
/// as a target of zero — which is a target, and a false one.
///
/// The caller therefore holds `TrackingTarget option` and builds this only
/// when there is something to build it from (DF-TE-0015).
type TargetProgress =
    { TargetUnits: int
      TargetDisplayHours: int
      TargetDisplayMinutes: int
      /// Whole per-cent, truncated, and NOT capped at 100. Exceeding a target
      /// is a fact worth reporting; a caller drawing a bar clamps its own
      /// width. Truncated rather than rounded so the figure never claims more
      /// progress than was recorded — 79.9 hours against 80 reads 99%.
      PercentRecorded: int
      /// Never negative: "minus four hours remaining" is not a remainder.
      RemainingUnits: int
      RemainingDisplayHours: int
      RemainingDisplayMinutes: int
      Reached: bool }

module TargetProgress =

    /// Integer arithmetic throughout (TE-R-007). Both sides are already in
    /// six-minute units, so the comparison needs no conversion and introduces
    /// no rounding of its own.
    let against (target: TrackingTarget) (summary: PeriodSummary) : TargetProgress =
        let targetUnits = TrackingTarget.units target
        let recorded = summary.TotalBillableUnits
        let remaining = max 0 (targetUnits - recorded)
        let targetHours, targetMinutes = TrackingTarget.toHoursAndMinutes target

        { TargetUnits = targetUnits
          TargetDisplayHours = targetHours
          TargetDisplayMinutes = targetMinutes
          PercentRecorded = recorded * 100 / targetUnits
          RemainingUnits = remaining
          RemainingDisplayHours = remaining * MinutesPerBillableUnit / 60
          RemainingDisplayMinutes = remaining * MinutesPerBillableUnit % 60
          Reached = remaining = 0 }
