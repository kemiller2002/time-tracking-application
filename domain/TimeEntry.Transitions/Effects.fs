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
    | SplitDoesNotPreserveTotal of sourceSeconds: int * childSeconds: int
    /// TE-R-041/TE-R-042 are enforced by `Duration`'s constructor, so a
    /// non-positive child cannot reach a transition. This case covers the
    /// remaining structural failure: too few parts to be a split.
    | SplitNeedsAtLeastTwoChildren of supplied: int
    /// Two children, or a child and the source, were given the same identity.
    | SplitChildIdentityNotUnique of duplicated: EntryId
    /// The entry the command names was not supplied to the transition.
    | EntryNotLoaded of EntryId
    /// A transition whose semantics no repository requirement defines.
    | TransitionUndefined of questionId: string

/// The result of requesting a transition: either a new authoritative state
/// plus the effects it requires, or a typed rejection. Never both, and never
/// a partially applied state.
type Outcome =
    | Accepted of entries: TimeEntry list * effects: Effect list
    | Rejected of Rejection
