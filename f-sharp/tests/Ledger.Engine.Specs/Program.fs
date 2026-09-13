open System
open System.Text.Json.Nodes
open Ledger.Domain
open Ledger.Engine
open Ledger.Engine.Protocol
open Ledger.Engine.Integration
open EchelonFoundry.Chrona.Integration

let assertTrue condition message = if not condition then failwith message

let reset () = Session.current <- Session.initial ()

let sendJson (json: string) : JsonObject = (JsonNode.Parse(Dispatch.handle json)).AsObject()
let viewOf (response: JsonObject) = response.["view"].AsObject()
let effectsOf (response: JsonObject) = response.["effects"].AsArray()
let stringView (response: JsonObject) (key: string) = (viewOf response).[key].GetValue<string>()
let boolView (response: JsonObject) (key: string) = (viewOf response).[key].GetValue<bool>()
let itemsView (response: JsonObject) (key: string) = (viewOf response).[key].AsArray()

let eventMessage (name: string) (key: string option) (value: string option) =
    let o = JsonObject()
    o.["kind"] <- JsonValue.Create("Event")
    let eventObj = JsonObject()
    eventObj.["name"] <- JsonValue.Create(name)
    key |> Option.iter (fun k -> eventObj.["key"] <- JsonValue.Create(k: string))
    value |> Option.iter (fun v -> eventObj.["value"] <- JsonValue.Create(v: string))
    o.["event"] <- eventObj
    o.ToJsonString()

let initializeMessage =
    let o = JsonObject()
    o.["kind"] <- JsonValue.Create("Initialize")
    o.["protocolVersion"] <- JsonValue.Create(1)
    o.["capabilities"] <- JsonArray(JsonValue.Create("Storage"))
    o.ToJsonString()

let private storageResult (correlationId: string) (outcomeKind: string) (value: string option) (reason: string option) =
    let o = JsonObject()
    o.["kind"] <- JsonValue.Create("EffectResult")
    let result = JsonObject()
    result.["correlationId"] <- JsonValue.Create(correlationId)
    result.["kind"] <- JsonValue.Create("StorageResult")
    let outcome = JsonObject()
    outcome.["kind"] <- JsonValue.Create(outcomeKind)
    value |> Option.iter (fun v -> outcome.["value"] <- JsonValue.Create(v: string))
    reason |> Option.iter (fun r -> outcome.["reason"] <- JsonValue.Create(r: string))
    result.["outcome"] <- outcome
    o.["result"] <- result
    o.ToJsonString()

let loadSuccess value = storageResult "load" "Success" value None
let loadFailure reason = storageResult "load" "Failure" None (Some reason)
let saveFailure reason = storageResult "save" "Failure" None (Some reason)
let saveUnknown reason = storageResult "save" "Unknown" None (Some reason)

let private httpResult (correlationId: string) (outcomeKind: string) (status: int option) (body: string option) (reason: string option) =
    let o = JsonObject()
    o.["kind"] <- JsonValue.Create("EffectResult")
    let result = JsonObject()
    result.["correlationId"] <- JsonValue.Create(correlationId)
    result.["kind"] <- JsonValue.Create("HttpResult")
    let outcome = JsonObject()
    outcome.["kind"] <- JsonValue.Create(outcomeKind)
    status |> Option.iter (fun s -> outcome.["status"] <- JsonValue.Create(s: int))
    body |> Option.iter (fun b -> outcome.["body"] <- JsonValue.Create(b: string))
    reason |> Option.iter (fun r -> outcome.["reason"] <- JsonValue.Create(r: string))
    result.["outcome"] <- outcome
    o.["result"] <- result
    o.ToJsonString()

let githubPullSuccess status body = httpResult "github-pull" "Success" (Some status) body None
let githubPushSuccess status body = httpResult "github-push" "Success" (Some status) body None
let githubPushUnknown reason = httpResult "github-push" "Unknown" None None (Some reason)

let toBase64 (text: string) = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes text)

/// Shaped like GitHub's real Contents API GET response — only `sha` and
/// `content` are read by GitHubSync.parseGetResponse, but real responses
/// carry more fields, so extras are included to guard against over-narrow parsing.
let contentsGetBody sha documentJson =
    let o = JsonObject()
    o.["name"] <- JsonValue.Create("ledger.json")
    o.["sha"] <- JsonValue.Create(sha: string)
    o.["content"] <- JsonValue.Create(toBase64 documentJson)
    o.["encoding"] <- JsonValue.Create("base64")
    o.ToJsonString()

let contentsPutBody sha =
    let o = JsonObject()
    let content = JsonObject()
    content.["sha"] <- JsonValue.Create(sha: string)
    o.["content"] <- content
    let commit = JsonObject()
    commit.["sha"] <- JsonValue.Create("commit-sha")
    o.["commit"] <- commit
    o.ToJsonString()

let saveGitHubConfig owner repo folder branch token =
    sendJson (eventMessage "DraftGitHubOwnerChanged" None (Some(owner: string))) |> ignore
    sendJson (eventMessage "DraftGitHubRepoChanged" None (Some(repo: string))) |> ignore
    folder |> Option.iter (fun f -> sendJson (eventMessage "DraftGitHubFolderChanged" None (Some f)) |> ignore)
    branch |> Option.iter (fun b -> sendJson (eventMessage "DraftGitHubBranchChanged" None (Some b)) |> ignore)
    sendJson (eventMessage "DraftGitHubTokenChanged" None (Some(token: string))) |> ignore
    sendJson (eventMessage "SaveGitHubConfig" None None)

let whoAmIBody (login: string) (name: string option) =
    let o = JsonObject()
    o.["login"] <- JsonValue.Create(login)
    o.["name"] <- (match name with Some n -> JsonValue.Create(n) :> JsonNode | None -> null)
    o.ToJsonString()

let githubWhoAmISuccess login name = httpResult "github-whoami" "Success" (Some 200) (Some(whoAmIBody login name)) None
let resolveIdentity login name = sendJson (githubWhoAmISuccess login name)

/// Shaped like a `GET git/refs/heads/{branch}` response — only `object.sha`
/// (the commit the ref points at) is read by `GitHubSync.parseRefResponse`.
let refBody (commitSha: string) =
    let o = JsonObject()
    let object = JsonObject()
    object.["sha"] <- JsonValue.Create(commitSha)
    object.["type"] <- JsonValue.Create("commit")
    o.["ref"] <- JsonValue.Create("refs/heads/main")
    o.["object"] <- object
    o.ToJsonString()

/// Shaped like a `GET git/commits/{sha}` response — only `tree.sha` is read
/// by `GitHubSync.parseCommitBaseTreeResponse`.
let commitGetBody (treeSha: string) =
    let o = JsonObject()
    let tree = JsonObject()
    tree.["sha"] <- JsonValue.Create(treeSha)
    o.["tree"] <- tree
    o.ToJsonString()

/// Shaped like a `POST git/trees` or `POST git/commits` response — both
/// carry the new object's id in a bare top-level `sha`, read by
/// `GitHubSync.parseShaResponse`.
let shaBody (sha: string) =
    let o = JsonObject()
    o.["sha"] <- JsonValue.Create(sha)
    o.ToJsonString()

/// Shaped like a `PATCH git/refs/{ref}` response — the atomic commit
/// chain's terminal step, read by `GitHubSync.parsePutResponse`'s
/// `object.sha` fallback.
let refUpdateBody (newCommitSha: string) =
    let o = JsonObject()
    let object = JsonObject()
    object.["sha"] <- JsonValue.Create(newCommitSha)
    o.["ref"] <- JsonValue.Create("refs/heads/main")
    o.["object"] <- object
    o.ToJsonString()

/// Saves settings and resolves identity in one go — the state every
/// pull/push-capable test needs before it can proceed, since `handle`
/// now blocks Pull/Push until `SaveGitHubConfig`'s auto-triggered
/// "github-whoami" round trip has resolved a `Login`.
let configureGitHub owner repo folder branch token login name =
    saveGitHubConfig owner repo folder branch token |> ignore
    resolveIdentity login name

let todayAt hour minute =
    let today = DateTime.UtcNow.Date
    DateTimeOffset(today.AddHours(float hour).AddMinutes(float minute), TimeSpan.Zero).ToString "O"

let fillCreateDraft startedAt endedAt =
    sendJson (eventMessage "DraftActivityTypeChanged" None (Some "not-defined")) |> ignore
    sendJson (eventMessage "DraftProjectChanged" None (Some "not-defined")) |> ignore
    sendJson (eventMessage "DraftDescriptionChanged" None (Some "Write tests")) |> ignore
    sendJson (eventMessage "DraftBusinessPurposeChanged" None (Some "Coverage")) |> ignore
    sendJson (eventMessage "DraftStartedAtChanged" None (Some startedAt)) |> ignore
    sendJson (eventMessage "DraftEndedAtChanged" None (Some endedAt)) |> ignore

let createTodayActivity startHour endHour =
    fillCreateDraft (todayAt startHour 0) (todayAt endHour 0)
    sendJson (eventMessage "CreateActivity" None None)

let seedDocument () : LedgerDocument =
    let environment = (Session.initial ()).Environment
    let command : CreateActivityCommand =
        { ActivityTypeId = "not-defined"; ProjectId = "not-defined"; Description = "Preloaded"; BusinessPurpose = "Preloaded purpose"
          Outcome = ""; TagIds = []; EntryMethod = Manual; ReconstructionReason = None
          StartedAt = DateTimeOffset.Parse(todayAt 8 0); EndedAt = DateTimeOffset.Parse(todayAt 9 0); ClientTimestamp = None }
    match Commands.create environment LedgerDocument.empty command with
    | Success(document, _) -> document
    | result -> failwith $"seed failed: {result}"

// --- A test-only in-memory simulation of the create-only GitHub -----------
// --- semantics buildCandidatePutEffect/buildReceiptPutEffect rely on -------
// --- (specification §14/§34's partial-failure-recovery proofs; CHR-INT-014).
//
// This is deliberately test-only, not production code: CHR-INT-015 (WASM
// startup wiring, still deferred) has to model this as an explicit
// multi-step effect chain — like GitHubSync.fs's existing atomic-commit
// chain — because the real WASM boundary allows only one HTTP effect in
// flight at a time per `Dispatch.handle` call, not an arbitrary in-process
// loop like the one below. What this harness proves is that
// `ObservationReconciliation.decide`'s pure decision, driven through a
// realistic multi-run read/write sequence with injected write failures,
// converges to exactly one candidate and one receipt per observation no
// matter how many times it runs or in what order failures land.
type private FakeStore() =
    let files = System.Collections.Generic.Dictionary<string, string>()
    let mutable failNextWriteTo: string option = None

    member _.ArmFailure(path: string) = failNextWriteTo <- Some path
    member _.TryRead(path: string) = match files.TryGetValue path with true, v -> Some v | false, _ -> None
    member _.Contains(path: string) = files.ContainsKey path

    /// Mirrors `buildCandidatePutEffect`/`buildReceiptPutEffect`'s create-
    /// only semantics: succeeds only if nothing exists there yet, unless a
    /// deliberately-armed failure consumes this exact write instead
    /// (simulating a network failure or a lost response — never a
    /// silent overwrite of someone else's write).
    member _.TryCreate(path: string, content: string) =
        if failNextWriteTo = Some path then
            failNextWriteTo <- None
            false
        elif files.ContainsKey path then
            false
        else
            files.[path] <- content
            true

