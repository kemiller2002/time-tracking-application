/// Verifies the pure parts of the GitHub transport.
///
/// The transport's I/O cannot be exercised here — that needs a real repository
/// and a token. What *can* be verified is everything that is a function of its
/// inputs: URL construction, HTTP status classification, retry policy, and
/// precondition checking. Those were deliberately factored out of `HttpStore`
/// into `HttpProtocol` so this file could exist.
module TimeEntry.Tests.HttpProtocolTests

open Xunit
open TimeEntry.GitHub.Store
open TimeEntry.GitHub.HttpProtocol

let private target =
    { Owner = "kemiller2002"
      Repository = "time-tracking-application"
      Branch = "main" }

// --- URLs ------------------------------------------------------------------

[<Fact>]
let ``the contents url is scoped to the branch`` () =
    // Without ?ref the API answers from the default branch, which would read
    // the wrong ledger on any other branch.
    let url = Url.contents target "ledger/entries/ab/abcdef.json"
    Assert.Contains("?ref=main", url)
    Assert.StartsWith("https://api.github.com/repos/kemiller2002/time-tracking-application/contents/", url)

[<Fact>]
let ``a path is url-escaped in the contents url`` () =
    // Slashes must survive as path separators after escaping.
    let url = Url.contents target "ledger/entries/ab/a b.json"
    Assert.Contains("a%20b.json", url)

[<Fact>]
let ``the tree url requests a recursive listing`` () =
    // One call for the whole ledger; see Layout for why it cannot be narrowed.
    Assert.Contains("?recursive=1", Url.tree target "treesha")

[<Fact>]
let ``ref read and ref update use the documented distinct paths`` () =
    // GitHub reads a single ref at /git/ref/... and updates at /git/refs/...
    Assert.EndsWith("/git/ref/heads/main", Url.ref target)
    Assert.EndsWith("/git/refs/heads/main", Url.refUpdate target)

// --- status classification -------------------------------------------------

[<Fact>]
let ``401 is an authorization failure`` () =
    match classify 401 "bad credentials" None None with
    | Unauthorized _ -> ()
    | other -> failwithf "expected Unauthorized, got %A" other

[<Fact>]
let ``403 with no remaining quota is a rate limit, not an auth failure`` () =
    // GitHub overloads 403. Treating a rate limit as an auth failure would
    // make a transient condition look permanent and stop the client retrying.
    match classify 403 "rate limit exceeded" None (Some 0) with
    | RateLimited _ -> ()
    | other -> failwithf "expected RateLimited, got %A" other

[<Fact>]
let ``403 with quota remaining is a genuine authorization failure`` () =
    match classify 403 "resource not accessible" None (Some 4999) with
    | Unauthorized _ -> ()
    | other -> failwithf "expected Unauthorized, got %A" other

[<Fact>]
let ``403 with a retry-after header is a rate limit`` () =
    // The secondary rate limit sends Retry-After without zeroing the quota.
    match classify 403 "secondary rate limit" (Some 30) None with
    | RateLimited(Some 30) -> ()
    | other -> failwithf "expected RateLimited 30, got %A" other

[<Fact>]
let ``429 is a rate limit`` () =
    match classify 429 "too many requests" (Some 60) None with
    | RateLimited(Some 60) -> ()
    | other -> failwithf "expected RateLimited 60, got %A" other

[<Fact>]
let ``404 is not found`` () =
    match classify 404 "Not Found" None None with
    | NotFound _ -> ()
    | other -> failwithf "expected NotFound, got %A" other

[<Theory>]
[<InlineData(409)>]
[<InlineData(422)>]
let ``a rejected ref update is reported as a moved head`` (status: int) =
    // 422 is what GitHub answers for a non-fast-forward ref update.
    match classify status "Update is not a fast forward" None None with
    | HeadMoved _ -> ()
    | other -> failwithf "expected HeadMoved, got %A" other

