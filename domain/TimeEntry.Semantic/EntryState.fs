/// Tier 1 — Semantic Model. Entry state and history.
module TimeEntry.Semantic.EntryState

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values

/// The effective values of an entry — what it currently says happened.
///
/// `Duration` is the authoritative time quantity (DF-TE-0002). There is no
/// `BillableUnits` field: units are projected on demand, so no code path can
/// let a stored unit count drift from the exact duration.
///
/// `Description` is `option` because OQ-3 (is a description mandatory?) is
/// unresolved. `Labels` is absent entirely because OQ-2 is unresolved — no
/// repository document defines a `label` entity distinct from activity type
/// and project, and inventing one is forbidden.
type EntryFacts =
    { Project: ProjectId
      ActivityType: ActivityTypeId
      Date: EntryDate
      Duration: Duration
      Description: Description option
      Origin: EntryOrigin
      Evidence: EvidenceRef list }

/// Why a revision exists. Every state change appends one; none is ever
/// rewritten (TE-R-030..TE-R-032).
///
/// A correction is a `RevisionChange`, NOT an `EntryState` case. This is the
/// main place the requirement analysis diverged from the shape the execution
/// script offered: a corrected entry still counts toward totals with its new
/// values, so "Corrected" is not a state an entry rests in — it is an event in
/// the entry's history (TE-R-050 "the original entry will remain in history;
/// this change creates a correction record"). Modelling it as a state would
/// have made `Corrected` and `Active` behave identically for totals and
/// capabilities, which is the duplication SDE warns against.
type RevisionChange =
    | Created
    | Corrected of reason: Reason
    | Voided of reason: Reason
    | Restored of reason: Reason
    /// This entry was split; it is superseded by its children.
    | SplitInto of children: EntryId list
    /// This entry was merged away into another entry.
    | MergedInto of target: EntryId
    /// This entry was produced by splitting `source`.
    | CreatedBySplitOf of source: EntryId
    /// This entry was produced by merging `sources`.
    | CreatedByMergeOf of sources: EntryId list
    | EvidenceAttached of evidence: EvidenceRef
    | EvidenceReassignedFrom of source: EntryId

/// One immutable point in an entry's history. Supplies everything the history
/// view must show (TE-R-052).
type Revision =
    { Id: RevisionId
      Change: RevisionChange
      /// The effective facts *as of* this revision, so history can show
      /// original values and changed values without recomputation.
      Facts: EntryFacts
      RecordedAt: Instant
      RecordedBy: UserId
      Device: DeviceLabel }

/// Why an entry no longer counts toward totals despite never being deleted.
type SupersessionCause =
    | SupersededBySplit of children: EntryId list
    | SupersededByMerge of target: EntryId

/// The authoritative lifecycle state of an entry.
///
/// Derived from requirement analysis, not adopted from the script's example:
///
/// - `Active` — counts toward totals; the normal state, including after a
///   correction and after a restore.
/// - `Void` — excluded from totals, record retained, restorable
///   (TE-R-060, TE-R-063, TE-R-025).
/// - `Superseded` — excluded from totals because its time now lives in other
///   entries. Without this state a split would double-count: the source and
///   its children would both be `Active` (TE-R-040).
///
/// Operations legal against one case are deliberately not legal against
/// another; see `Capabilities` (TE-R-096, TE-R-097).
type EntryState =
    | Active
    | Void of reason: Reason * voidedAt: Instant
    | Superseded of cause: SupersessionCause

/// An entry: identity, current state, effective facts, and complete history.
///
/// `History` is newest-first and is never empty — an entry always has at least
/// its `Created` revision. `Version` is external concurrency evidence and is
/// `None` until the entry has been persisted at least once (TE-R-073).
type TimeEntry =
    { Id: EntryId
      State: EntryState
      Effective: EntryFacts
      History: Revision list
      Version: VersionToken option }

module EntryState =

    /// TE-R-060/TE-R-063: only `Active` entries contribute time.
    let countsTowardTotals (state: EntryState) =
        match state with
        | Active -> true
        | Void _ -> false
        | Superseded _ -> false

module TimeEntry =

    let countsTowardTotals (entry: TimeEntry) = EntryState.countsTowardTotals entry.State

    /// The duration an entry contributes to a total: zero when it does not
    /// count. Returning seconds rather than `Duration` is deliberate —
    /// `Duration` is constrained to be positive, and a non-counting entry
    /// contributes exactly zero, which is not a valid `Duration`.
    let contributedSeconds (entry: TimeEntry) =
        if countsTowardTotals entry then
            Duration.seconds entry.Effective.Duration
        else
            0

    /// The revision that created the entry (TE-R-052 "original values").
    let originalRevision (entry: TimeEntry) = entry.History |> List.tryLast

    let latestRevision (entry: TimeEntry) = entry.History |> List.tryHead

    /// Whether this entry has ever been corrected — drives the "Corrected
    /// once" badge present in the recovered screen design
    /// (static-ui-screens/today.html).
    let correctionCount (entry: TimeEntry) =
        entry.History
        |> List.filter (fun r ->
            match r.Change with
            | Corrected _ -> true
            | _ -> false)
        |> List.length

    /// Append a revision. The only sanctioned way history grows: it pushes
    /// onto the front and never mutates or drops an existing revision, so
    /// "history cannot silently disappear" is a structural property rather
    /// than something each transition must remember (TE-R-030..TE-R-032).
    let appendRevision (revision: Revision) (state: EntryState) (facts: EntryFacts) (entry: TimeEntry) =
        { entry with
            State = state
            Effective = facts
            History = revision :: entry.History }
