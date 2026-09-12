/// Tier 1 — Semantic Model. Domain identifiers.
///
/// This tier answers "what can be true?" and must not know about HTTP, JSON,
/// GitHub, the browser, serialization, or any infrastructure
/// (.sde/architecture/FOUR-TIER-ARCHITECTURE.md). The project references only
/// FSharp.Core so that constraint is structural, not merely a convention.
module TimeEntry.Semantic.Identifiers

open System

/// Why a supplied identifier was refused.
type IdentifierError =
    | IdentifierEmpty
    | IdentifierTooLong of maxLength: int
    | IdentifierMalformed of reason: string

[<Literal>]
let private MaxIdentifierLength = 128

/// Identifiers are opaque non-empty strings. They are deliberately NOT raw
/// strings at use sites (TE-R-096): a ProjectId cannot be passed where an
/// EntryId is expected.
let private makeIdentifier (raw: string) : Result<string, IdentifierError> =
    if String.IsNullOrWhiteSpace raw then Error IdentifierEmpty
    elif raw.Length > MaxIdentifierLength then Error(IdentifierTooLong MaxIdentifierLength)
    else Ok(raw.Trim())

/// Catalogue identifiers are additionally constrained to `^[a-z0-9-]+$`,
/// because `schemas/domain/project.schema.json` and
/// `schemas/domain/activity-type.schema.json` say so. Accepting an id the
/// documented contract rejects would let the domain build records that fail
/// schema validation downstream, which is worse than rejecting them here.
let private isSlugChar (c: char) =
    (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'

let private makeSlugIdentifier (raw: string) : Result<string, IdentifierError> =
    makeIdentifier raw
    |> Result.bind (fun trimmed ->
        if trimmed |> Seq.forall isSlugChar then
            Ok trimmed
        else
            Error(IdentifierMalformed "expected only lowercase letters, digits and hyphens"))

/// Identity of a time entry. Stable across corrections: a correction produces a
/// new *revision* of the same entry, never a new entry (TE-R-050).
type EntryId =
    private
    | EntryId of string

    member this.Value = let (EntryId value) = this in value

module EntryId =
    let create raw = makeIdentifier raw |> Result.map EntryId
    let value (EntryId v) = v

/// Identity of one immutable revision in an entry's history. Every state
/// change appends a revision; none is ever rewritten (TE-R-030, TE-R-031).
type RevisionId =
    private
    | RevisionId of string

    member this.Value = let (RevisionId value) = this in value

module RevisionId =
    let create raw = makeIdentifier raw |> Result.map RevisionId
    let value (RevisionId v) = v

type ProjectId =
    private
    | ProjectId of string

    member this.Value = let (ProjectId value) = this in value

module ProjectId =
    let create raw = makeSlugIdentifier raw |> Result.map ProjectId
    let value (ProjectId v) = v

type ActivityTypeId =
    private
    | ActivityTypeId of string

    member this.Value = let (ActivityTypeId value) = this in value

module ActivityTypeId =
    let create raw = makeSlugIdentifier raw |> Result.map ActivityTypeId
    let value (ActivityTypeId v) = v

type UserId =
    private
    | UserId of string

    member this.Value = let (UserId value) = this in value

module UserId =
    let create raw = makeIdentifier raw |> Result.map UserId
    let value (UserId v) = v

/// A human-readable label for the device that originated a change. Required by
/// the history view (TE-R-052).
type DeviceLabel =
    private
    | DeviceLabel of string

    member this.Value = let (DeviceLabel value) = this in value

module DeviceLabel =
    let create raw = makeIdentifier raw |> Result.map DeviceLabel
    let value (DeviceLabel v) = v