[<Theory>]
[<InlineData(500)>]
[<InlineData(502)>]
[<InlineData(503)>]
let ``server errors are transport failures`` (status: int) =
    match classify status "upstream" None None with
    | TransportFailure _ -> ()
    | other -> failwithf "expected TransportFailure, got %A" other

[<Fact>]
let ``an unmapped status is reported with its code rather than guessed`` () =
    match classify 418 "teapot" None None with
    | UnexpectedResponse(418, _) -> ()
    | other -> failwithf "expected UnexpectedResponse 418, got %A" other

// --- retry policy ----------------------------------------------------------

[<Fact>]
let ``a conflict is never retried`` () =
    // TE-R-072: a stale write must surface as an outcome the domain
    // reconciles. Retrying it would be the "re-read and overwrite" the
    // requirements forbid.
    Assert.Equal(None, retryDelaySeconds 1 (PreconditionFailed("p", Some "a", Some "b")))
    Assert.Equal(None, retryDelaySeconds 1 (HeadMoved("a", "b")))

[<Fact>]
let ``an authorization failure is never retried`` () =
    Assert.Equal(None, retryDelaySeconds 1 (Unauthorized "bad token"))
    Assert.Equal(None, retryDelaySeconds 1 (NotFound "gone"))

[<Fact>]
let ``a rate limit waits at least as long as the server advised`` () =
    // Backing off less than advised earns a longer block.
    Assert.Equal(Some 120, retryDelaySeconds 1 (RateLimited(Some 120)))

[<Fact>]
let ``a rate limit with no advice backs off exponentially`` () =
    Assert.Equal(Some 2, retryDelaySeconds 1 (RateLimited None))
    Assert.Equal(Some 8, retryDelaySeconds 3 (RateLimited None))

[<Fact>]
let ``backoff is capped`` () =
    // Unbounded doubling would eventually wait for hours.
    Assert.Equal(Some 60, retryDelaySeconds 20 (TransportFailure "reset"))

// --- preconditions ---------------------------------------------------------

let private write path expected =
    { Path = path
      Content = "{}"
      ExpectedSha = expected }

[<Fact>]
let ``a matching expected sha passes`` () =
    Assert.Equal(None, checkPreconditions [ "a.json", "sha1" ] [ write "a.json" (Some "sha1") ])

[<Fact>]
let ``a create against an absent file passes`` () =
    Assert.Equal(None, checkPreconditions [] [ write "a.json" None ])

[<Fact>]
let ``a stale expected sha fails and names the file`` () =
    match checkPreconditions [ "a.json", "sha2" ] [ write "a.json" (Some "sha1") ] with
    | Some(PreconditionFailed("a.json", Some "sha1", Some "sha2")) -> ()
    | other -> failwithf "expected PreconditionFailed, got %A" other

[<Fact>]
let ``a create against an existing file fails`` () =
    // How "another device already created this entry" is detected.
    match checkPreconditions [ "a.json", "sha1" ] [ write "a.json" None ] with
    | Some(PreconditionFailed("a.json", None, Some "sha1")) -> ()
    | other -> failwithf "expected PreconditionFailed, got %A" other

[<Fact>]
let ``an update to a file that has vanished fails`` () =
    match checkPreconditions [] [ write "a.json" (Some "sha1") ] with
    | Some(PreconditionFailed("a.json", Some "sha1", None)) -> ()
    | other -> failwithf "expected PreconditionFailed, got %A" other

[<Fact>]
let ``one bad write in a group fails the whole group`` () =
    // A split must not half-apply: the first violation stops everything.
    let writes =
        [ write "a.json" (Some "sha1")
          write "b.json" (Some "stale")
          write "c.json" None ]

    match checkPreconditions [ "a.json", "sha1"; "b.json", "sha2" ] writes with
    | Some(PreconditionFailed("b.json", _, _)) -> ()
    | other -> failwithf "expected the group to fail on b.json, got %A" other