let private candidateKey (observationId: string) = $"candidate:{observationId}"
let private receiptKey (observationId: string) = $"receipt:{observationId}"

/// One simulated reconciliation pass over a batch of raw observation
/// payloads — read what's already there, decide, write candidate-then-
/// receipt in that order (specification §22's critical write ordering:
/// never write a receipt before its candidate exists). Returns nothing;
/// callers assert against `store`'s resulting contents.
let private runReconciliationPass (store: FakeStore) (environment: Environment) (rawObservations: string list) =
    for rawJson in rawObservations do
        match TimeObservation.deserialize rawJson with
        | Error _ -> ()
        | Ok observation ->
            let cKey = candidateKey observation.ObservationId
            let rKey = receiptKey observation.ObservationId
            let existingReceipt = store.Contains rKey
            let existingCandidate =
                store.TryRead cKey |> Option.bind (fun j -> match TimeCandidate.deserialize j with Ok c -> Some c | Error _ -> None)
            let writeReceipt (candidateId: string) =
                let receipt: ProcessingReceipt =
                    { ReceiptVersion = ProcessingReceipt.CurrentReceiptVersion; ObservationId = observation.ObservationId
                      ProcessedAt = DateTimeOffset.UtcNow; Result = CandidateCreated candidateId }
                store.TryCreate(rKey, ProcessingReceipt.serialize receipt) |> ignore
            match ObservationReconciliation.decide environment existingReceipt existingCandidate observation with
            | ObservationReconciliation.AlreadyProcessed -> ()
            | ObservationReconciliation.ReceiptRepairNeeded candidate -> writeReceipt candidate.CandidateId
            | ObservationReconciliation.CandidateCreated candidate ->
                // Critical ordering (§22): the receipt is only ever attempted
                // once the candidate write itself has actually succeeded.
                if store.TryCreate(cKey, TimeCandidate.serialize candidate) then writeReceipt candidate.CandidateId
            | ObservationReconciliation.Rejected reason ->
                let receipt: ProcessingReceipt =
                    { ReceiptVersion = ProcessingReceipt.CurrentReceiptVersion; ObservationId = observation.ObservationId
                      ProcessedAt = DateTimeOffset.UtcNow; Result = Rejected reason }
                store.TryCreate(rKey, ProcessingReceipt.serialize receipt) |> ignore

let private rawObservation (observationId: string) (projectId: string) =
    $"""{{"contractVersion":"1","observationId":"{observationId}","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"{projectId}","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[]}}"""

/// Shaped like GitHub's real Contents API directory listing (a bare JSON
/// array, unlike every single-file GET this file already builds via
/// `contentsGetBody`) — see `GitHubSync.parseObservationListing`.
let private observationsListingBody (entries: (string * string) list) =
    let arr = JsonArray()
    entries
    |> List.iter (fun (name, path) ->
        let o = JsonObject()
        o.["name"] <- JsonValue.Create(name: string)
        o.["path"] <- JsonValue.Create(path: string)
        o.["type"] <- JsonValue.Create("file")
        arr.Add(o))
    arr.ToJsonString()

let private decodedPutContent (effect: JsonObject) : string =
    Text.Encoding.UTF8.GetString(Convert.FromBase64String((JsonNode.Parse(effect.["body"].GetValue<string>()).AsObject().["content"]).GetValue<string>()))

let private onlyHttpEffect (response: JsonObject) : JsonObject =
    effectsOf response |> Seq.map (fun e -> e.AsObject()) |> Seq.filter (fun e -> e.["kind"].GetValue<string>() = "Http") |> Seq.exactlyOne

let private referenceJsonWithProjects (projectIds: string list) =
    let projects = projectIds |> List.map (fun id -> $"""{{"id":"{id}","name":"{id}","active":true}}""") |> String.concat ","
    $"""{{"projects":[{projects}],"activityTypes":[],"tags":[]}}"""

/// Resolves identity and moves straight through the reference pull, so
/// CHR-INT-015's reconciliation is seeded and its first request (the
/// first project's inbox listing) is already sitting in the returned
/// response's effects.
let private beginReconciliationWithProjects (projectIds: string list) : JsonObject =
    configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller")
    |> ignore
    sendJson (httpResult "github-reference-pull" "Success" (Some 200) (Some(contentsGetBody "reference-sha" (referenceJsonWithProjects projectIds))) None)

