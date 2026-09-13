namespace Ledger.Engine

open System
open System.Text.Json.Nodes
open Ledger.Domain
open EchelonFoundry.Chrona.Integration

/// SDE Tier 3: Chrona's own records for an inbound `TimeObservation`
/// working its way toward becoming (or not becoming) an authoritative
/// `Ledger.Domain.Model.Activity`. See
/// `docs/integration/INTEGRATION-CONTRACT.md`.
///
/// This module is the boundary adaptation the specification calls for
/// (§32 "historical adaptation belongs at the boundary") — it is the
/// only place in this codebase that references
/// `EchelonFoundry.Chrona.Integration`. Nothing here performs I/O: no
/// GitHub calls, no WASM, matching §38's "separate pure and impure
/// logic." The actual GitHub reading/writing and WASM startup wiring
/// that will call into this module are deferred to follow-up work
/// (CHR-INT-011 onward).
module Integration =

    /// Two independent state systems (specification §30), never
    /// collapsed into one: this is the *time decision* a Chrona user
    /// makes about a candidate — separate from, and irrelevant to,
    /// whether the *observation itself* has been processed (see
    /// `ObservationResult`, below). "Processed" never means "accepted as
    /// billable time."
    type CandidateState =
        | Proposed
        | Accepted
        | Modified
        | Rejected

    /// Not yet a Chrona time entry (specification §19) — an accepted
    /// candidate becomes an `Activity` only via the existing
    /// `Ledger.Domain.Commands.create`, in a future PR. Every field here
    /// is exactly what the source `TimeObservation` asserted; nothing in
    /// this record represents a Chrona decision about correctness.
    type TimeCandidate =
        { CandidateId: string
          SourceObservationId: string
          SourceSystem: string
          ProjectId: string
          WorkItemId: string option
          ActorId: string option
          ProposedStart: DateTimeOffset option
          ProposedEnd: DateTimeOffset option
          ProposedDurationMinutes: int option
          Description: string option
          State: CandidateState }

    module TimeCandidate =
        /// Deterministic — derived from the observation id rather than
        /// freshly generated (specification §25: "prefer deterministic
        /// identity if it fits the existing architecture"). Two
        /// reconciliation passes over the same observation always
        /// compute the same candidate id without needing to search for
        /// one first, which is exactly the property idempotent startup
        /// reconciliation (§21-26) depends on.
        let candidateId (observationId: string) = $"candidate:{observationId}"

        let private candidateStateText =
            function
            | Proposed -> "proposed"
            | Accepted -> "accepted"
            | Modified -> "modified"
            | Rejected -> "rejected"

        let private parseCandidateState =
            function
            | "proposed" -> Some Proposed
            | "accepted" -> Some Accepted
            | "modified" -> Some Modified
            | "rejected" -> Some Rejected
            | _ -> None

        let private optionalStringNode (value: string option) : JsonNode =
            match value with
            | Some v -> JsonValue.Create(v: string) :> JsonNode
            | None -> null

        let private optionalIntNode (value: int option) : JsonNode =
            match value with
            | Some v -> JsonValue.Create(v: int) :> JsonNode
            | None -> null

        let private optionalInstantNode (value: DateTimeOffset option) : JsonNode =
            match value with
            | Some v -> JsonValue.Create(v.ToString "O") :> JsonNode
            | None -> null

        /// Deterministic, hand-rolled — matching this codebase's existing
        /// `GitHubSync.fs`/`Chrona.Integration`'s own serialization
        /// convention rather than reflection-based defaults.
        let serialize (candidate: TimeCandidate) : string =
            let o = JsonObject()
            o.["candidateId"] <- JsonValue.Create(candidate.CandidateId)
            o.["sourceObservationId"] <- JsonValue.Create(candidate.SourceObservationId)
            o.["sourceSystem"] <- JsonValue.Create(candidate.SourceSystem)
            o.["projectId"] <- JsonValue.Create(candidate.ProjectId)
            o.["workItemId"] <- optionalStringNode candidate.WorkItemId
            o.["actorId"] <- optionalStringNode candidate.ActorId
            o.["proposedStart"] <- optionalInstantNode candidate.ProposedStart
            o.["proposedEnd"] <- optionalInstantNode candidate.ProposedEnd
            o.["proposedDurationMinutes"] <- optionalIntNode candidate.ProposedDurationMinutes
            o.["description"] <- optionalStringNode candidate.Description
            o.["state"] <- JsonValue.Create(candidateStateText candidate.State)
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
            stringField node key
            |> Option.bind (fun text -> match DateTimeOffset.TryParse text with true, dt -> Some dt | _ -> None)

        let deserialize (json: string) : Result<TimeCandidate, string> =
            try
                let node = JsonNode.Parse(json).AsObject()
                match stringField node "candidateId", stringField node "sourceObservationId", stringField node "sourceSystem", stringField node "projectId", stringField node "state" |> Option.bind parseCandidateState with
                | Some candidateId, Some sourceObservationId, Some sourceSystem, Some projectId, Some state ->
                    Ok
                        { CandidateId = candidateId
                          SourceObservationId = sourceObservationId
                          SourceSystem = sourceSystem
                          ProjectId = projectId
                          WorkItemId = stringField node "workItemId"
                          ActorId = stringField node "actorId"
                          ProposedStart = instantField node "proposedStart"
                          ProposedEnd = instantField node "proposedEnd"
                          ProposedDurationMinutes = intField node "proposedDurationMinutes"
                          Description = stringField node "description"
                          State = state }
                | _ -> Error "candidate JSON is missing a required field or has an unrecognized state"
            with ex ->
                Error $"candidate JSON could not be read: {ex.Message}"

    /// Chrona-owned structural mapping from a validated `TimeObservation`
    /// to a proposed `TimeCandidate` — never re-validates what
    /// `TimeObservation.create`/`deserialize` already guaranteed
    /// structurally, and never decides Chrona domain policy (does this
    /// project exist, is it active, does it overlap an existing entry,
    /// ...). That is `ObservationReconciliation`'s job, below, informed
    /// by the current `Environment`.
    module ObservationMapping =
        let toCandidateProposal (observation: TimeObservation) : TimeCandidate =
            { CandidateId = TimeCandidate.candidateId observation.ObservationId
              SourceObservationId = observation.ObservationId
              SourceSystem = observation.SourceSystem
              ProjectId = observation.ProjectId
              WorkItemId = observation.WorkItemId
              ActorId = observation.ActorId
              ProposedStart = observation.StartedAt
              ProposedEnd = observation.EndedAt
              ProposedDurationMinutes = observation.DurationMinutes
              Description = observation.Description
              State = Proposed }

    /// The outcome recorded once Chrona has processed one observation —
    /// permanent regardless of whatever a human later decides about the
    /// resulting candidate (specification §16/§29/§30). Never conflated
    /// with `CandidateState`: a candidate later `Rejected` by a user
    /// still has a `CandidateCreated` receipt — ingestion succeeded even
    /// though the claimed time was not, in the end, recorded.
    type ObservationResult =
        | CandidateCreated of candidateId: string
        | Rejected of reason: string
        | Ignored of reason: string

    type ProcessingReceipt =
        { ReceiptVersion: string
          ObservationId: string
          ProcessedAt: DateTimeOffset
          Result: ObservationResult }

    module ProcessingReceipt =
        [<Literal>]
        let CurrentReceiptVersion = "1"

        let private resultText =
            function
            | CandidateCreated _ -> "candidate-created"
            | Rejected _ -> "rejected"
            | Ignored _ -> "ignored"

        /// Matches the specification's own worked example (§16): a bare
        /// `"result"` discriminator plus whichever of `candidateId`/
        /// `reason` that result carries.
        let serialize (receipt: ProcessingReceipt) : string =
            let o = JsonObject()
            o.["receiptVersion"] <- JsonValue.Create(receipt.ReceiptVersion)
            o.["observationId"] <- JsonValue.Create(receipt.ObservationId)
            o.["processedAt"] <- JsonValue.Create(receipt.ProcessedAt.ToString "O")
            o.["result"] <- JsonValue.Create(resultText receipt.Result)
            match receipt.Result with
            | CandidateCreated candidateId -> o.["candidateId"] <- JsonValue.Create(candidateId)
            | Rejected reason | Ignored reason -> o.["reason"] <- JsonValue.Create(reason)
            o.ToJsonString()

        let deserialize (json: string) : Result<ProcessingReceipt, string> =
            try
                let node = JsonNode.Parse(json).AsObject()
                let stringField (key: string) = match node.[key] with null -> None | v -> try Some(v.GetValue<string>()) with _ -> None
                match stringField "receiptVersion", stringField "observationId", stringField "processedAt", stringField "result" with
                | Some receiptVersion, Some observationId, Some processedAtText, Some resultText ->
                    match DateTimeOffset.TryParse processedAtText with
                    | false, _ -> Error "processing receipt has an unreadable processedAt"
                    | true, processedAt ->
                        let result =
                            match resultText with
                            | "candidate-created" -> stringField "candidateId" |> Option.map CandidateCreated
                            | "rejected" -> stringField "reason" |> Option.map Rejected
                            | "ignored" -> stringField "reason" |> Option.map Ignored
                            | _ -> None
                        match result with
                        | Some r -> Ok { ReceiptVersion = receiptVersion; ObservationId = observationId; ProcessedAt = processedAt; Result = r }
                        | None -> Error "processing receipt has an unrecognized result, or is missing the field that result requires"
                | _ -> Error "processing receipt JSON is missing a required field"
            with ex ->
                Error $"processing receipt JSON could not be read: {ex.Message}"

    /// The pure decision at the heart of startup reconciliation
    /// (specification §21-26). Contains no GitHub calls (§38's "keep
    /// dependencies explicit... do not hide GitHub clients in global
    /// state" — there is no global state here at all): every fact this
    /// needs about what already exists is a parameter, supplied by
    /// whatever future GitHub-facing code (CHR-INT-011 onward) has
    /// already read.
    module ObservationReconciliation =

        /// What the caller must do next for one observation. The two
        /// "nothing new to create" cases are kept distinct
        /// (`AlreadyProcessed` vs. `ReceiptRepairNeeded`) because they
        /// call for different I/O: the first calls for none at all, the
        /// second calls for writing *only* the missing receipt, for the
        /// exact candidate that already exists — never a new one
        /// (specification §22/23's "candidate written, receipt failed"
        /// recovery case).
        type ObservationStatus =
            | AlreadyProcessed
            | ReceiptRepairNeeded of TimeCandidate
            | CandidateCreated of TimeCandidate
            | Rejected of reason: string

        let private describeErrors (errors: TimeObservation.Error list) : string =
            let describe =
                function
                | TimeObservation.MissingObservationId -> "missing observationId"
                | TimeObservation.MissingSourceSystem -> "missing sourceSystem"
                | TimeObservation.MissingOrganizationId -> "missing organizationId"
                | TimeObservation.MissingProjectId -> "missing projectId"
                | TimeObservation.MissingObservedAt -> "missing observedAt"
                | TimeObservation.InvalidTimeRange -> "invalid time range"
                | TimeObservation.InvalidDuration -> "invalid duration"
                | TimeObservation.AmbiguousTimeRepresentation -> "both an interval and a duration were given"
                | TimeObservation.MissingTimeRepresentation -> "neither an interval nor a duration was given"
                | TimeObservation.UnsupportedContractVersion v -> $"unsupported contract version \"{v}\""
                | TimeObservation.MalformedPayload message -> $"malformed payload ({message})"
            errors |> List.map describe |> String.concat "; "

        /// Chrona domain policy this assembly is responsible for
        /// (specification §10's table, §62's "unknown project handling"):
        /// `Chrona.Integration` only guarantees the payload is
        /// structurally legal, never that `ProjectId` actually exists.
        /// For V1, an unrecognized project is rejected outright rather
        /// than guessed at or silently invented (§62: "explicit rejection
        /// or unresolved state is safer than guessing").
        let private applyDomainPolicy (environment: Environment) (observation: TimeObservation) : ObservationStatus =
            if environment.Projects.ContainsKey observation.ProjectId then
                CandidateCreated(ObservationMapping.toCandidateProposal observation)
            else
                Rejected $"Unknown project '{observation.ProjectId}'."

        /// Given a validated observation and what's already known to
        /// exist for its id, decide what happens next.
        let decide (environment: Environment) (existingReceipt: bool) (existingCandidate: TimeCandidate option) (observation: TimeObservation) : ObservationStatus =
            match existingReceipt, existingCandidate with
            | true, _ -> AlreadyProcessed
            | false, Some candidate -> ReceiptRepairNeeded candidate
            | false, None -> applyDomainPolicy environment observation

        /// The full per-observation decision starting from raw, untrusted
        /// wire bytes — mirrors specification §21's algorithm end-to-end
        /// as one pure function (deserialize -> validate contract ->
        /// validate Chrona policy -> propose a candidate), still
        /// performing no I/O: `rawJson` is whatever the caller already
        /// read, `existingReceipt`/`existingCandidate` whatever the
        /// caller already looked up.
        let reconcileRaw (environment: Environment) (existingReceipt: bool) (existingCandidate: TimeCandidate option) (rawJson: string) : ObservationStatus =
            if existingReceipt then
                AlreadyProcessed
            else
                match existingCandidate with
                | Some candidate -> ReceiptRepairNeeded candidate
                | None ->
                    match TimeObservation.deserialize rawJson with
                    | Error errors -> Rejected(describeErrors errors)
                    | Ok observation -> applyDomainPolicy environment observation
