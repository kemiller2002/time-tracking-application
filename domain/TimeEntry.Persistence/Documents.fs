/// Tier 4 — Persistence. The stored shape of a time entry.
///
/// This is the anti-corruption boundary (TE-R-094). These types exist so the
/// domain never has to know what a stored record looks like, and the stored
/// record never has to change when the domain's internals do.
///
/// Three deliberate properties:
///
/// 1. **Primitives only.** Every field is a string, an int64, or an array of
///    those. No F# unions, no options. Discriminated unions are flattened onto
///    a `*_kind` discriminator plus the fields that case carries. This keeps
///    serialization reflection-free and, more importantly, keeps the stored
///    form reviewable as plain JSON in a GitHub diff.
///
/// 2. **No version field.** The concurrency token is the GitHub blob SHA,
///    which is a hash *of this content*. Storing it inside the content would
///    be circular — writing the token would change the token. Version
///    therefore travels as file metadata, supplied by the store, never as part
///    of the document (see `Mapping.fromDocument`, which takes it separately).
///
/// 3. **Field names match the established contract.** `exact_duration_ms`,
///    `project_id`, `activity_type_id` are the names `worker/src/store.js`
///    already used, so a future import from that format is a field-for-field
///    mapping rather than a translation (DF-TE-0009).
module TimeEntry.Persistence.Documents

/// The document schema's own version. Bumped only when the stored shape
/// changes incompatibly; a reader refuses a version it does not know rather
/// than guessing (TE-R-084).
[<Literal>]
let CurrentSchemaVersion = "1.0.0"

[<CLIMutable>]
type EvidenceDocument =
    { uri: string
      /// Null when the evidence carries no label.
      label: string
      attached_at_ms: int64 }

[<CLIMutable>]
type FactsDocument =
    { project_id: string
      activity_type_id: string
      /// ISO calendar date, `YYYY-MM-DD`. Stored as a date rather than a day
      /// ordinal so a reviewer reading the file in GitHub can see which day an
      /// entry belongs to.
      date: string
      /// Exact elapsed time — the authoritative quantity (DF-TE-0009).
      exact_duration_ms: int64
      /// Null when absent. Optional at creation per DF-TE-0005.
      description: string
      /// "timed" | "manual"
      origin_kind: string
      /// Required when `origin_kind` is "manual", null otherwise.
      manual_reason: string
      evidence: EvidenceDocument array }

/// One history revision.
///
/// `change_kind` selects which of the `change_*` fields carry meaning. A
/// reader validates that pairing rather than trusting it, so a hand-edited or
/// truncated file produces a typed error instead of a half-built entry.
[<CLIMutable>]
type RevisionDocument =
    { revision_id: string
      /// "created" | "corrected" | "voided" | "restored" | "split_into"
      /// | "merged_into" | "created_by_split_of" | "created_by_merge_of"
      /// | "evidence_attached" | "evidence_reassigned_from"
      change_kind: string
      /// Set for corrected / voided / restored.
      change_reason: string
      /// Set for split_into and created_by_merge_of.
      change_entry_ids: string array
      /// Set for merged_into, created_by_split_of, evidence_reassigned_from.
      change_entry_id: string
      /// Set for evidence_attached.
      change_evidence: EvidenceDocument
      /// The effective facts as of this revision, so history can show
      /// before-and-after without replaying (TE-R-052).
      facts: FactsDocument
      recorded_at_ms: int64
      recorded_by: string
      device: string }

/// A complete stored entry: one file per entry.
///
/// One file per entry rather than one per day is a concurrency decision. A
/// GitHub blob SHA is per file, so per-entry files give each entry its own
/// independent version token — two entries created on the same day cannot
/// conflict with each other (TE-R-073). A per-day file would serialise every
/// write on that day through a single SHA and manufacture conflicts the domain
/// does not have.
[<CLIMutable>]
type EntryDocument =
    { schema_version: string
      entry_id: string
      /// "active" | "void" | "superseded_by_split" | "superseded_by_merge"
      state_kind: string
      /// Required when `state_kind` is "void".
      void_reason: string
      /// Required (> 0) when `state_kind` is "void"; 0 otherwise. Zero is a
      /// safe sentinel only because the reader validates it against
      /// `state_kind` rather than trusting it.
      voided_at_ms: int64
      /// Required and non-empty when `state_kind` is "superseded_by_split".
      superseded_children: string array
      /// Required when `state_kind` is "superseded_by_merge".
      superseded_target: string
      effective: FactsDocument
      /// Newest first, matching `TimeEntry.History`. Never empty.
      history: RevisionDocument array }

/// Why a stored document could not be interpreted.
///
/// Typed rather than an exception or a string so a bad record becomes one
/// unreadable entry rather than a failed projection (TE-R-084).
type DocumentError =
    | UnsupportedSchemaVersion of found: string * supported: string
    | MissingField of path: string
    | InvalidField of path: string * detail: string
    | UnknownStateKind of found: string
    | UnknownChangeKind of found: string
    | EmptyHistory
    /// `state_kind` and the fields it requires disagree.
    | StateFieldsInconsistent of stateKind: string * detail: string

// ---------------------------------------------------------------------------
// Catalogue
// ---------------------------------------------------------------------------

/// One catalogue entry, field-for-field as
/// `schemas/domain/project.schema.json` and
/// `activity-type.schema.json` define it: `id`, `name`, `active`, `version`.
/// Both schemas are identical, so one document type serves both.
[<CLIMutable>]
type CatalogueEntryDocument =
    { id: string
      name: string
      active: bool
      version: string }

/// The stored catalogue.
///
/// A single file, unlike entries. Entries get a file each because each needs
/// its own independent concurrency token (see `EntryDocument`); the catalogue
/// is a read-only upstream projection this application never writes, so there
/// is no write contention to avoid and one file is simpler to review.
[<CLIMutable>]
type CatalogueDocument =
    { schema_version: string
      projects: CatalogueEntryDocument array
      activity_types: CatalogueEntryDocument array }

// ---------------------------------------------------------------------------
// Preferences
// ---------------------------------------------------------------------------

/// The stored preferences.
///
/// One file, like the catalogue and unlike entries: entries get a file each so
/// each carries an independent concurrency token, and preferences have no such
/// need — there is one of them and one person setting it.
///
/// `monthly_target_units` is six-minute units, matching the currency the
/// domain holds a target in (`TrackingTarget`). Stored as units rather than
/// hours so the file cannot express a target the domain cannot hold, and so a
/// later target of "seven and a half hours" needs no schema change. Zero means
/// no target is set: the field is absent or zero, and both read as absence,
/// because a hand-edited file in a GitHub diff may well have the key deleted
/// rather than zeroed.
[<CLIMutable>]
type PreferencesDocument =
    { schema_version: string
      monthly_target_units: int64 }
