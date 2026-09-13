namespace EchelonFoundry.Chrona.Integration

open System
open System.Globalization
open System.Text.Json.Nodes

/// The public inbound integration contract for Chrona: the data Chrona
/// accepts from external systems (ROS, a manual import, ...) for
/// consideration as time. See `docs/integration/INTEGRATION-CONTRACT.md`
/// for the full ownership/versioning/compatibility standard this module
/// implements.
///
/// This is deliberately transport-independent (no GitHub/HTTP/WASM code),
/// stands alone (no dependency on `Ledger.Domain`/`Ledger.Engine`/
/// `Ledger.Wasm`), and is deterministic: `serialize`/`deserialize` build
/// and read JSON explicitly rather than relying on default reflection-
/// based serializer behavior.

/// A pointer to supporting material for a `TimeObservation` — never the
/// material itself. `Reference` is producer-defined and this assembly
/// does not interpret its shape (a commit SHA, a PR number, a work-item
/// id, ...); `Kind` gives Chrona (and a person reviewing a candidate) a
/// hint for how to render or follow it. Example kinds: "github-commit",
/// "github-pr", "ros-work-item", "ros-execution".
type Evidence =
    { Kind: string
      Reference: string }

/// A statement from an external system that work activity occurred,
/// submitted for Chrona's consideration as time. This is deliberately
/// NOT a Chrona time entry (Chrona's existing authoritative record is
/// `Ledger.Domain.Model.Activity`, created only via `Ledger.Domain.
/// Commands.create`) — Chrona alone decides whether, and how, an
/// observation becomes recorded time. Every field beyond identity/
/// versioning is exactly what the producer asserted; Chrona domain
/// policy (does this project exist, is this actor recognized, does it
/// overlap another entry, ...) is decided entirely outside this
/// assembly, by Chrona itself.
type TimeObservation =
    { ContractVersion: string
      ObservationId: string
      SourceSystem: string
      OrganizationId: string
      ProjectId: string
      WorkItemId: string option
      ActorId: string option
      StartedAt: DateTimeOffset option
      EndedAt: DateTimeOffset option
      DurationMinutes: int option
      Description: string option
      Evidence: Evidence list
      ObservedAt: DateTimeOffset }

