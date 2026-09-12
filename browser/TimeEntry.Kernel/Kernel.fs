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
open TimeEntry.Semantic.Preferences
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Identity
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Projection.Query
open TimeEntry.Projection.Projection
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects
open TimeEntry.Transitions.Transitions
open TimeEntry.Persistence
open TimeEntry.GitHub
open TimeEntry.Browser

/// Rounding policy for display. A single place, so the browser cannot pick
/// its own and disagree with the server's totals.
let private displayPolicy = RoundUp

let private jsonOptions = JsonSerializerOptions(WriteIndented = false)

let private errorResult (detail: string) =
    let node = JsonObject()
    node.Add("ok", JsonValue.Create false)
    node.Add("error", JsonValue.Create detail)
    node.ToJsonString()

/// Render a typed domain error as the sentence a person reads.
///
/// The page displays this string; it never parses it. Keeping the domain's
/// own error type out of the wire format is what stops the browser growing a
/// second copy of the rule that produced it (TE-R-092) — and wording it here
/// rather than dumping it with `%A` is what keeps technical vocabulary out of
/// the interface (TE-R-053).
let private describe (result: Result<'a, 'e>) =
    result |> Result.mapError (fun e -> Wording.ofError (box e))

/// How the design writes a duration: `today.html` shows "52m" under an hour
/// and "1h 04m" over it, zero-padding the minutes. Composed here rather than
/// in the bridge, so the browser holds no format and performs no arithmetic
/// (TE-R-085).
let private displayTime (hours: int) (minutes: int) =
    if hours = 0 then
        sprintf "%dm" minutes
    else
        sprintf "%dh %02dm" hours minutes

/// Exact elapsed time, written out.
///
/// Distinct from `displayTime`, which shows BILLED time — a 52-minute entry
/// displays as 54 minutes there, because billing rounds up (DF-TE-0002).
/// A split preview must show the exact figure instead: the invariant the
/// person is being asked to satisfy is exact (TE-R-040), so showing them a
/// rounded remainder would ask them to balance one quantity while displaying
/// another.
///
/// Signed, because a preview's remainder goes negative the moment more time
/// is allocated than the source holds, and "-6m" is the fact worth showing.
let private displayExact (milliseconds: int64) =
    let sign = if milliseconds < 0L then "-" else ""
    let magnitude = abs milliseconds
    let totalMinutes = magnitude / (MillisecondsPerSecond * int64 SecondsPerMinute)
    let hours = totalMinutes / 60L
    let minutes = totalMinutes % 60L

    if hours = 0L then
        sprintf "%s%dm" sign minutes
    else
        sprintf "%s%dh %02dm" sign hours minutes