let tests : (string * (unit -> unit)) list =
    [
      "Initialize requests a Storage load for the ledger and one for cached GitHub sync settings", fun () ->
        reset ()
        let response = sendJson initializeMessage
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 2) $"expected exactly two effects, got {effects.Length}"
        assertTrue (effects |> List.forall (fun e -> e.["kind"].GetValue<string>() = "Storage" && e.["operation"].GetValue<string>() = "get")) "Initialize did not request only Storage gets"
        let keys = effects |> List.map (fun e -> e.["key"].GetValue<string>()) |> Set.ofList
        assertTrue (keys = Set.ofList [ "business-activity-ledger:v1"; "business-activity-ledger:github-config:v1" ]) $"unexpected storage keys requested: {keys}"

      "accumulated draft fields + CreateActivity produce a Recorded row in dayActivities", fun () ->
        reset ()
        let response = createTodayActivity 9 10
        let activities = itemsView response "dayActivities"
        assertTrue (activities.Count = 1) $"expected one activity, got {activities.Count}"
        assertTrue (stringView response "createError" = "") "unexpected create error"
        let effects = effectsOf response
        assertTrue (effects.Count = 1 && effects.[0].AsObject().["operation"].GetValue<string>() = "set") "create did not request a Storage save"

      "CreateActivity with missing fields surfaces createError and does not save", fun () ->
        reset ()
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "not-defined")) |> ignore
        let response = sendJson (eventMessage "CreateActivity" None None)
        assertTrue (stringView response "createError" <> "") "missing-field create did not surface an error"
        assertTrue ((itemsView response "dayActivities").Count = 0) "an activity was recorded despite missing fields"

      "a Storage load with prior JSON restores activities into the view", fun () ->
        reset ()
        let document = seedDocument ()
        let json = DocumentCodec.encode document
        sendJson initializeMessage |> ignore
        let response = sendJson (loadSuccess (Some json))
        let activities = itemsView response "dayActivities"
        assertTrue (activities.Count = 1) $"expected the preloaded activity to appear, got {activities.Count}"
        assertTrue (stringView response "persistenceError" = "") "load surfaced an unexpected persistence error"

      "a Storage load with no prior value leaves the document empty without error", fun () ->
        reset ()
        sendJson initializeMessage |> ignore
        let response = sendJson (loadSuccess None)
        assertTrue ((itemsView response "dayActivities").Count = 0) "unexpected activities on first visit"
        assertTrue (stringView response "persistenceError" = "") "first visit surfaced a persistence error"

      "corrupted stored JSON surfaces persistenceError without crashing", fun () ->
        reset ()
        sendJson initializeMessage |> ignore
        let response = sendJson (loadSuccess (Some "not valid json"))
        assertTrue (stringView response "persistenceError" <> "") "corrupted storage did not surface a persistence error"

      "a Storage load failure surfaces persistenceError", fun () ->
        reset ()
        sendJson initializeMessage |> ignore
        let response = sendJson (loadFailure "network error")
        assertTrue (stringView response "persistenceError" <> "") "load failure did not surface a persistence error"

      "a Storage save failure surfaces persistenceError, not silent loss", fun () ->
        reset ()
        createTodayActivity 9 10 |> ignore
        let response = sendJson (saveFailure "quota exceeded")
        assertTrue (stringView response "persistenceError" <> "") "save failure did not surface a persistence error"

      "an unknown save outcome surfaces persistenceError rather than being treated as success or silently lost", fun () ->
        reset ()
        createTodayActivity 9 10 |> ignore
        let response = sendJson (saveUnknown "connection dropped before a response arrived")
        assertTrue (stringView response "persistenceError" <> "") "unknown save outcome was not surfaced"

      "SplitActivity dispatch produces replacement rows and marks the source Superseded", fun () ->
        reset ()
        let createResponse = createTodayActivity 9 11
        let activityId = (itemsView createResponse "dayActivities").[0].AsObject().["id"].GetValue<string>()
        sendJson (eventMessage "DraftReasonChanged" None (Some "split for billing")) |> ignore
        sendJson (eventMessage "DraftSplitPartDurationChanged" (Some "0") (Some "60")) |> ignore
        sendJson (eventMessage "DraftSplitPartDurationChanged" (Some "1") (Some "60")) |> ignore
        let response = sendJson (eventMessage "SplitActivity" (Some activityId) None)
        let splitError = stringView response "splitError"
        assertTrue (splitError = "") $"split was rejected: {splitError}"
        let activities = itemsView response "dayActivities"
        assertTrue (activities.Count = 2) $"expected two replacement activities, got {activities.Count}"
        assertTrue (activities |> Seq.forall (fun a -> a.AsObject().["statusLabel"].GetValue<string>() = "Recorded")) "a replacement was not Recorded"

      "MergeActivities dispatch on toggled contiguous sources produces one merged row", fun () ->
        reset ()
        let first = createTodayActivity 9 10
        let firstId = (itemsView first "dayActivities").[0].AsObject().["id"].GetValue<string>()
        fillCreateDraft (todayAt 10 0) (todayAt 11 0) |> ignore
        let second = sendJson (eventMessage "CreateActivity" None None)
        let secondId = (itemsView second "dayActivities") |> Seq.find (fun a -> a.AsObject().["id"].GetValue<string>() <> firstId) |> fun a -> a.AsObject().["id"].GetValue<string>()
        sendJson (eventMessage "ToggleMergeSource" (Some firstId) (Some "on")) |> ignore
        sendJson (eventMessage "ToggleMergeSource" (Some secondId) (Some "on")) |> ignore
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "DraftProjectChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "DraftDescriptionChanged" None (Some "Merged work")) |> ignore
        sendJson (eventMessage "DraftBusinessPurposeChanged" None (Some "Coverage")) |> ignore
        sendJson (eventMessage "DraftReasonChanged" None (Some "combine")) |> ignore
        let response = sendJson (eventMessage "MergeActivities" None None)
        let mergeError = stringView response "mergeError"
        assertTrue (mergeError = "") $"merge was rejected: {mergeError}"
        assertTrue ((itemsView response "dayActivities").Count = 1) "merge did not collapse to a single row"

      "a timer stopped almost immediately is discarded and records nothing", fun () ->
        reset ()
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "DraftProjectChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "StartTimer" None None) |> ignore
        let response = sendJson (eventMessage "StopTimer" None None)
        assertTrue (stringView response "timerPhase" = "none") "timer did not clear after stopping"
        assertTrue ((itemsView response "dayActivities").Count = 0) "an immediately-stopped timer recorded an activity"

      /// dom-bindings.js dispatches this every six seconds while a timer
      /// runs, purely so the elapsed-time label keeps reading a fresh clock value
      /// instead of sitting frozen between real events (see Dispatch.fs's
      /// "Tick" case). Must be a true no-op: no document change, no effect,
      /// timer state untouched.
      "Tick while a timer is running is a true no-op — no document change, no effects, timer keeps running", fun () ->
        reset ()
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "DraftProjectChanged" None (Some "not-defined")) |> ignore
        sendJson (eventMessage "StartTimer" None None) |> ignore
        let response = sendJson (eventMessage "Tick" None None)
        assertTrue (stringView response "timerPhase" = "running") "a Tick changed the timer's phase"
        assertTrue (effectsOf response |> Seq.isEmpty) "a Tick requested an effect — it must be a pure view refresh"

      "AttestDay then amending the same activity flags amended_after_review", fun () ->
        reset ()
        let created = createTodayActivity 9 10
        let activityId = (itemsView created "dayActivities").[0].AsObject().["id"].GetValue<string>()
        sendJson (eventMessage "DraftAttestationStatementChanged" None (Some "Reviewed and accurate")) |> ignore
        sendJson (eventMessage "AttestDay" None None) |> ignore
        sendJson (eventMessage "DraftDescriptionChanged" None (Some "changed after review")) |> ignore
        sendJson (eventMessage "DraftReasonChanged" None (Some "correction")) |> ignore
        let response = sendJson (eventMessage "AmendActivity" (Some activityId) None)
        let amendError = stringView response "amendError"
        assertTrue (amendError = "") $"amend was rejected: {amendError}"
        let warnings = itemsView response "dayReviewWarnings"
        assertTrue (warnings.Count = 1 && warnings.[0].AsObject().["activityId"].GetValue<string>() = activityId) "amended-after-review was not flagged"

      "SelectReportFormat + ViewDay produces non-empty reportContent for json/markdown/csv", fun () ->
        reset ()
        createTodayActivity 9 10 |> ignore
        for format in [ "json"; "markdown"; "csv" ] do
            sendJson (eventMessage "SelectReportFormat" None (Some format)) |> ignore
            let response = sendJson (eventMessage "ViewDay" None (Some((DateOnly.FromDateTime DateTime.UtcNow).ToString "yyyy-MM-dd")))
            assertTrue (stringView response "reportContent" <> "") $"{format} report content was empty"

      // --- GitHub sync ---------------------------------------------------------

      "SaveGitHubConfig with all fields configures sync, defaults an omitted branch to main, and auto-triggers identity lookup", fun () ->
        reset ()
        let response = saveGitHubConfig "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token"
        assertTrue (stringView response "githubConfigError" = "") "unexpected githubConfig error"
        assertTrue (boolView response "gitHubSyncConfigured") "sync was not marked configured"
        assertTrue (not (boolView response "gitHubSyncIdentified")) "sync was marked identified before the whoami round trip completed"
        assertTrue (stringView response "gitHubSyncStatus" = "identifying") "status was not 'identifying' right after save"
        assertTrue (stringView response "gitHubSyncOwner" = "kemiller2002") "owner was not stored"
        assertTrue (stringView response "gitHubSyncRepo" = "ledger-data") "repo was not stored"
        assertTrue (stringView response "gitHubSyncFolder" = "time-entries") "folder was not stored"
        assertTrue (stringView response "gitHubSyncBranch" = "main") "an omitted branch did not default to main"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 2) $"expected the identity lookup plus a config cache-save, got {effects.Length}"
        let whoami = effects |> List.find (fun e -> e.["kind"].GetValue<string>() = "Http")
        assertTrue (whoami.["method"].GetValue<string>() = "GET" && (whoami.["url"].GetValue<string>()).EndsWith "/user")
            "SaveGitHubConfig did not request a GET to GitHub's /user endpoint"
        let cacheSave = effects |> List.find (fun e -> e.["kind"].GetValue<string>() = "Storage")
        assertTrue (cacheSave.["operation"].GetValue<string>() = "set" && cacheSave.["key"].GetValue<string>() = "business-activity-ledger:github-config:v1")
            "SaveGitHubConfig did not cache the config to its own localStorage key"

      "SaveGitHubConfig with a missing field surfaces githubConfigError and leaves sync unconfigured", fun () ->
        reset ()
        sendJson (eventMessage "DraftGitHubOwnerChanged" None (Some "kemiller2002")) |> ignore
        let response = sendJson (eventMessage "SaveGitHubConfig" None None)
        assertTrue (stringView response "githubConfigError" <> "") "missing-field config save did not surface an error"
        assertTrue (not (boolView response "gitHubSyncConfigured")) "sync was marked configured despite missing fields"

      "SaveGitHubConfig with an omitted folder defaults to a dedicated folder, never the repo root", fun () ->
        reset ()
        let response = configureGitHub "kemiller2002" "ledger-data" None None "ghp_test_token" "kemiller2002" (Some "Kevin Miller")
        assertTrue (stringView response "githubConfigError" = "") "unexpected githubConfig error with an omitted folder"
        assertTrue (stringView response "gitHubSyncFolder" <> "") "an omitted folder left the stored folder blank"
        assertTrue (stringView response "gitHubSyncFilePath" <> "kemiller2002/ledger.json") "an omitted folder still placed the file directly under the repo root"
        assertTrue ((stringView response "gitHubSyncFilePath").EndsWith "/kemiller2002/ledger.json") "the default folder's file path was not folder- and person-scoped"

      "a successful identity lookup resolves the login/display name and unblocks the per-person file path", fun () ->
        reset ()
        saveGitHubConfig "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" |> ignore
        let response = resolveIdentity "kemiller2002" (Some "Kevin Miller")
        assertTrue (stringView response "githubSyncError" = "") "unexpected githubSync error on a successful identity lookup"
        assertTrue (boolView response "gitHubSyncIdentified") "sync was not marked identified after a successful whoami"
        assertTrue (stringView response "gitHubSyncLogin" = "kemiller2002") "the resolved login was not stored"
        assertTrue (stringView response "gitHubSyncDisplayName" = "Kevin Miller") "the resolved display name was not stored"
        assertTrue (stringView response "gitHubSyncFilePath" = "time-entries/kemiller2002/ledger.json") "the file path was not scoped to folder AND person"

      "PullFromGitHub before configuring sync surfaces githubSyncError and requests no effect", fun () ->
        reset ()
        let response = sendJson (eventMessage "PullFromGitHub" None None)
        assertTrue (stringView response "githubSyncError" <> "") "an unconfigured pull did not surface an error"
        assertTrue ((effectsOf response).Count = 0) "an unconfigured pull requested an effect anyway"

      "PullFromGitHub after saving but before identity resolves surfaces githubSyncError and requests no effect", fun () ->
        reset ()
        saveGitHubConfig "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" |> ignore
        let response = sendJson (eventMessage "PullFromGitHub" None None)
        assertTrue (stringView response "githubSyncError" <> "") "a pull requested before identity resolved did not surface an error"
        assertTrue ((effectsOf response).Count = 0) "a pull requested before identity resolved requested an effect anyway"

      "PullFromGitHub once identified requests a GET Http effect against the Contents API, scoped to folder and person, with an auth header", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (eventMessage "PullFromGitHub" None None)
        let effects = effectsOf response
        assertTrue (effects.Count = 1) $"expected exactly one effect, got {effects.Count}"
        let effect = effects.[0].AsObject()
        assertTrue (effect.["kind"].GetValue<string>() = "Http") "pull did not request an Http effect"
        assertTrue (effect.["method"].GetValue<string>() = "GET") "pull was not a GET"
        let url = effect.["url"].GetValue<string>()
        assertTrue (url.Contains "/repos/kemiller2002/ledger-data/contents/time-entries/kemiller2002/ledger.json") $"url missing expected owner/repo/folder/person/file structure: {url}"
        let headers = effect.["headers"].AsObject()
        assertTrue (headers.["Authorization"].GetValue<string>() = "Bearer ghp_test_token") "Authorization header was missing or wrong"

      "a successful GitHub pull (200) replaces the document, records the sha, and re-caches to Storage", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let document = seedDocument ()
        let response = sendJson (githubPullSuccess 200 (Some(contentsGetBody "abc123" (DocumentCodec.encode document))))
        assertTrue (stringView response "githubSyncError" = "") "unexpected githubSync error on a successful pull"
        assertTrue ((itemsView response "dayActivities").Count = 1) "the pulled document's activity did not appear"
        assertTrue (stringView response "gitHubSyncSha" = "abc123") "the pulled sha was not recorded"
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "sync status was not 'synced' after a successful pull"
        let effects = effectsOf response
        assertTrue (effects.Count = 1 && effects.[0].AsObject().["kind"].GetValue<string>() = "Storage" && effects.[0].AsObject().["operation"].GetValue<string>() = "set")
            "a successful pull did not re-cache the document to Storage"

      "a 404 GitHub pull is treated as 'nothing there yet', not a failure", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (githubPullSuccess 404 (Some """{"message":"Not Found"}"""))
        assertTrue (stringView response "gitHubSyncStatus" = "idle") "a 404 pull was not treated as idle/not-yet-existing"
        assertTrue (stringView response "githubSyncError" <> "") "a 404 pull gave no guidance to the user"
        assertTrue ((itemsView response "dayActivities").Count = 0) "a 404 pull fabricated an activity"

      "a mutating command kicks off the atomic commit chain once identified, alongside the usual Storage cache save", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = createTodayActivity 9 10
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 2) $"expected a Storage save and the commit chain's first step, got {effects.Length}"
        assertTrue (effects |> List.exists (fun e -> e.["kind"].GetValue<string>() = "Storage")) "the document was not cached locally"
        let refGet = effects |> List.find (fun e -> e.["kind"].GetValue<string>() = "Http")
        assertTrue
            (refGet.["correlationId"].GetValue<string>() = "github-commit-ref"
             && refGet.["method"].GetValue<string>() = "GET"
             && (refGet.["url"].GetValue<string>()).Contains "/git/refs/heads/main")
            "a mutating command did not start the atomic commit chain by reading the branch's ref"

      "the atomic commit chain reads the branch, builds a tree with both files, creates a commit, and moves the branch onto it", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore

        let step2 = sendJson (httpResult "github-commit-ref" "Success" (Some 200) (Some(refBody "parent-commit-sha")) None)
        let baseGet = effectsOf step2 |> Seq.map (fun e -> e.AsObject()) |> Seq.exactlyOne
        assertTrue
            (baseGet.["correlationId"].GetValue<string>() = "github-commit-base" && (baseGet.["url"].GetValue<string>()).Contains "/git/commits/parent-commit-sha")
            "reading the branch's ref did not fetch that commit's base tree"

        let step3 = sendJson (httpResult "github-commit-base" "Success" (Some 200) (Some(commitGetBody "base-tree-sha")) None)
        let treeCreate = effectsOf step3 |> Seq.map (fun e -> e.AsObject()) |> Seq.exactlyOne
        assertTrue
            (treeCreate.["correlationId"].GetValue<string>() = "github-commit-tree" && treeCreate.["method"].GetValue<string>() = "POST")
            "reading the base tree did not create a new tree"
        let treeBody = JsonNode.Parse(treeCreate.["body"].GetValue<string>()).AsObject()
        assertTrue (treeBody.["base_tree"].GetValue<string>() = "base-tree-sha") "the new tree was not built on the fetched base tree"
        let entries = treeBody.["tree"].AsArray() |> Seq.map (fun e -> e.["path"].GetValue<string>()) |> Set.ofSeq
        assertTrue (entries |> Set.exists (fun p -> p.EndsWith "ledger.json")) "the new tree did not include ledger.json"
        assertTrue (entries |> Set.exists (fun p -> p.EndsWith "metadata.json")) "the new tree did not include metadata.json"

        let step4 = sendJson (httpResult "github-commit-tree" "Success" (Some 201) (Some(shaBody "new-tree-sha")) None)
        let commitCreate = effectsOf step4 |> Seq.map (fun e -> e.AsObject()) |> Seq.exactlyOne
        assertTrue
            (commitCreate.["correlationId"].GetValue<string>() = "github-commit-create" && commitCreate.["method"].GetValue<string>() = "POST")
            "creating the new tree did not create a new commit"
        let commitCreateBody = JsonNode.Parse(commitCreate.["body"].GetValue<string>()).AsObject()
        assertTrue (commitCreateBody.["tree"].GetValue<string>() = "new-tree-sha") "the new commit did not reference the new tree"
        assertTrue
            ((commitCreateBody.["parents"].AsArray() |> Seq.head).GetValue<string>() = "parent-commit-sha")
            "the new commit's parent was not the branch's original commit"

        let step5 = sendJson (httpResult "github-commit-create" "Success" (Some 201) (Some(shaBody "new-commit-sha")) None)
        let refUpdate = effectsOf step5 |> Seq.map (fun e -> e.AsObject()) |> Seq.exactlyOne
        assertTrue
            (refUpdate.["correlationId"].GetValue<string>() = "github-push" && refUpdate.["method"].GetValue<string>() = "PATCH")
            "creating the new commit did not move the branch onto it"
        let refUpdateRequestBody = JsonNode.Parse(refUpdate.["body"].GetValue<string>()).AsObject()
        assertTrue (refUpdateRequestBody.["sha"].GetValue<string>() = "new-commit-sha") "the ref update did not target the newly-created commit"

        let final = sendJson (httpResult "github-push" "Success" (Some 200) (Some(refUpdateBody "new-commit-sha")) None)
        assertTrue (stringView final "gitHubSyncStatus" = "synced") "the completed commit chain did not mark the sync as synced"

      "a non-fast-forward ref update (422) is treated the same as a stale-sha conflict (409)", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushSuccess 422 (Some """{"message":"Update is not a fast forward"}"""))
        assertTrue (stringView response "gitHubSyncStatus" = "merging") "a 422 non-fast-forward ref update was not treated as a conflict to auto-resolve"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue
            (effects.Length = 1 && effects.[0].["correlationId"].GetValue<string>() = "github-push-conflict-pull")
            "a 422 conflict did not fetch the remote ledger to merge"

      "a successful GitHub push (201) records the new sha and marks status synced", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushSuccess 201 (Some(contentsPutBody "new-sha-1")))
        assertTrue (stringView response "githubSyncError" = "") "unexpected githubSync error on a successful push"
        assertTrue (stringView response "gitHubSyncSha" = "new-sha-1") "the new sha from a successful push was not recorded"
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "sync status was not 'synced' after a successful push"

      "a 409 GitHub push conflict fetches the remote ledger to merge instead of just erroring", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushSuccess 409 (Some """{"message":"sha does not match"}"""))
        assertTrue (stringView response "gitHubSyncStatus" = "merging") "a 409 push was not marked as merging while it resolves automatically"
        assertTrue (stringView response "githubSyncError" = "") "a 409 push should not surface an error while auto-merge is in flight"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue
            (effects.Length = 1 && effects.[0].["kind"].GetValue<string>() = "Http" && effects.[0].["correlationId"].GetValue<string>() = "github-push-conflict-pull")
            "a 409 push conflict did not fetch the remote ledger to merge"

      "a merge fetch after a push conflict combines local and remote activities and retries the push", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushSuccess 409 (Some """{"message":"sha does not match"}""")) |> ignore
        let remoteJson = DocumentCodec.encode (seedDocument ())
        let response = sendJson (httpResult "github-push-conflict-pull" "Success" (Some 200) (Some(contentsGetBody "remote-sha-1" remoteJson)) None)
        assertTrue (stringView response "gitHubSyncStatus" = "pushing") "a successful merge fetch did not move to retrying the push"
        let activities = itemsView response "dayActivities"
        assertTrue (activities.Count = 2) $"expected both the local and remote activities after merge, got {activities.Count}"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue
            (effects |> List.exists (fun e -> e.["kind"].GetValue<string>() = "Storage" && e.["key"].GetValue<string>() = "business-activity-ledger:v1"))
            "the merged document was not cached to Storage"
        assertTrue
            (effects |> List.exists (fun e -> e.["kind"].GetValue<string>() = "Http" && e.["correlationId"].GetValue<string>() = "github-commit-ref"))
            "the merge did not restart the atomic commit chain to retry the push"

      "a retried push after an automatic merge succeeding marks the sync as synced again", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushSuccess 409 (Some """{"message":"sha does not match"}""")) |> ignore
        let remoteJson = DocumentCodec.encode (seedDocument ())
        sendJson (httpResult "github-push-conflict-pull" "Success" (Some 200) (Some(contentsGetBody "remote-sha-1" remoteJson)) None) |> ignore
        let response = sendJson (githubPushSuccess 201 (Some(contentsPutBody "final-sha-1")))
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "the retried push after a merge did not resolve to synced"
        assertTrue (stringView response "githubSyncError" = "") "the retried push after a merge left a stale conflict error"

      "a merge fetch that fails falls back to asking the user to pull and resolve manually", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushSuccess 409 (Some """{"message":"sha does not match"}""")) |> ignore
        let response = sendJson (httpResult "github-push-conflict-pull" "Failure" None None (Some "network error"))
        assertTrue (stringView response "gitHubSyncStatus" = "conflict") "a failed merge fetch did not fall back to the conflict status"
        assertTrue (stringView response "githubSyncError" <> "") "a failed merge fetch did not surface guidance to resolve manually"

      /// The same "never collapse an unknown outcome" doctrine already
      /// covered for Storage saves (see above) applies to a GitHub push too:
      /// a dropped connection must not be read as either success or failure.
      "an unknown GitHub push outcome (e.g. a timeout) triggers automatic reconciliation rather than leaving the user to guess", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushUnknown "timed out after 15000ms")
        assertTrue (stringView response "gitHubSyncStatus" = "reconciling") "an unknown push outcome did not move to reconciling"
        assertTrue (stringView response "githubSyncError" = "") "reconciliation should not surface an error while it is still in flight"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue
            (effects.Length = 1 && effects.[0].["kind"].GetValue<string>() = "Http" && effects.[0].["correlationId"].GetValue<string>() = "github-push-reconcile-pull")
            "an unknown push outcome did not fetch the ledger to reconcile"

      "reconciliation classifies a remote document matching the attempt as Applied and marks the sync synced", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushUnknown "timed out after 15000ms") |> ignore
        let attemptedJson = DocumentCodec.encode Session.current.Document
        let response = sendJson (httpResult "github-push-reconcile-pull" "Success" (Some 200) (Some(contentsGetBody "confirmed-sha-1" attemptedJson)) None)
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "a remote document matching the attempt was not classified as Applied"
        assertTrue (stringView response "githubSyncError" = "") "an Applied reconciliation should not leave an error behind"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue effects.IsEmpty "an Applied reconciliation should not retry the push — nothing was lost"

      "reconciliation classifies a differing remote document as not (yet) applied, merges, and retries the push", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushUnknown "timed out after 15000ms") |> ignore
        let remoteJson = DocumentCodec.encode (seedDocument ())
        let response = sendJson (httpResult "github-push-reconcile-pull" "Success" (Some 200) (Some(contentsGetBody "remote-sha-2" remoteJson)) None)
        assertTrue (stringView response "gitHubSyncStatus" = "pushing") "a differing remote document did not move to retrying the push"
        let activities = itemsView response "dayActivities"
        assertTrue (activities.Count = 2) $"expected both the attempted and remote activities merged, got {activities.Count}"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue
            (effects |> List.exists (fun e -> e.["kind"].GetValue<string>() = "Http" && e.["correlationId"].GetValue<string>() = "github-commit-ref"))
            "reconciliation did not restart the atomic commit chain after merging"

      "a reconciliation fetch that itself comes back unknown stays unknown rather than guessing", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushUnknown "timed out after 15000ms") |> ignore
        let response = sendJson (httpResult "github-push-reconcile-pull" "Unknown" None None (Some "timed out after 15000ms"))
        assertTrue (stringView response "gitHubSyncStatus" = "unknown") "a reconciliation fetch that itself timed out should stay 'unknown', not guess"
        assertTrue (stringView response "githubSyncError" <> "") "a still-unknown reconciliation gave no guidance to the user"

      // --- Settings round trip (reportFormat, timezone) -----------------------

      "SaveSettings applies a timezone preference locally even without GitHub configured, and requests no effect", fun () ->
        reset ()
        sendJson (eventMessage "DraftTimezoneChanged" None (Some "America/New_York")) |> ignore
        let response = sendJson (eventMessage "SaveSettings" None None)
        assertTrue (stringView response "timezone" = "America/New_York") "the timezone preference was not applied locally"
        assertTrue ((effectsOf response).Count = 0) "an unconfigured SaveSettings requested an effect anyway"

      "SelectReportFormat once identified pushes a settings.json PUT containing the new format", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (eventMessage "SelectReportFormat" None (Some "markdown"))
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 1) $"expected exactly one effect (the settings push), got {effects.Length}"
        let push = effects.[0]
        assertTrue (push.["kind"].GetValue<string>() = "Http" && push.["method"].GetValue<string>() = "PUT" && (push.["url"].GetValue<string>()).Contains "settings.json")
            "SelectReportFormat did not PUT settings.json"
        let decoded = Text.Encoding.UTF8.GetString(Convert.FromBase64String((JsonNode.Parse(push.["body"].GetValue<string>()).AsObject().["content"]).GetValue<string>()))
        assertTrue (decoded.Contains "\"reportFormat\":\"markdown\"") $"settings push body did not contain the new report format: {decoded}"

      "SaveSettings once identified pushes a settings.json PUT containing the new timezone", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        sendJson (eventMessage "DraftTimezoneChanged" None (Some "America/New_York")) |> ignore
        let response = sendJson (eventMessage "SaveSettings" None None)
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 1 && effects.[0].["kind"].GetValue<string>() = "Http") "SaveSettings once identified did not push to GitHub"
        let decoded = Text.Encoding.UTF8.GetString(Convert.FromBase64String((JsonNode.Parse(effects.[0].["body"].GetValue<string>()).AsObject().["content"]).GetValue<string>()))
        assertTrue (decoded.Contains "\"timezone\":\"America/New_York\"") $"settings push body did not contain the new timezone: {decoded}"

      "a successful settings pull (200) applies reportFormat and timezone and records the sha", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let settingsJson = GitHubSync.buildSettingsJson "csv" (Some "Europe/London")
        let response = sendJson (httpResult "github-settings-pull" "Success" (Some 200) (Some(contentsGetBody "settings-sha-1" settingsJson)) None)
        assertTrue (stringView response "githubSettingsError" = "") "unexpected githubSettings error on a successful settings pull"
        assertTrue (stringView response "reportFormat" = "csv") "the pulled report format was not applied"
        assertTrue (stringView response "timezone" = "Europe/London") "the pulled timezone was not applied"

      "a 404 settings pull is treated as 'nothing saved yet', not a failure", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (httpResult "github-settings-pull" "Success" (Some 404) (Some """{"message":"Not Found"}""") None)
        assertTrue (stringView response "githubSettingsError" = "") "a 404 settings pull was treated as an error"

      // --- Reference data (projects/activity types/tags) round trip -----------

      "a successful reference pull (200) replaces the option lists with what reference.json declares", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let referenceJson = """{"projects":[{"id":"acme","name":"Acme Corp","active":true}],"activityTypes":[{"id":"design","name":"Design","active":true}],"tags":[{"id":"urgent","name":"Urgent","active":true}]}"""
        let response = sendJson (httpResult "github-reference-pull" "Success" (Some 200) (Some(contentsGetBody "reference-sha-1" referenceJson)) None)
        assertTrue (stringView response "githubReferenceError" = "") "unexpected githubReference error on a successful reference pull"
        let projectIds = itemsView response "projectOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        let activityTypeIds = itemsView response "activityTypeOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        let tagIds = itemsView response "tagOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        assertTrue (projectIds = [ "acme" ]) $"pulled reference data did not replace projectOptions, got {projectIds}"
        assertTrue (activityTypeIds = [ "design" ]) $"pulled reference data did not replace activityTypeOptions, got {activityTypeIds}"
        assertTrue (tagIds = [ "urgent" ]) $"pulled reference data did not replace tagOptions, got {tagIds}"

      "an inactive item in a pulled reference.json is excluded from the option lists", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let referenceJson = """{"projects":[{"id":"acme","name":"Acme Corp","active":true},{"id":"old-co","name":"Old Co","active":false}],"activityTypes":[],"tags":[]}"""
        let response = sendJson (httpResult "github-reference-pull" "Success" (Some 200) (Some(contentsGetBody "reference-sha-2" referenceJson)) None)
        let projectIds = itemsView response "projectOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        assertTrue (projectIds = [ "acme" ]) $"an inactive project leaked into projectOptions, got {projectIds}"

      "a 404 reference pull is treated as 'nothing published yet', not a failure, and keeps the fixture defaults", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (httpResult "github-reference-pull" "Success" (Some 404) (Some """{"message":"Not Found"}""") None)
        assertTrue (stringView response "githubReferenceError" = "") "a 404 reference pull was treated as an error"
        let projectIds = itemsView response "projectOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        assertTrue (List.contains "not-defined" projectIds) "a missing reference.json should leave the fixture's default projects in place"

      "resolving identity requests a reference pull alongside the settings and ledger pulls", fun () ->
        reset ()
        saveGitHubConfig "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" |> ignore
        let response = resolveIdentity "kemiller2002" (Some "Kevin Miller")
        let httpEffects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> Seq.filter (fun e -> e.["kind"].GetValue<string>() = "Http") |> List.ofSeq
        assertTrue (httpEffects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "reference.json"))
            "resolving identity did not also request the shared reference catalog"

      // --- Reference data admin (add/toggle Projects/Activity Types/Tags) -----

      "AddProject without GitHub sync configured applies locally and requests no effect", fun () ->
        reset ()
        sendJson (eventMessage "DraftNewProjectNameChanged" None (Some "Acme Corp")) |> ignore
        let response = sendJson (eventMessage "AddProject" None None)
        assertTrue (stringView response "adminError" = "") "unexpected adminError adding a project without GitHub sync configured"
        let projectIds = itemsView response "projectOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        assertTrue (List.contains "acme-corp" projectIds) $"the new project did not appear in projectOptions, got {projectIds}"
        assertTrue ((effectsOf response).Count = 0) "adding a project without GitHub sync configured requested an effect anyway"

      "AddProject once identified pushes an updated reference.json and clears the draft field", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        sendJson (eventMessage "DraftNewProjectNameChanged" None (Some "Acme Corp")) |> ignore
        let response = sendJson (eventMessage "AddProject" None None)
        assertTrue (stringView response "adminError" = "") "unexpected adminError adding a project once identified"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 1) $"expected exactly one effect (the reference.json push), got {effects.Length}"
        let push = effects.[0]
        assertTrue (push.["kind"].GetValue<string>() = "Http" && push.["method"].GetValue<string>() = "PUT" && (push.["url"].GetValue<string>()).Contains "reference.json")
            "AddProject once identified did not PUT reference.json"
        let decoded = Text.Encoding.UTF8.GetString(Convert.FromBase64String((JsonNode.Parse(push.["body"].GetValue<string>()).AsObject().["content"]).GetValue<string>()))
        assertTrue (decoded.Contains "\"name\":\"Acme Corp\"") $"reference.json push body did not contain the new project: {decoded}"

      "AddActivityType and AddTag with a blank name surface adminError and push nothing", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (eventMessage "AddActivityType" None None)
        assertTrue (stringView response "adminError" <> "") "a blank activity type name was not rejected"
        assertTrue ((effectsOf response).Count = 0) "a rejected AddActivityType still requested an effect"
        let tagResponse = sendJson (eventMessage "AddTag" None None)
        assertTrue (stringView tagResponse "adminError" <> "") "a blank tag name was not rejected"

      "SetProjectActive deactivates the fixture's default project, removing it from projectOptions, and pushes the update once identified", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (eventMessage "SetProjectActive" (Some "not-defined") (Some "off"))
        let projectIds = itemsView response "projectOptions" |> Seq.map (fun i -> i.AsObject().["id"].GetValue<string>()) |> List.ofSeq
        assertTrue (not (List.contains "not-defined" projectIds)) "a deactivated project still appeared in projectOptions"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 1 && effects.[0].["kind"].GetValue<string>() = "Http") "SetProjectActive once identified did not push reference.json"
        let decoded = Text.Encoding.UTF8.GetString(Convert.FromBase64String((JsonNode.Parse(effects.[0].["body"].GetValue<string>()).AsObject().["content"]).GetValue<string>()))
        assertTrue (decoded.Contains "\"active\":false") $"reference.json push body did not mark the project inactive: {decoded}"

      "SetProjectActive on an unknown id surfaces adminError", fun () ->
        reset ()
        let response = sendJson (eventMessage "SetProjectActive" (Some "missing") (Some "off"))
        assertTrue (stringView response "adminError" <> "") "toggling an unknown project id was not rejected"

      "a successful reference.json push (201) is used as the sha on the next admin push", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        sendJson (httpResult "github-reference-push" "Success" (Some 201) (Some(contentsPutBody "reference-sha-new")) None) |> ignore
        sendJson (eventMessage "DraftNewTagNameChanged" None (Some "Urgent")) |> ignore
        let response = sendJson (eventMessage "AddTag" None None)
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        let pushBody = JsonNode.Parse(effects.[0].["body"].GetValue<string>()).AsObject()
        assertTrue (pushBody.["sha"].GetValue<string>() = "reference-sha-new") "the next reference push did not carry forward the sha from the prior successful push"

      "a 409 reference.json push conflict surfaces githubReferenceError", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (httpResult "github-reference-push" "Success" (Some 409) (Some """{"message":"Conflict"}""") None)
        assertTrue (stringView response "githubReferenceError" <> "") "a 409 reference push conflict was not surfaced"

      // --- GitHub sync config persistence across a reload ---------------------

      "a cached config missing Login re-triggers the identity lookup on load", fun () ->
        reset ()
        let cached : Session.GitHubSyncConfig =
            { Owner = "kemiller2002"; Repo = "ledger-data"; Folder = "time-entries"; Branch = "main"; Token = "ghp_cached_token"; Login = None; DisplayName = None }
        let response = sendJson (storageResult "github-config-load" "Success" (Some(GitHubSync.encodeConfig cached)) None)
        assertTrue (boolView response "gitHubSyncConfigured") "a cached config was not restored into session state"
        assertTrue (stringView response "gitHubSyncStatus" = "identifying") "status was not 'identifying' for a cached config missing Login"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 1 && effects.[0].["kind"].GetValue<string>() = "Http" && (effects.[0].["url"].GetValue<string>()).EndsWith "/user")
            "a cached config missing Login did not re-trigger the identity lookup"

      "a cached config that already has Login goes straight to a settings pull and a ledger pull on load, skipping the identity lookup", fun () ->
        reset ()
        let cached : Session.GitHubSyncConfig =
            { Owner = "kemiller2002"; Repo = "ledger-data"; Folder = "time-entries"; Branch = "main"; Token = "ghp_cached_token"
              Login = Some "kemiller2002"; DisplayName = Some "Kevin Miller" }
        let response = sendJson (storageResult "github-config-load" "Success" (Some(GitHubSync.encodeConfig cached)) None)
        assertTrue (boolView response "gitHubSyncIdentified") "a cached, already-identified config was not restored as identified"
        assertTrue (stringView response "gitHubSyncStatus" = "idle") "status was not 'idle' for an already-identified cached config"
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 3) $"expected a settings pull, a ledger pull, and a reference pull, got {effects.Length}"
        assertTrue (effects |> List.forall (fun e -> e.["kind"].GetValue<string>() = "Http")) "all three auto-pulls should be Http effects"
        assertTrue (effects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "settings.json"))
            "an already-identified cached config did not go straight to a settings pull"
        assertTrue (effects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "ledger.json"))
            "an already-identified cached config did not also auto-pull the ledger"
        assertTrue (effects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "reference.json"))
            "an already-identified cached config did not also auto-pull the shared reference catalog"

      "a successful identity lookup re-caches the config (now including Login) and triggers a settings pull and a ledger pull", fun () ->
        reset ()
        saveGitHubConfig "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" |> ignore
        let response = resolveIdentity "kemiller2002" (Some "Kevin Miller")
        let effects = effectsOf response |> Seq.map (fun e -> e.AsObject()) |> List.ofSeq
        assertTrue (effects.Length = 4) $"expected a config re-cache, a settings pull, a ledger pull, and a reference pull, got {effects.Length}"
        let cacheSave = effects |> List.find (fun e -> e.["kind"].GetValue<string>() = "Storage")
        assertTrue (cacheSave.["key"].GetValue<string>() = "business-activity-ledger:github-config:v1") "the re-cache did not target the github-config storage key"
        assertTrue (cacheSave.["value"].GetValue<string>().Contains "\"login\":\"kemiller2002\"") "the re-cached config did not include the resolved login"
        let httpEffects = effects |> List.filter (fun e -> e.["kind"].GetValue<string>() = "Http")
        assertTrue (httpEffects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "settings.json")) "the identity lookup did not follow up with a settings pull"
        assertTrue (httpEffects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "ledger.json")) "the identity lookup did not also follow up with a ledger pull"
        assertTrue (httpEffects |> List.exists (fun e -> (e.["url"].GetValue<string>()).Contains "reference.json")) "the identity lookup did not also follow up with a reference pull"

      // --- Multi-person folder segregation (GitHubSync module directly) ------

      "two different people's data paths never collide, even with the same repo/folder settings", fun () ->
        let config : Session.GitHubSyncConfig =
            { Owner = "acme-co"; Repo = "shared-ledger"; Folder = "time-tracking-data"; Branch = "main"; Token = "irrelevant-here"; Login = None; DisplayName = None }
        let alicePath = GitHubSync.dataFilePath config "alice"
        let bobPath = GitHubSync.dataFilePath config "bob"
        assertTrue (alicePath <> bobPath) "two different logins produced the same ledger file path"
        assertTrue (alicePath = "time-tracking-data/alice/ledger.json") $"unexpected path shape for alice: {alicePath}"
        assertTrue (bobPath = "time-tracking-data/bob/ledger.json") $"unexpected path shape for bob: {bobPath}"
        let aliceMetadata = GitHubSync.metadataFilePath config "alice"
        assertTrue (aliceMetadata = "time-tracking-data/alice/metadata.json") $"unexpected metadata path shape for alice: {aliceMetadata}"
        assertTrue (aliceMetadata <> alicePath) "the ledger and metadata files resolved to the same path"

      // --- Integration: TimeObservation -> TimeCandidate mapping, receipts, --
      // --- and the pure reconciliation decision (no GitHub involved) --------

      "ObservationMapping.toCandidateProposal preserves every field and proposes it as Proposed", fun () ->
        let evidence = [ { Kind = "github-commit"; Reference = "abc123" } ]
        let observedAt = DateTimeOffset.Parse "2026-09-13T14:03:00-04:00"
        let observation =
            TimeObservation.create "ros:activity:001" "ros" "echelon-foundry" "not-defined" (Some "STRATA-1") (Some "kevin")
                None None (Some 30) (Some "Did work") evidence observedAt
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        let candidate = ObservationMapping.toCandidateProposal observation
        assertTrue (candidate.CandidateId = "candidate:ros:activity:001") $"unexpected deterministic candidate id: {candidate.CandidateId}"
        assertTrue (candidate.SourceObservationId = observation.ObservationId) "SourceObservationId was not preserved"
        assertTrue (candidate.ProjectId = observation.ProjectId) "ProjectId was not preserved"
        assertTrue (candidate.WorkItemId = observation.WorkItemId) "WorkItemId was not preserved"
        assertTrue (candidate.ProposedDurationMinutes = observation.DurationMinutes) "ProposedDurationMinutes was not preserved"
        assertTrue (candidate.State = Proposed) "a freshly mapped candidate must start Proposed"

      "TimeCandidate.candidateId is deterministic — the same observation id always produces the same candidate id", fun () ->
        assertTrue (TimeCandidate.candidateId "ros:activity:001" = TimeCandidate.candidateId "ros:activity:001") "candidateId was not deterministic"

      "TimeCandidate serialize then deserialize round-trips", fun () ->
        let candidate : TimeCandidate =
            { CandidateId = "candidate:ros:activity:001"; SourceObservationId = "ros:activity:001"; SourceSystem = "ros"; ProjectId = "not-defined"
              WorkItemId = Some "STRATA-1"; ActorId = Some "kevin"
              ProposedStart = Some(DateTimeOffset.Parse "2026-09-13T09:00:00-04:00"); ProposedEnd = Some(DateTimeOffset.Parse "2026-09-13T10:00:00-04:00")
              ProposedDurationMinutes = None; Description = Some "Did work"; State = Proposed }
        match TimeCandidate.deserialize (TimeCandidate.serialize candidate) with
        | Ok roundTripped -> assertTrue (roundTripped = candidate) $"round trip did not preserve the candidate: {roundTripped} <> {candidate}"
        | Error message -> failwith $"round trip failed to deserialize: {message}"

      "ProcessingReceipt serialize then deserialize round-trips for CandidateCreated, Rejected, and Ignored", fun () ->
        let processedAt = DateTimeOffset.Parse "2026-09-13T14:05:18-04:00"
        for result in [ CandidateCreated "candidate:ros:activity:001"; Rejected "Unknown project 'x'."; Ignored "duplicate delivery" ] do
            let receipt : ProcessingReceipt = { ReceiptVersion = ProcessingReceipt.CurrentReceiptVersion; ObservationId = "ros:activity:001"; ProcessedAt = processedAt; Result = result }
            match ProcessingReceipt.deserialize (ProcessingReceipt.serialize receipt) with
            | Ok roundTripped -> assertTrue (roundTripped = receipt) $"round trip did not preserve the receipt for {result}: {roundTripped} <> {receipt}"
            | Error message -> failwith $"round trip failed to deserialize for {result}: {message}"

      "ObservationReconciliation.decide proposes a candidate for a new, valid observation against a known project", fun () ->
        let environment = (Session.initial ()).Environment
        let observation =
            TimeObservation.create "ros:activity:new" "ros" "echelon-foundry" "not-defined" None None None None (Some 30) None [] DateTimeOffset.UtcNow
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match ObservationReconciliation.decide environment false None observation with
        | ObservationReconciliation.CandidateCreated candidate -> assertTrue (candidate.SourceObservationId = "ros:activity:new") "the wrong observation was mapped"
        | other -> failwith $"expected CandidateCreated, got {other}"

      "ObservationReconciliation.decide reports AlreadyProcessed when a receipt already exists — never proposes a second candidate", fun () ->
        let environment = (Session.initial ()).Environment
        let observation =
            TimeObservation.create "ros:activity:done" "ros" "echelon-foundry" "not-defined" None None None None (Some 30) None [] DateTimeOffset.UtcNow
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match ObservationReconciliation.decide environment true None observation with
        | ObservationReconciliation.AlreadyProcessed -> ()
        | other -> failwith $"expected AlreadyProcessed, got {other}"

      "ObservationReconciliation.decide repairs a missing receipt for an already-existing candidate, without proposing a new one", fun () ->
        let environment = (Session.initial ()).Environment
        let existing : TimeCandidate =
            { CandidateId = "candidate:ros:activity:partial"; SourceObservationId = "ros:activity:partial"; SourceSystem = "ros"; ProjectId = "not-defined"
              WorkItemId = None; ActorId = None; ProposedStart = None; ProposedEnd = None; ProposedDurationMinutes = Some 30; Description = None; State = Proposed }
        let observation =
            TimeObservation.create "ros:activity:partial" "ros" "echelon-foundry" "not-defined" None None None None (Some 30) None [] DateTimeOffset.UtcNow
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match ObservationReconciliation.decide environment false (Some existing) observation with
        | ObservationReconciliation.ReceiptRepairNeeded candidate -> assertTrue (candidate = existing) "a repaired receipt must reuse the exact existing candidate, never mint a new one"
        | other -> failwith $"expected ReceiptRepairNeeded, got {other}"

      "ObservationReconciliation.decide rejects an observation referencing a project Chrona does not recognize", fun () ->
        let environment = (Session.initial ()).Environment
        let observation =
            TimeObservation.create "ros:activity:unknown-project" "ros" "echelon-foundry" "no-such-project" None None None None (Some 30) None [] DateTimeOffset.UtcNow
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        match ObservationReconciliation.decide environment false None observation with
        | ObservationReconciliation.Rejected reason -> assertTrue (reason.Contains "no-such-project") $"rejection reason did not name the unknown project: {reason}"
        | other -> failwith $"expected Rejected, got {other}"

      "ObservationReconciliation.reconcileRaw rejects malformed JSON without throwing, and creates no candidate", fun () ->
        let environment = (Session.initial ()).Environment
        match ObservationReconciliation.reconcileRaw environment false None "{ not valid json" with
        | ObservationReconciliation.Rejected _ -> ()
        | other -> failwith $"expected Rejected, got {other}"

      "ObservationReconciliation.reconcileRaw rejects an unsupported contract version explicitly, not silently", fun () ->
        let environment = (Session.initial ()).Environment
        let json = """{"contractVersion":"99","observationId":"id","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"not-defined","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[]}"""
        match ObservationReconciliation.reconcileRaw environment false None json with
        | ObservationReconciliation.Rejected reason -> assertTrue (reason.Contains "99") $"rejection reason did not name the unsupported version: {reason}"
        | other -> failwith $"expected Rejected, got {other}"

      "ObservationReconciliation.reconcileRaw round-trips a valid raw payload into a candidate proposal", fun () ->
        let environment = (Session.initial ()).Environment
        let json = """{"contractVersion":"1","observationId":"ros:activity:raw","sourceSystem":"ros","organizationId":"echelon-foundry","projectId":"not-defined","durationMinutes":30,"observedAt":"2026-09-13T14:03:00-04:00","evidence":[]}"""
        match ObservationReconciliation.reconcileRaw environment false None json with
        | ObservationReconciliation.CandidateCreated candidate -> assertTrue (candidate.SourceObservationId = "ros:activity:raw") "the raw payload was not mapped to the right observation id"
        | other -> failwith $"expected CandidateCreated, got {other}"

      "two distinct observations produce two distinct candidates, even with identical field values otherwise", fun () ->
        let environment = (Session.initial ()).Environment
        let observationFor id =
            TimeObservation.create id "ros" "echelon-foundry" "not-defined" None None None None (Some 30) None [] DateTimeOffset.UtcNow
            |> function Ok o -> o | Error e -> failwith $"test setup failed: {e}"
        let first = ObservationReconciliation.decide environment false None (observationFor "ros:activity:a")
        let second = ObservationReconciliation.decide environment false None (observationFor "ros:activity:b")
        match first, second with
        | ObservationReconciliation.CandidateCreated c1, ObservationReconciliation.CandidateCreated c2 ->
            assertTrue (c1.CandidateId <> c2.CandidateId) "two different observations produced the same candidate id"
        | _ -> failwith $"expected two CandidateCreated results, got {first} and {second}"

      // --- GitHubSync: integration observation reading + candidate/receipt --
      // --- persistence (CHR-INT-011/012/013 — building effects only, no ------
      // --- orchestration wired in yet) -----------------------------------

      let integrationConfig : Session.GitHubSyncConfig =
          { Owner = "acme-co"; Repo = "shared-ledger"; Folder = "time-tracking-data"; Branch = "main"; Token = "ghp_test_token"; Login = None; DisplayName = None }

      "candidatePath and observationReceiptPath live under <folder>/integration/, safely encoding an id containing ':'", fun () ->
        let candidateId = TimeCandidate.candidateId "ros:activity:001"
        let cPath = GitHubSync.candidatePath integrationConfig candidateId
        let rPath = GitHubSync.observationReceiptPath integrationConfig "ros:activity:001"
        assertTrue (cPath.StartsWith "time-tracking-data/integration/candidates/") $"unexpected candidate path: {cPath}"
        assertTrue (not (cPath.Contains ":")) $"candidate path did not encode the ':' in the candidate id: {cPath}"
        assertTrue (rPath.StartsWith "time-tracking-data/integration/observation-receipts/") $"unexpected receipt path: {rPath}"
        assertTrue (cPath <> rPath) "the candidate and receipt paths must never collide"

      "observationInboxDirectory matches the path Chrona.Integration's public StorageConvention builds for the same folder/project", fun () ->
        let directory = GitHubSync.observationInboxDirectory integrationConfig "strata"
        assertTrue (directory = "time-tracking-data/integration/projects/strata/observations/inbox") $"unexpected directory: {directory}"

      "buildObservationsListEffect requests a GET against the project's inbox directory", fun () ->
        let effect = GitHubSync.buildObservationsListEffect integrationConfig "strata"
        match effect with
        | HttpEffect(correlationId, method, url, _, body, _) ->
            assertTrue (correlationId = "github-observations-list") $"unexpected correlation id: {correlationId}"
            assertTrue (method = "GET") $"expected GET, got {method}"
            assertTrue (url.Contains "/contents/time-tracking-data/integration/projects/strata/observations/inbox") $"unexpected URL: {url}"
            assertTrue (body.IsNone) "a listing GET should carry no body"
        | other -> failwith $"expected an HttpEffect, got {other}"

      "parseObservationListing extracts only .json file entries, skipping directories and non-json files", fun () ->
        let body =
            """[
                 {"name":"ros-activity-001.json","path":"time-tracking-data/integration/projects/strata/observations/inbox/ros-activity-001.json","type":"file"},
                 {"name":"ros-activity-002.json","path":"time-tracking-data/integration/projects/strata/observations/inbox/ros-activity-002.json","type":"file"},
                 {"name":"README.md","path":"time-tracking-data/integration/projects/strata/observations/inbox/README.md","type":"file"},
                 {"name":"subdir","path":"time-tracking-data/integration/projects/strata/observations/inbox/subdir","type":"dir"}
               ]"""
        match GitHubSync.parseObservationListing body with
        | Ok entries ->
            assertTrue (entries.Length = 2) $"expected exactly 2 .json file entries, got {entries.Length}: {entries}"
            assertTrue (entries |> List.forall (fun e -> e.Name.EndsWith ".json")) "a non-.json entry leaked through"
        | Error message -> failwith $"expected success, got {message}"

      "parseObservationListing surfaces a parse failure rather than throwing", fun () ->
        match GitHubSync.parseObservationListing "{ not an array" with
        | Error _ -> ()
        | Ok entries -> failwith $"expected a parse error, got {entries}"

      "buildObservationGetEffect requests a GET against an already-known observation path", fun () ->
        match GitHubSync.buildObservationGetEffect integrationConfig "time-tracking-data/integration/projects/strata/observations/inbox/ros-activity-001.json" with
        | HttpEffect(correlationId, method, url, _, _, _) ->
            assertTrue (correlationId = "github-observation-pull") $"unexpected correlation id: {correlationId}"
            assertTrue (method = "GET" && url.Contains "ros-activity-001.json") $"unexpected GET: {method} {url}"
        | other -> failwith $"expected an HttpEffect, got {other}"

      "buildCandidateGetEffect and buildReceiptGetEffect request GETs against their deterministic paths", fun () ->
        match GitHubSync.buildCandidateGetEffect integrationConfig "candidate:ros:activity:001" with
        | HttpEffect("github-candidate-pull", "GET", url, _, _, _) -> assertTrue (url.Contains "/integration/candidates/") $"unexpected candidate GET url: {url}"
        | other -> failwith $"expected a github-candidate-pull GET, got {other}"
        match GitHubSync.buildReceiptGetEffect integrationConfig "ros:activity:001" with
        | HttpEffect("github-receipt-pull", "GET", url, _, _, _) -> assertTrue (url.Contains "/integration/observation-receipts/") $"unexpected receipt GET url: {url}"
        | other -> failwith $"expected a github-receipt-pull GET, got {other}"

      "buildCandidatePutEffect and buildReceiptPutEffect are always create-only — never carry a sha", fun () ->
        match GitHubSync.buildCandidatePutEffect integrationConfig "candidate:ros:activity:001" """{"candidateId":"candidate:ros:activity:001"}""" with
        | HttpEffect("github-candidate-push", "PUT", _, _, Some body, _) ->
            let bodyObject = JsonNode.Parse(body).AsObject()
            assertTrue (isNull (bodyObject.["sha"] :> obj)) "a candidate create must never carry a sha — it would allow overwriting an existing candidate"
        | other -> failwith $"expected a github-candidate-push PUT with a body, got {other}"
        match GitHubSync.buildReceiptPutEffect integrationConfig "ros:activity:001" """{"observationId":"ros:activity:001"}""" with
        | HttpEffect("github-receipt-push", "PUT", _, _, Some body, _) ->
            let bodyObject = JsonNode.Parse(body).AsObject()
            assertTrue (isNull (bodyObject.["sha"] :> obj)) "a receipt create must never carry a sha — two clients racing must fail safely, not overwrite"
        | other -> failwith $"expected a github-receipt-push PUT with a body, got {other}"

      // --- CHR-INT-014: partial-failure recovery, proven via simulated -------
      // --- persistence (specification §14/§34's acceptance tests) -----------

      "Test A: one valid observation produces one candidate and one receipt", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        runReconciliationPass store environment [ rawObservation "ros:activity:A" "not-defined" ]
        assertTrue (store.Contains(candidateKey "ros:activity:A")) "no candidate was created"
        assertTrue (store.Contains(receiptKey "ros:activity:A")) "no receipt was created"

      "Test B: reconciling the same observation five times (Chrona loading five times) still yields one candidate and one receipt", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        let observations = [ rawObservation "ros:activity:B" "not-defined" ]
        for _ in 1..5 do
            runReconciliationPass store environment observations
        let candidateJson = store.TryRead(candidateKey "ros:activity:B") |> Option.get
        assertTrue (store.Contains(receiptKey "ros:activity:B")) "the receipt disappeared across repeated reconciliation"
        // Re-reconciling never touches an already-processed observation's
        // candidate content — it must still deserialize to the same value.
        match TimeCandidate.deserialize candidateJson with
        | Ok candidate -> assertTrue (candidate.SourceObservationId = "ros:activity:B") "the surviving candidate belongs to the wrong observation"
        | Error message -> failwith $"the candidate became unreadable after repeated reconciliation: {message}"

      "Test C: ROS retrying delivery of the same observation five times within one pass still yields one candidate", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        let sameObservationFiveTimes = List.replicate 5 (rawObservation "ros:activity:C" "not-defined")
        runReconciliationPass store environment sameObservationFiveTimes
        assertTrue (store.Contains(candidateKey "ros:activity:C")) "no candidate was created"
        assertTrue (store.Contains(receiptKey "ros:activity:C")) "no receipt was created"

      "candidate persistence fails -> no receipt is ever written", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        store.ArmFailure(candidateKey "ros:activity:candidate-fails")
        runReconciliationPass store environment [ rawObservation "ros:activity:candidate-fails" "not-defined" ]
        assertTrue (not (store.Contains(candidateKey "ros:activity:candidate-fails"))) "the armed candidate write failure did not take effect"
        assertTrue (not (store.Contains(receiptKey "ros:activity:candidate-fails"))) "a receipt was written even though its candidate write failed — this would permanently mark the observation processed with nothing to show for it"

      "Test D: candidate succeeds but its receipt write fails -> the next reconciliation pass repairs only the missing receipt, minting no second candidate", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        let observationId = "ros:activity:receipt-fails"
        store.ArmFailure(receiptKey observationId)
        runReconciliationPass store environment [ rawObservation observationId "not-defined" ]
        assertTrue (store.Contains(candidateKey observationId)) "the candidate write should have succeeded"
        assertTrue (not (store.Contains(receiptKey observationId))) "the armed receipt write failure did not take effect"
        let candidateAfterFirstPass = store.TryRead(candidateKey observationId) |> Option.get
        runReconciliationPass store environment [ rawObservation observationId "not-defined" ]
        assertTrue (store.Contains(receiptKey observationId)) "the second pass did not repair the missing receipt"
        let candidateAfterSecondPass = store.TryRead(candidateKey observationId) |> Option.get
        assertTrue (candidateAfterFirstPass = candidateAfterSecondPass) "repairing the receipt must never mint (or alter) a second candidate"

      "two observations with different ids are always treated as two independent observations, never merged", fun () ->
        let environment = (Session.initial ()).Environment
        let store = FakeStore()
        runReconciliationPass store environment [ rawObservation "ros:activity:x" "not-defined"; rawObservation "ros:activity:y" "not-defined" ]
        assertTrue (store.Contains(candidateKey "ros:activity:x") && store.Contains(candidateKey "ros:activity:y")) "both observations should have produced their own candidate"
        assertTrue (candidateKey "ros:activity:x" <> candidateKey "ros:activity:y") "two different observation ids collided on the same candidate key"

      // --- CHR-INT-015: startup reconciliation actually wired into Dispatch.fs ---
      //
      // The tests above (CHR-INT-014) prove `ObservationReconciliation.decide`
      // converges correctly through a test-only in-process harness. These
      // drive the *real* production wiring — `Dispatch.handle` itself, one
      // EffectResultMessage at a time, exactly as `web/dom-bindings.js`
      // would — proving the actual multi-step effect chain (list -> read ->
      // check receipt -> check candidate -> write candidate -> write
      // receipt) is sequenced correctly end to end.

      "reconciliation is seeded the moment reference.json resolves, requesting the first project's inbox listing", fun () ->
        reset ()
        let response = beginReconciliationWithProjects [ "acme" ]
        let listing = onlyHttpEffect response
        assertTrue (listing.["correlationId"].GetValue<string>() = "github-observations-list") "reference.json resolving did not request an inbox listing"
        let listingUrl = listing.["url"].GetValue<string>()
        assertTrue (listingUrl.Contains "integration/projects/acme/observations/inbox") $"the inbox listing targeted the wrong path: {listingUrl}"

      "a 404 (or empty) inbox listing for the only known project ends reconciliation without further requests", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        let response = sendJson (httpResult "github-observations-list" "Success" (Some 404) (Some """{"message":"Not Found"}""") None)
        assertTrue ((effectsOf response).Count = 0) "an empty/missing inbox for the only project should end reconciliation, not request anything else"

      "an empty first project's inbox moves reconciliation on to the next project's listing", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme"; "beta" ] |> ignore
        let response = sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [])) None)
        let listing = onlyHttpEffect response
        assertTrue
            ((listing.["url"].GetValue<string>()).Contains "integration/projects/beta/observations/inbox")
            "an empty inbox did not advance reconciliation to the next project"

      "a full end-to-end pass — one new observation — lists, reads, checks, and writes a candidate then its receipt, in that order", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        let observationJson = rawObservation "ros:activity:e2e-1" "acme"
        let listResponse =
            sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [ "e2e-1.json", "chrona-data/integration/projects/acme/observations/inbox/e2e-1.json" ])) None)
        let readRequest = onlyHttpEffect listResponse
        assertTrue (readRequest.["correlationId"].GetValue<string>() = "github-observation-pull") "the listing did not request the observation file next"
        let readUrl = readRequest.["url"].GetValue<string>()
        assertTrue (readUrl.Contains "inbox/e2e-1.json") $"the observation read targeted the wrong path: {readUrl}"

        let receiptCheckResponse = sendJson (httpResult "github-observation-pull" "Success" (Some 200) (Some(contentsGetBody "obs-sha" observationJson)) None)
        let receiptCheck = onlyHttpEffect receiptCheckResponse
        let receiptCheckUrl = receiptCheck.["url"].GetValue<string>()
        assertTrue
            (receiptCheck.["correlationId"].GetValue<string>() = "github-receipt-pull" && receiptCheckUrl.Contains "observation-receipts/ros_3aactivity_3ae2e-1.json")
            $"reading the observation did not check for an existing receipt at the expected path: {receiptCheckUrl}"

        let candidateCheckResponse = sendJson (httpResult "github-receipt-pull" "Success" (Some 404) None None)
        let candidateCheck = onlyHttpEffect candidateCheckResponse
        let candidateCheckUrl = candidateCheck.["url"].GetValue<string>()
        let expectedCandidateSegment = TimeCandidate.candidateId "ros:activity:e2e-1" |> StorageConvention.safeSegment
        assertTrue
            (candidateCheck.["correlationId"].GetValue<string>() = "github-candidate-pull" && candidateCheckUrl.Contains expectedCandidateSegment)
            $"a missing receipt did not check for an existing candidate at the expected path: {candidateCheckUrl}"

        let candidateWriteResponse = sendJson (httpResult "github-candidate-pull" "Success" (Some 404) None None)
        let candidateWrite = onlyHttpEffect candidateWriteResponse
        assertTrue
            (candidateWrite.["correlationId"].GetValue<string>() = "github-candidate-push" && candidateWrite.["method"].GetValue<string>() = "PUT")
            "a genuinely new observation (no receipt, no candidate) did not write a candidate"
        let candidateBody = decodedPutContent candidateWrite
        assertTrue (not (JsonNode.Parse(candidateWrite.["body"].GetValue<string>()).AsObject().ContainsKey "sha")) "the candidate write was not create-only (it carried a sha)"
        match TimeCandidate.deserialize candidateBody with
        | Error message -> failwith $"the written candidate body could not be read back: {message}"
        | Ok candidate ->
            assertTrue (candidate.SourceObservationId = "ros:activity:e2e-1") "the written candidate referenced the wrong observation"
            assertTrue (candidate.CandidateId = TimeCandidate.candidateId "ros:activity:e2e-1") "the written candidate's id was not the deterministic candidate:<observationId> id"
            assertTrue (candidate.State = Proposed) "a freshly-created candidate must start Proposed"

        let receiptWriteResponse = sendJson (httpResult "github-candidate-push" "Success" (Some 201) None None)
        let receiptWrite = onlyHttpEffect receiptWriteResponse
        assertTrue
            (receiptWrite.["correlationId"].GetValue<string>() = "github-receipt-push" && receiptWrite.["method"].GetValue<string>() = "PUT")
            "a successful candidate write did not proceed to write its receipt — critical ordering (§22) requires the receipt only after the candidate succeeds"
        assertTrue (not (JsonNode.Parse(receiptWrite.["body"].GetValue<string>()).AsObject().ContainsKey "sha")) "the receipt write was not create-only (it carried a sha)"
        match ProcessingReceipt.deserialize (decodedPutContent receiptWrite) with
        | Error message -> failwith $"the written receipt body could not be read back: {message}"
        | Ok receipt ->
            assertTrue (receipt.ObservationId = "ros:activity:e2e-1") "the written receipt referenced the wrong observation"
            match receipt.Result with
            | CandidateCreated candidateId -> assertTrue (candidateId = TimeCandidate.candidateId "ros:activity:e2e-1") "the receipt's candidateId did not match the candidate that was written"
            | _ -> failwith "a newly-created candidate's receipt must record CandidateCreated"

        // Only one observation existed and it is now fully processed —
        // reconciliation should end cleanly with no further requests.
        let finalResponse = sendJson (httpResult "github-receipt-push" "Success" (Some 201) None None)
        assertTrue ((effectsOf finalResponse).Count = 0) "reconciliation kept going after its only observation was fully processed"

      "an observation whose receipt already exists is skipped without reading or writing anything else", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [ "already-done.json", "chrona-data/integration/projects/acme/observations/inbox/already-done.json" ])) None)
        |> ignore
        sendJson (httpResult "github-observation-pull" "Success" (Some 200) (Some(contentsGetBody "obs-sha" (rawObservation "ros:activity:already-done" "acme"))) None)
        |> ignore
        let response = sendJson (httpResult "github-receipt-pull" "Success" (Some 200) None None)
        assertTrue ((effectsOf response).Count = 0) "an observation with an existing receipt should end reconciliation (only one observation was queued), not request anything else"

      "an existing candidate with a missing receipt only repairs the receipt — it never writes a second candidate", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [ "repair.json", "chrona-data/integration/projects/acme/observations/inbox/repair.json" ])) None)
        |> ignore
        sendJson (httpResult "github-observation-pull" "Success" (Some 200) (Some(contentsGetBody "obs-sha" (rawObservation "ros:activity:repair" "acme"))) None)
        |> ignore
        sendJson (httpResult "github-receipt-pull" "Success" (Some 404) None None) |> ignore
        let existingCandidate: TimeCandidate =
            { CandidateId = TimeCandidate.candidateId "ros:activity:repair"; SourceObservationId = "ros:activity:repair"; SourceSystem = "ros"
              ProjectId = "acme"; WorkItemId = None; ActorId = None; ProposedStart = None; ProposedEnd = None
              ProposedDurationMinutes = Some 30; Description = None; State = Proposed }
        let response =
            sendJson (httpResult "github-candidate-pull" "Success" (Some 200) (Some(contentsGetBody "candidate-sha" (TimeCandidate.serialize existingCandidate))) None)
        let repairWrite = onlyHttpEffect response
        assertTrue
            (repairWrite.["correlationId"].GetValue<string>() = "github-receipt-push")
            "an existing candidate with no receipt should go straight to writing the repair receipt, not a new candidate"
        match ProcessingReceipt.deserialize (decodedPutContent repairWrite) with
        | Error message -> failwith $"the repair receipt body could not be read back: {message}"
        | Ok receipt ->
            match receipt.Result with
            | CandidateCreated candidateId -> assertTrue (candidateId = existingCandidate.CandidateId) "the repair receipt referenced a different candidate than the one that already existed"
            | _ -> failwith "repairing a missing receipt for an already-created candidate must record CandidateCreated"

      "an observation for a project Chrona does not recognize is rejected — its receipt records the rejection, and no candidate is ever written", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [ "unknown-project.json", "chrona-data/integration/projects/acme/observations/inbox/unknown-project.json" ])) None)
        |> ignore
        sendJson (httpResult "github-observation-pull" "Success" (Some 200) (Some(contentsGetBody "obs-sha" (rawObservation "ros:activity:unknown-project" "no-such-project"))) None)
        |> ignore
        sendJson (httpResult "github-receipt-pull" "Success" (Some 404) None None) |> ignore
        let response = sendJson (httpResult "github-candidate-pull" "Success" (Some 404) None None)
        let write = onlyHttpEffect response
        assertTrue
            (write.["correlationId"].GetValue<string>() = "github-receipt-push")
            "an observation for an unrecognized project should be rejected straight to a receipt — never a candidate write"
        match ProcessingReceipt.deserialize (decodedPutContent write) with
        | Error message -> failwith $"the rejection receipt body could not be read back: {message}"
        | Ok receipt ->
            match receipt.Result with
            | Rejected reason -> assertTrue (reason.Contains "no-such-project") $"the rejection reason did not identify the unrecognized project: {reason}"
            | _ -> failwith "an observation for an unrecognized project must be recorded as Rejected, not CandidateCreated"

      "a failed or unconfirmed candidate write is never followed by a receipt write, and the observation is simply dropped for this session", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        sendJson (httpResult "github-observations-list" "Success" (Some 200) (Some(observationsListingBody [ "candidate-write-fails.json", "chrona-data/integration/projects/acme/observations/inbox/candidate-write-fails.json" ])) None)
        |> ignore
        sendJson (httpResult "github-observation-pull" "Success" (Some 200) (Some(contentsGetBody "obs-sha" (rawObservation "ros:activity:candidate-write-fails" "acme"))) None)
        |> ignore
        sendJson (httpResult "github-receipt-pull" "Success" (Some 404) None None) |> ignore
        sendJson (httpResult "github-candidate-pull" "Success" (Some 404) None None) |> ignore
        let response = sendJson (httpResult "github-candidate-push" "Failure" None None (Some "network error"))
        assertTrue
            ((effectsOf response).Count = 0)
            "a failed candidate write must never be followed by a receipt write — this would permanently mark the observation processed with nothing to show for it"

      "an unreadable reference.json never seeds reconciliation — no observations-list request is ever made", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = sendJson (httpResult "github-reference-pull" "Failure" None None (Some "network error"))
        assertTrue ((effectsOf response).Count = 0) "a failed reference.json pull should not start reconciliation"

      // --- CHR-INT-016: minimal integration processing status view fields ---

      "integrationReconciliationActive is false and the status label is empty before any reconciliation has started", fun () ->
        reset ()
        let response = sendJson initializeMessage
        assertTrue (boolView response "integrationReconciliationActive" = false) "a fresh session should not report reconciliation as active"
        assertTrue (stringView response "integrationReconciliationStatusLabel" = "") "a fresh session should have no status label"

      "the status label names the current project while reconciliation is checking its inbox", fun () ->
        reset ()
        let response = beginReconciliationWithProjects [ "acme" ]
        assertTrue (boolView response "integrationReconciliationActive") "reconciliation just started but was not reported as active"
        let label = stringView response "integrationReconciliationStatusLabel"
        assertTrue (label.Contains "acme") $"the status label did not name the project being checked: {label}"

      "integrationReconciliationActive returns to false once reconciliation has processed its only observation", fun () ->
        reset ()
        beginReconciliationWithProjects [ "acme" ] |> ignore
        let response = sendJson (httpResult "github-observations-list" "Success" (Some 404) (Some """{"message":"Not Found"}""") None)
        assertTrue (boolView response "integrationReconciliationActive" = false) "reconciliation should report inactive once its only project's empty inbox ends the pass"
        assertTrue (stringView response "integrationReconciliationStatusLabel" = "") "the status label should clear once reconciliation ends"
    ]

[<EntryPoint>]
let main _ =
    let failures =
        tests
        |> List.choose (fun (name, test) ->
            try
                test ()
                printfn $"PASS {name}"
                None
            with error ->
                Some $"FAIL {name}: {error.Message}")
    failures |> List.iter (eprintfn "%s")
    if List.isEmpty failures then
        printfn $"All {tests.Length} F# engine specifications passed."
        0
    else
        eprintfn $"{failures.Length} of {tests.Length} F# engine specifications failed."
        1
