/// The browser kernel. Everything the page can ask for passes through here,
/// and every answer is computed in F#.
///
/// The signature of every entry point is `string -> string`. That is
/// deliberate: it is what stops the C# interop shim acquiring domain
/// knowledge. A shim that only forwards a string cannot inspect or decide
/// anything about a command (TE-R-091).
module TimeEntry.Kernel

open System.Text.Json
open System.Text.Json.Nodes
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Projection.Query
open TimeEntry.Projection.Projection
open TimeEntry.Persistence

/// Rounding policy for display. A single place, so the browser cannot pick
/// its own and disagree with the server's totals.
let private displayPolicy = RoundUp

let private jsonOptions = JsonSerializerOptions(WriteIndented = false)

let private errorResult (detail: string) =
    let node = JsonObject()
    node.Add("ok", JsonValue.Create false)
    node.Add("error", JsonValue.Create detail)
    node.ToJsonString()

/// Project a day's entries into a flat view state.
///
/// Input is the stored form of the entries plus a date; output is the
/// `ListProjection` the page renders. Filtering, sorting, totals and
/// capabilities are all decided here, never in the browser (TE-R-080).
let viewDay (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let documentsNode = request.["entries"]

            let documents =
                match documentsNode with
                | :? JsonArray as items ->
                    items
                    |> Seq.map (fun item ->
                        match item with
                        | null -> Error "null entry"
                        | node ->
                            match Serialization.read (node.ToJsonString()) with
                            | Error e -> Error(sprintf "%A" e)
                            | Ok document ->
                                match Mapping.fromDocument None document with
                                | Error e -> Error(sprintf "%A" e)
                                | Ok entry -> Ok entry)
                    |> List.ofSeq
                | _ -> []

            let entries =
                documents
                |> List.choose (fun r ->
                    match r with
                    | Ok entry -> Some entry
                    | Error _ -> None)

            let unreadable =
                documents
                |> List.choose (fun r ->
                    match r with
                    | Ok _ -> None
                    | Error detail -> Some detail)

            let date =
                match request.["date"] with
                | null -> None
                | value ->
                    let parts = value.ToString().Split('-')

                    if parts.Length = 3 then
                        match
                            System.Int32.TryParse parts.[0],
                            System.Int32.TryParse parts.[1],
                            System.Int32.TryParse parts.[2]
                        with
                        | (true, y), (true, m), (true, d) ->
                            match EntryDate.ofYearMonthDay y m d with
                            | Ok parsed -> Some parsed
                            | Error _ -> None
                        | _ -> None
                    else
                        None

            match date with
            | None -> errorResult "date must be YYYY-MM-DD"
            | Some day ->
                let projection =
                    Projection.project displayPolicy (fun _ -> []) (EntryQuery.forDay day) entries

                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("totalMilliseconds", JsonValue.Create projection.TotalMilliseconds)
                node.Add("totalBillableUnits", JsonValue.Create projection.TotalBillableUnits)
                node.Add("displayHours", JsonValue.Create projection.TotalDisplayHours)
                node.Add("displayMinutes", JsonValue.Create projection.TotalDisplayMinutes)
                node.Add("countedEntries", JsonValue.Create projection.CountedEntries)
                node.Add("excludedEntries", JsonValue.Create projection.ExcludedEntries)
                node.Add("isEmpty", JsonValue.Create projection.IsEmpty)
                node.Add("unreadable", JsonValue.Create(List.length unreadable))

                let rows = JsonArray()

                for row in projection.Entries do
                    let item = JsonObject()
                    item.Add("id", JsonValue.Create(TimeEntry.Semantic.Identifiers.EntryId.value row.Id))
                    item.Add("durationMilliseconds", JsonValue.Create row.DurationMilliseconds)
                    item.Add("billableUnits", JsonValue.Create row.BillableUnits)
                    item.Add("displayHours", JsonValue.Create row.DisplayHours)
                    item.Add("displayMinutes", JsonValue.Create row.DisplayMinutes)
                    item.Add("countsTowardTotals", JsonValue.Create row.CountsTowardTotals)

                    item.Add(
                        "description",
                        match row.Description with
                        | Some text -> JsonValue.Create text
                        | None -> null
                    )

                    let badges = JsonArray()

                    for badge in row.Badges do
                        badges.Add(JsonValue.Create(sprintf "%A" badge))

                    item.Add("badges", badges)

                    let capabilities = JsonArray()

                    for capability in row.Capabilities do
                        capabilities.Add(JsonValue.Create(sprintf "%A" capability))

                    item.Add("capabilities", capabilities)
                    rows.Add item

                node.Add("entries", rows)
                node.ToJsonString(jsonOptions)
    with ex ->
        // The boundary never throws into JS: a malformed request becomes a
        // structured answer the page can render.
        errorResult (ex.GetType().Name + ": " + ex.Message)
