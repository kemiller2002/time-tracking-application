namespace Ledger.Engine

open System.Text.Json.Nodes

/// Domain-agnostic wire protocol between the browser bridge and the WASM
/// engine. Small explicit DTOs and hand-written JSON encode/decode — no
/// reflection-driven marshalling, no shared object graph with the domain.
module Protocol =

    type SemanticEvent = { Name: string; Key: string option; Value: string option }

    type EffectOutcome =
        | OutcomeSuccess of status: int
        | OutcomeFailure of reason: string
        | OutcomeCancelled
        | OutcomeUnknown of reason: string

    /// `value` is the read value for a "get" (None means the key was absent —
    /// a normal outcome, not a failure).
    type StorageOutcome =
        | StorageSuccess of value: string option
        | StorageFailure of reason: string

    type EffectResult =
        | HttpResult of correlationId: string * outcome: EffectOutcome
        | StorageResult of correlationId: string * outcome: StorageOutcome

    type BrowserToEngineMessage =
        | Initialize of protocolVersion: int * capabilities: string list
        | Event of SemanticEvent
        | EffectResultMessage of EffectResult

    /// A `ViewItem` (one `VItems` row) is a `Map<string, ViewValue>` that
    /// itself only contains scalar variants — `VItems` is never nested inside
    /// a `VItems` element.
    type ViewValue =
        | VString of string
        | VNumber of float
        | VBool of bool
        | VItems of Map<string, ViewValue> list

    type StorageOperation =
        | StorageGet
        | StorageSet of value: string
        | StorageRemove

    type EffectRequest =
        | HttpEffect of correlationId: string * method: string * url: string * timeoutMs: int
        | StorageEffect of correlationId: string * operation: StorageOperation * key: string

    type EngineToBrowserMessage =
        { View: Map<string, ViewValue>
          Effects: EffectRequest list
          Cancellations: string list }

    let private optionalString (obj: JsonObject) (key: string) : string option =
        match obj.[key] with
        | null -> None
        | node -> Some(node.GetValue<string>())

    let parseMessage (json: string) : BrowserToEngineMessage =
        let node = JsonNode.Parse(json)
        let obj = node.AsObject()

        match obj.["kind"].GetValue<string>() with
        | "Initialize" ->
            let version = obj.["protocolVersion"].GetValue<int>()
            let capabilities = obj.["capabilities"].AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq
            Initialize(version, capabilities)
        | "Event" ->
            let eventObj = obj.["event"].AsObject()
            Event { Name = eventObj.["name"].GetValue<string>(); Key = optionalString eventObj "key"; Value = optionalString eventObj "value" }
        | "EffectResult" ->
            let resultObj = obj.["result"].AsObject()
            let correlationId = resultObj.["correlationId"].GetValue<string>()
            match resultObj.["kind"].GetValue<string>() with
            | "HttpResult" ->
                let outcomeObj = resultObj.["outcome"].AsObject()
                let outcome =
                    match outcomeObj.["kind"].GetValue<string>() with
                    | "Success" -> OutcomeSuccess(outcomeObj.["status"].GetValue<int>())
                    | "Failure" -> OutcomeFailure(outcomeObj.["reason"].GetValue<string>())
                    | "Cancelled" -> OutcomeCancelled
                    | "OutcomeUnknown" -> OutcomeUnknown(outcomeObj.["reason"].GetValue<string>())
                    | other -> failwithf "unknown EffectOutcome kind '%s'" other
                EffectResultMessage(HttpResult(correlationId, outcome))
            | "StorageResult" ->
                let outcomeObj = resultObj.["outcome"].AsObject()
                let outcome =
                    match outcomeObj.["kind"].GetValue<string>() with
                    | "Success" -> StorageSuccess(optionalString outcomeObj "value")
                    | "Failure" -> StorageFailure(outcomeObj.["reason"].GetValue<string>())
                    | other -> failwithf "unknown StorageOutcome kind '%s'" other
                EffectResultMessage(StorageResult(correlationId, outcome))
            | other -> failwithf "unknown EffectResult kind '%s'" other
        | other -> failwithf "unknown BrowserToEngineMessage kind '%s'" other

    let rec private viewValueNode (value: ViewValue) : JsonNode =
        match value with
        | VString text -> JsonValue.Create(text) :> JsonNode
        | VNumber number -> JsonValue.Create(number) :> JsonNode
        | VBool flag -> JsonValue.Create(flag) :> JsonNode
        | VItems items ->
            let array = JsonArray()
            for item in items do
                let itemObject = JsonObject()
                for KeyValue(key, itemValue) in item do
                    itemObject.[key] <- viewValueNode itemValue
                array.Add(itemObject)
            array :> JsonNode

    let private effectRequestNode (effect: EffectRequest) : JsonNode =
        let effectObject = JsonObject()
        match effect with
        | HttpEffect(correlationId, method, url, timeoutMs) ->
            effectObject.["kind"] <- JsonValue.Create("Http")
            effectObject.["correlationId"] <- JsonValue.Create(correlationId)
            effectObject.["method"] <- JsonValue.Create(method)
            effectObject.["url"] <- JsonValue.Create(url)
            effectObject.["timeoutMs"] <- JsonValue.Create(timeoutMs)
        | StorageEffect(correlationId, operation, key) ->
            effectObject.["kind"] <- JsonValue.Create("Storage")
            effectObject.["correlationId"] <- JsonValue.Create(correlationId)
            effectObject.["key"] <- JsonValue.Create(key)
            match operation with
            | StorageGet -> effectObject.["operation"] <- JsonValue.Create("get")
            | StorageSet value ->
                effectObject.["operation"] <- JsonValue.Create("set")
                effectObject.["value"] <- JsonValue.Create(value)
            | StorageRemove -> effectObject.["operation"] <- JsonValue.Create("remove")
        effectObject :> JsonNode

    let serializeMessage (message: EngineToBrowserMessage) : string =
        let root = JsonObject()
        let viewObject = JsonObject()
        for KeyValue(key, value) in message.View do
            viewObject.[key] <- viewValueNode value
        root.["view"] <- viewObject

        let effects = JsonArray()
        for effect in message.Effects do
            effects.Add(effectRequestNode effect)
        root.["effects"] <- effects

        let cancellations = JsonArray()
        for correlationId in message.Cancellations do
            cancellations.Add(JsonValue.Create(correlationId))
        root.["cancellations"] <- cancellations
        root.ToJsonString()
