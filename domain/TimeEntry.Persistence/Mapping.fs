/// Tier 4 — Persistence. Translation between domain state and stored document.
///
/// `toDocument` is total: a valid `TimeEntry` always has a document form.
/// `fromDocument` is partial and returns a typed `DocumentError`, because a
/// stored file may have been hand-edited, truncated, or written by a newer
/// schema. Nothing here throws (TE-R-084).
///
/// Round-trip identity — `fromDocument v (toDocument e) = Ok e` — is the
/// property that makes this boundary trustworthy, and it is tested directly.
module TimeEntry.Persistence.Mapping

open System
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.EntryState
open TimeEntry.Persistence.Documents

// ---------------------------------------------------------------------------
// Result plumbing
// ---------------------------------------------------------------------------

/// Apply a fallible mapping across a list, failing on the first error.
let private traverse (f: 'a -> Result<'b, 'e>) (items: 'a list) : Result<'b list, 'e> =
    let folder acc item =
        acc
        |> Result.bind (fun mapped -> f item |> Result.map (fun value -> value :: mapped))

    items |> List.fold folder (Ok []) |> Result.map List.rev

/// Adapt a smart constructor's own error into a `DocumentError` that names the
/// field it came from, so a bad record says *where* it is bad.
let private at (path: string) (result: Result<'a, 'e>) : Result<'a, DocumentError> =
    result |> Result.mapError (fun e -> InvalidField(path, sprintf "%A" e))

let private requireText (path: string) (value: string) : Result<string, DocumentError> =
    if String.IsNullOrWhiteSpace value then
        Error(MissingField path)
    else
        Ok value