module TimeObservation =

    /// The only contract version this package currently produces or
    /// accepts. See `docs/integration/INTEGRATION-CONTRACT.md`'s
    /// "Versioning" section for why this is independent of the NuGet
    /// package version.
    [<Literal>]
    let CurrentContractVersion = "1"

    /// Structural validity failures only (see `create`'s doc comment for
    /// the line between this assembly's responsibility and Chrona's own
    /// domain policy) — plus the two failure modes specific to reading an
    /// untrusted wire payload (`MalformedPayload`, `UnsupportedContractVersion`).
    type Error =
        | MissingObservationId
        | MissingSourceSystem
        | MissingOrganizationId
        | MissingProjectId
        | MissingObservedAt
        | InvalidTimeRange
        | InvalidDuration
        | AmbiguousTimeRepresentation
        | MissingTimeRepresentation
        | UnsupportedContractVersion of string
        | MalformedPayload of string

    let private isBlank (value: string) = String.IsNullOrWhiteSpace value

    /// Every legal way to express "one immutable observation's time" for
    /// contract version 1: exactly an interval, or exactly a duration —
    /// never both (§9's "reject contradictory input"), never neither,
    /// never a single bare instant with nothing to pair it with.
    let private timeRepresentationErrors
        (startedAt: DateTimeOffset option)
        (endedAt: DateTimeOffset option)
        (durationMinutes: int option)
        : Error list =
        match startedAt, endedAt, durationMinutes with
        | Some s, Some e, None -> if e < s then [ InvalidTimeRange ] else []
        | None, None, Some d -> if d <= 0 then [ InvalidDuration ] else []
        | Some _, Some _, Some _ -> [ AmbiguousTimeRepresentation ]
        | None, None, None -> [ MissingTimeRepresentation ]
        | Some _, None, _ | None, Some _, _ -> [ InvalidTimeRange ]

    /// Validated construction — the only way to obtain a `TimeObservation`
    /// from already-typed F# values (see `deserialize` for the untrusted-
    /// wire-payload entry point, which reuses this same validation).
    ///
    /// Validates only structural legality:
    ///   - the required identity fields are non-blank;
    ///   - exactly one legal time representation is present;
    ///   - `endedAt >= startedAt` for an interval; `durationMinutes > 0`
    ///     for a duration.
    ///
    /// Does NOT validate — and never will, regardless of future contract
    /// versions — anything that requires knowing about Chrona's own
    /// state: whether `projectId`/`workItemId`/`actorId` are recognized,
    /// whether this would overlap an existing entry, or any other Chrona
    /// domain policy. That decision belongs entirely to Chrona itself,
    /// outside this assembly.
    let create
        (observationId: string)
        (sourceSystem: string)
        (organizationId: string)
        (projectId: string)
        (workItemId: string option)
        (actorId: string option)
        (startedAt: DateTimeOffset option)
        (endedAt: DateTimeOffset option)
        (durationMinutes: int option)
        (description: string option)
        (evidence: Evidence list)
        (observedAt: DateTimeOffset)
        : Result<TimeObservation, Error list> =
        let errors =
            [ if isBlank observationId then MissingObservationId
              if isBlank sourceSystem then MissingSourceSystem
              if isBlank organizationId then MissingOrganizationId
              if isBlank projectId then MissingProjectId
              yield! timeRepresentationErrors startedAt endedAt durationMinutes ]
        match errors with
        | [] ->
            Ok
                { ContractVersion = CurrentContractVersion
                  ObservationId = observationId
                  SourceSystem = sourceSystem
                  OrganizationId = organizationId
                  ProjectId = projectId
                  WorkItemId = workItemId
                  ActorId = actorId
                  StartedAt = startedAt
                  EndedAt = endedAt
                  DurationMinutes = durationMinutes
                  Description = description
                  Evidence = evidence
                  ObservedAt = observedAt }
        | _ -> Error errors

    // --- Serialization ----------------------------------------------------
    //
    // Hand-rolled via System.Text.Json.Nodes, matching this codebase's
    // existing convention (Ledger.Engine/GitHubSync.fs, Protocol.fs) rather
    // than relying on default reflection-based serialization — the wire
    // shape is a contract, not an implementation detail. Key order below
    // matches docs/integration/INTEGRATION-CONTRACT.md's worked example.

    let private optionalStringNode (value: string option) : JsonNode =
        match value with
        | Some v -> JsonValue.Create(v: string) :> JsonNode
        | None -> null

    let private optionalIntNode (value: int option) : JsonNode =
        match value with
        | Some v -> JsonValue.Create(v: int) :> JsonNode
        | None -> null

    let private instantText (value: DateTimeOffset) = value.ToString("O", CultureInfo.InvariantCulture)

    let private optionalInstantNode (value: DateTimeOffset option) : JsonNode =
        match value with
        | Some v -> JsonValue.Create(instantText v) :> JsonNode
        | None -> null

    let private evidenceNode (item: Evidence) : JsonNode =
        let o = JsonObject()
        o.["kind"] <- JsonValue.Create(item.Kind)
        o.["reference"] <- JsonValue.Create(item.Reference)
        o

    /// The deterministic wire representation. Two equal `TimeObservation`
    /// values always produce byte-identical output; field order never
    /// depends on dictionary/hash ordering (there is none — every field
    /// is assigned explicitly, in the fixed order below).
    let serialize (observation: TimeObservation) : string =
        let o = JsonObject()
        o.["contractVersion"] <- JsonValue.Create(observation.ContractVersion)
        o.["observationId"] <- JsonValue.Create(observation.ObservationId)
        o.["sourceSystem"] <- JsonValue.Create(observation.SourceSystem)
        o.["organizationId"] <- JsonValue.Create(observation.OrganizationId)
        o.["projectId"] <- JsonValue.Create(observation.ProjectId)
        o.["workItemId"] <- optionalStringNode observation.WorkItemId
        o.["actorId"] <- optionalStringNode observation.ActorId
        o.["startedAt"] <- optionalInstantNode observation.StartedAt
        o.["endedAt"] <- optionalInstantNode observation.EndedAt
        o.["durationMinutes"] <- optionalIntNode observation.DurationMinutes
        o.["description"] <- optionalStringNode observation.Description
        let evidenceArray = JsonArray()
        observation.Evidence |> List.iter (fun item -> evidenceArray.Add(evidenceNode item))
        o.["evidence"] <- evidenceArray
        o.["observedAt"] <- JsonValue.Create(instantText observation.ObservedAt)
        o.ToJsonString()

    let private stringField (node: JsonObject) (key: string) : string option =
        match node.[key] with
        | null -> None
        | v -> try Some(v.GetValue<string>()) with _ -> None

    let private intField (node: JsonObject) (key: string) : int option =
        match node.[key] with
        | null -> None
        | v -> try Some(v.GetValue<int>()) with _ -> None

    let private instantField (node: JsonObject) (key: string) : DateTimeOffset option =
        match stringField node key with
        | None -> None
        | Some text ->
            match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, dt -> Some dt
            | false, _ -> None

    /// Reads an untrusted wire payload back into a validated
    /// `TimeObservation` — the only way to obtain one from a string.
    /// A contract version other than `CurrentContractVersion` is refused
    /// outright (`UnsupportedContractVersion`) rather than guessed at;
    /// malformed JSON produces `MalformedPayload` rather than an
    /// unhandled exception; an unrecognized extra field is silently
    /// ignored (forward-compatible with a future contract version's
    /// additive fields, as long as `contractVersion` still matches).
    /// Every structural check `create` performs also applies here — a
    /// payload missing a required field or asserting a contradictory
    /// time representation is rejected exactly the same way a
    /// hand-constructed one would be.
    let deserialize (json: string) : Result<TimeObservation, Error list> =
        try
            let node = JsonNode.Parse(json).AsObject()
            let contractVersion = stringField node "contractVersion" |> Option.defaultValue ""
            if contractVersion <> CurrentContractVersion then
                Error [ UnsupportedContractVersion contractVersion ]
            else
                match instantField node "observedAt" with
                | None -> Error [ MissingObservedAt ]
                | Some observedAt ->
                    let evidence =
                        match node.["evidence"] with
                        | null -> []
                        | evidenceNodeValue ->
                            evidenceNodeValue.AsArray()
                            |> Seq.map (fun item ->
                                let eo = item.AsObject()
                                { Kind = stringField eo "kind" |> Option.defaultValue ""
                                  Reference = stringField eo "reference" |> Option.defaultValue "" })
                            |> List.ofSeq
                    create
                        (stringField node "observationId" |> Option.defaultValue "")
                        (stringField node "sourceSystem" |> Option.defaultValue "")
                        (stringField node "organizationId" |> Option.defaultValue "")
                        (stringField node "projectId" |> Option.defaultValue "")
                        (stringField node "workItemId")
                        (stringField node "actorId")
                        (instantField node "startedAt")
                        (instantField node "endedAt")
                        (intField node "durationMinutes")
                        (stringField node "description")
                        evidence
                        observedAt
        with ex ->
            Error [ MalformedPayload ex.Message ]
