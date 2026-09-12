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
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Projection.Query
open TimeEntry.Projection.Projection
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Transitions
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

/// Render a typed domain error as text for the page.
///
/// The page displays this string; it never parses it. Keeping the domain's
/// own error type out of the wire format is what stops the browser growing a
/// second copy of the rule that produced it (TE-R-092).
let private describe (result: Result<'a, 'e>) =
    result |> Result.mapError (fun e -> sprintf "%A" e)

/// How the design writes a duration: `today.html` shows "52m" under an hour
/// and "1h 04m" over it, zero-padding the minutes. Composed here rather than
/// in the bridge, so the browser holds no format and performs no arithmetic
/// (TE-R-085).
let private displayTime (hours: int) (minutes: int) =
    if hours = 0 then
        sprintf "%dm" minutes
    else
        sprintf "%dh %02dm" hours minutes

/// A badge as the adopted design system states it: the words and the tone
/// class, both decided here.
///
/// The vocabulary is taken from `static-ui-screens/today.html`, which already
/// writes "Timer", "Manual entry", "Corrected once", "2 evidence" and
/// "Purpose missing" against `badge-good` / `badge-warn` / `badge-change`.
/// Adopting it rather than inventing new words keeps one design authority
/// (DF-TE-0008).
///
/// Two cases the screens never showed — a removed entry and a superseded one
/// — are worded here as "Removed" and "Replaced", following `remove.html`
/// ("Remove from totals"). That is a gap in the design, recorded as OQ-7
/// rather than presented as settled.
let private badgeView (badge: EntryBadge) : string * string =
    match badge with
    | TimedEntry -> "Timer", "badge-good"
    | ManualEntry -> "Manual entry", "badge-warn"
    | CorrectedBadge 1 -> "Corrected once", "badge-change"
    | CorrectedBadge times -> sprintf "Corrected %d times" times, "badge-change"
    | EvidenceCount 1 -> "1 evidence", "badge-good"
    | EvidenceCount count -> sprintf "%d evidence" count, "badge-good"
    | PurposeMissing -> "Purpose missing", "badge-warn"
    | VoidedBadge -> "Removed", "badge-warn"
    | SupersededBadge -> "Replaced", "badge-change"

/// How the design writes an entry's classification: "Research · Echelon
/// Foundry" — activity type first, then project (`today.html`).
///
/// Resolving an id to its display name is a catalogue lookup, and composing
/// the two into one line is a formatting rule. Both are done here so the
/// browser receives a finished string (TE-R-085). A reference the catalogue
/// does not know falls back to its id rather than to an empty string, so a
/// missing catalogue entry is visible instead of silently blank.
let private classificationOf (catalogue: Catalogue) (view: EntryView) : string =
    let projectName =
        match Catalogue.tryProject view.Project catalogue with
        | Some project -> CatalogueName.value project.Name
        | None -> ProjectId.value view.Project

    let activityName =
        match Catalogue.tryActivityType view.ActivityType catalogue with
        | Some activityType -> CatalogueName.value activityType.Name
        | None -> ActivityTypeId.value view.ActivityType

    sprintf "%s · %s" activityName projectName

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

            // Optional: supplied so ids can be resolved to the names the
            // design shows. A view without it still renders, with ids.
            let catalogue =
                match request.["catalogue"] with
                | null -> Catalogue.empty
                | node ->
                    match Serialization.readCatalogue (node.ToJsonString()) with
                    | Error _ -> Catalogue.empty
                    | Ok document ->
                        Mapping.catalogueFromDocument document
                        |> Result.defaultValue Catalogue.empty

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

                // Finished strings, as today.html writes them: "4h 18m" and
                // "4 entries". The bridge places them; it composes nothing.
                node.Add(
                    "displayTotal",
                    JsonValue.Create(displayTime projection.TotalDisplayHours projection.TotalDisplayMinutes)
                )

                node.Add(
                    "countLabel",
                    JsonValue.Create(
                        if projection.CountedEntries = 1 then
                            "1 entry"
                        else
                            sprintf "%d entries" projection.CountedEntries
                    )
                )
                node.Add("unreadable", JsonValue.Create(List.length unreadable))

                let rows = JsonArray()

                for row in projection.Entries do
                    let item = JsonObject()
                    item.Add("id", JsonValue.Create(TimeEntry.Semantic.Identifiers.EntryId.value row.Id))
                    item.Add("durationMilliseconds", JsonValue.Create row.DurationMilliseconds)
                    item.Add("billableUnits", JsonValue.Create row.BillableUnits)
                    item.Add("displayHours", JsonValue.Create row.DisplayHours)
                    item.Add("displayMinutes", JsonValue.Create row.DisplayMinutes)
                    item.Add("displayTime", JsonValue.Create(displayTime row.DisplayHours row.DisplayMinutes))
                    item.Add("countsTowardTotals", JsonValue.Create row.CountsTowardTotals)

                    item.Add(
                        "description",
                        match row.Description with
                        | Some text -> JsonValue.Create text
                        | None -> null
                    )

                    item.Add("classification", JsonValue.Create(classificationOf catalogue row))

                    let badges = JsonArray()

                    for badge in row.Badges do
                        let text, tone = badgeView badge
                        let rendered = JsonObject()
                        rendered.Add("text", JsonValue.Create text)
                        rendered.Add("tone", JsonValue.Create tone)
                        badges.Add rendered

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


