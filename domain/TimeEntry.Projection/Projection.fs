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
      /// Exact seconds, so a caller that needs precision has it.
      DurationSeconds: int
      /// Billable units under the projection's rounding policy.
      BillableUnits: int
      /// TE-R-085: presentation-boundary hours/minutes, computed here so the
      /// browser never does time arithmetic.
      DisplayHours: int
      DisplayMinutes: int
      CountsTowardTotals: bool
      Badges: EntryBadge list
      /// TE-R-097: what the user may do, decided here.
      Capabilities: EntryCapability list
      Obligations: Obligation list }

/// A complete list view with its totals.
type ListProjection =
    { Entries: EntryView list
      /// Exact integer sum over counting entries (TE-R-007).
      TotalSeconds: int
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

        projectOk && activityOk && dateOk && textOk && matchesVisibility query.Visibility entry

    /// Sorting is total and tie-broken by entry id, so the order is fully
    /// determined by (state, query) and never depends on input order
    /// (TE-R-081).
    let private sortBy (sort: EntrySort) (entries: TimeEntry list) =
        let byId (e: TimeEntry) = EntryId.value e.Id
        let day (e: TimeEntry) = EntryDate.dayNumber e.Effective.Date
        let seconds (e: TimeEntry) = Duration.seconds e.Effective.Duration

        match sort with
        | ChronologicalAscending -> entries |> List.sortBy (fun e -> day e, byId e)
        | ChronologicalDescending -> entries |> List.sortBy (fun e -> -(day e), byId e)
        | LongestFirst -> entries |> List.sortBy (fun e -> -(seconds e), byId e)
        | ShortestFirst -> entries |> List.sortBy (fun e -> seconds e, byId e)

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
          DurationSeconds = Duration.seconds entry.Effective.Duration
          BillableUnits = BillableUnits.units units
          DisplayHours = hours
          DisplayMinutes = minutes
          CountsTowardTotals = TimeEntry.countsTowardTotals entry
          Badges = badgesFor entry
          Capabilities = Capabilities.available entry.State
          Obligations = Obligation.intrinsic entry @ extraObligations }

    /// Build a list view.
    ///
    /// Totals are summed over *counting* entries only, in whole seconds, and
    /// projected to units once at the end. Summing per-entry rounded units
    /// instead would drift — ten 3-minute entries are 30 minutes (5 units),
    /// not ten rounded-up units.
    let project
        (policy: RoundingPolicy)
        (obligationsFor: TimeEntry -> Obligation list)
        (query: EntryQuery)
        (entries: TimeEntry list)
        : ListProjection =

        let matched = entries |> List.filter (matches query) |> sortBy query.Sort
        let views = matched |> List.map (fun e -> toView policy (obligationsFor e) e)
        let counting = matched |> List.filter TimeEntry.countsTowardTotals
        let totalSeconds = counting |> List.sumBy TimeEntry.contributedSeconds

        let totalUnits, totalHours, totalMinutes =
            match Duration.ofSeconds totalSeconds with
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
          TotalSeconds = totalSeconds
          TotalBillableUnits = totalUnits
          TotalDisplayHours = totalHours
          TotalDisplayMinutes = totalMinutes
          CountedEntries = List.length counting
          ExcludedEntries = List.length matched - List.length counting
          OpenObligations = views |> List.collect (fun v -> v.Obligations) |> List.distinct
          IsEmpty = List.isEmpty matched }
