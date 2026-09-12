// End-to-end verification of the GitHub transport (domain/TimeEntry.GitHub).
//
// The transport is the one part of the system that cannot be verified by unit
// tests: its whole job is I/O. This script exercises it against a real
// repository.
//
//   dotnet build TimeEntry.sln
//   GITHUB_TOKEN=... dotnet fsi tools/verify-github-transport.fsx [branch]
//
// The read path runs anywhere the token can read. The write path additionally
// needs a token permitted to POST to the Git Data API; it is probed for and
// skipped with a stated reason rather than failing, so a read-only run still
// reports something honest.
//
// In the agent sandbox this script's read checks pass and the write checks
// skip: the proxy answers 403 "Write access to this GitHub API path is not
// permitted through this proxy."

#load "../domain/TimeEntry.Semantic/Identifiers.fs"
#load "../domain/TimeEntry.Semantic/Duration.fs"
#load "../domain/TimeEntry.Semantic/Values.fs"
#load "../domain/TimeEntry.Semantic/EntryState.fs"
#load "../domain/TimeEntry.Semantic/Capabilities.fs"
#load "../domain/TimeEntry.Transitions/Commands.fs"
#load "../domain/TimeEntry.Transitions/Effects.fs"
#load "../domain/TimeEntry.Transitions/Transitions.fs"
#load "../domain/TimeEntry.Persistence/Documents.fs"
#load "../domain/TimeEntry.Persistence/Layout.fs"
#load "../domain/TimeEntry.Persistence/Mapping.fs"
#load "../domain/TimeEntry.Persistence/Serialization.fs"
#load "../domain/TimeEntry.GitHub/Store.fs"
#load "../domain/TimeEntry.GitHub/HttpProtocol.fs"
#load "../domain/TimeEntry.GitHub/Interpreter.fs"
#load "../domain/TimeEntry.GitHub/HttpStore.fs"

open System
open System.Net.Http
open TimeEntry.GitHub
open TimeEntry.GitHub.Store

let branch =
    match fsi.CommandLineArgs |> Array.tryItem 1 with
    | Some value -> value
    | None -> "main"

let token =
    match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
    | null
    | "" -> failwith "GITHUB_TOKEN is required"
    | value -> value

let target: HttpProtocol.RepositoryRef =
    { Owner = "kemiller2002"
      Repository = "time-tracking-application"
      Branch = branch }

let store = HttpStore.create (HttpStore.configure (new HttpClient()) token) target

let mutable failures = 0
let mutable skipped = 0

let check name ok detail =
    if ok then
        printfn "  PASS  %-46s %s" name detail
    else
        failures <- failures + 1
        printfn "  FAIL  %-46s %s" name detail

let skip name reason =
    skipped <- skipped + 1
    printfn "  SKIP  %-46s %s" name reason

printfn "GitHub transport verification — %s/%s @ %s" target.Owner target.Repository branch
printfn ""
printfn "read path"

match store.ReadHead() |> Async.RunSynchronously with
| Ok sha -> check "ReadHead returns a 40-char sha" (sha.Length = 40) sha
| Error e -> check "ReadHead" false (sprintf "%A" e)

match store.ListEntryPaths() |> Async.RunSynchronously with
| Ok paths ->
    // An empty ledger is a correct answer, not an error.
    check "ListEntryPaths reads the recursive tree" true (sprintf "%d entry paths" (List.length paths))

    check
        "every listed path is an entry path"
        (paths |> List.forall TimeEntry.Persistence.Layout.isEntryPath)
        "Layout filter applied"
| Error e -> check "ListEntryPaths" false (sprintf "%A" e)

match store.ReadFile "README.md" |> Async.RunSynchronously with
| Ok(Some file) ->
    check "ReadFile decodes base64 content" (file.Content.Length > 0) (sprintf "%d bytes" file.Content.Length)
    check "ReadFile reports a blob sha" (file.Sha.Length = 40) file.Sha
| Ok None -> check "ReadFile existing" false "returned None for a file that exists"
| Error e -> check "ReadFile existing" false (sprintf "%A" e)

// 404 must be Ok None: "this entry does not exist yet" is a legitimate
// answer, and treating it as a failure would break entry creation.
match store.ReadFile "ledger/entries/zz/definitely-absent.json" |> Async.RunSynchronously with
| Ok None -> check "ReadFile maps 404 to Ok None" true "absent file"
| Ok(Some _) -> check "ReadFile missing" false "returned content for an absent file"
| Error e -> check "ReadFile missing" false (sprintf "expected Ok None, got %A" e)

printfn ""
printfn "write path"

// An impossible expected head. This is rejected by the interpreter's OWN
// client-side head check, before any write is attempted — so it verifies that
// guard and nothing about GitHub. Labelled accordingly: an earlier version of
// this script reported it as "ref compare-and-swap enforced", which
// overclaimed, because no request ever left the process.
match
    store.Commit
        { Message = "must not land"
          Writes = []
          ExpectedHeadSha = "0000000000000000000000000000000000000000" }
    |> Async.RunSynchronously
with
| Error(HeadMoved _) ->
    check "client-side head guard rejects a stale head" true "no write attempted"
| Ok _ -> check "client-side head guard" false "accepted an impossible head"
| Error e -> check "client-side head guard" false (sprintf "%A" e)

// Whether writes are permitted at all. Creating a blob is the safest possible
// probe: a blob referenced by no tree is unreachable and garbage-collected,
// so this changes no branch and no file.
let blobProbe =
    async {
        use request =
            new HttpRequestMessage(HttpMethod.Post, HttpProtocol.Url.blobs target)

        request.Content <-
            new StringContent(
                """{"content":"transport probe","encoding":"utf-8"}""",
                Text.Encoding.UTF8,
                "application/json"
            )

        let client = HttpStore.configure (new HttpClient()) token
        let! response = client.SendAsync request |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return int response.StatusCode, body
    }
    |> Async.RunSynchronously

match blobProbe with
| status, _ when status >= 200 && status < 300 ->
    check "blob creation is permitted" true (sprintf "HTTP %d" status)
    printfn ""
    printfn "  NOTE: writes are permitted here. The full write path — tree, commit,"
    printfn "        ref compare-and-swap, and the per-file precondition checks —"
    printfn "        is still NOT exercised by this script, because doing so lands"
    printfn "        real commits. Run it against a scratch branch to cover that."
| status, body ->
    let detail = body.Replace("\n", " ")
    skip "write path" (sprintf "HTTP %d: %s" status (detail.Substring(0, min 70 detail.Length)))

printfn ""
printfn "%d failed, %d skipped" failures skipped
exit failures
