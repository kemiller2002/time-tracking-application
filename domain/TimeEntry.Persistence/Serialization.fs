/// Tier 4 — Persistence. JSON encoding of a stored entry.
///
/// Kept separate from `Mapping` so the wire format and the domain translation
/// can be reasoned about independently: `Mapping` is pure data shuffling with
/// no dependency on a serializer, and this module is the only place that knows
/// JSON exists.
///
/// ## Why this is written by hand
///
/// It used to be `JsonSerializer.Serialize(document, options)` — reflection
/// over the record's fields. That reads better and was the right first move,
/// but it has two costs that grew:
///
/// 1. **It cannot be trimmed.** The trim analyser reports `IL2026` against
///    reflection-based `System.Text.Json`, because it cannot see which types
///    will be constructed at run time. Trimming had to be disabled for the
///    whole WebAssembly bundle, which shipped at 29 MB.
///
///    The usual answer — `JsonSerializerContext` with `[JsonSerializable]` —
///    is **not available here**. That is a Roslyn source generator, and F#
///    does not run Roslyn generators. It is the same reason `[JSExport]` is
///    inert in F# and the browser host needs a C# file. Verified rather than
///    assumed: WI-0030 was written on the assumption that source generation
///    was the fix, and it is not.
///
/// 2. **The stored field names were implicit.** Under reflection they came
///    from the F# record's field names, so renaming a field in `Documents`
///    would silently change the format of every stored file. Writing them out
///    makes the contract explicit, which is what `Documents` says matters:
///    these names match what `worker/src/store.js` already established.
///
/// The cost is that a new field must be added in two places. That is a real
/// cost, and the round-trip tests are what make it a loud one rather than a
/// quiet one.
module TimeEntry.Persistence.Serialization

open System.Text
open System.Text.Json
open TimeEntry.Persistence.Documents

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

/// Indented, with nulls written out.
///
/// Indentation is a deliberate choice, not a default: these files are reviewed
/// as GitHub diffs, and a minified blob would make every correction look like
/// a whole-file rewrite. Writing nulls rather than omitting them keeps a
/// document's shape stable, so a diff shows a value changing rather than a
/// key appearing.
let private writerOptions = JsonWriterOptions(Indented = true)

/// A string field, or JSON null. `null` and `""` are NOT the same: an empty
/// description is a description, and a missing one is not.
let private writeText (writer: Utf8JsonWriter) (name: string) (value: string) =
    if isNull value then writer.WriteNull name else writer.WriteString(name, value)

let private writeEvidence (writer: Utf8JsonWriter) (evidence: EvidenceDocument) =
    writer.WriteStartObject()
    writer.WriteString("uri", evidence.uri)
    writeText writer "label" evidence.label
    writer.WriteNumber("attached_at_ms", evidence.attached_at_ms)
    writer.WriteEndObject()

let private writeEvidenceArray (writer: Utf8JsonWriter) (name: string) (items: EvidenceDocument array) =
    if isNull items then
        writer.WriteNull name
    else
        writer.WriteStartArray name

        for item in items do
            writeEvidence writer item

        writer.WriteEndArray()

let private writeStringArray (writer: Utf8JsonWriter) (name: string) (items: string array) =
    if isNull items then
        writer.WriteNull name
    else
        writer.WriteStartArray name

        for item in items do
            writer.WriteStringValue item

        writer.WriteEndArray()

let private writeFacts (writer: Utf8JsonWriter) (name: string) (facts: FactsDocument) =
    writer.WriteStartObject name
    writer.WriteString("project_id", facts.project_id)
    writer.WriteString("activity_type_id", facts.activity_type_id)
    writer.WriteString("date", facts.date)
    writer.WriteNumber("exact_duration_ms", facts.exact_duration_ms)
    writeText writer "description" facts.description
    writer.WriteString("origin_kind", facts.origin_kind)
    writeText writer "manual_reason" facts.manual_reason
    writeEvidenceArray writer "evidence" facts.evidence
    writer.WriteEndObject()

let private writeRevision (writer: Utf8JsonWriter) (revision: RevisionDocument) =
    writer.WriteStartObject()
    writer.WriteString("revision_id", revision.revision_id)
    writer.WriteString("change_kind", revision.change_kind)
    writeText writer "change_reason" revision.change_reason
    writeStringArray writer "change_entry_ids" revision.change_entry_ids
    writeText writer "change_entry_id" revision.change_entry_id

    if isNull (box revision.change_evidence) then
        writer.WriteNull "change_evidence"
    else
        writer.WritePropertyName "change_evidence"
        writeEvidence writer revision.change_evidence

    writeFacts writer "facts" revision.facts
    writer.WriteNumber("recorded_at_ms", revision.recorded_at_ms)
    writer.WriteString("recorded_by", revision.recorded_by)
    writer.WriteString("device", revision.device)
    writer.WriteEndObject()

let private toJson (build: Utf8JsonWriter -> unit) =
    use stream = new System.IO.MemoryStream()
    use writer = new Utf8JsonWriter(stream, writerOptions)
    build writer
    writer.Flush()
    Encoding.UTF8.GetString(stream.ToArray())

