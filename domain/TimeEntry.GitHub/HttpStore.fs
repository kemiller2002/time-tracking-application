/// Tier 4 — Host. The GitHub transport.
///
/// Implements the `GitHubStore` port over the REST API. This is the only file
/// in the repository that performs network I/O.
///
/// **Runtime verification status.** The pure parts — URL construction, status
/// classification, retry policy — are in `HttpProtocol` and are tested. The
/// I/O in this file is *not* runtime-verified: doing so needs a real
/// repository and a token, which this environment has neither of. It compiles
/// under warnings-as-errors and the shapes it sends match GitHub's documented
/// request bodies, but treat it as unexercised until it has run against a real
/// repository. `docs/time-entry/TRACEABILITY.md` records it that way.
///
/// ## Why writes use the Git Data API
///
/// The contents API writes one file per call. A split writes three files, and
/// three separate calls can half-succeed — losing or duplicating recorded
/// time (TE-R-035). The Git Data API builds one tree, one commit, and then
/// moves the branch ref once, so the whole group lands atomically.
///
/// ## Why per-file SHA checks are sound here
///
/// The Git Data API has no per-file compare-and-swap. It does not need one:
/// **on a single branch, any change to any file moves the branch head.** So
/// if a file this commit depends on changed after it was read, the ref update
/// is no longer a fast-forward from the head we read, and GitHub rejects it.
/// The ref CAS is what makes the write safe; the per-file check exists only to
/// report *which* file conflicted, which a bare ref rejection cannot tell the
/// user (TE-R-071).
module TimeEntry.GitHub.HttpStore

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open TimeEntry.GitHub.Store
open TimeEntry.GitHub.Credential
open TimeEntry.GitHub.HttpProtocol

/// Git's file mode for a non-executable blob.
[<Literal>]
let private BlobMode = "100644"

let private headerInt (response: HttpResponseMessage) (name: string) =
    match response.Headers.TryGetValues name with
    | true, values ->
        values
        |> Seq.tryHead
        |> Option.bind (fun raw ->
            match Int32.TryParse raw with
            | true, value -> Some value
            | _ -> None)
    | _ -> None

/// Issue a request and classify anything that is not a success.
///
/// The credential is acquired HERE, per request, rather than fixed on the
/// client at construction. A static token would not need that; a device-flow
/// or installation token does, because it expires and the refresh has to
/// happen somewhere. Asking every time is what lets the mechanism change
/// without this file changing (DF-TE-0011).
/// Where the server clock comes in.
///
/// `Date` is required of every HTTP response (RFC 9110 §6.6.1), so the
/// server's time is already arriving with every read and needs no request of
/// its own (DF-TE-0017, TE-R-008). Recorded on the way past, including on a
/// FAILED response: a 409 or a 401 carries a `Date` too, and a clock that is
/// hours out is exactly the sort of thing that causes failures, so throwing
/// the evidence away on the error path would discard it when it is most
/// useful.
///
/// A ref cell, which is mutation — permitted here because it stands in for an
/// external system that genuinely changes over time, the same allowance the
/// test store double takes. Nothing below Tier 4 sees it.
let private observe (observed: int64 option ref) (response: HttpResponseMessage) =
    if response.Headers.Date.HasValue then
        observed.Value <- Some(response.Headers.Date.Value.ToUnixTimeMilliseconds())

let private send
    (observed: int64 option ref)
    (credential: CredentialSource)
    (client: HttpClient)
    (request: HttpRequestMessage)
    =
    async {
        let! authorization = credential.Acquire()

        match authorization with
        // No request is sent. A missing credential is a fact the client
        // already knows, and spending a round trip to be told 401 would both
        // waste it and lose the distinction.
        | Error error -> return Error(CredentialMissing(describeError error))
        | Ok granted ->

        match granted with
        | AuthorizationHeader(scheme, parameter) ->
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue(scheme, parameter)
        // Deliberately sets no header at all rather than an empty one: a
        // proxy holding the session needs the request to arrive unadorned.
        | AmbientAuthority -> ()

        try
            let! response = client.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            observe observed response

            if response.IsSuccessStatusCode then
                return Ok body
            else
                return
                    Error(
                        classify
                            (int response.StatusCode)
                            body
                            (headerInt response "retry-after")
                            (headerInt response "x-ratelimit-remaining")
                    )
        with
        | :? HttpRequestException as ex -> return Error(TransportFailure ex.Message)
        | :? System.Threading.Tasks.TaskCanceledException as ex ->
            return Error(TransportFailure("timed out: " + ex.Message))
        // Anything else from the send is still a transport failure — that is
        // what "the request did not complete" means, whatever type carried
        // the news. Naming only the two expected exceptions let a third
        // escape: in the browser a failed fetch arrives as an
        // AggregateException wrapping a TypeError, which was not caught here
        // and tore through the per-effect `Failed` outcome to become a
        // whole-request error. One failing effect must not erase the
        // reporting of the others.
        | ex ->
            let rec innermost (e: exn) =
                if isNull e.InnerException then e else innermost e.InnerException

            let root = innermost ex
            return Error(TransportFailure(root.GetType().Name + ": " + root.Message))
    }

