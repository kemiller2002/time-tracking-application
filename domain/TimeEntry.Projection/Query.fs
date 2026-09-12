/// Tier 3 — Application / Projection. The query language for list views.
///
/// TE-R-080: filtering, sorting, searching, and totals are owned by F#. This
/// type is the whole vocabulary the browser may use to ask for a view; it
/// cannot express "and also sort it yourself".
module TimeEntry.Projection.Query

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values

type EntrySort =
    | ChronologicalAscending
    | ChronologicalDescending
    | LongestFirst
    | ShortestFirst

/// An inclusive range of ledger days.
type DateRange = { From: EntryDate; To: EntryDate }

module DateRange =
    let contains (date: EntryDate) (range: DateRange) =
        EntryDate.compare date range.From >= 0 && EntryDate.compare date range.To <= 0

    let single (date: EntryDate) = { From = date; To = date }

/// Whether entries excluded from totals are shown.
///
/// Void and superseded entries are never silently dropped — the ledger is
/// meant to be transparent about history (TE-R-030, exec-contract §4) — so the
/// caller states what it wants and the default view still reveals that they
/// exist via `ExcludedCount`.
type NonCountingVisibility =
    | CountingOnly
    | IncludeVoided
    | IncludeAll

type EntryQuery =
    { Project: ProjectId option
      ActivityType: ActivityTypeId option
      /// Free-text search over description. `None` means no text filter;
      /// whitespace-only text is treated as `None` by the projection so an
      /// empty search box does not hide every entry.
      Text: string option
      DateRange: DateRange option
      Sort: EntrySort
      Visibility: NonCountingVisibility }

module EntryQuery =

    /// The default day view: everything that counts, newest first.
    let forDay (date: EntryDate) =
        { Project = None
          ActivityType = None
          Text = None
          DateRange = Some(DateRange.single date)
          Sort = ChronologicalDescending
          Visibility = CountingOnly }
