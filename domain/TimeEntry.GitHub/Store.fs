/// Tier 4 — Host. The port the effect interpreter writes through.
///
/// This is a *port*, not an implementation: a record of functions rather than
/// an interface, so a test can supply an in-memory store as plain data and the
/// interpreter can be verified with no network at all.
///
/// It is expressed in GitHub's vocabulary — paths, blob SHAs, commits, refs —
/// because that is what Tier 4 is for. Nothing below this tier sees any of it
/// (TE-R-094).
module TimeEntry.GitHub.Store

/// A file as the backing repository currently holds it.
type StoredFile =
    { Path: string
      Content: string
      /// The blob SHA. This is the concurrency token the domain knows as an
      /// opaque `VersionToken` (TE-R-073).
      Sha: string }

/// One file to write in a commit. `ExpectedSha` is the precondition:
/// `Some sha` means "replace exactly this version", `None` means "create;
/// fail if it already exists".
type FileWrite =
    { Path: string
      Content: string
      ExpectedSha: string option }

/// A group of writes to land together.
///
/// `ExpectedHeadSha` makes the whole commit a compare-and-swap on the branch
/// ref: if the branch moved since it was read, the ref update fails and
/// nothing lands. Combined with the per-file `ExpectedSha` checks, that is
/// what closes the window between reading a version and writing against it.
type CommitRequest =
    { Message: string
      Writes: FileWrite list
      ExpectedHeadSha: string }

type CommitResult =
    { HeadSha: string
      /// New blob SHA per written path, in request order.
      WrittenShas: (string * string) list }

/// Why a store operation did not succeed.
///
/// `PreconditionFailed` is separated from the rest because it is not an
/// error in the usual sense — it is the expected outcome of a stale write and
/// must reach the domain as a conflict, not as a failure (TE-R-072).
type StoreError =
    /// A file's current SHA is not the one the caller expected, or a create
    /// found an existing file.
    | PreconditionFailed of path: string * expected: string option * actual: string option
    /// The branch ref moved between read and commit.
    | HeadMoved of expected: string * actual: string
    | NotFound of path: string
    /// The server refused what was sent. Distinct from `CredentialMissing`:
    /// this one cost a round trip and means the credential is wrong, not
    /// absent.
    | Unauthorized of detail: string
    /// No credential could be produced, so no request was sent. Carries the
    /// client-side reason (`Credential.CredentialError`, rendered) rather than
    /// a status code, because there was no response to get one from.
    | CredentialMissing of detail: string
    /// GitHub's secondary rate limit or abuse detection; carries the advised
    /// wait where the response gave one.
    | RateLimited of retryAfterSeconds: int option
    | TransportFailure of detail: string
    | UnexpectedResponse of status: int * detail: string

/// The operations the interpreter needs. Deliberately small: read one file,
/// list the ledger's paths, commit a group of writes.
///
/// `NoEquality`/`NoComparison` because the fields are functions: comparing two
/// stores is meaningless, and F# requires that to be stated rather than
/// inferred.
[<NoEquality; NoComparison>]
type GitHubStore =
    { ReadFile: string -> Async<Result<StoredFile option, StoreError>>
      /// Paths under the ledger root, from one recursive tree read.
      ListEntryPaths: unit -> Async<Result<string list, StoreError>>
      ReadHead: unit -> Async<Result<string, StoreError>>
      Commit: CommitRequest -> Async<Result<CommitResult, StoreError>> }
