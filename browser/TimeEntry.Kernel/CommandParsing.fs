/// Turning a JSON request into a domain `Command`.
///
/// This module is the whole of the browser's command vocabulary, and it is
/// deliberately the only place that knows how a request is spelled. Two
/// properties are worth stating plainly, because they are what keep the
/// domain authoritative across the boundary:
///
/// 1. **Every value is built by a domain smart constructor.** Nothing here
///    decides whether a duration, an identifier, a reason or a date is valid;
///    it parses JSON and hands the result to the type that owns the rule. A
///    command that will not construct is refused before any transition sees
///    it (TE-R-096).
///
/// 2. **Nothing here decides whether a command is *allowed*.** Capability,
///    catalogue eligibility, version staleness and total preservation are all
///    the transitions' judgements. This module's failures are only ever
///    "that request was not well formed" (TE-R-092).
///
/// A request that parses may still be rejected, and that is the normal case,
/// not an error path.
module TimeEntry.Browser.CommandParsing

open System.Text.Json.Nodes
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands

/// Render a typed domain error as the sentence a person reads.
///
/// Worded by `Wording`, not by `%A`. A refusal is the one thing a user sees
/// when something goes wrong, and an F# union literal is the worst possible
/// moment to show them one (TE-R-053).
let describe (result: Result<'a, 'e>) =
    result |> Result.mapError (fun e -> Wording.ofError (box e))

// ---------------------------------------------------------------------------
// Reading fields
// ---------------------------------------------------------------------------

/// A required, non-blank string.
let text (node: JsonNode) (name: string) =
    match node.[name] with
    | null -> Error(sprintf "missing '%s'" name)
    | value ->
        let raw = value.ToString()

        if System.String.IsNullOrWhiteSpace raw then
            Error(sprintf "'%s' is empty" name)
        else
            Ok raw

let optionalText (node: JsonNode) (name: string) =
    match node.[name] with
    | null -> None
    | value ->
        let raw = value.ToString()
        if System.String.IsNullOrWhiteSpace raw then None else Some raw

let private entryIdField node name =
    text node name |> Result.bind (fun raw -> EntryId.create raw |> describe)

let private projectField node name =
    text node name |> Result.bind (fun raw -> ProjectId.create raw |> describe)

let private activityField node name =
    text node name |> Result.bind (fun raw -> ActivityTypeId.create raw |> describe)

let private versionField node name =
    text node name |> Result.bind (fun raw -> VersionToken.create raw |> describe)

let private reasonField node name =
    text node name |> Result.bind (fun raw -> Reason.create raw |> describe)

let private optionalDescription node name =
    match optionalText node name with
    | None -> Ok None
    | Some raw -> Description.create raw |> describe |> Result.map Some

let parseDate (raw: string) =
    let parts = raw.Split('-')

    if parts.Length <> 3 then
        Error "date must be YYYY-MM-DD"
    else
        match System.Int32.TryParse parts.[0], System.Int32.TryParse parts.[1], System.Int32.TryParse parts.[2] with
        | (true, y), (true, m), (true, d) -> EntryDate.ofYearMonthDay y m d |> describe
        | _ -> Error "date must be YYYY-MM-DD"

let private dateField node name =
    text node name |> Result.bind parseDate

/// A duration, given either as exact milliseconds (a stopped timer) or as
/// whole billable units (the six-minute manual grid, system-prompt §8.4).
///
/// Units rather than minutes is deliberate: the browser must not multiply by
/// 6 or by 60,000, because that would put the unit definition in two places
/// and a time conversion in the bridge (TE-R-085). It sends a count; this
/// converts.
let private durationField (node: JsonNode) : Result<Duration, string> =
    match node.["durationMs"], node.["durationUnits"] with
    | null, null -> Error "missing 'durationMs' or 'durationUnits'"
    | value, _ when not (isNull value) ->
        match System.Int64.TryParse(value.ToString()) with
        | true, ms -> Duration.ofMilliseconds ms |> describe
        | _ -> Error "durationMs must be an integer"
    | _, units ->
        match System.Int64.TryParse(units.ToString()) with
        | true, count -> Duration.ofMilliseconds (count * MillisecondsPerBillableUnit) |> describe
        | _ -> Error "durationUnits must be an integer"

