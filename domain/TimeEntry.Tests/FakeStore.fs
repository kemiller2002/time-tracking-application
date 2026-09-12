/// A store double for the effect interpreter.
///
/// Extracted from the test suite because it is infrastructure, not a test: it
/// stands in for GitHub, and keeping it separate lets each suite read as
/// behaviour rather than setup.
module TimeEntry.Tests.FakeStore

open TimeEntry.GitHub.Store

// ---------------------------------------------------------------------------
// In-memory store
// ---------------------------------------------------------------------------

/// Content-addressed, like a real blob SHA, so a SHA changes exactly when the
/// content does. A counter would let a stale-version test pass by accident.
let shaOf (content: string) =
    use sha = System.Security.Cryptography.SHA1.Create()

    content
    |> System.Text.Encoding.UTF8.GetBytes
    |> sha.ComputeHash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

type private StoreState =
    { Files: Map<string, StoredFile>
      Head: string
      Commits: int }

/// A store double.
///
/// Mutation is confined to one ref cell: it stands in for an external mutable
/// system, which is the one place a functional design legitimately has some.
/// `failWith` lets a test inject a transport-level failure.
type Fake(initial: (string * string) list, ?failWith: StoreError) =
    /// Fires inside `Commit`, before preconditions are checked. This is the
    /// only way to simulate the real race: the interpreter reads HEAD and then
    /// commits, so a store double that can only be disturbed *between* calls
    /// can never exercise the ref compare-and-swap.
    let mutable onBeforeCommit: (unit -> unit) option = None

    /// What the store reports as the server's clock. `None` until a test says
    /// otherwise, which is the honest default: a store nobody has talked to
    /// has heard no `Date` header.
    let mutable observedServerTimeMs: int64 option = None

    let state =
        ref
            { Files =
                initial
                |> List.map (fun (path, content) ->
                    path,
                    { Path = path
                      Content = content
                      Sha = shaOf content })
                |> Map.ofList
              Head = "head-0"
              Commits = 0 }

    member _.Head = state.Value.Head
    member _.CommitCount = state.Value.Commits
    member _.Paths = state.Value.Files |> Map.toList |> List.map fst

    member _.ShaOf(path: string) =
        state.Value.Files |> Map.tryFind path |> Option.map (fun f -> f.Sha)

    /// Simulate someone else changing a file, which is how a stale version
    /// arises in practice.
    member _.ChangeBehindOurBack(path: string, content: string) =
        let current = state.Value

        state.Value <-
            { current with
                Files =
                    current.Files
                    |> Map.add
                        path
                        { Path = path
                          Content = content
                          Sha = shaOf content }
                Head = "head-moved" }

    member _.InterfereDuringCommit(action: unit -> unit) = onBeforeCommit <- Some action

    /// Stand in for a `Date` header the real transport would have recorded.
    member _.ServerSaysItIs(epochMilliseconds: int64) =
        observedServerTimeMs <- Some epochMilliseconds

    member this.Store: GitHubStore =
        let fail () =
            match failWith with
            | Some error -> Some(Error error)
            | None -> None

        { ReadFile =
            fun path ->
                async {
                    return
                        match fail () with
                        | Some error -> error
                        | None -> Ok(state.Value.Files |> Map.tryFind path)
                }
          ListEntryPaths =
            fun () ->
                async {
                    return
                        match fail () with
                        | Some error -> error
                        | None -> Ok(state.Value.Files |> Map.toList |> List.map fst)
                }
          ObservedServerTimeMs = fun () -> observedServerTimeMs
          ReadHead =
            fun () ->
                async {
                    return
                        match fail () with
                        | Some error -> error
                        | None -> Ok state.Value.Head
                }
          Commit =
            fun request ->
                async {
                    onBeforeCommit |> Option.iter (fun act -> act ())
                    let current = state.Value

                    match fail () with
                    | Some error -> return error
                    | None ->

                    if request.ExpectedHeadSha <> current.Head then
                        return Error(HeadMoved(request.ExpectedHeadSha, current.Head))
                    else
                        // Check every precondition BEFORE applying anything, so
                        // the commit is all-or-nothing exactly as the Git Data
                        // API's tree-plus-ref update is.
                        let violation =
                            request.Writes
                            |> List.tryPick (fun write ->
                                let existing = current.Files |> Map.tryFind write.Path

                                match write.ExpectedSha, existing with
                                | Some expected, Some file when file.Sha = expected -> None
                                | None, None -> None
                                | expected, actual ->
                                    Some(
                                        PreconditionFailed(
                                            write.Path,
                                            expected,
                                            actual |> Option.map (fun f -> f.Sha)
                                        )
                                    ))

                        match violation with
                        | Some error -> return Error error
                        | None ->
                            let written =
                                request.Writes
                                |> List.map (fun write -> write.Path, shaOf write.Content)

                            let files =
                                request.Writes
                                |> List.fold
                                    (fun acc write ->
                                        acc
                                        |> Map.add
                                            write.Path
                                            { Path = write.Path
                                              Content = write.Content
                                              Sha = shaOf write.Content })
                                    current.Files

                            let head = sprintf "head-%d" (current.Commits + 1)

                            state.Value <-
                                { Files = files
                                  Head = head
                                  Commits = current.Commits + 1 }

                            return Ok { HeadSha = head; WrittenShas = written }
                } }

