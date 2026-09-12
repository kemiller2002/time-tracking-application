open System
open System.Text.Json.Nodes
open Ledger.Domain
open Ledger.Engine

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
let githubMetadataPushSuccess status body = httpResult "github-metadata-push" "Success" (Some status) body None

let resolveIdentity login name = sendJson (githubWhoAmISuccess login name)

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
    sendJson (eventMessage "DraftActivityTypeChanged" None (Some "research")) |> ignore
    sendJson (eventMessage "DraftProjectChanged" None (Some "echelon-foundry")) |> ignore
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
        { ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Preloaded"; BusinessPurpose = "Preloaded purpose"
          Outcome = ""; TagIds = []; EntryMethod = Manual; ReconstructionReason = None
          StartedAt = DateTimeOffset.Parse(todayAt 8 0); EndedAt = DateTimeOffset.Parse(todayAt 9 0); ClientTimestamp = None }
    match Commands.create environment LedgerDocument.empty command with
    | Success(document, _) -> document
    | result -> failwith $"seed failed: {result}"

let tests : (string * (unit -> unit)) list =
    [
      "Initialize requests a Storage load with the fixed ledger key", fun () ->
        reset ()
        let response = sendJson initializeMessage
        let effects = effectsOf response
        assertTrue (effects.Count = 1) $"expected exactly one effect, got {effects.Count}"
        let effect = effects.[0].AsObject()
        assertTrue (effect.["kind"].GetValue<string>() = "Storage") "Initialize did not request a Storage effect"
        assertTrue (effect.["operation"].GetValue<string>() = "get") "Initialize did not request a get"
        assertTrue (effect.["key"].GetValue<string>() = "business-activity-ledger:v1") "Initialize used the wrong storage key"

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
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "research")) |> ignore
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
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "research")) |> ignore
        sendJson (eventMessage "DraftProjectChanged" None (Some "echelon-foundry")) |> ignore
        sendJson (eventMessage "DraftDescriptionChanged" None (Some "Merged work")) |> ignore
        sendJson (eventMessage "DraftBusinessPurposeChanged" None (Some "Coverage")) |> ignore
        sendJson (eventMessage "DraftReasonChanged" None (Some "combine")) |> ignore
        let response = sendJson (eventMessage "MergeActivities" None None)
        let mergeError = stringView response "mergeError"
        assertTrue (mergeError = "") $"merge was rejected: {mergeError}"
        assertTrue ((itemsView response "dayActivities").Count = 1) "merge did not collapse to a single row"

      "a timer stopped almost immediately is discarded and records nothing", fun () ->
        reset ()
        sendJson (eventMessage "DraftActivityTypeChanged" None (Some "research")) |> ignore
        sendJson (eventMessage "DraftProjectChanged" None (Some "echelon-foundry")) |> ignore
        sendJson (eventMessage "StartTimer" None None) |> ignore
        let response = sendJson (eventMessage "StopTimer" None None)
        assertTrue (stringView response "timerPhase" = "none") "timer did not clear after stopping"
        assertTrue ((itemsView response "dayActivities").Count = 0) "an immediately-stopped timer recorded an activity"

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
        let effects = effectsOf response
        assertTrue (effects.Count = 1) $"expected exactly one effect (the identity lookup), got {effects.Count}"
        let effect = effects.[0].AsObject()
        assertTrue (effect.["kind"].GetValue<string>() = "Http" && effect.["method"].GetValue<string>() = "GET" && (effect.["url"].GetValue<string>()).EndsWith "/user")
            "SaveGitHubConfig did not request a GET to GitHub's /user endpoint"

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

      "a mutating command auto-pushes both ledger.json and metadata.json once identified, alongside the usual Storage cache save", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let response = createTodayActivity 9 10
        let effects = effectsOf response
        assertTrue (effects.Count = 3) $"expected a Storage save, a ledger PUT, and a metadata PUT, got {effects.Count}"
        let httpEffects = effects |> Seq.map (fun e -> e.AsObject()) |> Seq.filter (fun e -> e.["kind"].GetValue<string>() = "Http") |> List.ofSeq
        assertTrue (httpEffects.Length = 2) $"expected exactly two Http effects, got {httpEffects.Length}"
        assertTrue (httpEffects |> List.forall (fun e -> e.["method"].GetValue<string>() = "PUT")) "auto-push effects were not both PUTs"
        let ledgerPush = httpEffects |> List.find (fun e -> (e.["url"].GetValue<string>()).Contains "ledger.json")
        let metadataPush = httpEffects |> List.find (fun e -> (e.["url"].GetValue<string>()).Contains "metadata.json")
        assertTrue (ledgerPush.["body"].GetValue<string>().Contains "\"branch\":\"main\"") "ledger push body missing expected shape"
        let metadataBody = metadataPush.["body"].GetValue<string>()
        assertTrue (metadataBody.Contains "\"branch\":\"main\"") "metadata push body missing expected shape"

      "a successful GitHub push (201) records the new sha and marks status synced", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushSuccess 201 (Some(contentsPutBody "new-sha-1")))
        assertTrue (stringView response "githubSyncError" = "") "unexpected githubSync error on a successful push"
        assertTrue (stringView response "gitHubSyncSha" = "new-sha-1") "the new sha from a successful push was not recorded"
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "sync status was not 'synced' after a successful push"

      "a successful metadata push records its own sha under a separate error key, without touching the ledger sync status", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushSuccess 201 (Some(contentsPutBody "ledger-sha-1"))) |> ignore
        let response = sendJson (githubMetadataPushSuccess 201 (Some(contentsPutBody "metadata-sha-1")))
        assertTrue (stringView response "githubMetadataError" = "") "unexpected githubMetadata error on a successful metadata push"
        assertTrue (stringView response "gitHubSyncSha" = "ledger-sha-1") "a metadata push overwrote the ledger's own sha"
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "a metadata push changed the ledger sync status"

      "a metadata push failure surfaces githubMetadataError without downgrading the ledger's 'synced' status", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        sendJson (githubPushSuccess 201 (Some(contentsPutBody "ledger-sha-1"))) |> ignore
        let response = sendJson (httpResult "github-metadata-push" "Failure" None None (Some "connection reset"))
        assertTrue (stringView response "githubMetadataError" <> "") "a metadata push failure was not surfaced"
        assertTrue (stringView response "gitHubSyncStatus" = "synced") "a metadata push failure incorrectly downgraded the ledger sync status"

      "a 409 GitHub push conflict surfaces githubSyncError with status 'conflict', without touching the document", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        let created = createTodayActivity 9 10
        let response = sendJson (githubPushSuccess 409 (Some """{"message":"sha does not match"}"""))
        assertTrue (stringView response "gitHubSyncStatus" = "conflict") "a 409 push was not marked as a conflict"
        assertTrue (stringView response "githubSyncError" <> "") "a 409 push conflict was not surfaced"
        assertTrue ((itemsView response "dayActivities").Count = (itemsView created "dayActivities").Count) "a push conflict altered the document"

      /// The same "never collapse an unknown outcome" doctrine already
      /// covered for Storage saves (see above) applies to a GitHub push too:
      /// a dropped connection must not be read as either success or failure.
      "an unknown GitHub push outcome (e.g. a timeout) is never treated as success or failure", fun () ->
        reset ()
        configureGitHub "kemiller2002" "ledger-data" (Some "time-entries") None "ghp_test_token" "kemiller2002" (Some "Kevin Miller") |> ignore
        createTodayActivity 9 10 |> ignore
        let response = sendJson (githubPushUnknown "timed out after 15000ms")
        assertTrue (stringView response "gitHubSyncStatus" = "unknown") "an unknown push outcome was not reported as 'unknown'"
        assertTrue (stringView response "githubSyncError" <> "") "an unknown push outcome gave no guidance to the user"

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
