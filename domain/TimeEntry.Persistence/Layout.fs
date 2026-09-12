/// Tier 4 — Persistence. Where a stored entry lives in the repository.
///
/// The layout is a separate concern from the document shape: changing where
/// files live must not change what they contain, and vice versa.
module TimeEntry.Persistence.Layout

open TimeEntry.Semantic.Identifiers

/// Root of the ledger inside the backing repository.
[<Literal>]
let LedgerRoot = "ledger/entries"

/// How many leading characters of the entry id form the shard directory.
[<Literal>]
let ShardLength = 2

/// Characters an entry id may contribute to a path. Identifiers are opaque
/// domain strings, and a path is a different alphabet — anything outside this
/// set is escaped rather than trusted, so an identifier can never climb out of
/// the ledger root or collide with a path separator.
let private isPathSafe (c: char) =
    (c >= 'a' && c <= 'z')
    || (c >= 'A' && c <= 'Z')
    || (c >= '0' && c <= '9')
    || c = '-'
    || c = '_'

let private escape (raw: string) =
    raw
    |> Seq.map (fun c -> if isPathSafe c then string c else sprintf "~%02x" (int c))
    |> String.concat ""

/// The path of one entry's document.
///
/// Keyed by **entry id**, not by date. That is deliberate and is the whole
/// reason this module exists as a decision rather than a convenience:
///
/// - A correction may change an entry's `Date` (`CorrectEntryRequest` carries
///   whole facts, and system-prompt 8.7 makes the date editable). Under a
///   date-partitioned layout every such correction would be a file *rename* —
///   a delete plus a create, which GitHub's contents API cannot do atomically,
///   so an interrupted correction could leave the entry recorded twice or not
///   at all. Keying on immutable identity removes that failure mode entirely,
///   without inventing a rule forbidding date corrections.
///
/// - Sharded on the first two characters so no single directory grows without
///   bound. Directory listings are paginated by the API, and git itself shards
///   objects this way.
///
/// - One file per entry, so each entry carries its own independent blob SHA
///   (TE-R-073). A per-day file would funnel every write for that day through
///   one SHA and manufacture conflicts the domain does not have.
let entryPath (entryId: EntryId) : string =
    let safe = escape (EntryId.value entryId)

    let shard =
        if safe.Length >= ShardLength then
            safe.Substring(0, ShardLength)
        else
            safe.PadRight(ShardLength, '_')

    sprintf "%s/%s/%s.json" LedgerRoot shard safe

/// Whether a repository path is one of this ledger's entry documents. Used to
/// filter a recursive tree listing.
///
/// Loading a day means reading the tree and filtering, rather than listing a
/// date directory. That costs one recursive tree call and keeps the write path
/// conflict-free, which is the right trade for a ledger whose volume is a few
/// thousand entries a year.
let isEntryPath (path: string) : bool =
    not (isNull path)
    && path.StartsWith(LedgerRoot + "/", System.StringComparison.Ordinal)
    && path.EndsWith(".json", System.StringComparison.Ordinal)

/// The catalogue's path.
///
/// Outside `LedgerRoot` so that `isEntryPath` cannot match it and a recursive
/// tree read never mistakes the catalogue for an entry.
[<Literal>]
let CataloguePath = "ledger/catalogue.json"

/// The preferences file's path.
///
/// Beside the catalogue and outside `LedgerRoot`, for the same reason: a
/// recursive tree read filters on `isEntryPath`, and a preferences file inside
/// the entry root would be offered to the entry decoder and reported as one
/// unreadable entry on every load.
[<Literal>]
let PreferencesPath = "ledger/preferences.json"
