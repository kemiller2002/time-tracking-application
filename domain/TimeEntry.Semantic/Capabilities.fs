/// Tier 1 — Semantic Model. Capabilities and obligations.
///
/// TE-R-097: capabilities are computed from authoritative state. The UI
/// consumes them and must never independently decide that an action is legal,
/// which is what stops policy being duplicated between F# and the browser.
module TimeEntry.Semantic.Capabilities

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState

/// An action a user may attempt against an entry in its current state.
type EntryCapability =
    | CanCorrect
    | CanSplit
    | CanVoid
    | CanRestore
    | CanMerge
    | CanAttachEvidence

/// Why an otherwise-plausible action is unavailable. Returned alongside the
/// capability set so the UI can explain a disabled control instead of silently
/// omitting it.
type CapabilityDenial =
    | AlreadyVoid
    | AlreadySuperseded of SupersessionCause
    | NotVoid
    /// The transition's semantics are not defined by any repository
    /// requirement, so offering it would mean inventing domain behavior.
    | BlockedByOpenQuestion of questionId: string

module Capabilities =

    /// The capabilities available in a given state.
    ///
    /// `CanMerge` is available on `Active` since DF-TE-0006 resolved OQ-4:
    /// merge supersedes its sources into a new entry, reusing `Superseded`
    /// rather than `Void`.
    let available (state: EntryState) : EntryCapability list =
        match state with
        | Active -> [ CanCorrect; CanSplit; CanVoid; CanMerge; CanAttachEvidence ]
        | Void _ -> [ CanRestore ]
        | Superseded _ -> []

    let has (capability: EntryCapability) (state: EntryState) =
        available state |> List.contains capability

    /// Why a capability is unavailable, when it is.
    let denialFor (capability: EntryCapability) (state: EntryState) : CapabilityDenial option =
        if has capability state then
            None
        else
            // State-dominant cases first. `Superseded` is the strongest fact
            // about an entry and must win over `NotVoid`: a merged or split
            // source is not restorable *because it is superseded*, and
            // reporting "not void" would invite the caller to try voiding it.
            match capability, state with
            | _, Superseded cause -> Some(AlreadySuperseded cause)
            | _, Void _ -> Some AlreadyVoid
            | CanRestore, Active -> Some NotVoid
            | _ -> None

/// Work an entry still owes before it can be attested.
///
/// Grounded in the recovered screen design, which surfaces exactly these:
/// `static-ui-screens/today.html` renders a `badge-warn` reading "Purpose
/// missing" and a notice "The LinkedIn entry needs a clear business purpose
/// before attestation"; `states.html` and `review.html` carry the sync and
/// conflict states.
///
/// Obligations are first-class rather than error strings so incomplete work is
/// visible and enumerable (execution rule §9).
type Obligation =
    /// The entry has not yet been written to the backend.
    | PersistencePending
    /// The backend holds a newer version; a stale write was refused
    /// (TE-R-070..TE-R-072).
    | ConflictAwaitingReconciliation of theirs: VersionToken
    /// A business purpose is required before daily attestation.
    | BusinessPurposeMissing
    /// The entry references a project that is not in the loaded project set.
    | ProjectReferenceUnresolved of ProjectId
    /// A persisted record could not be interpreted (TE-R-084).
    | PersistedRecordUnreadable of detail: string

module Obligation =

    /// Whether an obligation prevents attestation of the day.
    let blocksAttestation (obligation: Obligation) =
        match obligation with
        | BusinessPurposeMissing -> true
        | ConflictAwaitingReconciliation _ -> true
        | PersistedRecordUnreadable _ -> true
        | ProjectReferenceUnresolved _ -> true
        | PersistencePending -> false

    /// Obligations derivable from the entry alone. Obligations that depend on
    /// external knowledge — `ProjectReferenceUnresolved`, `PersistencePending`,
    /// `ConflictAwaitingReconciliation` — are contributed by Tier 3, which
    /// knows the loaded project set and the persistence outcome.
    let intrinsic (entry: TimeEntry) : Obligation list =
        [ if EntryState.countsTowardTotals entry.State && entry.Effective.Description.IsNone then
              BusinessPurposeMissing ]
