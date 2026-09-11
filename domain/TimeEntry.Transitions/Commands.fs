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

/// One entry being merged away. Carries its own expected version, because
/// each source was read independently and any one of them may be stale
/// (TE-R-070), and its own revision id for the supersession revision it gains.
type MergeSource =
    { EntryId: EntryId
      ExpectedVersion: VersionToken
      NewRevisionId: RevisionId }

/// Merge N entries into one (TE-R-026, DF-TE-0006).
///
/// There is no `Duration` field: the merged duration is *computed* as the sum
/// of the sources' durations. Supplying it would create a value that could
/// disagree with the sources; computing it makes total preservation
/// structural rather than validated. System-prompt 8.11 asks only to "preview
/// merged time", never to edit it — unlike split, which explicitly permits
/// "optional time correction".
type MergeEntriesRequest =
    { NewEntryId: EntryId
      Sources: MergeSource list
      /// The final category and project chosen in the merge UI (8.11).
      Project: ProjectId
      ActivityType: ActivityTypeId
      /// Combined description (8.11 "combine descriptions").
      Description: Description option
      /// Evidence chosen to carry onto the merged entry (8.11).
      Evidence: EvidenceRef list
      /// 8.11: "require reason".
      Reason: Reason
      Attribution: Attribution }

/// Every legal user intention against the ledger.
type Command =
    | CreateEntry of CreateEntryRequest
    | CorrectEntry of CorrectEntryRequest
    | SplitEntry of SplitEntryRequest
    | VoidEntry of VoidEntryRequest
    | RestoreEntry of RestoreEntryRequest
    | AttachEvidence of AttachEvidenceRequest
    | MergeEntries of MergeEntriesRequest