// ---------------------------------------------------------------------------
// The manual-entry duration grid
// ---------------------------------------------------------------------------

/// The options the six-minute quick-duration grid offers.
///
/// Computed here, not authored in HTML. The grid's two halves — the label a
/// person reads ("18 min") and the value the command carries (3 units) — are
/// two expressions of one definition, `MinutesPerBillableUnit`. The static
/// design screens hard-code the minute labels `6 12 18 … 60` in markup, which
/// is exactly the duplication TE-R-085 forbids at the live boundary: change
/// the unit size and the markup would silently disagree with the domain.
///
/// So the page asks how many options it may show and receives both halves
/// already computed. It renders text and carries an opaque count.
let durationGrid (requestJson: string) : string =
    try
        let requested =
            match JsonNode.Parse requestJson with
            | null -> None
            | request ->
                match request.["maxUnits"] with
                | null -> None
                | value ->
                    match System.Int32.TryParse(value.ToString()) with
                    | true, n -> Some n
                    | _ -> None

        match requested |> Option.defaultValue BillableUnitsPerHour with
        | n when n < 1 -> errorResult "maxUnits must be at least 1"
        | maxUnits ->
            let options = JsonArray()

            for count in 1..maxUnits do
                match Duration.ofMilliseconds (int64 count * MillisecondsPerBillableUnit) with
                | Error _ ->
                    // A count large enough to exceed the maximum duration is
                    // simply not offered. Silently dropping it is right here
                    // and only here: the grid is a menu of shortcuts, so an
                    // unrepresentable one has nothing to report.
                    ()
                | Ok duration ->
                    let units = BillableUnits.ofDuration displayPolicy duration
                    let hours, minutes = BillableUnits.toHoursAndMinutes units
                    let option = JsonObject()
                    option.Add("units", JsonValue.Create count)
                    option.Add("displayHours", JsonValue.Create hours)
                    option.Add("displayMinutes", JsonValue.Create minutes)

                    option.Add(
                        "label",
                        JsonValue.Create(
                            if hours = 0 then sprintf "%d min" minutes
                            elif minutes = 0 then sprintf "%dh" hours
                            else sprintf "%dh %02dm" hours minutes
                        )
                    )

                    options.Add option

            let node = JsonObject()
            node.Add("ok", JsonValue.Create true)
            node.Add("options", options)
            node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


// ---------------------------------------------------------------------------
// The catalogue, as choices
// ---------------------------------------------------------------------------