/// A badge as the adopted design system states it: the words and the tone
/// class, both decided here.
///
/// The vocabulary is taken from `static-ui-screens/today.html`, which already
/// writes "Timer", "Manual entry", "Corrected once", "2 evidence" and
/// "Purpose missing" against `badge-good` / `badge-warn` / `badge-change`.
/// Adopting it rather than inventing new words keeps one design authority
/// (DF-TE-0008).
///
/// Two cases the screens never showed are a removed entry and a superseded
/// one. The user has settled the first: a removed entry is labelled "Removed"
/// (DF-TE-0014, resolving OQ-7). The second was not named in that answer, so
/// "Replaced" remains this kernel's reading of `remove.html`'s "Remove from
/// totals" vocabulary rather than a stated word — recorded as OQ-12, and kept
/// distinct from "Removed" because a superseded entry has a successor and a
/// removed one does not.
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
///
/// `visibility` selects whether entries that do not count toward totals are
/// shown. The page may ask; it may not decide — and it cannot express
/// "show these and also sort them yourself", because `EntryQuery` has no
/// vocabulary for it.
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
                            | Error e -> Error(Wording.ofError (box e))
                            | Ok document ->
                                match Mapping.fromDocument None document with
                                | Error e -> Error(Wording.ofError (box e))
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

            // Named rather than numbered so a request is readable in a
            // network log, and so an unrecognised value is a refusal rather
            // than a silent fallback to the safest-looking option: a view
            // that quietly hides removed entries when the page asked to show
            // them is how a restore becomes impossible to reach.
            let visibility =
                match request.["visibility"] with
                | null -> Ok CountingOnly
                | value ->
                    match value.ToString() with
                    | "counting" -> Ok CountingOnly
                    | "includeRemoved" -> Ok IncludeVoided
                    | "all" -> Ok IncludeAll
                    | other -> Error(sprintf "unknown visibility '%s'" other)

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

            match date, visibility with
            | None, _ -> errorResult "date must be YYYY-MM-DD"
            | _, Error detail -> errorResult detail
            | Some day, Ok shown ->
                let query =
                    { EntryQuery.forDay day with
                        Visibility = shown }

                let projection = Projection.project displayPolicy (fun _ -> []) query entries

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

                    // The ids as well as the composed label: a correction
                    // form has to pre-select the entry's CURRENT project and
                    // activity, and it must do that by id rather than by
                    // matching the display name, which is not unique and not
                    // stable.
                    item.Add("projectId", JsonValue.Create(ProjectId.value row.Project))
                    item.Add("activityTypeId", JsonValue.Create(ActivityTypeId.value row.ActivityType))
                    item.Add("classification", JsonValue.Create(classificationOf catalogue row))

                    // The evidence this entry holds, so a split can offer it
                    // for reassignment. Sent whole because the transition
                    // requires an exact match on the way back (TE-R-045).
                    let evidence = JsonArray()

                    for item' in (row: EntryView).Evidence do
                        let rendered = JsonObject()
                        rendered.Add("uri", JsonValue.Create item'.Uri)

                        rendered.Add(
                            "label",
                            match item'.Label with
                            | Some label -> JsonValue.Create(Description.value label)
                            | None -> null
                        )

                        rendered.Add(
                            "attachedAtMs",
                            JsonValue.Create(Instant.epochMilliseconds item'.AttachedAt)
                        )

                        evidence.Add rendered

                    item.Add("evidence", evidence)

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
                        // The stable wire name, not prose: the page keys on
                        // these to decide which controls to offer.
                        capabilities.Add(JsonValue.Create(Wording.capabilityName capability))

                    item.Add("capabilities", capabilities)
                    rows.Add item

                node.Add("entries", rows)
                node.ToJsonString(jsonOptions)
    with ex ->
        // The boundary never throws into JS: a malformed request becomes a
        // structured answer the page can render.
        errorResult (ex.GetType().Name + ": " + ex.Message)


/// A moment, written for a person.
///
/// The offset comes from the host as data rather than being read here: the
/// kernel performs no effects and has no business knowing where the reader
/// is. The browser knows its own offset and states it; this does arithmetic
/// on what it was told. Absent an offset, UTC is used and labelled as such —
/// guessing a zone would silently misdate every revision by up to a day.
let private displayMoment (offsetMinutes: int) (epochMilliseconds: int64) =
    let shifted =
        System.DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds).ToOffset(
            System.TimeSpan.FromMinutes(float offsetMinutes)
        )

    let suffix = if offsetMinutes = 0 then " UTC" else ""
    shifted.ToString("d MMM yyyy, HH:mm") + suffix

/// What a revision did, in the words a ledger uses rather than the words a
/// database uses.
///
/// TE-R-053 forbids technical event-sourcing language in the main UI, so
/// "superseded", "revision" and "event" do not appear here. The domain keeps
/// its own vocabulary; this translates it once, at the boundary.
let private changeWording (change: RevisionChange) =
    match change with
    | Created -> "Recorded", None
    | Corrected reason -> "Corrected", Some(Reason.value reason)
    | Voided reason -> "Removed from totals", Some(Reason.value reason)
    | Restored reason -> "Returned to totals", Some(Reason.value reason)
    | SplitInto children ->
        sprintf "Split into %d records" (List.length children), None
    | MergedInto _ -> "Merged into another record", None
    | CreatedBySplitOf source ->
        sprintf "Created by splitting %s" (EntryId.value source), None
    | CreatedByMergeOf sources ->
        sprintf "Created by merging %d records" (List.length sources), None
    | EvidenceAttached evidence -> "Evidence attached", Some evidence.Uri
    | EvidenceReassignedFrom source ->
        sprintf "Evidence moved from %s" (EntryId.value source), None

/// Which facts differ between two points in an entry's history.
///
/// This is TE-R-052's "original values and changed values". Computed by
/// comparing a revision's facts against the one before it, rather than stored
/// as a diff: a stored diff can disagree with the values it claims to
/// describe, and the facts are already recorded at every revision precisely
/// so this does not need reconstruction.
let private differences (previous: EntryFacts option) (current: EntryFacts) =
    match previous with
    | None -> []
    | Some before ->
        [ if before.Duration <> current.Duration then
              "Duration",
              displayExact (Duration.milliseconds before.Duration),
              displayExact (Duration.milliseconds current.Duration)

          if before.Project <> current.Project then
              "Project", ProjectId.value before.Project, ProjectId.value current.Project

          if before.ActivityType <> current.ActivityType then
              "Activity type",
              ActivityTypeId.value before.ActivityType,
              ActivityTypeId.value current.ActivityType

          if before.Date <> current.Date then
              let by, bm, bd = EntryDate.toYearMonthDay before.Date
              let cy, cm, cd = EntryDate.toYearMonthDay current.Date

              "Date",
              sprintf "%04d-%02d-%02d" by bm bd,
              sprintf "%04d-%02d-%02d" cy cm cd

          if before.Description <> current.Description then
              "Description",
              (match before.Description with
               | Some d -> Description.value d
               | None -> "(none)"),
              (match current.Description with
               | Some d -> Description.value d
               | None -> "(none)") ]

/// One entry's whole history (TE-R-052).
///
/// Shows what each change did, why, when, by whom and from which device, plus
/// the values that changed — and the current effective result, which is what
/// the ledger actually stands behind.
///
/// Raw stored JSON is NOT included. TE-R-054 permits a raw record view for
/// advanced users but forbids exposing it by default, and nothing here needs
/// it: every value a reader wants is already rendered.
let entryHistory (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let offsetMinutes =
                match request.["timeZoneOffsetMinutes"] with
                | null -> 0
                | value ->
                    match System.Int32.TryParse(value.ToString()) with
                    | true, parsed -> parsed
                    | _ -> 0

            let catalogue =
                match request.["catalogue"] with
                | null -> Catalogue.empty
                | node ->
                    match Serialization.readCatalogue (node.ToJsonString()) with
                    | Error _ -> Catalogue.empty
                    | Ok document ->
                        Mapping.catalogueFromDocument document
                        |> Result.defaultValue Catalogue.empty

            let entries =
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
                                match Mapping.fromDocument None document with
                                | Error _ -> None
                                | Ok entry -> Some entry)
                    |> List.ofSeq
                | _ -> []

            match request.["entryId"] with
            | null -> errorResult "missing 'entryId'"
            | wanted ->
                let id = wanted.ToString()

                match entries |> List.tryFind (fun e -> EntryId.value e.Id = id) with
                | None -> errorResult (sprintf "no entry '%s' is loaded" id)
                | Some entry ->
                    let node = JsonObject()
                    node.Add("ok", JsonValue.Create true)
                    node.Add("entryId", JsonValue.Create id)

                    let view = Projection.toView displayPolicy [] entry
                    node.Add("description", JsonValue.Create(Option.toObj view.Description))
                    node.Add("classification", JsonValue.Create(classificationOf catalogue view))
                    node.Add("displayTime", JsonValue.Create(displayTime view.DisplayHours view.DisplayMinutes))
                    node.Add("countsTowardTotals", JsonValue.Create view.CountsTowardTotals)

                    // History is newest first in the domain. Rendered oldest
                    // first, because a history read top to bottom is a story
                    // and a story starts at the beginning — and because
                    // "what changed" only means anything against what came
                    // before it.
                    let oldestFirst = List.rev entry.History
                    let revisions = JsonArray()

                    oldestFirst
                    |> List.iteri (fun index revision ->
                        let item = JsonObject()
                        let title, detail = changeWording revision.Change
                        item.Add("change", JsonValue.Create title)
                        item.Add("detail", JsonValue.Create(Option.toObj detail))

                        item.Add(
                            "recordedAt",
                            JsonValue.Create(
                                displayMoment offsetMinutes (Instant.epochMilliseconds revision.RecordedAt)
                            )
                        )

                        item.Add("recordedBy", JsonValue.Create(UserId.value revision.RecordedBy))
                        item.Add("device", JsonValue.Create(DeviceLabel.value revision.Device))

                        let previous =
                            if index = 0 then
                                None
                            else
                                Some (List.item (index - 1) oldestFirst).Facts

                        let changes = JsonArray()

                        for field, before, after in differences previous revision.Facts do
                            let change = JsonObject()
                            change.Add("field", JsonValue.Create field)
                            change.Add("from", JsonValue.Create before)
                            change.Add("to", JsonValue.Create after)
                            changes.Add change

                        item.Add("changed", changes)
                        revisions.Add item)

                    node.Add("revisions", revisions)
                    node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


/// Whether a day's record is complete enough to stand behind.
///
/// `review.html` lists named checks with a tick or a warning. The checks are
/// built from the domain's own `Obligation`s rather than re-derived here, so
/// "what must be true before a day can be attested" has one definition —
/// `Obligation.blocksAttestation` — and this reports it rather than repeating
/// it.
///
/// One check on that screen is NOT here: "No overlapping time". The user has
/// settled OQ-10 — overlapping recorded time is NOT a defect, because time is
/// recorded in six-minute units and two units can legitimately cover the same
/// stretch of clock (DF-TE-0013). There is therefore nothing for the check to
/// fail, and no row is emitted: a permanent tick would imply a test that runs.
/// Reconciling genuinely double-counted time is deferred work (WI-0052), not a
/// daily check.
let reviewDay (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let entries =
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
                                match Mapping.fromDocument None document with
                                | Error _ -> None
                                | Ok entry -> Some entry)
                    |> List.ofSeq
                | _ -> []

            match
                (match request.["date"] with
                 | null -> Error "missing 'date'"
                 | value -> CommandParsing.parseDate (value.ToString()))
            with
            | Error detail -> errorResult detail
            | Ok day ->
                let onDay =
                    entries |> List.filter (fun entry -> entry.Effective.Date = day)

                let counting = onDay |> List.filter TimeEntry.countsTowardTotals
                let summary = PeriodSummary.ofEntries displayPolicy onDay

                let obligations = counting |> List.collect Obligation.intrinsic

                let missingPurpose =
                    obligations
                    |> List.filter (fun o ->
                        match o with
                        | BusinessPurposeMissing -> true
                        | _ -> false)
                    |> List.length

                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("displayTotal", JsonValue.Create(displayTime summary.DisplayHours summary.DisplayMinutes))
                node.Add("countedEntries", JsonValue.Create summary.CountedEntries)
                node.Add("displayTimed", JsonValue.Create(displayExact summary.TimedMilliseconds))
                node.Add("displayManual", JsonValue.Create(displayExact summary.ManualMilliseconds))
                node.Add("entriesWithEvidence", JsonValue.Create summary.EntriesWithEvidence)
                node.Add("correctedEntries", JsonValue.Create summary.CorrectedEntries)
                node.Add("removedEntries", JsonValue.Create summary.RemovedEntries)

                // A day with nothing in it is not "complete" — there is
                // nothing to attest. Saying so is more useful than a row of
                // ticks over an empty ledger.
                let empty = List.isEmpty counting

                let checks = JsonArray()

                let addCheck (title: string) (note: string) (ok: bool) =
                    let item = JsonObject()
                    item.Add("title", JsonValue.Create title)
                    item.Add("note", JsonValue.Create note)
                    item.Add("status", JsonValue.Create(if ok then "complete" else "attention"))
                    checks.Add item

                addCheck
                    "Every record describes the work performed"
                    (if missingPurpose = 0 then
                         sprintf "%d of %d records describe the work." summary.CountedEntries summary.CountedEntries
                     else
                         sprintf
                             "%d of %d records still need a description."
                             missingPurpose
                             summary.CountedEntries)
                    (missingPurpose = 0)

                addCheck
                    "Manual records state why they were entered by hand"
                    // Structural rather than checked: `Manual` cannot be
                    // constructed without a `Reason`, so a manual record
                    // without one does not exist to be found.
                    (if summary.ManualMilliseconds = 0L then
                         "No manual records today."
                     else
                         sprintf "%s entered by hand, each with a stated reason." (displayExact summary.ManualMilliseconds))
                    true

                addCheck
                    "Removed time is excluded but retained"
                    (if summary.RemovedEntries = 0 then
                         "Nothing was removed today."
                     else
                         sprintf
                             "%d record(s) removed from totals; their history is kept."
                             summary.RemovedEntries)
                    true

                node.Add("checks", checks)
                node.Add("needsAttention", JsonValue.Create missingPurpose)
                node.Add("isEmpty", JsonValue.Create empty)

                // The single question the screen exists to answer. Decided by
                // the domain's own rule, not by counting ticks above.
                node.Add(
                    "canAttest",
                    JsonValue.Create(
                        not empty && not (obligations |> List.exists Obligation.blocksAttestation)
                    )
                )

                node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


/// A month's recorded work.
///
/// The same entries the day view reads, summarised over a date range. Every
/// figure is computed by `PeriodSummary`, including the ones that look like
/// presentation: decimal hours are exact tenths rather than a float, because
/// a billable unit is exactly one tenth of an hour and TE-R-007 forbids
/// floating point as authoritative for billable time.
///
/// What this deliberately does NOT report is a target. `month.html` shows
/// "80h target" and a progress bar against it, but no repository document
/// states a target, where it comes from, or who sets it. Rendering a bar
/// against an invented number would be inventing a requirement, so the
/// figures are reported without one and OQ-9 records the gap.
/// The preferences the page holds, decoded here rather than in the page.
///
/// The page carries the preferences file the same way it carries entry
/// documents: verbatim, as it was read. Decoding it in F# means the page never
/// learns what `monthly_target_units` is, and a corrupt file produces one
/// worded message instead of a JavaScript `undefined` propagating into a bar
/// width (TE-R-091).
///
/// An absent `preferences` key is "nothing set", not an error. A month can be
/// viewed before any target exists, and requiring the key would make the first
/// view of a new ledger fail.
let private preferencesFrom (request: JsonNode) : Result<Preferences, string> =
    match request.["preferences"] with
    | null -> Ok Preferences.none
    | node ->
        match Serialization.readPreferences (node.ToJsonString()) with
        | Error decodeError -> Error(Wording.ofError (box decodeError))
        | Ok document ->
            match Mapping.preferencesFromDocument document with
            | Error documentError -> Error(Wording.ofError (box documentError))
            | Ok preferences -> Ok preferences

/// Progress against a set target, as the design writes it.
///
/// Emitted as `null` when no target is set, and the page renders no bar. That
/// is the whole of DF-TE-0015's restraint: `month.html` draws a bar against
/// "80h target", and since no document says where 80 comes from, a ledger with
/// no target set gets its figures and no bar rather than a bar against an
/// invented number.
///
/// `barPercent` is clamped to 100 and `percentRecorded` is not. A bar wider
/// than its track is a rendering bug; "112% recorded" is a fact. Separating
/// them here keeps the page from having to decide which it wants.
let private targetNode (summary: PeriodSummary) (target: TrackingTarget) : JsonObject =
    let progress = TargetProgress.against target summary
    let node = JsonObject()
    node.Add("targetUnits", JsonValue.Create progress.TargetUnits)

    node.Add(
        "displayTarget",
        JsonValue.Create(displayTime progress.TargetDisplayHours progress.TargetDisplayMinutes)
    )

    node.Add("percentRecorded", JsonValue.Create progress.PercentRecorded)
    node.Add("barPercent", JsonValue.Create(min 100 progress.PercentRecorded))
    node.Add("reached", JsonValue.Create progress.Reached)

    node.Add(
        "displayRemaining",
        JsonValue.Create(displayTime progress.RemainingDisplayHours progress.RemainingDisplayMinutes)
    )

    // `month.html`'s two lines, worded here. "Formal program determination:
    // Not evaluated" is kept verbatim from the design: the screen states
    // plainly that this view makes no such determination, and dropping that
    // sentence would let a reader take the bar for one.
    // The bar is `role="img"`, so its only accessible name is this label.
    // `month.html` writes it as "68 percent of 80 hour tracking target
    // recorded" — a sentence, composed from two domain figures, which is
    // exactly the kind of string TE-R-085 keeps out of the page.
    node.Add(
        "ariaLabel",
        JsonValue.Create(
            sprintf
                "%d percent of a %s tracking target recorded"
                progress.PercentRecorded
                (displayTime progress.TargetDisplayHours progress.TargetDisplayMinutes)
        )
    )

    node.Add(
        "headline",
        JsonValue.Create(if progress.Reached then "Tracking target reached: Yes" else "Tracking target reached: No")
    )

    node.Add(
        "detail",
        JsonValue.Create(
            if progress.Reached then
                "Formal program determination: Not evaluated."
            else
                sprintf
                    "%s remains. Formal program determination: Not evaluated."
                    (displayTime progress.RemainingDisplayHours progress.RemainingDisplayMinutes)
        )
    )

    node

let viewMonth (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let readInt (name: string) =
                match request.[name] with
                | null -> Error(sprintf "missing '%s'" name)
                | value ->
                    match System.Int32.TryParse(value.ToString()) with
                    | true, parsed -> Ok parsed
                    | _ -> Error(sprintf "'%s' must be an integer" name)

            let entries =
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
                                match Mapping.fromDocument None document with
                                | Error _ -> None
                                | Ok entry -> Some entry)
                    |> List.ofSeq
                | _ -> []

            match readInt "year", readInt "month" with
            | Error detail, _
            | _, Error detail -> errorResult detail
            | Ok year, Ok month ->
                // The range is the whole month, built from the calendar rather
                // than assumed: month lengths differ and February differs by
                // year, so counting days here would be a second calendar.
                match EntryDate.ofYearMonthDay year month 1 with
                | Error e -> errorResult (Wording.ofError (box e))
                | Ok first ->
                    let lastDay = System.DateTime.DaysInMonth(year, month)

                    match EntryDate.ofYearMonthDay year month lastDay with
                    | Error e -> errorResult (Wording.ofError (box e))
                    | Ok last ->
                        let query =
                            { EntryQuery.forDay first with
                                DateRange = Some { From = first; To = last }
                                // Removed entries are counted as removed, so
                                // they must be selected in order to be seen.
                                Visibility = IncludeAll }

                        let selected =
                            entries
                            |> List.filter (fun entry ->
                                match query.DateRange with
                                | Some range -> DateRange.contains entry.Effective.Date range
                                | None -> true)

                        let summary = PeriodSummary.ofEntries displayPolicy selected

                        let node = JsonObject()
                        node.Add("ok", JsonValue.Create true)
                        node.Add("totalMilliseconds", JsonValue.Create summary.TotalMilliseconds)
                        node.Add("totalBillableUnits", JsonValue.Create summary.TotalBillableUnits)

                        node.Add(
                            "displayTotal",
                            JsonValue.Create(displayTime summary.DisplayHours summary.DisplayMinutes)
                        )

                        // "54.6" — composed here so the browser never builds a
                        // number out of two domain figures.
                        node.Add(
                            "displayDecimalHours",
                            JsonValue.Create(
                                sprintf "%d.%d" summary.DecimalHoursWhole summary.DecimalHoursTenths
                            )
                        )

                        node.Add("activeDays", JsonValue.Create summary.ActiveDays)
                        node.Add("countedEntries", JsonValue.Create summary.CountedEntries)
                        node.Add("correctedEntries", JsonValue.Create summary.CorrectedEntries)
                        node.Add("removedEntries", JsonValue.Create summary.RemovedEntries)
                        node.Add("entriesWithEvidence", JsonValue.Create summary.EntriesWithEvidence)

                        node.Add(
                            "displayTimed",
                            JsonValue.Create(displayExact summary.TimedMilliseconds)
                        )

                        node.Add(
                            "displayManual",
                            JsonValue.Create(displayExact summary.ManualMilliseconds)
                        )

                        // Integer division, and stated as a count beside it so
                        // "72%" is never the only thing shown. A percentage
                        // alone hides whether it is 31 of 43 or 3 of 4.
                        node.Add(
                            "evidencePercent",
                            JsonValue.Create(
                                if summary.CountedEntries = 0 then
                                    0
                                else
                                    summary.EntriesWithEvidence * 100 / summary.CountedEntries
                            )
                        )

                        match preferencesFrom request with
                        | Error detail -> errorResult detail
                        | Ok preferences ->

                        match preferences.MonthlyTarget with
                        | None -> node.Add("target", null)
                        | Some target -> node.Add("target", targetNode summary target)

                        let days = JsonArray()

                        for day in summary.Days do
                            let y, m, d = EntryDate.toYearMonthDay day.Date
                            let item = JsonObject()
                            item.Add("date", JsonValue.Create(sprintf "%04d-%02d-%02d" y m d))
                            item.Add("totalMilliseconds", JsonValue.Create day.TotalMilliseconds)
                            item.Add("billableUnits", JsonValue.Create day.BillableUnits)
                            item.Add("countedEntries", JsonValue.Create day.CountedEntries)

                            item.Add(
                                "displayTime",
                                JsonValue.Create(displayTime day.DisplayHours day.DisplayMinutes)
                            )

                            // How tall this day's bar should be, as a
                            // percentage of the month's busiest day. Computed
                            // here because it is arithmetic over domain
                            // quantities; the page sets a height and nothing
                            // more (TE-R-085).
                            let busiest =
                                summary.Days |> List.map (fun d -> d.TotalMilliseconds) |> List.max

                            item.Add(
                                "relativeHeightPercent",
                                JsonValue.Create(
                                    if busiest = 0L then
                                        0
                                    else
                                        int (day.TotalMilliseconds * 100L / busiest)
                                )
                            )

                            days.Add item

                        node.Add("days", days)
                        node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


// ---------------------------------------------------------------------------
// Who is signed in
// ---------------------------------------------------------------------------

/// What the page should say about the current sign-in.
///
/// The page holds an ID token and must not look inside it: the display name,
/// the actor and the verdict on whether the token is still usable are all
/// decisions, and this is where decisions live (TE-R-091). A page that read
/// `name` out of the payload itself would also have to decide what to do when
/// there isn't one, which is exactly the judgement `Identity.display` makes.
///
/// Not signed in is `ok: true` with `signedIn: false`, not an error: nobody
/// has done anything wrong by not having signed in yet. A token that is
/// present but unusable IS an error, because something needs saying about it
/// — most often that it has expired and the person should sign in again.
let identityView (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            match request.["occurredAtMs"] with
            | null -> errorResult "missing 'occurredAtMs'"
            | value ->
                match System.Int64.TryParse(value.ToString()) with
                | false, _ -> errorResult "occurredAtMs must be an integer"
                | true, ms ->
                    let now = Instant.ofEpochMilliseconds ms

                    match request.["identity"] with
                    | null ->
                        let node = JsonObject()
                        node.Add("ok", JsonValue.Create true)
                        node.Add("signedIn", JsonValue.Create false)
                        node.Add("display", JsonValue.Create "Not signed in")
                        node.ToJsonString(jsonOptions)
                    | _ ->
                        match CommandParsing.identityFrom request now with
                        | Error detail -> errorResult detail
                        | Ok identity ->
                            match Identity.actor identity with
                            | Error e -> errorResult (Wording.ofError (box e))
                            | Ok actor ->
                                let node = JsonObject()
                                node.Add("ok", JsonValue.Create true)
                                node.Add("signedIn", JsonValue.Create true)
                                node.Add("display", JsonValue.Create(Identity.display identity))
                                node.Add("actor", JsonValue.Create(UserId.value actor))

                                node.Add(
                                    "provider",
                                    JsonValue.Create(
                                        IdentityProvider.label (Identity.provider identity)
                                    )
                                )

                                node.ToJsonString(jsonOptions)
    with ex ->
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
// Split preview
// ---------------------------------------------------------------------------

/// What a split would come to, before it is committed.
///
/// TE-R-044 requires a preview, and a preview is arithmetic over domain
/// quantities — exactly what the browser must not do (TE-R-085). So the page
/// sends the child durations it has collected so far and receives the
/// allocated total, the remainder, and whether the two balance.
///
/// Nothing is applied and nothing is validated beyond reading the numbers: an
/// unbalanced preview is a normal intermediate state, not an error. The
/// refusal happens at `dispatch`, where the transition owns it.
let splitPreview (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let source =
                match request.["sourceMilliseconds"] with
                | null -> Error "missing 'sourceMilliseconds'"
                | value ->
                    match System.Int64.TryParse(value.ToString()) with
                    | true, ms -> Ok ms
                    | _ -> Error "sourceMilliseconds must be an integer"

            match source with
            | Error detail -> errorResult detail
            | Ok sourceMilliseconds ->
                // A child still being typed contributes nothing rather than
                // failing the preview. It is counted as incomplete so the page
                // can say so, because a remainder of zero across two complete
                // children and one empty one is NOT a balanced split.
                let contributions =
                    match request.["children"] with
                    | :? JsonArray as items ->
                        items
                        |> Seq.map (fun item ->
                            match item with
                            | null -> None
                            | child ->
                                match child.["durationMs"], child.["durationUnits"] with
                                | null, null -> None
                                | value, _ when not (isNull value) ->
                                    match System.Int64.TryParse(value.ToString()) with
                                    | true, ms when ms > 0L -> Some ms
                                    | _ -> None
                                | _, units ->
                                    match System.Int64.TryParse(units.ToString()) with
                                    | true, count when count > 0L -> Some(count * MillisecondsPerBillableUnit)
                                    | _ -> None)
                        |> List.ofSeq
                    | _ -> []

                let allocated = contributions |> List.sumBy (Option.defaultValue 0L)
                let incomplete = contributions |> List.filter Option.isNone |> List.length
                let remaining = sourceMilliseconds - allocated

                let node = JsonObject()
                node.Add("ok", JsonValue.Create true)
                node.Add("sourceMilliseconds", JsonValue.Create sourceMilliseconds)
                node.Add("allocatedMilliseconds", JsonValue.Create allocated)
                node.Add("remainingMilliseconds", JsonValue.Create remaining)
                node.Add("incompleteChildren", JsonValue.Create incomplete)
                // Two complete children whose durations sum exactly, and
                // nothing half-filled. Both halves matter (TE-R-040, TE-R-041).
                node.Add(
                    "balances",
                    JsonValue.Create(remaining = 0L && incomplete = 0 && List.length contributions >= 2)
                )
                node.Add("displaySource", JsonValue.Create(displayExact sourceMilliseconds))
                node.Add("displayAllocated", JsonValue.Create(displayExact allocated))
                node.Add("displayRemaining", JsonValue.Create(displayExact remaining))

                node.Add(
                    "summary",
                    JsonValue.Create(
                        if List.length contributions < 2 then
                            "A split needs at least two parts."
                        elif incomplete > 0 then
                            sprintf "%d part(s) still need a duration." incomplete
                        elif remaining = 0L then
                            "The parts account for all of the time."
                        elif remaining > 0L then
                            sprintf "%s is still unallocated." (displayExact remaining)
                        else
                            sprintf "The parts exceed the entry by %s." (displayExact (abs remaining))
                    )
                )

                node.ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


// ---------------------------------------------------------------------------
// Merge preview
// ---------------------------------------------------------------------------

/// What a merge would come to.
///
/// System-prompt §8.11 asks the merge screen to "preview merged time", and
/// like the split preview that is arithmetic the browser may not do.
///
/// This deliberately states FACTS and does not judge. Whether a merge is
/// legal — at least two sources, all mergeable, all on one day, references
/// retained — is decided by `mergeEntries`, and re-deciding it here would put
/// the same rules in two places where they could drift apart. So the preview
/// reports the combined time and the days the sources fall on; if that is two
/// days, the submission is refused by the transition in its own words.
let mergePreview (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            let wanted =
                match request.["sourceIds"] with
                | :? JsonArray as items ->
                    items
                    |> Seq.choose (fun i -> if isNull i then None else Some(i.ToString()))
                    |> Set.ofSeq
                | _ -> Set.empty

            let sources =
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
                                match Mapping.fromDocument None document with
                                | Error _ -> None
                                | Ok entry ->
                                    if wanted.Contains(EntryId.value entry.Id) then
                                        Some entry
                                    else
                                        None)
                    |> List.ofSeq
                | _ -> []

            let combined =
                sources
                |> List.sumBy (fun entry -> Duration.milliseconds entry.Effective.Duration)

            let days =
                sources
                |> List.map (fun entry ->
                    let y, m, d = EntryDate.toYearMonthDay entry.Effective.Date
                    sprintf "%04d-%02d-%02d" y m d)
                |> List.distinct
                |> List.sort

            let node = JsonObject()
            node.Add("ok", JsonValue.Create true)
            node.Add("sourceCount", JsonValue.Create(List.length sources))
            node.Add("combinedMilliseconds", JsonValue.Create combined)
            node.Add("displayCombined", JsonValue.Create(displayExact combined))

            let dayList = JsonArray()

            for day in days do
                dayList.Add(JsonValue.Create day)

            node.Add("days", dayList)

            node.Add(
                "summary",
                JsonValue.Create(
                    if List.length sources < 2 then
                        "Choose at least two entries to merge."
                    elif List.length days > 1 then
                        // A fact about the selection, not a restatement of the
                        // rule. The transition is what refuses it.
                        sprintf "These entries fall on %d different days." (List.length days)
                    else
                        sprintf "%d entries totalling %s." (List.length sources) (displayExact combined)
                )
            )

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
                    | Error e -> Error(Wording.ofError (box e))
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

// Field reading, validation and command construction all live in
// `CommandParsing`. Keeping them out of this file is what stops the
// dispatcher growing a second, looser idea of what a valid command is.

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
/// Read the three things every command request carries beside the command
/// itself: the catalogue it was chosen against, the entries it was composed
/// against, and the version each of those was read at.
///
/// Extracted so `dispatch` and `persist` cannot drift apart on what a request
/// means. They differ in exactly one way — whether the effects are performed —
/// and nothing else should be able to differ by accident.
let private prepare (request: JsonNode) : Result<Catalogue * TimeEntry list * Command, string> =
    let catalogue =
        match request.["catalogue"] with
        | null -> Ok Catalogue.empty
        | node ->
            match Serialization.readCatalogue (node.ToJsonString()) with
            | Error e -> Error(Wording.ofError (box e))
            | Ok document -> Mapping.catalogueFromDocument document |> describe

    // Version tokens arrive alongside the documents, never inside them: the
    // token is a hash OF the document, so storing it in the content would be
    // circular (see `Persistence.Documents`). The transport knows path -> blob
    // SHA, so it supplies a map keyed by entry id.
    //
    // An entry loaded without a token gets `Version = None`, and every
    // mutating transition then refuses it with `VersionConflict`. That is the
    // correct outcome, not a gap: a caller that cannot say which version it
    // read must not be allowed to overwrite (TE-R-070).
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

    catalogue
    |> Result.bind (fun cat ->
        match request.["command"] with
        | null -> Error "missing 'command'"
        | command -> CommandParsing.parse command |> Result.map (fun parsed -> cat, loaded, parsed))

/// A rejection, as a normal answer. The UI needs to render "an archived
/// project cannot take new time" as readily as a success.
let private rejectedNode (rejection: Rejection) =
    let node = JsonObject()
    node.Add("ok", JsonValue.Create true)
    node.Add("accepted", JsonValue.Create false)
    node.Add("rejection", JsonValue.Create(Wording.rejection rejection))
    node

/// An acceptance: the whole resulting set in stored form, the ids touched, and
/// the effects the domain asked for.
let private acceptedNode (loaded: TimeEntry list) (changed: TimeEntry list) (effects: Effect list) =
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
    node

/// Apply a command WITHOUT performing its effects.
///
/// Input carries the loaded catalogue, the loaded entries, and the command.
/// Output carries the whole resulting set in its stored form — the loaded
/// entries with the command's changes folded in — plus the ids the command
/// touched and the effects the domain requested, NAMED, never performed
/// (TE-R-093).
///
/// This is the right entry point when there is no credential, and the only one
/// that cannot touch the network.
let dispatch (requestJson: string) : string =
    try
        match JsonNode.Parse requestJson with
        | null -> errorResult "empty request"
        | request ->
            match prepare request with
            | Error detail -> errorResult detail
            | Ok(catalogue, loaded, command) ->
                match apply catalogue loaded command with
                | Rejected rejection -> (rejectedNode rejection).ToJsonString(jsonOptions)
                | Accepted(changed, effects) ->
                    (acceptedNode loaded changed effects).ToJsonString(jsonOptions)
    with ex ->
        errorResult (ex.GetType().Name + ": " + ex.Message)


// ---------------------------------------------------------------------------
// Performing the effects
// ---------------------------------------------------------------------------

/// Where the ledger lives, and how this page proves it may write there.
///
/// The credential is built through `Credential`'s port, so the mechanism is
/// replaceable without this module changing (DF-TE-0011). Today the page sends
/// a token; a device flow or a same-origin proxy would send something else, or
/// nothing, and only the construction below would move.
let private sessionFrom (request: JsonNode) =
    let text (name: string) =
        match request.[name] with
        | null -> Error(sprintf "missing '%s'" name)
        | value ->
            let raw = value.ToString()

            if System.String.IsNullOrWhiteSpace raw then
                Error(sprintf "'%s' is empty" name)
            else
                Ok raw

    match request.["repository"] with
    | null -> Error "missing 'repository'"
    | repository ->
        let field (name: string) =
            match repository.[name] with
            | null -> Error(sprintf "missing 'repository.%s'" name)
            | value ->
                let raw = value.ToString()

                if System.String.IsNullOrWhiteSpace raw then
                    Error(sprintf "'repository.%s' is empty" name)
                else
                    Ok raw

        field "owner"
        |> Result.bind (fun owner ->
            field "repo" |> Result.map (fun repo -> owner, repo))
        |> Result.bind (fun (owner, repo) ->
            field "branch" |> Result.map (fun branch -> owner, repo, branch))
        |> Result.map (fun (owner, repo, branch) ->
            // `apiRoot` is optional and defaults to github.com. A GitHub
            // Enterprise Server repository is not reachable at
            // api.github.com, so the field exists; omitting it is the common
            // case and must not be a configuration step.
            let target =
                match repository.["apiRoot"] with
                | null -> HttpProtocol.RepositoryRef.gitHub owner repo branch
                | value ->
                    let raw = value.ToString()

                    if System.String.IsNullOrWhiteSpace raw then
                        HttpProtocol.RepositoryRef.gitHub owner repo branch
                    else
                        HttpProtocol.RepositoryRef.at raw owner repo branch

            // A request with no token gets the token source anyway, holding an
            // empty string: `Credential.token` then answers
            // `CredentialUnavailable` client-side and no request is sent. The
            // alternative — falling back to `ambient` — would quietly turn
            // "not signed in" into an unauthenticated request and a confusing
            // 401 (DF-TE-0011).
            let credential =
                match text "token" with
                | Ok value -> Credential.token value
                | Error _ -> Credential.token ""

            target, credential)

/// One effect's result, as the page needs to see it.
///
/// `Persisted` carries the new blob SHAs, which matters more than it looks:
/// they are the version tokens for the entries just written, so the page can
/// go on to correct or remove them. Without this, anything created in the
/// browser could never be changed again — which is exactly the gap that
/// existed while effects were only named.
let private outcomeNode (effect: Effect) (outcome: Interpreter.EffectOutcome) =
    let node = JsonObject()
    node.Add("effect", JsonValue.Create(effectName effect))

    match outcome with
    | Interpreter.Persisted versions ->
        node.Add("outcome", JsonValue.Create "persisted")
        let written = JsonObject()

        for entryId, token in versions do
            written.Add(EntryId.value entryId, JsonValue.Create(VersionToken.value token))

        node.Add("versions", written)
    | Interpreter.Conflicted conflict ->
        // TE-R-072: a stale write is an outcome the domain reconciles, not a
        // failure to retry. The page must re-read and decide, so it is told
        // which entry and which versions disagreed.
        node.Add("outcome", JsonValue.Create "conflicted")
        node.Add("path", JsonValue.Create conflict.Path)

        node.Add(
            "entryId",
            match conflict.EntryId with
            | Some id -> JsonValue.Create(EntryId.value id)
            | None -> null
        )

        node.Add(
            "expectedVersion",
            match conflict.Expected with
            | Some token -> JsonValue.Create(VersionToken.value token)
            | None -> null
        )

        node.Add(
            "actualVersion",
            match conflict.Actual with
            | Some token -> JsonValue.Create(VersionToken.value token)
            | None -> null
        )
    | Interpreter.Failed error ->
        node.Add("outcome", JsonValue.Create "failed")
        node.Add("detail", JsonValue.Create(Wording.storeError error))
    | Interpreter.EntriesLoaded(entries, _) ->
        node.Add("outcome", JsonValue.Create "loaded")
        node.Add("count", JsonValue.Create(List.length entries))
    | Interpreter.CatalogueLoaded _ -> node.Add("outcome", JsonValue.Create "catalogueLoaded")
    | Interpreter.CatalogueUnreadable detail ->
        node.Add("outcome", JsonValue.Create "catalogueUnreadable")
        node.Add("detail", JsonValue.Create detail)

    node

/// Apply a command AND perform the effects it asks for.
///
/// The same request as `dispatch`, plus `repository` and `token`. The answer is
/// the same too, plus `performed` — one entry per effect — and `versions`,
/// the new blob SHA of every entry written.
///
/// The domain still decides everything and still only *asks* for effects; this
/// is the interpreter running them, which is a host responsibility (TE-R-093).
/// A rejected command performs nothing at all: effects exist only inside an
/// `Accepted`, so there is no path here that writes on a refusal.
let persistWith
    (storeFor: HttpProtocol.RepositoryRef -> Credential.CredentialSource -> Store.GitHubStore)
    (requestJson: string)
    : Async<string> =
    async {
        try
            match JsonNode.Parse requestJson with
            | null -> return errorResult "empty request"
            | request ->
                // The repository is checked BEFORE the command. A host that
                // was started without one would otherwise report "missing
                // 'entryId'" for every command a user typed correctly —
                // blaming them for a configuration problem that is not
                // theirs.
                match sessionFrom request, prepare request with
                | Error detail, _ -> return errorResult detail
                | _, Error detail -> return errorResult detail
                | Ok(target, credential), Ok(catalogue, loaded, command) ->
                    match apply catalogue loaded command with
                    | Rejected rejection -> return (rejectedNode rejection).ToJsonString(jsonOptions)
                    | Accepted(changed, effects) ->
                        let store = storeFor target credential

                        let node = acceptedNode loaded changed effects
                        let performed = JsonArray()
                        let versions = JsonObject()

                        // Sequentially, and deliberately: each effect is a
                        // commit against a branch whose head the next one
                        // reads. Running them concurrently would make every
                        // commit after the first race the head it depends on.
                        for effect in effects do
                            let! outcome = Interpreter.interpret store effect
                            performed.Add(outcomeNode effect outcome)

                            match outcome with
                            | Interpreter.Persisted written ->
                                for entryId, token in written do
                                    versions.Add(
                                        EntryId.value entryId,
                                        JsonValue.Create(VersionToken.value token)
                                    )
                            | _ -> ()

                        node.Add("performed", performed)
                        node.Add("versions", versions)
                        node.Add("credential", JsonValue.Create credential.Describe)
                        return node.ToJsonString(jsonOptions)
        with ex ->
            return errorResult (ex.GetType().Name + ": " + ex.Message)
    }


/// Read the whole ledger and the catalogue from the repository.
///
/// The symmetric half of `persist`. Both go through the interpreter rather
/// than touching the store directly, so the two effects the domain already
/// defines for loading — `LoadEntries` and `LoadProjects` — are the only way
/// in, and nothing here decides what a readable ledger is.
///
/// Entries come back in their **stored** form plus a `versions` map, because
/// the blob SHA the read observed is precisely the token a later command must
/// name (TE-R-070). Reading and then having to re-read to learn the version
/// would reopen the window the token exists to close.
///
/// `unreadable` is reported rather than hidden: one corrupt file must become
/// one unreadable entry, not a failed load (TE-R-084). A caller that is told
/// "412 entries, 1 unreadable" can act; one silently given 412 cannot.
let loadLedgerWith
    (storeFor: HttpProtocol.RepositoryRef -> Credential.CredentialSource -> Store.GitHubStore)
    (requestJson: string)
    : Async<string> =
    async {
        try
            match JsonNode.Parse requestJson with
            | null -> return errorResult "empty request"
            | request ->
                match sessionFrom request with
                | Error detail -> return errorResult detail
                | Ok(target, credential) ->

                // `LoadEntries` carries a date, and the caller supplies it
                // rather than this fabricating one. The interpreter does not
                // use it to narrow the read — paths are keyed by identity, not
                // date (see `Layout`) — but inventing a value to satisfy a
                // required field is how a field stops meaning anything.
                // Filtering stays in the projection, where
                // `EntryQuery.DateRange` already does it.
                match
                    (match request.["date"] with
                     | null -> Error "missing 'date'"
                     | value -> CommandParsing.parseDate (value.ToString()))
                with
                | Error detail -> return errorResult detail
                | Ok day ->
                    let store = storeFor target credential

                    let! entriesOutcome = Interpreter.interpret store (LoadEntries day)

                    let! catalogueOutcome = Interpreter.interpret store LoadProjects

                    // Read with the ledger rather than on its own, so the page
                    // has the month's target at the same moment it has the
                    // month's entries. A second round trip from the page would
                    // let the two arrive out of order and render a bar against
                    // a total that has not loaded yet.
                    let! preferencesOutcome = Interpreter.readPreferences store

                    let node = JsonObject()

                    match entriesOutcome with
                    | Interpreter.EntriesLoaded(entries, unreadable) ->
                        node.Add("ok", JsonValue.Create true)
                        let documents = JsonArray()
                        let versions = JsonObject()

                        for entry in entries do
                            documents.Add(JsonNode.Parse(Serialization.write (Mapping.toDocument entry)))

                            match entry.Version with
                            | Some token ->
                                versions.Add(
                                    EntryId.value entry.Id,
                                    JsonValue.Create(VersionToken.value token)
                                )
                            | None -> ()

                        node.Add("entries", documents)
                        node.Add("versions", versions)
                        node.Add("unreadable", JsonValue.Create(List.length unreadable))

                        let details = JsonArray()

                        for item in unreadable do
                            let entry = JsonObject()
                            entry.Add("path", JsonValue.Create item.Path)
                            entry.Add("detail", JsonValue.Create item.Detail)
                            details.Add entry

                        node.Add("unreadableDetail", details)

                        match catalogueOutcome with
                        | Interpreter.CatalogueLoaded catalogue ->
                            node.Add(
                                "catalogue",
                                JsonNode.Parse(
                                    Serialization.writeCatalogue (Mapping.catalogueToDocument catalogue)
                                )
                            )
                        | Interpreter.CatalogueUnreadable detail ->
                            // Never reported as an EMPTY catalogue: an empty
                            // catalogue refuses every project, which would look
                            // like "all your projects were archived" rather than
                            // "the catalogue could not be read".
                            node.Add("catalogue", null)
                            node.Add("catalogueError", JsonValue.Create detail)
                        | Interpreter.Failed error ->
                            node.Add("catalogue", null)
                            node.Add("catalogueError", JsonValue.Create(Wording.storeError error))
                        | _ ->
                            // `LoadProjects` answers with a catalogue, an
                            // unreadable catalogue, or a failure. Anything
                            // else is a wiring mistake, and naming the union
                            // case would tell a user nothing they could act
                            // on.
                            node.Add("catalogue", null)

                            node.Add(
                                "catalogueError",
                                JsonValue.Create "The repository answered with something other than a catalogue."
                            )

                        // Preferences are returned in their STORED form,
                        // like entries and the catalogue: the page hands the
                        // document straight back to `viewMonth`, which decodes
                        // it. An unreadable or unreachable preferences file is
                        // named rather than silently treated as "no target
                        // set" — the ledger may well have one.
                        match preferencesOutcome with
                        | Interpreter.PreferencesLoaded preferences ->
                            node.Add(
                                "preferences",
                                JsonNode.Parse(
                                    Serialization.writePreferences (
                                        Mapping.preferencesToDocument preferences
                                    )
                                )
                            )
                        | Interpreter.PreferencesUnreadable detail ->
                            node.Add("preferences", null)
                            node.Add("preferencesError", JsonValue.Create detail)
                        | Interpreter.PreferencesUnavailable error ->
                            node.Add("preferences", null)
                            node.Add("preferencesError", JsonValue.Create(Wording.storeError error))

                        return node.ToJsonString(jsonOptions)
                    | Interpreter.Failed error -> return errorResult (Wording.storeError error)
                    | _ ->
                        return
                            errorResult
                                "The repository answered with something other than a list of entries."
        with ex ->
            return errorResult (ex.GetType().Name + ": " + ex.Message)
    }

/// What the repository holds for one entry, for reviewing a stale write.
///
/// TE-R-071 asks a conflict to surface the current saved version against the
/// proposed one. This is the saved half, and it is deliberately the only half
/// this answers: the proposed half is the command the person just filled in
/// and can still see, whereas what someone else wrote is invisible to them.
///
/// Projected through the same `EntryView` the timeline uses, so the values
/// shown in a conflict are computed exactly like the values shown everywhere
/// else. A second formatting path here is how a review panel starts
/// disagreeing with the list behind it.
///
/// It reads ONE entry and does not touch the loaded set. Re-reading the whole
/// ledger would replace the state the person is still deciding about, which
/// is the one thing a review must not do.
let reviewEntryWith
    (storeFor: HttpProtocol.RepositoryRef -> Credential.CredentialSource -> Store.GitHubStore)
    (requestJson: string)
    : Async<string> =
    async {
        try
            match JsonNode.Parse requestJson with
            | null -> return errorResult "empty request"
            | request ->
                match sessionFrom request with
                | Error detail -> return errorResult detail
                | Ok(target, credential) ->

                match
                    (match request.["entryId"] with
                     | null -> Error "missing 'entryId'"
                     | value -> EntryId.create (value.ToString()) |> describe)
                with
                | Error detail -> return errorResult detail
                | Ok entryId ->
                    let catalogue =
                        match request.["catalogue"] with
                        | null -> Catalogue.empty
                        | node ->
                            match Serialization.readCatalogue (node.ToJsonString()) with
                            | Error _ -> Catalogue.empty
                            | Ok document ->
                                Mapping.catalogueFromDocument document
                                |> Result.defaultValue Catalogue.empty

                    let store = storeFor target credential
                    let! found = Interpreter.readEntry store entryId

                    let node = JsonObject()
                    node.Add("ok", JsonValue.Create true)

                    match found with
                    | Choice1Of2 entry ->
                        let view = Projection.toView displayPolicy [] entry
                        node.Add("found", JsonValue.Create true)
                        node.Add("description", JsonValue.Create(Option.toObj view.Description))
                        node.Add("classification", JsonValue.Create(classificationOf catalogue view))
                        node.Add("displayTime", JsonValue.Create(displayTime view.DisplayHours view.DisplayMinutes))
                        node.Add("durationMilliseconds", JsonValue.Create view.DurationMilliseconds)
                        node.Add("countsTowardTotals", JsonValue.Create view.CountsTowardTotals)

                        node.Add(
                            "version",
                            match entry.Version with
                            | Some token -> JsonValue.Create(VersionToken.value token)
                            | None -> null
                        )

                        let badges = JsonArray()

                        for badge in view.Badges do
                            let text, tone = badgeView badge
                            let rendered = JsonObject()
                            rendered.Add("text", JsonValue.Create text)
                            rendered.Add("tone", JsonValue.Create tone)
                            badges.Add rendered

                        node.Add("badges", badges)
                    | Choice2Of2 unreadable ->
                        // Not an error: an entry that has been removed from the
                        // repository entirely is a real thing to discover
                        // during a conflict, and saying "not readable" is more
                        // useful than failing the review.
                        node.Add("found", JsonValue.Create false)
                        node.Add("detail", JsonValue.Create unreadable.Detail)

                    return node.ToJsonString(jsonOptions)
        with ex ->
            return errorResult (ex.GetType().Name + ": " + ex.Message)
    }

/// The store this runs against in a browser: the real GitHub transport.
///
/// Separated from `persistWith` so the wiring above — dispatch, interpret,
/// collect the new versions — can be verified against a store double with no
/// network at all. A `persist` that built its own `HttpClient` inline could
/// only be tested by talking to GitHub, which would make the most important
/// part of this file the least covered.
/// Set or clear the monthly tracking target.
///
/// The person using the ledger sets it (DF-TE-0015, resolving OQ-9), so this
/// is the one thing the browser writes that is not a ledger fact. It appends
/// no revision and attributes nothing: a preference is not a record of
/// something that happened, and giving it an `Attribution` would put a
/// non-event into the history the ledger exists to keep.
///
/// The request is whole hours, because "80h target" is how the design states
/// one and a whole hour is what a person types. Units are the stored currency
/// and hours are the typed one; converting here means the page never does
/// arithmetic on a domain quantity (TE-R-085).
///
/// `clear: true` removes the target, and is a distinct instruction rather than
/// `hours: 0`. Zero is not a target, so a form that submitted it would be
/// asking for something unrepresentable; saying "clear" says what was meant.
let setMonthlyTargetWith
    (storeFor: HttpProtocol.RepositoryRef -> Credential.CredentialSource -> Store.GitHubStore)
    (requestJson: string)
    : Async<string> =
    async {
        try
            match JsonNode.Parse requestJson with
            | null -> return errorResult "empty request"
            | request ->
                match sessionFrom request with
                | Error detail -> return errorResult detail
                | Ok(target, credential) ->

                let clearing =
                    match request.["clear"] with
                    | null -> false
                    | value ->
                        match System.Boolean.TryParse(value.ToString()) with
                        | true, parsed -> parsed
                        | _ -> false

                let intended =
                    if clearing then
                        Ok None
                    else
                        match request.["hours"] with
                        | null -> Error "missing 'hours'"
                        | value ->
                            match System.Int32.TryParse(value.ToString()) with
                            | false, _ -> Error "'hours' must be a whole number of hours"
                            | true, hours ->
                                TrackingTarget.ofHours hours
                                |> Result.mapError (fun e -> Wording.targetError e)
                                |> Result.map Some

                match intended with
                | Error detail -> return errorResult detail
                | Ok monthlyTarget ->
                    let store = storeFor target credential
                    let! saved = Interpreter.savePreferences store { MonthlyTarget = monthlyTarget }

                    match saved with
                    | Error error -> return errorResult (Wording.storeError error)
                    | Ok preferences ->
                        let node = JsonObject()
                        node.Add("ok", JsonValue.Create true)

                        node.Add(
                            "preferences",
                            JsonNode.Parse(
                                Serialization.writePreferences (Mapping.preferencesToDocument preferences)
                            )
                        )

                        // Worded here so the page reports what happened
                        // without composing a sentence of its own
                        // (TE-R-085).
                        node.Add(
                            "outcome",
                            JsonValue.Create(
                                match preferences.MonthlyTarget with
                                | None -> "The monthly tracking target was removed."
                                | Some set ->
                                    let hours, minutes = TrackingTarget.toHoursAndMinutes set
                                    sprintf "The monthly tracking target is now %s." (displayTime hours minutes)
                            )
                        )

                        return node.ToJsonString(jsonOptions)
        with ex ->
            return errorResult (ex.GetType().Name + ": " + ex.Message)
    }

let private httpStore (target: HttpProtocol.RepositoryRef) (credential: Credential.CredentialSource) =
    // Not disposed: the store closes over it and is used after this returns.
    // One client per persist rather than one per request — a client per
    // request exhausts sockets, and in the browser it is a fetch wrapper
    // holding nothing that needs releasing.
    let client = new System.Net.Http.HttpClient()
    HttpStore.create (HttpStore.configure client credential) target

/// Apply a command and perform its effects against the real repository.
let persist (requestJson: string) : Async<string> = persistWith httpStore requestJson

/// Read the ledger and catalogue from the real repository.
let loadLedger (requestJson: string) : Async<string> = loadLedgerWith httpStore requestJson

/// Read one entry from the real repository, for reviewing a stale write.
let reviewEntry (requestJson: string) : Async<string> = reviewEntryWith httpStore requestJson

/// Set or clear the monthly tracking target in the real repository.
let setMonthlyTarget (requestJson: string) : Async<string> = setMonthlyTargetWith httpStore requestJson
