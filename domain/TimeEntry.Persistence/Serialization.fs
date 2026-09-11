/// Tier 4 — Persistence. JSON encoding of a stored entry.
///
/// Kept separate from `Mapping` so the wire format and the domain translation
/// can be reasoned about independently: `Mapping` is pure data shuffling with
/// no dependency on a serializer, and this module is the only place that knows
/// JSON exists.
module TimeEntry.Persistence.Serialization

open System.Text.Json
open System.Text.Json.Serialization
open TimeEntry.Persistence.Documents

/// Indented, with nulls written out.
///
/// Indentation is a deliberate choice, not a default: these files are reviewed
/// as GitHub diffs, and a minified blob would make every correction look like
/// a whole-file rewrite. Writing nulls rather than omitting them keeps a
/// document's shape stable, so a diff shows a value changing rather than a
/// key appearing.
let options =
    let o = JsonSerializerOptions(WriteIndented = true)
    o.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
    o

/// The DTOs are primitives only (see `Documents`), so this needs no custom
/// converters and cannot be surprised by an F# union or option.
let write (document: EntryDocument) : string =
    JsonSerializer.Serialize(document, options)

/// Why a stored file could not be decoded at the JSON level, as distinct from
/// being well-formed JSON with bad content (which is `DocumentError`).
type DecodeError =
    | MalformedJson of detail: string
    | EmptyContent

let read (content: string) : Result<EntryDocument, DecodeError> =
    if System.String.IsNullOrWhiteSpace content then
        Error EmptyContent
    else
        try
            // An F# record is not a nullable type, so the decoded value is
            // boxed to test it: `JsonSerializer` can still hand back null for
            // the JSON literal `null`.
            let document = JsonSerializer.Deserialize<EntryDocument>(content, options)

            if isNull (box document) then
                Error(MalformedJson "document decoded to null")
            else
                Ok document
        with :? JsonException as ex ->
            // Caught and converted rather than propagated: a corrupt file must
            // become one unreadable entry, not a failed projection
            // (TE-R-084).
            Error(MalformedJson ex.Message)
