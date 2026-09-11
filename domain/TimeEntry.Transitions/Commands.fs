/// Tier 2 — State Transition. User intentions, expressed explicitly.
///
/// There is deliberately no generic `UpdateEntry` command. Correcting,
/// splitting, voiding, and restoring are materially different domain
/// transitions with different evidence requirements and different history
/// consequences; a single update command would hide that (execution rule §7).
module TimeEntry.Transitions.Commands

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState

/// Fields every command carries: who, when, from which device, and — for
/// commands against an existing entry — which version the caller believed it
/// was acting on.
///
/// `ExpectedVersion` is not optional for mutations. Requiring it at the type
/// level is what makes "never silently overwrite a newer correction"
/// (TE-R-070) impossible to forget: a caller cannot construct a mutating
/// command without stating the version it read.
type Attribution =
    { Actor: UserId
      Device: DeviceLabel
      OccurredAt: Instant
      /// Pre-allocated by Tier 3. Tier 2 is pure and cannot generate identity.
      NewRevisionId: RevisionId }

type CreateEntryRequest =
    { NewEntryId: EntryId
      Facts: EntryFacts
      Attribution: Attribution }

type CorrectEntryRequest =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      /// The corrected values. Supplied whole rather than as a patch so the
      /// resulting revision records complete facts and history can show
      /// before/after without reconstruction (TE-R-052).
      CorrectedFacts: EntryFacts
      Reason: Reason
      Attribution: Attribution }

/// One child of a split. Carries its own identity and revision id because
/// Tier 2 cannot mint them.
type SplitChild =
    { NewEntryId: EntryId
      NewRevisionId: RevisionId
      Duration: Duration
      Project: ProjectId
      ActivityType: ActivityTypeId
      Description: Description option
      /// Evidence moved from the source to this child (TE-R-045).
      ReassignedEvidence: EvidenceRef list }

type SplitEntryRequest =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      Children: SplitChild list
      Attribution: Attribution }

type VoidEntryRequest =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      Reason: Reason
      Attribution: Attribution }

type RestoreEntryRequest =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      Reason: Reason
      Attribution: Attribution }

type AttachEvidenceRequest =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      Evidence: EvidenceRef
      Attribution: Attribution }

/// Every legal user intention against the ledger.
///
/// Merge (TE-R-026) is absent: OQ-4 leaves its lineage semantics undefined,
/// and the two candidate readings produce different irreversible history.
/// Adding a `MergeEntries` case would require inventing that semantics, so the
/// command does not exist rather than existing and behaving arbitrarily.
type Command =
    | CreateEntry of CreateEntryRequest
    | CorrectEntry of CorrectEntryRequest
    | SplitEntry of SplitEntryRequest
    | VoidEntry of VoidEntryRequest
    | RestoreEntry of RestoreEntryRequest
    | AttachEvidence of AttachEvidenceRequest