/// Read an array field, applying a parser to each element and failing on the
/// first element that will not parse.
///
/// Failing on the first rather than dropping it matters: a split that
/// silently lost a child would change the total, and total preservation is
/// the one thing split exists to guarantee (TE-R-040).
let private eachOf (node: JsonNode) (name: string) (parse: JsonNode -> Result<'a, string>) =
    match node.[name] with
    | :? JsonArray as items ->
        items
        |> Seq.fold
            (fun acc item ->
                match acc, item with
                | Error e, _ -> Error e
                | Ok _, null -> Error(sprintf "'%s' contains a null element" name)
                | Ok parsed, element -> parse element |> Result.map (fun one -> one :: parsed))
            (Ok [])
        |> Result.map List.rev
    | null -> Error(sprintf "missing '%s'" name)
    | _ -> Error(sprintf "'%s' must be an array" name)

// ---------------------------------------------------------------------------
// Attribution
// ---------------------------------------------------------------------------

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
let attributionOf (command: JsonNode) (revisionId: string) : Result<Attribution, string> =
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

/// Revision ids are derived from the entry id and the change, rather than
/// supplied by the page.
///
/// Tier 2 is pure and cannot mint identity, so someone above it must. Doing
/// it here keeps the page from inventing a scheme, and keeps the ids legible
/// in a GitHub diff — which is the whole reason this ledger is stored as
/// reviewable JSON.
let private revisionFor (entryId: EntryId) (change: string) =
    sprintf "%s-%s" (EntryId.value entryId) change

// ---------------------------------------------------------------------------
// Facts
// ---------------------------------------------------------------------------

/// The complete facts a create or a correction carries.
///
/// Correction supplies whole facts rather than a patch, because the resulting
/// revision records complete values and history can then show before-and-after
/// without reconstruction (TE-R-052). The same reader serves both.
let private factsOf (node: JsonNode) : Result<EntryFacts, string> =
    projectField node "projectId"
    |> Result.bind (fun project ->
        activityField node "activityTypeId"
        |> Result.map (fun activity -> project, activity))
    |> Result.bind (fun (project, activity) ->
        dateField node "date" |> Result.map (fun date -> project, activity, date))
    |> Result.bind (fun (project, activity, date) ->
        durationField node |> Result.map (fun duration -> project, activity, date, duration))
    |> Result.bind (fun (project, activity, date, duration) ->
        optionalDescription node "description"
        |> Result.map (fun description -> project, activity, date, duration, description))
    |> Result.bind (fun (project, activity, date, duration, description) ->
        // An entry is `Manual` exactly when it carries a reason for being
        // entered by hand (system-prompt §8.4). The page does not get to
        // label an entry manual without one, because the type will not hold
        // a `Manual` without a `Reason`.
        (match optionalText node "manualReason" with
         | None -> Ok Timed
         | Some raw -> Reason.create raw |> describe |> Result.map Manual)
        |> Result.map (fun origin ->
            { Project = project
              ActivityType = activity
              Date = date
              Duration = duration
              Description = description
              Origin = origin
              Evidence = [] }))

/// A piece of supporting evidence (TE-R-033, system-prompt §8.12).
let private evidenceOf (node: JsonNode) (attachedAt: Instant) : Result<EvidenceRef, string> =
    text node "uri"
    |> Result.bind (fun uri ->
        (match optionalText node "label" with
         | None -> Ok None
         | Some raw -> Description.create raw |> describe |> Result.map Some)
        |> Result.map (fun label ->
            { Uri = uri
              Label = label
              AttachedAt = attachedAt }))

// ---------------------------------------------------------------------------
// The commands
// ---------------------------------------------------------------------------

let private create (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        factsOf command |> Result.map (fun facts -> entryId, facts))
    |> Result.bind (fun (entryId, facts) ->
        attributionOf command (revisionFor entryId "r1")
        |> Result.map (fun attribution ->
            CreateEntry
                { NewEntryId = entryId
                  Facts = facts
                  Attribution = attribution }))

let private correct (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        versionField command "expectedVersion" |> Result.map (fun version -> entryId, version))
    |> Result.bind (fun (entryId, version) ->
        factsOf command |> Result.map (fun facts -> entryId, version, facts))
    |> Result.bind (fun (entryId, version, facts) ->
        reasonField command "reason"
        |> Result.map (fun reason -> entryId, version, facts, reason))
    |> Result.bind (fun (entryId, version, facts, reason) ->
        attributionOf command (revisionFor entryId "correct")
        |> Result.map (fun attribution ->
            CorrectEntry
                { EntryId = entryId
                  ExpectedVersion = version
                  CorrectedFacts = facts
                  Reason = reason
                  Attribution = attribution }))

let private voidEntry (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        versionField command "expectedVersion" |> Result.map (fun version -> entryId, version))
    |> Result.bind (fun (entryId, version) ->
        reasonField command "reason" |> Result.map (fun reason -> entryId, version, reason))
    |> Result.bind (fun (entryId, version, reason) ->
        attributionOf command (revisionFor entryId "void")
        |> Result.map (fun attribution ->
            // Annotated for the same reason as `restore` below: the two
            // request types have identical field sets.
            let request: VoidEntryRequest =
                { EntryId = entryId
                  ExpectedVersion = version
                  Reason = reason
                  Attribution = attribution }

            VoidEntry request))

let private restore (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        versionField command "expectedVersion" |> Result.map (fun version -> entryId, version))
    |> Result.bind (fun (entryId, version) ->
        reasonField command "reason" |> Result.map (fun reason -> entryId, version, reason))
    |> Result.bind (fun (entryId, version, reason) ->
        attributionOf command (revisionFor entryId "restore")
        |> Result.map (fun attribution ->
            // Annotated because `RestoreEntryRequest` and `VoidEntryRequest`
            // have identical field sets, so F# resolves an unannotated
            // construction to whichever was declared later. This exact
            // ambiguity produced 13 compile errors once already.
            let request: RestoreEntryRequest =
                { EntryId = entryId
                  ExpectedVersion = version
                  Reason = reason
                  Attribution = attribution }

            RestoreEntry request))

let private splitChildOf (child: JsonNode) =
    entryIdField child "entryId"
    |> Result.bind (fun entryId ->
        durationField child |> Result.map (fun duration -> entryId, duration))
    |> Result.bind (fun (entryId, duration) ->
        projectField child "projectId" |> Result.map (fun project -> entryId, duration, project))
    |> Result.bind (fun (entryId, duration, project) ->
        activityField child "activityTypeId"
        |> Result.map (fun activity -> entryId, duration, project, activity))
    |> Result.bind (fun (entryId, duration, project, activity) ->
        optionalDescription child "description"
        |> Result.bind (fun description ->
            RevisionId.create (revisionFor entryId "r1")
            |> describe
            |> Result.map (fun revision ->
                { NewEntryId = entryId
                  NewRevisionId = revision
                  Duration = duration
                  Project = project
                  ActivityType = activity
                  Description = description
                  // Evidence reassignment is a separate decision the split
                  // screen makes per item (TE-R-045); nothing is moved unless
                  // the request says so.
                  ReassignedEvidence = [] })))

let private split (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        versionField command "expectedVersion" |> Result.map (fun version -> entryId, version))
    |> Result.bind (fun (entryId, version) ->
        eachOf command "children" splitChildOf
        |> Result.map (fun children -> entryId, version, children))
    |> Result.bind (fun (entryId, version, children) ->
        attributionOf command (revisionFor entryId "split")
        |> Result.map (fun attribution ->
            SplitEntry
                { EntryId = entryId
                  ExpectedVersion = version
                  Children = children
                  Attribution = attribution }))

let private mergeSourceOf (source: JsonNode) =
    entryIdField source "entryId"
    |> Result.bind (fun entryId ->
        versionField source "expectedVersion"
        |> Result.bind (fun version ->
            // Each source carries its own expected version because each was
            // read independently and any one of them may be stale (TE-R-070).
            RevisionId.create (revisionFor entryId "merged")
            |> describe
            |> Result.map (fun revision ->
                { EntryId = entryId
                  ExpectedVersion = version
                  NewRevisionId = revision })))

let private merge (command: JsonNode) =
    entryIdField command "newEntryId"
    |> Result.bind (fun newEntryId ->
        eachOf command "sources" mergeSourceOf
        |> Result.map (fun sources -> newEntryId, sources))
    |> Result.bind (fun (newEntryId, sources) ->
        projectField command "projectId"
        |> Result.bind (fun project ->
            activityField command "activityTypeId"
            |> Result.map (fun activity -> newEntryId, sources, project, activity)))
    |> Result.bind (fun (newEntryId, sources, project, activity) ->
        optionalDescription command "description"
        |> Result.map (fun description -> newEntryId, sources, project, activity, description))
    |> Result.bind (fun (newEntryId, sources, project, activity, description) ->
        reasonField command "reason"
        |> Result.map (fun reason -> newEntryId, sources, project, activity, description, reason))
    |> Result.bind (fun (newEntryId, sources, project, activity, description, reason) ->
        attributionOf command (revisionFor newEntryId "r1")
        |> Result.map (fun attribution ->
            // No duration field: the merged duration is the sum of the
            // sources', computed by the transition. A supplied value could
            // disagree with them; a computed one cannot (DF-TE-0006).
            MergeEntries
                { NewEntryId = newEntryId
                  Sources = sources
                  Project = project
                  ActivityType = activity
                  Description = description
                  Evidence = []
                  Reason = reason
                  Attribution = attribution }))

let private attachEvidence (command: JsonNode) =
    entryIdField command "entryId"
    |> Result.bind (fun entryId ->
        versionField command "expectedVersion" |> Result.map (fun version -> entryId, version))
    |> Result.bind (fun (entryId, version) ->
        attributionOf command (revisionFor entryId "evidence")
        |> Result.map (fun attribution -> entryId, version, attribution))
    |> Result.bind (fun (entryId, version, attribution) ->
        // Attached at the moment the command says it occurred, so the
        // evidence's timestamp and its revision's agree by construction.
        evidenceOf command attribution.OccurredAt
        |> Result.map (fun evidence ->
            AttachEvidence
                { EntryId = entryId
                  ExpectedVersion = version
                  Evidence = evidence
                  Attribution = attribution }))

/// Every command kind the browser may send.
///
/// An unknown kind is named in the error rather than ignored, so a page and a
/// kernel that have drifted apart say so instead of silently doing nothing.
let parse (command: JsonNode) : Result<Command, string> =
    match text command "kind" with
    | Error e -> Error e
    | Ok "create" -> create command
    | Ok "correct" -> correct command
    | Ok "void" -> voidEntry command
    | Ok "restore" -> restore command
    | Ok "split" -> split command
    | Ok "merge" -> merge command
    | Ok "attachEvidence" -> attachEvidence command
    | Ok other -> Error(sprintf "unsupported command kind '%s'" other)
