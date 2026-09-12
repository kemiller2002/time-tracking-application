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