let write (document: EntryDocument) : string =
    toJson (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema_version", document.schema_version)
        writer.WriteString("entry_id", document.entry_id)
        writer.WriteString("state_kind", document.state_kind)
        writeText writer "void_reason" document.void_reason
        writer.WriteNumber("voided_at_ms", document.voided_at_ms)
        writeStringArray writer "superseded_children" document.superseded_children
        writeText writer "superseded_target" document.superseded_target
        writeFacts writer "effective" document.effective
        writer.WriteStartArray "history"

        if not (isNull document.history) then
            for revision in document.history do
                writeRevision writer revision

        writer.WriteEndArray()
        writer.WriteEndObject())

let writeCatalogue (document: CatalogueDocument) : string =
    let writeEntries (writer: Utf8JsonWriter) name (items: CatalogueEntryDocument array) =
        writer.WriteStartArray(name: string)

        if not (isNull items) then
            for item in items do
                writer.WriteStartObject()
                writer.WriteString("id", item.id)
                writer.WriteString("name", item.name)
                writer.WriteBoolean("active", item.active)
                writer.WriteString("version", item.version)
                writer.WriteEndObject()

        writer.WriteEndArray()

    toJson (fun writer ->
        writer.WriteStartObject()
        writer.WriteString("schema_version", document.schema_version)
        writeEntries writer "projects" document.projects
        writeEntries writer "activity_types" document.activity_types
        writer.WriteEndObject())

// ---------------------------------------------------------------------------
// Reading
// ---------------------------------------------------------------------------

/// Why a stored file could not be decoded at the JSON level, as distinct from
/// being well-formed JSON with bad content (which is `DocumentError`).
type DecodeError =
    | MalformedJson of detail: string
    | EmptyContent

/// A missing property and an explicit `null` are both absence. The reader is
/// deliberately lenient about which one a writer chose, because a document
/// hand-edited in a GitHub diff may well have a key deleted rather than
/// nulled — and refusing that would turn a readable file into an unreadable
/// entry over punctuation.
let private text (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> null

let private required (element: JsonElement) (name: string) =
    match text element name with
    | null -> failwithf "missing or non-string '%s'" name
    | value -> value

let private number (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt64()
    | _ -> 0L

let private boolean (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.True -> true
    | _ -> false

let private array (element: JsonElement) (name: string) (read: JsonElement -> 'a) : 'a array =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray() |> Seq.map read |> Array.ofSeq
    | _ -> null

let private readEvidence (element: JsonElement) : EvidenceDocument =
    { uri = required element "uri"
      label = text element "label"
      attached_at_ms = number element "attached_at_ms" }

let private readFacts (element: JsonElement) (name: string) : FactsDocument =
    match element.TryGetProperty name with
    | true, facts when facts.ValueKind = JsonValueKind.Object ->
        { project_id = required facts "project_id"
          activity_type_id = required facts "activity_type_id"
          date = required facts "date"
          exact_duration_ms = number facts "exact_duration_ms"
          description = text facts "description"
          origin_kind = required facts "origin_kind"
          manual_reason = text facts "manual_reason"
          evidence = array facts "evidence" readEvidence }
    | _ -> failwithf "missing '%s'" name

let private readRevision (element: JsonElement) : RevisionDocument =
    { revision_id = required element "revision_id"
      change_kind = required element "change_kind"
      change_reason = text element "change_reason"
      change_entry_ids =
        array element "change_entry_ids" (fun item ->
            if item.ValueKind = JsonValueKind.String then
                item.GetString()
            else
                failwith "change_entry_ids must contain strings")
      change_entry_id = text element "change_entry_id"
      change_evidence =
        match element.TryGetProperty "change_evidence" with
        | true, value when value.ValueKind = JsonValueKind.Object -> readEvidence value
        | _ -> Unchecked.defaultof<EvidenceDocument>
      facts = readFacts element "facts"
      recorded_at_ms = number element "recorded_at_ms"
      recorded_by = required element "recorded_by"
      device = required element "device" }

/// Parse, then build. Any failure below — malformed JSON, a missing required
/// field, a value of the wrong kind — becomes `MalformedJson` rather than
/// propagating: a corrupt file must become one unreadable entry, not a failed
/// projection (TE-R-084).
let private decode (content: string) (build: JsonElement -> 'a) : Result<'a, DecodeError> =
    if System.String.IsNullOrWhiteSpace content then
        Error EmptyContent
    else
        try
            use parsed = JsonDocument.Parse content

            if parsed.RootElement.ValueKind <> JsonValueKind.Object then
                Error(MalformedJson "document is not a JSON object")
            else
                Ok(build parsed.RootElement)
        with
        | :? JsonException as ex -> Error(MalformedJson ex.Message)
        | ex -> Error(MalformedJson ex.Message)

let read (content: string) : Result<EntryDocument, DecodeError> =
    decode content (fun root ->
        { schema_version = required root "schema_version"
          entry_id = required root "entry_id"
          state_kind = required root "state_kind"
          void_reason = text root "void_reason"
          voided_at_ms = number root "voided_at_ms"
          superseded_children =
            array root "superseded_children" (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    item.GetString()
                else
                    failwith "superseded_children must contain strings")
          superseded_target = text root "superseded_target"
          effective = readFacts root "effective"
          history = array root "history" readRevision })

let readCatalogue (content: string) : Result<CatalogueDocument, DecodeError> =
    let readEntry (element: JsonElement) : CatalogueEntryDocument =
        { id = required element "id"
          name = required element "name"
          active = boolean element "active"
          version = required element "version" }

    decode content (fun root ->
        { schema_version = required root "schema_version"
          projects = array root "projects" readEntry
          activity_types = array root "activity_types" readEntry })