/// A client paired with the credential its requests should carry.
///
/// Carried together because every request needs both, and passing them
/// separately through a dozen helpers is how one of them eventually gets
/// forgotten.
[<NoEquality; NoComparison>]
type Session =
    { Client: HttpClient
      Credential: CredentialSource
      /// Epoch milliseconds from the most recent response's `Date` header.
      /// Shared by every request this session makes, so the latest answer
      /// wins — which is what "the server's clock" means.
      ObservedServerTimeMs: int64 option ref }

let private get (session: Session) (url: string) =
    async {
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        return! send session.ObservedServerTimeMs session.Credential session.Client request
    }

let private json (session: Session) (method: HttpMethod) (url: string) (body: JsonObject) =
    async {
        use request = new HttpRequestMessage(method, url)
        request.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        return! send session.ObservedServerTimeMs session.Credential session.Client request
    }

let private parse (body: string) : Result<JsonNode, StoreError> =
    try
        match JsonNode.Parse body with
        | null -> Error(UnexpectedResponse(200, "empty response body"))
        | node -> Ok node
    with :? JsonException as ex ->
        Error(UnexpectedResponse(200, "unparseable response: " + ex.Message))

let private field (name: string) (node: JsonNode) : Result<string, StoreError> =
    match node.[name] with
    | null -> Error(UnexpectedResponse(200, sprintf "response missing '%s'" name))
    | value -> Ok(value.ToString())

/// Configure an `HttpClient` for the GitHub API and pair it with a
/// credential source. The caller owns the client's lifetime; this does not
/// create one, because a client per request exhausts sockets.
///
/// Note what is NOT set here: the `Authorization` header. It is applied per
/// request from the credential source, so that a mechanism which refreshes
/// works without re-configuring the client (DF-TE-0011). The headers that
/// genuinely are constant — the API version, the accept type, the user agent —
/// stay defaults.
let configure (client: HttpClient) (credential: CredentialSource) : Session =
    client.DefaultRequestHeaders.Accept.Add(Headers.MediaTypeWithQualityHeaderValue "application/vnd.github+json")
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
    // GitHub rejects requests without a User-Agent.
    client.DefaultRequestHeaders.UserAgent.Add(Headers.ProductInfoHeaderValue("echelon-ledger", "1.0"))

    { Client = client
      Credential = credential
      ObservedServerTimeMs = ref None }

// ---------------------------------------------------------------------------
// Reads
// ---------------------------------------------------------------------------

let private readFile (session: Session) (target: RepositoryRef) (path: string) =
    async {
        let! body = get session (Url.contents target path)

        match body with
        // A missing file is a legitimate answer, not a failure: it is how
        // "this entry does not exist yet" is expressed.
        | Error(NotFound _) -> return Ok None
        | Error error -> return Error error
        | Ok raw ->
            match parse raw with
            | Error error -> return Error error
            | Ok node ->
                let decoded =
                    field "sha" node
                    |> Result.bind (fun sha ->
                        field "content" node
                        |> Result.map (fun encoded ->
                            let cleaned = encoded.Replace("\n", "").Replace("\r", "")

                            let content =
                                cleaned |> Convert.FromBase64String |> Encoding.UTF8.GetString

                            Some
                                { Path = path
                                  Content = content
                                  Sha = sha }))

                return decoded
    }

let private readHead (session: Session) (target: RepositoryRef) =
    async {
        let! body = get session (Url.ref target)

        match body |> Result.bind parse with
        | Error error -> return Error error
        | Ok node ->
            match node.["object"] with
            | null -> return Error(UnexpectedResponse(200, "ref response missing 'object'"))
            | objectNode -> return field "sha" objectNode
    }

let private readTreeSha (session: Session) (target: RepositoryRef) (commitSha: string) =
    async {
        let! body = get session (Url.commit target commitSha)

        match body |> Result.bind parse with
        | Error error -> return Error error
        | Ok node ->
            match node.["tree"] with
            | null -> return Error(UnexpectedResponse(200, "commit response missing 'tree'"))
            | treeNode -> return field "sha" treeNode
    }

/// Every blob path in the branch's tree, with its blob SHA.
let private readTree (session: Session) (target: RepositoryRef) =
    async {
        let! head = readHead session target

        match head with
        | Error error -> return Error error
        | Ok headSha ->
            let! treeSha = readTreeSha session target headSha

            match treeSha with
            | Error error -> return Error error
            | Ok tree ->
                let! body = get session (Url.tree target tree)

                match body |> Result.bind parse with
                | Error error -> return Error error
                | Ok node ->
                    match node.["tree"] with
                    | :? JsonArray as entries ->
                        return
                            entries
                            |> Seq.choose (fun entry ->
                                match entry with
                                | null -> None
                                | e when e.["type"] <> null && e.["type"].ToString() = "blob" ->
                                    Some(e.["path"].ToString(), e.["sha"].ToString())
                                | _ -> None)
                            |> List.ofSeq
                            |> Ok
                    | _ -> return Error(UnexpectedResponse(200, "tree response missing 'tree' array"))
    }

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