/// The project and activity-type choices a create form may offer.
///
/// The archived/available distinction is DF-TE-0007's, and the page must not
/// re-derive it from a boolean: it receives `selectable` already decided. An
/// archived reference is still *listed*, because hiding it would make the
/// rejection it produces unexplainable, but it is marked.
let catalogueChoices (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let loaded =
                match request.["catalogue"] with
                | null -> Error "missing 'catalogue'"
                | node ->
                    match Serialization.readCatalogue (node.ToJsonString()) with
                    | Error e -> Error(sprintf "%A" e)
                    | Ok document -> Mapping.catalogueFromDocument document |> describe

            match loaded with
            | Error detail -> errorResult detail
            | Ok catalogue ->
                let render (id: string) (name: CatalogueName) (status: CatalogueStatus) =
                    let item = JsonObject()
                    item.Add("id", JsonValue.Create id)
                    item.Add("name", JsonValue.Create(CatalogueName.value name))

                    item.Add(
                        "selectable",
                        JsonValue.Create(
                            match status with
                            | Available -> true
                            | Archived -> false
                        )
                    )

                    item

                let projects = JsonArray()

                for KeyValue(_, project) in catalogue.Projects do
                    projects.Add(render (ProjectId.value project.Id) project.Name project.Status)

                let activityTypes = JsonArray()

                for KeyValue(_, activityType) in catalogue.ActivityTypes do
                    activityTypes.Add(
                        render (ActivityTypeId.value activityType.Id) activityType.Name activityType.Status
                    )

                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("projects", projects)
                node.Add("activityTypes", activityTypes)
                node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


// ---------------------------------------------------------------------------
// Command dispatch
// ---------------------------------------------------------------------------

/// Read a required string from a JSON object.
let private text (node: JsonNode) (name: string) =
    match node.[name] with
    | null -> Error(sprintf "missing '%s'" name)
    | value ->
        let raw = value.ToString()

        if System.String.IsNullOrWhiteSpace raw then
            Error(sprintf "'%s' is empty" name)
        else
            Ok raw

let private optionalText (node: JsonNode) (name: string) =
    match node.[name] with
    | null -> None
    | value ->
        let raw = value.ToString()
        if System.String.IsNullOrWhiteSpace raw then None else Some raw

/// Who made the change, when, and from where.
///
/// The clock is read by the host and passed in as data: Tier 1 and Tier 2
/// perform no effects, so `OccurredAt` can only ever arrive as an argument
/// (TE-R-093). There is no default — a command that cannot say when it
/// happened is refused rather than stamped with zero.
///
/// Actor and device are the browser session. Identity is genuinely
/// unresolved in this slice — there is no sign-in — and is recorded as an
/// open question rather than invented (OQ-6).
let private attributionOf (command: JsonNode) (revisionId: string) : Result<Attribution, string> =
    let occurredAt =
        match command.["occurredAtMs"] with
        | null -> Error "missing 'occurredAtMs'"
        | value ->
            match System.Int64.TryParse(value.ToString()) with
            | true, ms -> Ok(Instant.ofEpochMilliseconds ms)
            | _ -> Error "occurredAtMs must be an integer"

    occurredAt
    |> Result.bind (fun at ->
        UserId.create "browser"
        |> describe
        |> Result.bind (fun actor ->
            DeviceLabel.create "browser"
            |> describe
            |> Result.bind (fun device ->
                RevisionId.create revisionId
                |> describe
                |> Result.map (fun revision ->
                    { Actor = actor
                      Device = device
                      OccurredAt = at
                      NewRevisionId = revision }))))

let private parseDate (raw: string) =
    let parts = raw.Split('-')

    if parts.Length <> 3 then
        Error "date must be YYYY-MM-DD"
    else
        match
            System.Int32.TryParse parts.[0], System.Int32.TryParse parts.[1], System.Int32.TryParse parts.[2]
        with
        | (true, y), (true, m), (true, d) -> EntryDate.ofYearMonthDay y m d |> describe
        | _ -> Error "date must be YYYY-MM-DD"

/// Name the effect the domain asked for, without performing it.
///
/// The browser never executes an effect — that is the interpreter's job
/// (TE-R-093). Reporting the effect's *name* lets the page say "saving…"
/// truthfully while remaining unable to do the saving itself.
let private effectName (effect: Effect) =
    match effect with
    | LoadEntries _ -> "LoadEntries"
    | LoadProjects -> "LoadProjects"
    | PersistNewEntry _ -> "PersistNewEntry"
    | PersistCorrection _ -> "PersistCorrection"
    | PersistVoid _ -> "PersistVoid"
    | PersistRestore _ -> "PersistRestore"
    | PersistEvidenceAttachment _ -> "PersistEvidenceAttachment"
    | PersistSplit _ -> "PersistSplit"
    | PersistMerge _ -> "PersistMerge"

/// Fold a transition's result back into the loaded set.
///
/// `Outcome.Accepted` carries only the entries the command *touched* — a
/// delta, which is the right shape for a transition. The page, though, holds
/// a loaded set and needs the whole of it to re-project.
///
/// Reconciling the two is done here rather than in the bridge on purpose.
/// It is a rule about identity: a changed entry REPLACES the one it shares an
/// id with, a new one is APPENDED, and the loaded order is preserved so the
/// page's list does not reshuffle under the reader. Written in JavaScript that
/// would be the browser deciding what "the same entry" means (TE-R-098).
let private mergeById (loaded: TimeEntry list) (changed: TimeEntry list) : TimeEntry list =
    let sameId (a: TimeEntry) (b: TimeEntry) = EntryId.value a.Id = EntryId.value b.Id

    let replaced =
        loaded
        |> List.map (fun existing ->
            changed |> List.tryFind (sameId existing) |> Option.defaultValue existing)

    let appended =
        changed
        |> List.filter (fun candidate -> loaded |> List.exists (sameId candidate) |> not)

    replaced @ appended

/// Apply a command.
///
/// Input carries the loaded catalogue, the loaded entries, and the command.
/// Output carries the **whole resulting set** in its stored form — the loaded
/// entries with the command's changes folded in — plus the ids the command
/// touched and the effects the domain requested, so the page can hand those
/// straight to the interpreter without ever interpreting them itself.
///
/// A rejection is a normal answer, not an error: the UI needs to render
/// "an archived project cannot take new time" as readily as a success.
let dispatch (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->

        let catalogue =
            match request.["catalogue"] with
            | null -> Ok Catalogue.empty
            | node ->
                match Serialization.readCatalogue (node.ToJsonString()) with
                | Error e -> Error(sprintf "%A" e)
                | Ok document -> Mapping.catalogueFromDocument document |> describe

        // Version tokens arrive alongside the documents, never inside them:
        // the token is a hash OF the document, so storing it in the content
        // would be circular (see `Persistence.Documents`). The transport
        // knows path -> blob SHA, so it supplies a map keyed by entry id.
        //
        // An entry loaded without a token gets `Version = None`, and every
        // mutating transition then refuses it with `VersionConflict`. That is
        // the correct outcome, not a gap: a caller that cannot say which
        // version it read must not be allowed to overwrite (TE-R-070).
        let versionFor =
            match request.["versions"] with
            | :? JsonObject as tokens ->
                fun (entryId: string) ->
                    match tokens.[entryId] with
                    | null -> None
                    | value -> VersionToken.create (value.ToString()) |> Result.toOption
            | _ -> fun _ -> None

        let loaded =
            match request.["entries"] with
            | :? JsonArray as items ->
                items
                |> Seq.choose (fun item ->
                    match item with
                    | null -> None
                    | node ->
                        match Serialization.read (node.ToJsonString()) with
                        | Error _ -> None
                        | Ok document ->
                            match Mapping.fromDocument (versionFor document.entry_id) document with
                            | Error _ -> None
                            | Ok entry -> Some entry)
                |> List.ofSeq
            | _ -> []

        let built =
            catalogue
            |> Result.bind (fun cat ->
                match request.["command"] with
                | null -> Error "missing 'command'"
                | command ->
                    match text command "kind" with
                    | Error e -> Error e
                    | Ok "create" ->
                        // Every value is validated by a domain smart
                        // constructor. The kernel parses JSON; it does not
                        // decide what is valid.
                        text command "entryId"
                        |> Result.bind (fun id -> EntryId.create id |> describe)
                        |> Result.bind (fun entryId ->
                            text command "projectId"
                            |> Result.bind (fun raw -> ProjectId.create raw |> describe)
                            |> Result.map (fun project -> entryId, project))
                        |> Result.bind (fun (entryId, project) ->
                            text command "activityTypeId"
                            |> Result.bind (fun raw -> ActivityTypeId.create raw |> describe)
                            |> Result.map (fun activityType -> entryId, project, activityType))
                        |> Result.bind (fun (entryId, project, activityType) ->
                            text command "date"
                            |> Result.bind parseDate
                            |> Result.map (fun date -> entryId, project, activityType, date))
                        |> Result.bind (fun (entryId, project, activityType, date) ->
                            // Accepts either exact milliseconds (a stopped
                            // timer) or whole billable units (the
                            // six-minute manual grid, system-prompt 8.4).
                            //
                            // Units rather than minutes is deliberate: the
                            // browser must not multiply by 6 or by 60,000,
                            // because that would put the unit definition in
                            // two places and a time conversion in the
                            // bridge (TE-R-085). It sends a count; F#
                            // converts.
                            (match command.["durationMs"], command.["durationUnits"] with
                             | null, null -> Error "missing 'durationMs' or 'durationUnits'"
                             | value, _ when not (isNull value) ->
                                 match System.Int64.TryParse(value.ToString()) with
                                 | true, ms -> Duration.ofMilliseconds ms |> describe
                                 | _ -> Error "durationMs must be an integer"
                             | _, units ->
                                 match System.Int64.TryParse(units.ToString()) with
                                 | true, count ->
                                     Duration.ofMilliseconds (count * MillisecondsPerBillableUnit)
                                     |> describe
                                 | _ -> Error "durationUnits must be an integer")
                            |> Result.map (fun duration -> entryId, project, activityType, date, duration))
                        |> Result.bind (fun (entryId, project, activityType, date, duration) ->
                            (match optionalText command "description" with
                             | None -> Ok None
                             | Some raw -> Description.create raw |> describe |> Result.map Some)
                            |> Result.bind (fun description ->
                                attributionOf command (EntryId.value entryId + "-r1")
                                |> Result.map (fun attribution ->
                                    cat,
                                    CreateEntry
                                        { NewEntryId = entryId
                                          Facts =
                                            { Project = project
                                              ActivityType = activityType
                                              Date = date
                                              Duration = duration
                                              Description = description
                                              Origin = Timed
                                              Evidence = [] }
                                          Attribution = attribution })))
                    | Ok "void" ->
                        // Voiding takes a reason and the version the caller
                        // read. Both are required by the command type itself,
                        // which is why this branch cannot forget either
                        // (TE-R-070, system-prompt 8.9).
                        text command "entryId"
                        |> Result.bind (fun id -> EntryId.create id |> describe)
                        |> Result.bind (fun entryId ->
                            text command "expectedVersion"
                            |> Result.bind (fun raw -> VersionToken.create raw |> describe)
                            |> Result.map (fun version -> entryId, version))
                        |> Result.bind (fun (entryId, version) ->
                            text command "reason"
                            |> Result.bind (fun raw -> Reason.create raw |> describe)
                            |> Result.map (fun reason -> entryId, version, reason))
                        |> Result.bind (fun (entryId, version, reason) ->
                            attributionOf command (EntryId.value entryId + "-void")
                            |> Result.map (fun attribution ->
                                cat,
                                VoidEntry
                                    { EntryId = entryId
                                      ExpectedVersion = version
                                      Reason = reason
                                      Attribution = attribution }))
                    | Ok other -> Error(sprintf "unsupported command kind '%s'" other))

        match built with
        | Error detail -> errorResult detail
        | Ok(cat, command) ->
            match apply cat loaded command with
            | Rejected rejection ->
                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("accepted", JsonValue.Create false)
                // A typed rejection, rendered as text for the page. The page
                // displays it; it does not interpret it.
                node.Add("rejection", JsonValue.Create(sprintf "%A" rejection))
                node.ToJsonString(jsonOptions)
            | Accepted(changed, effects) ->
                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("accepted", JsonValue.Create true)

                let stored = JsonArray()

                for entry in mergeById loaded changed do
                    stored.Add(JsonNode.Parse(Serialization.write (Mapping.toDocument entry)))

                node.Add("entries", stored)

                let touched = JsonArray()

                for entry in changed do
                    touched.Add(JsonValue.Create(EntryId.value entry.Id))

                node.Add("changed", touched)

                let requested = JsonArray()

                for effect in effects do
                    requested.Add(JsonValue.Create(effectName effect))

                node.Add("effects", requested)
                node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)
