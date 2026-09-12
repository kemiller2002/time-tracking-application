/// The credential port.
///
/// DF-TE-0011 settles that the browser holds a token today, and says the
/// mechanism is expected to change. These tests exist to make that second half
/// true rather than aspirational: each one would fail if the transport went
/// back to a token baked into the client at construction.
module TimeEntry.Tests.CredentialTests

open System.Net
open System.Net.Http
open Xunit
open TimeEntry.GitHub.Store
open TimeEntry.GitHub.Credential

// ---------------------------------------------------------------------------
// A handler that records what was actually sent
// ---------------------------------------------------------------------------

/// Captures every request and answers each with a canned body, so an
/// assertion can be made about the HEADER rather than about the outcome.
/// Asserting on the outcome would pass whether or not the header was correct.
type private Recorder(reply: string) =
    inherit HttpMessageHandler()
    let sent = ResizeArray<string option>()

    member _.Authorizations = List.ofSeq sent

    override _.SendAsync(request, _) =
        sent.Add(
            match request.Headers.Authorization with
            | null -> None
            | value -> Some(value.Scheme + " " + value.Parameter)
        )

        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(reply)
        System.Threading.Tasks.Task.FromResult response

let private target =
    TimeEntry.GitHub.HttpProtocol.RepositoryRef.gitHub "owner" "repo" "main"

/// A HEAD read is the smallest operation that issues exactly one request.
let private readHeadWith (credential: CredentialSource) (recorder: Recorder) =
    use client = new HttpClient(recorder)

    let store =
        TimeEntry.GitHub.HttpStore.create
            (TimeEntry.GitHub.HttpStore.configure client credential)
            target

    store.ReadHead() |> Async.RunSynchronously

// ---------------------------------------------------------------------------

[<Fact>]
let ``a token credential is sent as a bearer header`` () =
    let recorder = new Recorder("""{ "object": { "sha": "abc123" } }""")
    let result = readHeadWith (token "ghp_example") recorder

    Assert.Equal<string option list>([ Some "Bearer ghp_example" ], recorder.Authorizations)
    Assert.Equal(Ok "abc123", result)

[<Fact>]
let ``ambient authority sends no authorization header at all`` () =
    // Not an empty header. A proxy holding the session needs the request to
    // arrive unadorned, and `Authorization: Bearer ` would earn a confusing
    // 401 instead.
    let recorder = new Recorder("""{ "object": { "sha": "abc123" } }""")
    let result = readHeadWith (ambient "proxy") recorder

    Assert.Equal<string option list>([ None ], recorder.Authorizations)
    Assert.Equal(Ok "abc123", result)

[<Fact>]
let ``a credential is acquired per request, not once per client`` () =
    // The point of the whole port. A mechanism that refreshes — device flow,
    // an installation token — produces a different value over time, and the
    // transport must pick that up without being rebuilt.
    let mutable issued = 0

    let rotating =
        ofAsync "rotating" (fun () ->
            async {
                issued <- issued + 1
                return Ok(AuthorizationHeader("Bearer", sprintf "token-%d" issued))
            })

    let recorder = new Recorder("""{ "object": { "sha": "abc123" } }""")
    use client = new HttpClient(recorder)
    let session = TimeEntry.GitHub.HttpStore.configure client rotating
    let store = TimeEntry.GitHub.HttpStore.create session target

    store.ReadHead() |> Async.RunSynchronously |> ignore
    store.ReadHead() |> Async.RunSynchronously |> ignore

    // Two requests, two different tokens. A client configured once with a
    // fixed header would have sent the same value twice.
    Assert.Equal<string option list>(
        [ Some "Bearer token-1"; Some "Bearer token-2" ],
        recorder.Authorizations
    )

[<Fact>]
let ``a missing credential sends no request at all`` () =
    // "Not signed in" is a client-side fact. Spending a round trip to be told
    // 401 would waste it and lose the distinction between absent and refused.
    let recorder = new Recorder("""{ "object": { "sha": "abc123" } }""")
    let result = readHeadWith (token "") recorder

    Assert.Empty(recorder.Authorizations)

    match result with
    | Error(CredentialMissing detail) -> Assert.Contains("no token was supplied", detail)
    | other -> failwithf "expected CredentialMissing, got %A" other

[<Fact>]
let ``an absent credential is a different outcome from a refused one`` () =
    // `CredentialMissing` never reaches the network; `Unauthorized` is what a
    // server said. Collapsing them would make "sign in" and "your access was
    // revoked" indistinguishable in the UI.
    let refusing =
        ofAsync "expiring" (fun () ->
            async { return Error(CredentialExpired "the stored token is past its expiry") })

    let recorder = new Recorder("")

    match readHeadWith refusing recorder with
    | Error(CredentialMissing detail) -> Assert.Contains("expired", detail)
    | other -> failwithf "expected CredentialMissing, got %A" other

[<Fact>]
let ``a credential failure is never retried`` () =
    // Retrying would turn "you are not signed in" into a silent delay. No
    // number of attempts makes a credential appear.
    let error = CredentialMissing "no token was supplied"

    Assert.Equal(None, TimeEntry.GitHub.HttpProtocol.retryDelaySeconds 1 error)
    Assert.Equal(None, TimeEntry.GitHub.HttpProtocol.retryDelaySeconds 5 error)

[<Fact>]
let ``describing a source names the mechanism and not the secret`` () =
    // `Describe` exists so a diagnostic can say which mechanism is in use
    // without anything being tempted to log the credential to find out.
    let source = token "ghp_a_real_looking_secret"

    Assert.Equal("token", source.Describe)
    Assert.DoesNotContain("ghp_", source.Describe)