let private createBlob (session: Session) (target: RepositoryRef) (content: string) =
    async {
        let body = JsonObject()
        body.Add("content", JsonValue.Create content)
        body.Add("encoding", JsonValue.Create "utf-8")
        let! response = json session HttpMethod.Post (Url.blobs target) body
        return response |> Result.bind parse |> Result.bind (field "sha")
    }

let private createTree
    (session: Session)
    (target: RepositoryRef)
    (baseTree: string)
    (entries: (string * string) list)
    =
    async {
        let items = JsonArray()

        for path, blobSha in entries do
            let item = JsonObject()
            item.Add("path", JsonValue.Create path)
            item.Add("mode", JsonValue.Create BlobMode)
            item.Add("type", JsonValue.Create "blob")
            item.Add("sha", JsonValue.Create blobSha)
            items.Add item

        let body = JsonObject()
        body.Add("base_tree", JsonValue.Create baseTree)
        body.Add("tree", items)
        let! response = json session HttpMethod.Post (Url.trees target) body
        return response |> Result.bind parse |> Result.bind (field "sha")
    }

let private createCommit
    (session: Session)
    (target: RepositoryRef)
    (message: string)
    (treeSha: string)
    (parent: string)
    =
    async {
        let parents = JsonArray()
        parents.Add(JsonValue.Create parent)
        let body = JsonObject()
        body.Add("message", JsonValue.Create message)
        body.Add("tree", JsonValue.Create treeSha)
        body.Add("parents", parents)
        let! response = json session HttpMethod.Post (Url.commits target) body
        return response |> Result.bind parse |> Result.bind (field "sha")
    }

/// Move the branch to the new commit.
///
/// `force = false` is the compare-and-swap and the reason the whole design
/// holds: GitHub accepts this only if it is a fast-forward from the current
/// head, so a concurrent write anywhere on the branch rejects it. Setting
/// `force = true` here would silently overwrite a newer correction, which
/// TE-R-070 forbids outright.
let private updateRef (session: Session) (target: RepositoryRef) (expectedHead: string) (commitSha: string) =
    async {
        let body = JsonObject()
        body.Add("sha", JsonValue.Create commitSha)
        body.Add("force", JsonValue.Create false)
        let! response = json session (HttpMethod "PATCH") (Url.refUpdate target) body

        return
            match response with
            | Ok _ -> Ok()
            | Error(HeadMoved _) -> Error(HeadMoved(expectedHead, "(rejected as non-fast-forward)"))
            | Error error -> Error error
    }

let private commit (session: Session) (target: RepositoryRef) (request: CommitRequest) =
    async {
        let! tree = readTree session target

        match tree with
        | Error error -> return Error error
        | Ok entries ->
            match checkPreconditions entries request.Writes with
            | Some violation -> return Error violation
            | None ->
                let! head = readHead session target

                match head with
                | Error error -> return Error error
                | Ok headSha when headSha <> request.ExpectedHeadSha ->
                    return Error(HeadMoved(request.ExpectedHeadSha, headSha))
                | Ok headSha ->
                    let! treeSha = readTreeSha session target headSha

                    match treeSha with
                    | Error error -> return Error error
                    | Ok baseTree ->
                        // Blobs first, so the tree can reference them.
                        let! blobs =
                            request.Writes
                            |> List.map (fun write ->
                                async {
                                    let! sha = createBlob session target write.Content
                                    return sha |> Result.map (fun s -> write.Path, s)
                                })
                            |> Async.Sequential

                        let failure =
                            blobs
                            |> Array.tryPick (fun r ->
                                match r with
                                | Error e -> Some e
                                | Ok _ -> None)

                        match failure with
                        | Some error -> return Error error
                        | None ->
                            let written =
                                blobs
                                |> Array.choose (fun r ->
                                    match r with
                                    | Ok pair -> Some pair
                                    | Error _ -> None)
                                |> List.ofArray

                            let! newTree = createTree session target baseTree written

                            match newTree with
                            | Error error -> return Error error
                            | Ok treeResult ->
                                let! newCommit =
                                    createCommit session target request.Message treeResult headSha

                                match newCommit with
                                | Error error -> return Error error
                                | Ok commitSha ->
                                    let! moved = updateRef session target headSha commitSha

                                    match moved with
                                    | Error error -> return Error error
                                    | Ok() ->
                                        return
                                            Ok
                                                { HeadSha = commitSha
                                                  WrittenShas = written }
    }

// ---------------------------------------------------------------------------
// The port
// ---------------------------------------------------------------------------

open TimeEntry.Persistence

/// Build a `GitHubStore` backed by the REST API.
///
/// The caller supplies and owns the `HttpClient` (see `configure`).
let create (session: Session) (target: RepositoryRef) : GitHubStore =
    { ReadFile = readFile session target
      ListEntryPaths =
        fun () ->
            async {
                let! tree = readTree session target

                return
                    tree
                    |> Result.map (fun entries ->
                        entries |> List.map fst |> List.filter Layout.isEntryPath)
            }
      ReadHead = fun () -> readHead session target
      Commit = commit session target
      ObservedServerTimeMs = fun () -> session.ObservedServerTimeMs.Value }