/// JSON absence and JSON `[]` are both "no items" here.
let private itemsOf (value: 'a array) : 'a list =
    if isNull (box value) then [] else List.ofArray value

let private nullIfNone (value: string option) : string =
    match value with
    | Some text -> text
    | None -> null

let private textOption (value: string) : string option =
    if String.IsNullOrWhiteSpace value then None else Some value

// ---------------------------------------------------------------------------
// Domain -> document
// ---------------------------------------------------------------------------

let private evidenceToDocument (evidence: EvidenceRef) : EvidenceDocument =
    { uri = evidence.Uri
      label = nullIfNone (evidence.Label |> Option.map Description.value)
      attached_at_ms = Instant.epochMilliseconds evidence.AttachedAt }

let private factsToDocument (facts: EntryFacts) : FactsDocument =
    let year, month, day = EntryDate.toYearMonthDay facts.Date

    { project_id = ProjectId.value facts.Project
      activity_type_id = ActivityTypeId.value facts.ActivityType
      date = sprintf "%04d-%02d-%02d" year month day
      exact_duration_ms = Duration.milliseconds facts.Duration
      description = nullIfNone (facts.Description |> Option.map Description.value)
      origin_kind =
        match facts.Origin with
        | Timed -> "timed"
        | Manual _ -> "manual"
      manual_reason =
        match facts.Origin with
        | Timed -> null
        | Manual reason -> Reason.value reason
      evidence = facts.Evidence |> List.map evidenceToDocument |> Array.ofList }

let private revisionToDocument (revision: Revision) : RevisionDocument =
    let baseDocument =
        { revision_id = RevisionId.value revision.Id
          change_kind = ""
          change_reason = null
          change_entry_ids = null
          change_entry_id = null
          change_evidence = Unchecked.defaultof<EvidenceDocument>
          facts = factsToDocument revision.Facts
          recorded_at_ms = Instant.epochMilliseconds revision.RecordedAt
          recorded_by = UserId.value revision.RecordedBy
          device = DeviceLabel.value revision.Device }

    let ids (entryIds: EntryId list) =
        entryIds |> List.map EntryId.value |> Array.ofList

    match revision.Change with
    | Created -> { baseDocument with change_kind = "created" }
    | Corrected reason ->
        { baseDocument with
            change_kind = "corrected"
            change_reason = Reason.value reason }
    | Voided reason ->
        { baseDocument with
            change_kind = "voided"
            change_reason = Reason.value reason }
    | Restored reason ->
        { baseDocument with
            change_kind = "restored"
            change_reason = Reason.value reason }
    | SplitInto children ->
        { baseDocument with
            change_kind = "split_into"
            change_entry_ids = ids children }
    | MergedInto target ->
        { baseDocument with
            change_kind = "merged_into"
            change_entry_id = EntryId.value target }
    | CreatedBySplitOf source ->
        { baseDocument with
            change_kind = "created_by_split_of"
            change_entry_id = EntryId.value source }
    | CreatedByMergeOf sources ->
        { baseDocument with
            change_kind = "created_by_merge_of"
            change_entry_ids = ids sources }
    | EvidenceAttached evidence ->
        { baseDocument with
            change_kind = "evidence_attached"
            change_evidence = evidenceToDocument evidence }
    | EvidenceReassignedFrom source ->
        { baseDocument with
            change_kind = "evidence_reassigned_from"
            change_entry_id = EntryId.value source }

/// Total: every valid entry has a document form.
let toDocument (entry: TimeEntry) : EntryDocument =
    let stateKind, voidReason, voidedAt, children, target =
        match entry.State with
        | Active -> "active", null, 0L, null, null
        | Void(reason, voidedAt) ->
            "void", Reason.value reason, Instant.epochMilliseconds voidedAt, null, null
        | Superseded(SupersededBySplit childIds) ->
            "superseded_by_split", null, 0L, (childIds |> List.map EntryId.value |> Array.ofList), null
        | Superseded(SupersededByMerge targetId) ->
            "superseded_by_merge", null, 0L, null, EntryId.value targetId

    { schema_version = CurrentSchemaVersion
      entry_id = EntryId.value entry.Id
      state_kind = stateKind
      void_reason = voidReason
      voided_at_ms = voidedAt
      superseded_children = children
      superseded_target = target
      effective = factsToDocument entry.Effective
      history = entry.History |> List.map revisionToDocument |> Array.ofList }

// ---------------------------------------------------------------------------
// Document -> domain
// ---------------------------------------------------------------------------

let private evidenceFromDocument (path: string) (document: EvidenceDocument) : Result<EvidenceRef, DocumentError> =
    if isNull (box document) then
        Error(MissingField path)
    else
        requireText (path + ".uri") document.uri
        |> Result.bind (fun uri ->
            let label =
                match textOption document.label with
                | None -> Ok None
                | Some text -> Description.create text |> at (path + ".label") |> Result.map Some

            label
            |> Result.map (fun labelValue ->
                { Uri = uri
                  Label = labelValue
                  AttachedAt = Instant.ofEpochMilliseconds document.attached_at_ms }))

let private parseDate (path: string) (value: string) : Result<EntryDate, DocumentError> =
    requireText path value
    |> Result.bind (fun text ->
        let parts = text.Split('-')

        if parts.Length <> 3 then
            Error(InvalidField(path, "expected YYYY-MM-DD"))
        else
            let parsed =
                parts
                |> Array.map (fun part ->
                    match Int32.TryParse part with
                    | true, number -> Some number
                    | _ -> None)

            match parsed with
            | [| Some year; Some month; Some day |] ->
                EntryDate.ofYearMonthDay year month day |> at path
            | _ -> Error(InvalidField(path, "expected YYYY-MM-DD")))

let private factsFromDocument (path: string) (document: FactsDocument) : Result<EntryFacts, DocumentError> =
    if isNull (box document) then
        Error(MissingField path)
    else

    let origin =
        match document.origin_kind with
        | "timed" -> Ok Timed
        | "manual" ->
            requireText (path + ".manual_reason") document.manual_reason
            |> Result.bind (fun text -> Reason.create text |> at (path + ".manual_reason"))
            |> Result.map Manual
        | other -> Error(InvalidField(path + ".origin_kind", sprintf "unknown origin '%s'" other))

    let description =
        match textOption document.description with
        | None -> Ok None
        | Some text -> Description.create text |> at (path + ".description") |> Result.map Some

    requireText (path + ".project_id") document.project_id
    |> Result.bind (fun raw -> ProjectId.create raw |> at (path + ".project_id"))
    |> Result.bind (fun project ->
        requireText (path + ".activity_type_id") document.activity_type_id
        |> Result.bind (fun raw -> ActivityTypeId.create raw |> at (path + ".activity_type_id"))
        |> Result.map (fun activityType -> project, activityType))
    |> Result.bind (fun (project, activityType) ->
        parseDate (path + ".date") document.date
        |> Result.map (fun date -> project, activityType, date))
    |> Result.bind (fun (project, activityType, date) ->
        Duration.ofMilliseconds document.exact_duration_ms
        |> at (path + ".exact_duration_ms")
        |> Result.map (fun duration -> project, activityType, date, duration))
    |> Result.bind (fun (project, activityType, date, duration) ->
        description
        |> Result.bind (fun descriptionValue ->
            origin
            |> Result.bind (fun originValue ->
                itemsOf document.evidence
                |> List.mapi (fun index item -> index, item)
                |> traverse (fun (index, item) ->
                    evidenceFromDocument (sprintf "%s.evidence[%d]" path index) item)
                |> Result.map (fun evidence ->
                    { Project = project
                      ActivityType = activityType
                      Date = date
                      Duration = duration
                      Description = descriptionValue
                      Origin = originValue
                      Evidence = evidence }))))

let private entryIdsFrom (path: string) (values: string array) : Result<EntryId list, DocumentError> =
    let items = itemsOf values

    if List.isEmpty items then
        Error(MissingField path)
    else
        items
        |> traverse (fun raw -> EntryId.create raw |> at path)

let private reasonFrom (path: string) (value: string) : Result<Reason, DocumentError> =
    requireText path value
    |> Result.bind (fun text -> Reason.create text |> at path)

let private changeFromDocument
    (path: string)
    (document: RevisionDocument)
    : Result<RevisionChange, DocumentError> =
    match document.change_kind with
    | "created" -> Ok Created
    | "corrected" -> reasonFrom (path + ".change_reason") document.change_reason |> Result.map Corrected
    | "voided" -> reasonFrom (path + ".change_reason") document.change_reason |> Result.map Voided
    | "restored" -> reasonFrom (path + ".change_reason") document.change_reason |> Result.map Restored
    | "split_into" ->
        entryIdsFrom (path + ".change_entry_ids") document.change_entry_ids
        |> Result.map SplitInto
    | "created_by_merge_of" ->
        entryIdsFrom (path + ".change_entry_ids") document.change_entry_ids
        |> Result.map CreatedByMergeOf
    | "merged_into" ->
        requireText (path + ".change_entry_id") document.change_entry_id
        |> Result.bind (fun raw -> EntryId.create raw |> at (path + ".change_entry_id"))
        |> Result.map MergedInto
    | "created_by_split_of" ->
        requireText (path + ".change_entry_id") document.change_entry_id
        |> Result.bind (fun raw -> EntryId.create raw |> at (path + ".change_entry_id"))
        |> Result.map CreatedBySplitOf
    | "evidence_reassigned_from" ->
        requireText (path + ".change_entry_id") document.change_entry_id
        |> Result.bind (fun raw -> EntryId.create raw |> at (path + ".change_entry_id"))
        |> Result.map EvidenceReassignedFrom
    | "evidence_attached" ->
        evidenceFromDocument (path + ".change_evidence") document.change_evidence
        |> Result.map EvidenceAttached
    | other -> Error(UnknownChangeKind other)

let private revisionFromDocument (path: string) (document: RevisionDocument) : Result<Revision, DocumentError> =
    if isNull (box document) then
        Error(MissingField path)
    else
        requireText (path + ".revision_id") document.revision_id
        |> Result.bind (fun raw -> RevisionId.create raw |> at (path + ".revision_id"))
        |> Result.bind (fun revisionId ->
            changeFromDocument path document
            |> Result.map (fun change -> revisionId, change))
        |> Result.bind (fun (revisionId, change) ->
            factsFromDocument (path + ".facts") document.facts
            |> Result.map (fun facts -> revisionId, change, facts))
        |> Result.bind (fun (revisionId, change, facts) ->
            requireText (path + ".recorded_by") document.recorded_by
            |> Result.bind (fun raw -> UserId.create raw |> at (path + ".recorded_by"))
            |> Result.bind (fun actor ->
                requireText (path + ".device") document.device
                |> Result.bind (fun raw -> DeviceLabel.create raw |> at (path + ".device"))
                |> Result.map (fun device ->
                    { Id = revisionId
                      Change = change
                      Facts = facts
                      RecordedAt = Instant.ofEpochMilliseconds document.recorded_at_ms
                      RecordedBy = actor
                      Device = device })))

let private stateFromDocument (document: EntryDocument) : Result<EntryState, DocumentError> =
    match document.state_kind with
    | "active" -> Ok Active
    | "void" ->
        if document.voided_at_ms <= 0L then
            Error(StateFieldsInconsistent("void", "voided_at_ms must be positive"))
        else
            reasonFrom "void_reason" document.void_reason
            |> Result.map (fun reason -> Void(reason, Instant.ofEpochMilliseconds document.voided_at_ms))
    | "superseded_by_split" ->
        entryIdsFrom "superseded_children" document.superseded_children
        |> Result.map (SupersededBySplit >> Superseded)
    | "superseded_by_merge" ->
        requireText "superseded_target" document.superseded_target
        |> Result.bind (fun raw -> EntryId.create raw |> at "superseded_target")
        |> Result.map (SupersededByMerge >> Superseded)
    | other -> Error(UnknownStateKind other)

/// Rebuild an entry from its stored document.
///
/// `version` is supplied by the store, not read from the document: it is the
/// blob SHA of this content, so it cannot live inside the content it hashes
/// (see `Documents`). Pass `None` for an entry that has not been persisted.
let fromDocument (version: VersionToken option) (document: EntryDocument) : Result<TimeEntry, DocumentError> =
    if isNull (box document) then
        Error(MissingField "document")
    elif document.schema_version <> CurrentSchemaVersion then
        Error(UnsupportedSchemaVersion(document.schema_version, CurrentSchemaVersion))
    else

    let history = itemsOf document.history

    if List.isEmpty history then
        Error EmptyHistory
    else
        requireText "entry_id" document.entry_id
        |> Result.bind (fun raw -> EntryId.create raw |> at "entry_id")
        |> Result.bind (fun entryId ->
            stateFromDocument document |> Result.map (fun state -> entryId, state))
        |> Result.bind (fun (entryId, state) ->
            factsFromDocument "effective" document.effective
            |> Result.map (fun facts -> entryId, state, facts))
        |> Result.bind (fun (entryId, state, facts) ->
            history
            |> List.mapi (fun index revision -> index, revision)
            |> traverse (fun (index, revision) ->
                revisionFromDocument (sprintf "history[%d]" index) revision)
            |> Result.map (fun revisions ->
                { Id = entryId
                  State = state
                  Effective = facts
                  History = revisions
                  Version = version }))

// ---------------------------------------------------------------------------
// Catalogue
// ---------------------------------------------------------------------------

let private catalogueEntryFields
    (path: string)
    (document: CatalogueEntryDocument)
    : Result<string * CatalogueName * CatalogueStatus * VersionToken, DocumentError> =
    if isNull (box document) then
        Error(MissingField path)
    else
        requireText (path + ".id") document.id
        |> Result.bind (fun id ->
            requireText (path + ".name") document.name
            |> Result.bind (fun rawName -> CatalogueName.create rawName |> at (path + ".name"))
            |> Result.bind (fun name ->
                requireText (path + ".version") document.version
                |> Result.bind (fun rawVersion ->
                    VersionToken.create rawVersion |> at (path + ".version"))
                |> Result.map (fun projectionVersion ->
                    id, name, CatalogueStatus.ofActiveFlag document.active, projectionVersion)))

let private projectFromDocument
    (path: string)
    (document: CatalogueEntryDocument)
    : Result<Project, DocumentError> =
    catalogueEntryFields path document
    |> Result.bind (fun (id, name, status, projectionVersion) ->
        ProjectId.create id
        |> at (path + ".id")
        |> Result.map (fun projectId ->
            { Id = projectId
              Name = name
              Status = status
              ProjectionVersion = projectionVersion }: Project))

let private activityTypeFromDocument
    (path: string)
    (document: CatalogueEntryDocument)
    : Result<ActivityType, DocumentError> =
    catalogueEntryFields path document
    |> Result.bind (fun (id, name, status, projectionVersion) ->
        ActivityTypeId.create id
        |> at (path + ".id")
        |> Result.map (fun activityTypeId ->
            { Id = activityTypeId
              Name = name
              Status = status
              ProjectionVersion = projectionVersion }: ActivityType))

/// Total: every catalogue has a document form.
///
/// Present for round-trip testing and for a future writer, even though this
/// application only ever reads the catalogue — a mapping verified in one
/// direction only is a mapping whose losses are invisible.
let catalogueToDocument (catalogue: Catalogue) : CatalogueDocument =
    let entry id name status projectionVersion : CatalogueEntryDocument =
        { id = id
          name = CatalogueName.value name
          active = CatalogueStatus.toActiveFlag status
          version = VersionToken.value projectionVersion }

    { schema_version = CurrentSchemaVersion
      projects =
        catalogue.Projects
        |> Map.toList
        |> List.map (fun (_, p) -> entry (ProjectId.value p.Id) p.Name p.Status p.ProjectionVersion)
        |> Array.ofList
      activity_types =
        catalogue.ActivityTypes
        |> Map.toList
        |> List.map (fun (_, a) ->
            entry (ActivityTypeId.value a.Id) a.Name a.Status a.ProjectionVersion)
        |> Array.ofList }

let catalogueFromDocument (document: CatalogueDocument) : Result<Catalogue, DocumentError> =
    if isNull (box document) then
        Error(MissingField "document")
    elif document.schema_version <> CurrentSchemaVersion then
        Error(UnsupportedSchemaVersion(document.schema_version, CurrentSchemaVersion))
    else
        itemsOf document.projects
        |> List.mapi (fun index item -> index, item)
        |> traverse (fun (index, item) -> projectFromDocument (sprintf "projects[%d]" index) item)
        |> Result.bind (fun projects ->
            itemsOf document.activity_types
            |> List.mapi (fun index item -> index, item)
            |> traverse (fun (index, item) ->
                activityTypeFromDocument (sprintf "activity_types[%d]" index) item)
            |> Result.map (fun activityTypes -> Catalogue.ofLists projects activityTypes))
