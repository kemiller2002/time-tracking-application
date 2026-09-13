namespace EchelonFoundry.Chrona.Integration

open System

/// Pure description of where Chrona's GitHub-backed datastore expects a
/// producer to write an inbound observation — never performs I/O itself
/// (this assembly contains no GitHub API code; see
/// `docs/integration/INTEGRATION-CONTRACT.md`). The actual reader/writer
/// lives inside Chrona itself (`Ledger.Engine`), free to reuse this path.
///
/// Deliberately not `organizations/<org>/projects/<project>/...` as
/// originally sketched: Chrona's existing GitHub datastore is scoped by
/// `<Folder>` (one GitHub repo/folder per sync target — see
/// `docs/integration/DATASTORE-CURRENT.md`), not by a multi-organization
/// hierarchy Chrona does not otherwise have. `OrganizationId` remains a
/// required field on every `TimeObservation` (a producer's own identity),
/// but does not select a path segment here — see
/// `docs/integration/INTEGRATION-CONTRACT.md`'s "Datastore path
/// adaptation" section for the full reasoning.
///
/// Only the inbox path is exposed here — a producer never needs to know
/// where Chrona keeps its own processing receipts or candidates, since
/// those are entirely Chrona-owned implementation details, not part of
/// the public integration boundary.
module StorageConvention =

    type PathError = BlankSegment of field: string

    /// GitHub Contents API paths are `/`-delimited; every path segment
    /// built from producer-supplied identity must never itself contain a
    /// `/` (or `..`, or any other character that could let a malformed or
    /// hostile `ObservationId`/`ProjectId` escape its intended directory).
    /// Rather than rejecting unusual characters outright, every character
    /// outside this safe set is percent-style-encoded as `_XX` (its
    /// two-digit hex code) — this keeps the transform total (it always
    /// produces a path, never fails on a merely unusual id) while making
    /// directory traversal structurally impossible.
    let private isSafeChar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-' || c = '_' || c = '.'

    /// Total — always produces a path segment, by encoding every
    /// character outside `[A-Za-z0-9._-]` as `_XX` (its two-digit hex
    /// code) rather than rejecting it. Exposed (not just used
    /// internally) so Chrona-internal code building its own
    /// integration-related paths (candidate/receipt files — see
    /// `docs/integration/INTEGRATION-CONTRACT.md`'s "only the inbox path
    /// is public" note) reuses the exact same encoding rather than
    /// reimplementing it.
    let safeSegment (value: string) : string =
        value
        |> Seq.map (fun c -> if isSafeChar c then string c else sprintf "_%02x" (int c))
        |> String.concat ""

    let private nonBlankSegment (field: string) (value: string) : Result<string, PathError> =
        if String.IsNullOrWhiteSpace value then Error(BlankSegment field) else Ok(safeSegment value)

    let private trimSlashes (value: string) = value.Trim('/')

    /// `<Folder>/integration/projects/<safe ProjectId>/observations/inbox`
    /// — the directory a producer's inbox files, and Chrona's own
    /// listing of them (specification §35's "list observation files"),
    /// both live under.
    let observationInboxDirectory (folder: string) (projectId: string) : Result<string, PathError> =
        if String.IsNullOrWhiteSpace folder then
            Error(BlankSegment "folder")
        else
            nonBlankSegment "projectId" projectId
            |> Result.map (fun safeProjectId -> $"{trimSlashes folder}/integration/projects/{safeProjectId}/observations/inbox")

    /// `<Folder>/integration/projects/<safe ProjectId>/observations/inbox/<safe ObservationId>.json`
    ///
    /// `folder` is the same GitHub-sync `Folder` Chrona's existing
    /// `GitHubSync.referenceFilePath`/`dataFilePath` scope every other
    /// datastore path to.
    let observationInboxPath (folder: string) (projectId: string) (observationId: string) : Result<string, PathError> =
        match observationInboxDirectory folder projectId, nonBlankSegment "observationId" observationId with
        | Error e, _ -> Error e
        | _, Error e -> Error e
        | Ok directory, Ok safeObservationId -> Ok $"{directory}/{safeObservationId}.json"
