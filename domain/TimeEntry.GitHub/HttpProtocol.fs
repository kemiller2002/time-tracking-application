/// Tier 4 — Host. The pure parts of talking to GitHub.
///
/// Deliberately separated from `HttpStore`: URL construction and
/// status/error mapping are total functions of their inputs, so they can be
/// verified without a network. What remains in `HttpStore` is the irreducible
/// I/O, which cannot be.
///
/// This split is the reason any of the transport layer is testable at all.
module TimeEntry.GitHub.HttpProtocol

open TimeEntry.GitHub.Store

/// Which repository and branch the ledger lives in.
type RepositoryRef =
    { Owner: string
      Repository: string
      Branch: string }

[<Literal>]
let ApiRoot = "https://api.github.com"

module Url =
    let private repo (target: RepositoryRef) =
        sprintf "%s/repos/%s/%s" ApiRoot target.Owner target.Repository

    /// Contents API, used only for reads: it returns the blob SHA alongside
    /// the content, which is the concurrency token.
    let contents (target: RepositoryRef) (path: string) =
        sprintf "%s/contents/%s?ref=%s" (repo target) (System.Uri.EscapeDataString path) target.Branch

    /// One recursive tree read lists the whole ledger. See `Layout` for why
    /// the ledger is not date-partitioned and therefore cannot be listed by
    /// directory.
    let tree (target: RepositoryRef) (treeSha: string) =
        sprintf "%s/git/trees/%s?recursive=1" (repo target) treeSha

    let ref (target: RepositoryRef) =
        sprintf "%s/git/ref/heads/%s" (repo target) target.Branch

    let refUpdate (target: RepositoryRef) =
        sprintf "%s/git/refs/heads/%s" (repo target) target.Branch

    let blobs (target: RepositoryRef) = sprintf "%s/git/blobs" (repo target)
    let trees (target: RepositoryRef) = sprintf "%s/git/trees" (repo target)
    let commits (target: RepositoryRef) = sprintf "%s/git/commits" (repo target)
    let commit (target: RepositoryRef) (sha: string) = sprintf "%s/git/commits/%s" (repo target) sha

/// Map an HTTP response onto a `StoreError`.
///
/// `retryAfter` and `rateLimitRemaining` come from response headers. GitHub
/// signals two different things with 403: a genuine permission problem and a
/// rate limit. They are distinguished by `x-ratelimit-remaining: 0`, because
/// treating a rate limit as an authorization failure would make a transient
/// condition look permanent and stop the client retrying.
let classify
    (status: int)
    (detail: string)
    (retryAfterSeconds: int option)
    (rateLimitRemaining: int option)
    : StoreError =
    match status with
    | 401 -> Unauthorized detail
    | 403 ->
        if rateLimitRemaining = Some 0 || retryAfterSeconds.IsSome then
            RateLimited retryAfterSeconds
        else
            Unauthorized detail
    | 429 -> RateLimited retryAfterSeconds
    | 404 -> NotFound detail
    // A ref update rejected as non-fast-forward: the branch moved. GitHub
    // answers 422 for this, and 409 for a general conflict.
    | 409
    | 422 -> HeadMoved("(expected)", "(moved)")
    | code when code >= 500 -> TransportFailure(sprintf "server error %d: %s" code detail)
    | code -> UnexpectedResponse(code, detail)

/// Whether a failure is worth retrying, and how long to wait.
///
/// A conflict is never retried here: TE-R-072 requires a stale write to
/// surface as an explicit outcome the domain reconciles, not something the
/// transport silently re-attempts. Retrying a `PreconditionFailed` would be
/// exactly the "automatically re-read and overwrite" the requirements forbid.
let retryDelaySeconds (attempt: int) (error: StoreError) : int option =
    let backoff = min 60 (1 <<< attempt)

    match error with
    | RateLimited(Some advised) -> Some(max advised backoff)
    | RateLimited None -> Some backoff
    | TransportFailure _ -> Some backoff
    | UnexpectedResponse(code, _) when code >= 500 -> Some backoff
    | PreconditionFailed _
    | HeadMoved _
    | NotFound _
    | Unauthorized _
    | UnexpectedResponse _ -> None

/// Check every write's precondition against a tree listing.
///
/// Pure, and therefore verifiable without a network — which is why it lives
/// here rather than in the transport. Fails on the first violation so the
/// caller learns *which* file conflicted, which a bare ref rejection cannot
/// tell the user (TE-R-071).
let checkPreconditions (tree: (string * string) list) (writes: FileWrite list) : StoreError option =
    let current = Map.ofList tree

    writes
    |> List.tryPick (fun write ->
        match write.ExpectedSha, Map.tryFind write.Path current with
        | Some expected, Some actual when expected = actual -> None
        | None, None -> None
        | expected, actual -> Some(PreconditionFailed(write.Path, expected, actual)))
