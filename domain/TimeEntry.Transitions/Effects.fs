/// Tier 2 — State Transition. Requested effects and transition outcomes.
///
/// TE-R-093: a transition never performs an external effect. It returns the
/// effects it *wants* as data, and Tier 4 executes them. That is what makes
/// every transition testable with no browser and no network call
/// (execution rule §16).
module TimeEntry.Transitions.Effects

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Semantic.Catalogue

/// A write the domain wants performed, paired with the version it expects to
/// be replacing. The interpreter must pass `ExpectedVersion` through to the
/// backend as a precondition, not as a hint (TE-R-072).
type PersistRequest =
    { Entry: TimeEntry
      /// `None` for a first write — the entry must not already exist.
      ExpectedVersion: VersionToken option }

/// Effects the domain may request. Note what is absent: no URL, no HTTP verb,
/// no commit message, no file path. Those are Tier 4 concerns, and keeping
/// them out of this type is the anti-corruption boundary (TE-R-094).
type Effect =
    | LoadEntries of forDate: EntryDate
    | LoadProjects
    | PersistNewEntry of PersistRequest
    | PersistCorrection of PersistRequest
    | PersistVoid of PersistRequest
    | PersistRestore of PersistRequest
    | PersistEvidenceAttachment of PersistRequest
    /// A split writes the superseded source and every child. Modelled as one
    /// effect rather than N so the interpreter can make the group as atomic as
    /// the backend permits, and so a partial write is a single reportable
    /// outcome rather than several (TE-R-035).
    | PersistSplit of source: PersistRequest * children: PersistRequest list
    /// A merge writes the new entry and every superseded source. One effect
    /// for the same reason as `PersistSplit`: the group must be as atomic as
    /// the backend permits, and a partial write must be one reportable
    /// outcome (TE-R-035).
    | PersistMerge of target: PersistRequest * sources: PersistRequest list

/// Why a transition was refused. Every case is a typed domain rejection — no
/// stringly-typed errors, so the UI can respond to a conflict differently from
/// a validation failure (execution rule §9).
type Rejection =
    /// The action is not legal in the entry's current state. Carries the
    /// capability that was attempted and the reason it is unavailable.
    | NotPermittedInState of attempted: EntryCapability * denial: CapabilityDenial
    /// TE-R-070/TE-R-072: the caller's expected version is not the current
    /// version. The transition writes nothing and reports both, so the UI can
    /// offer Review / Apply again / Discard mine (TE-R-071).
    | VersionConflict of expected: VersionToken * actual: VersionToken option
    /// TE-R-040: `sum(children) <> source`.
    | SplitDoesNotPreserveTotal of sourceMilliseconds: int64 * childMilliseconds: int64
    /// TE-R-041/TE-R-042 are enforced by `Duration`'s constructor, so a
    /// non-positive child cannot reach a transition. This case covers the
    /// remaining structural failure: too few parts to be a split.
    | SplitNeedsAtLeastTwoChildren of supplied: int
    /// Two children, or a child and the source, were given the same identity.
    | SplitChildIdentityNotUnique of duplicated: EntryId
    /// TE-R-045: a split may REASSIGN the source's evidence to its children.
    /// Reassignment moves what exists; it does not create. A child claiming
    /// evidence the source never held would be attaching new evidence under
    /// the name of a move, and doing so without the revision an attachment
    /// would have produced (TE-R-033).
    | EvidenceNotOnSource of uri: string
    /// The entry the command names was not supplied to the transition.
    | EntryNotLoaded of EntryId
    /// A merge needs something to merge.
    | MergeNeedsAtLeastTwoSources of supplied: int
    /// Two sources, or a source and the new entry, share an identity.
    | MergeSourceIdentityNotUnique of duplicated: EntryId
    /// The sources fall on different ledger days. Refused rather than
    /// silently resolved: the ledger is day-oriented (daily totals, daily
    /// attestation), so merging across days would move time between two days
    /// and change both days' totals. System-prompt 8.11 describes merging
    /// "adjacent or related activities", which does not authorise that.
    | MergeSpansMultipleDays of days: int
    /// The sources' durations sum to something `Duration` cannot represent.
    /// Reachable in principle by merging enough long entries; surfaced rather
    /// than clamped, because clamping would lose recorded time (TE-R-001).
    | MergeDurationOutOfRange of totalMilliseconds: int64
    /// DF-TE-0007: the command names a project or activity type that is
    /// unknown or archived. Checked on the *command*, never on stored state —
    /// existing entries against an archived project keep counting.
    | CatalogueRejected of CatalogueError
    /// A transition whose semantics no repository requirement defines.
    | TransitionUndefined of questionId: string

/// The result of requesting a transition: either a new authoritative state
/// plus the effects it requires, or a typed rejection. Never both, and never
/// a partially applied state.
type Outcome =
    | Accepted of entries: TimeEntry list * effects: Effect list
    | Rejected of Rejection
