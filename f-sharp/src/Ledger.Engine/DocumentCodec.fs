namespace Ledger.Engine

open System
open System.Text.Json.Nodes
open Ledger.Domain

/// Explicit JSON round-trip for `LedgerDocument` — the payload persisted via
/// the Storage effect. This module only establishes *structural* validity
/// (shapes parse, enums are one of the known values, dates parse); *business*
/// validity (references still exist/are active, no overlaps, non-blank
/// fields) is re-checked separately by `Commands.validateDocument` once a
/// document decodes successfully — a corrupted or hand-edited value is
/// rejected the same way a bad command would be, never silently coerced.
module DocumentCodec =

    let private encodeEvidenceType =
        function
        | UrlEvidence -> "Url"
        | LinkedInPostEvidence -> "LinkedInPost"
        | GitHubCommitEvidence -> "GitHubCommit"
        | PullRequestEvidence -> "PullRequest"
        | IssueEvidence -> "Issue"
        | CalendarEventEvidence -> "CalendarEvent"
        | DocumentEvidence -> "Document"
        | ScreenshotEvidence -> "Screenshot"
        | OtherEvidence -> "Other"

    let private decodeEvidenceType =
        function
        | "Url" -> Ok UrlEvidence
        | "LinkedInPost" -> Ok LinkedInPostEvidence
        | "GitHubCommit" -> Ok GitHubCommitEvidence
        | "PullRequest" -> Ok PullRequestEvidence
        | "Issue" -> Ok IssueEvidence
        | "CalendarEvent" -> Ok CalendarEventEvidence
        | "Document" -> Ok DocumentEvidence
        | "Screenshot" -> Ok ScreenshotEvidence
        | "Other" -> Ok OtherEvidence
        | other -> Error $"unknown evidence type '{other}'"

    let private encodeEntryMethod =
        function
        | Manual -> "Manual"
        | EntryMethod.Split -> "Split"
        | EntryMethod.Merge -> "Merge"
        | EntryMethod.Timer -> "Timer"

    let private decodeEntryMethod =
        function
        | "Manual" -> Ok Manual
        | "Split" -> Ok EntryMethod.Split
        | "Merge" -> Ok EntryMethod.Merge
        | "Timer" -> Ok EntryMethod.Timer
        | other -> Error $"unknown entry method '{other}'"

    let private optString (obj: JsonObject) (key: string) : string option =
        match obj.[key] with
        | null -> None
        | node -> Some(node.GetValue<string>())

    let private optInstant (obj: JsonObject) (key: string) : DateTimeOffset option =
        optString obj key |> Option.map DateTimeOffset.Parse

    let private requireField (obj: JsonObject) (key: string) : Result<JsonNode, string> =
        match obj.[key] with
        | null -> Error $"missing field '{key}'"
        | node -> Ok node

    let private ( >>= ) result f = Result.bind f result

    let private sequence (results: Result<'a, string> seq) : Result<'a list, string> =
        Seq.foldBack (fun item acc -> acc >>= fun items -> item |> Result.map (fun i -> i :: items)) results (Ok [])

    let private stringArray (values: string seq) =
        let array = JsonArray()
        for v in values do
            array.Add(JsonValue.Create(v: string))
        array :> JsonNode

    let private decodeStringArray (node: JsonNode) = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq

    // --- Evidence -----------------------------------------------------------

    let private encodeEvidence (evidence: Evidence) : JsonNode =
        let o = JsonObject()
        o.["evidenceLinkId"] <- JsonValue.Create(evidence.EvidenceLinkId)
        o.["type"] <- JsonValue.Create(encodeEvidenceType evidence.Type)
        o.["uri"] <- (evidence.Uri |> Option.map JsonValue.Create |> Option.defaultValue null)
        o.["note"] <- (evidence.Note |> Option.map JsonValue.Create |> Option.defaultValue null)
        o.["hash"] <- (evidence.Hash |> Option.map JsonValue.Create |> Option.defaultValue null)
        o.["label"] <- JsonValue.Create(evidence.Label)
        o.["attachedAt"] <- JsonValue.Create(evidence.AttachedAt.ToString("O"))
        o :> JsonNode

    let private decodeEvidence (node: JsonNode) : Result<Evidence, string> =
        let o = node.AsObject()
        requireField o "evidenceLinkId"
        >>= fun idNode ->
            requireField o "type"
            >>= fun typeNode -> decodeEvidenceType (typeNode.GetValue<string>())
            >>= fun evidenceType ->
                requireField o "attachedAt"
                >>= fun attachedAtNode ->
                    Ok
                        { EvidenceLinkId = idNode.GetValue<string>()
                          Type = evidenceType
                          Uri = optString o "uri"
                          Note = optString o "note"
                          Hash = optString o "hash"
                          Label = o.["label"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                          AttachedAt = DateTimeOffset.Parse(attachedAtNode.GetValue<string>()) }

    // --- AuditEvent -----------------------------------------------------------

    let private encodeAuditEvent (event: AuditEvent) : JsonNode =
        let o = JsonObject()
        let stamp (at: DateTimeOffset) = o.["at"] <- JsonValue.Create(at.ToString("O"))
        match event with
        | Created at ->
            o.["kind"] <- JsonValue.Create("Created")
            stamp at
        | Amended(at, reason) ->
            o.["kind"] <- JsonValue.Create("Amended")
            stamp at
            o.["reason"] <- JsonValue.Create(reason)
        | AuditEvent.Voided(at, reason) ->
            o.["kind"] <- JsonValue.Create("Voided")
            stamp at
            o.["reason"] <- JsonValue.Create(reason)
        | Restored(at, reason) ->
            o.["kind"] <- JsonValue.Create("Restored")
            stamp at
            o.["reason"] <- JsonValue.Create(reason)
        | AuditEvent.Superseded(at, reason) ->
            o.["kind"] <- JsonValue.Create("Superseded")
            stamp at
            o.["reason"] <- JsonValue.Create(reason)
        | EvidenceAttached(at, evidenceLinkId) ->
            o.["kind"] <- JsonValue.Create("EvidenceAttached")
            stamp at
            o.["evidenceLinkId"] <- JsonValue.Create(evidenceLinkId)
        | EvidenceDetached(at, evidenceLinkId, reason) ->
            o.["kind"] <- JsonValue.Create("EvidenceDetached")
            stamp at
            o.["evidenceLinkId"] <- JsonValue.Create(evidenceLinkId)
            o.["reason"] <- JsonValue.Create(reason)
        | SplitPerformed(at, replacementIds, reason) ->
            o.["kind"] <- JsonValue.Create("SplitPerformed")
            stamp at
            o.["replacementIds"] <- stringArray replacementIds
            o.["reason"] <- JsonValue.Create(reason)
        | Merged(at, sourceIds, reason) ->
            o.["kind"] <- JsonValue.Create("Merged")
            stamp at
            o.["sourceIds"] <- stringArray sourceIds
            o.["reason"] <- JsonValue.Create(reason)
        o :> JsonNode

    let private decodeAuditEvent (node: JsonNode) : Result<AuditEvent, string> =
        let o = node.AsObject()
        requireField o "kind"
        >>= fun kindNode ->
            requireField o "at"
            >>= fun atNode ->
                let at = DateTimeOffset.Parse(atNode.GetValue<string>())
                let reason () = o.["reason"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                match kindNode.GetValue<string>() with
                | "Created" -> Ok(Created at)
                | "Amended" -> Ok(Amended(at, reason ()))
                | "Voided" -> Ok(AuditEvent.Voided(at, reason ()))
                | "Restored" -> Ok(Restored(at, reason ()))
                | "Superseded" -> Ok(AuditEvent.Superseded(at, reason ()))
                | "EvidenceAttached" ->
                    requireField o "evidenceLinkId" >>= fun n -> Ok(EvidenceAttached(at, n.GetValue<string>()))
                | "EvidenceDetached" ->
                    requireField o "evidenceLinkId" >>= fun n -> Ok(EvidenceDetached(at, n.GetValue<string>(), reason ()))
                | "SplitPerformed" ->
                    requireField o "replacementIds" >>= fun n -> Ok(SplitPerformed(at, decodeStringArray n, reason ()))
                | "Merged" ->
                    requireField o "sourceIds" >>= fun n -> Ok(Merged(at, decodeStringArray n, reason ()))
                | other -> Error $"unknown audit event kind '{other}'"

    // --- Relationships -----------------------------------------------------------

    let private encodeRelationships (relationships: Relationships) : JsonNode =
        let o = JsonObject()
        o.["mergedInto"] <- (relationships.MergedInto |> Option.map JsonValue.Create |> Option.defaultValue null)
        o.["splitInto"] <- (relationships.SplitInto |> Option.map stringArray |> Option.defaultValue null)
        o.["mergedFrom"] <- (relationships.MergedFrom |> Option.map stringArray |> Option.defaultValue null)
        o :> JsonNode

    let private decodeRelationships (node: JsonNode) : Relationships =
        let o = node.AsObject()
        { MergedInto = optString o "mergedInto"
          SplitInto = (match o.["splitInto"] with null -> None | n -> Some(decodeStringArray n))
          MergedFrom = (match o.["mergedFrom"] with null -> None | n -> Some(decodeStringArray n)) }

    // --- Activity -----------------------------------------------------------

    let private encodeActivity (activity: Activity) : JsonNode =
        let o = JsonObject()
        o.["activityId"] <- JsonValue.Create(activity.ActivityId)
        o.["activityTypeId"] <- JsonValue.Create(activity.ActivityTypeId)
        o.["projectId"] <- JsonValue.Create(activity.ProjectId)
        o.["description"] <- JsonValue.Create(activity.Description)
        o.["businessPurpose"] <- JsonValue.Create(activity.BusinessPurpose)
        o.["outcome"] <- JsonValue.Create(activity.Outcome)
        o.["tagIds"] <- stringArray activity.TagIds
        o.["entryMethod"] <- JsonValue.Create(encodeEntryMethod activity.EntryMethod)
        o.["reconstructionReason"] <- (activity.ReconstructionReason |> Option.map JsonValue.Create |> Option.defaultValue null)
        o.["startedAt"] <- JsonValue.Create(activity.StartedAt.ToString("O"))
        o.["endedAt"] <- JsonValue.Create(activity.EndedAt.ToString("O"))
        o.["clientTimestamp"] <- (activity.ClientTimestamp |> Option.map (fun d -> JsonValue.Create(d.ToString("O"))) |> Option.defaultValue null)
        o.["serverReceivedAt"] <- JsonValue.Create(activity.ServerReceivedAt.ToString("O"))
        o.["voided"] <- JsonValue.Create(activity.Voided)
        o.["superseded"] <- JsonValue.Create(activity.Superseded)

        let evidence = JsonArray()
        for item in activity.Evidence do
            evidence.Add(encodeEvidence item)
        o.["evidence"] <- evidence

        let history = JsonArray()
        for item in activity.History do
            history.Add(encodeAuditEvent item)
        o.["history"] <- history

        o.["relationships"] <- encodeRelationships activity.Relationships
        o.["version"] <- JsonValue.Create(activity.Version)
        o.["updatedAt"] <- JsonValue.Create(activity.UpdatedAt.ToString("O"))
        o :> JsonNode

    let private decodeActivity (node: JsonNode) : Result<Activity, string> =
        let o = node.AsObject()
        requireField o "activityId"
        >>= fun idNode ->
            requireField o "activityTypeId"
            >>= fun typeIdNode ->
                requireField o "projectId"
                >>= fun projectIdNode ->
                    requireField o "entryMethod"
                    >>= fun methodNode -> decodeEntryMethod (methodNode.GetValue<string>())
                    >>= fun entryMethod ->
                        requireField o "startedAt"
                        >>= fun startedAtNode ->
                            requireField o "endedAt"
                            >>= fun endedAtNode ->
                                requireField o "serverReceivedAt"
                                >>= fun serverReceivedAtNode ->
                                    requireField o "version"
                                    >>= fun versionNode ->
                                        requireField o "updatedAt"
                                        >>= fun updatedAtNode ->
                                            let evidenceResult =
                                                match o.["evidence"] with
                                                | null -> Ok []
                                                | arrayNode -> arrayNode.AsArray() |> Seq.map decodeEvidence |> sequence
                                            evidenceResult
                                            >>= fun evidence ->
                                                let historyResult =
                                                    match o.["history"] with
                                                    | null -> Ok []
                                                    | arrayNode -> arrayNode.AsArray() |> Seq.map decodeAuditEvent |> sequence
                                                historyResult
                                                >>= fun history ->
                                                    let tagIds =
                                                        match o.["tagIds"] with
                                                        | null -> Set.empty
                                                        | n -> decodeStringArray n |> Set.ofList
                                                    let relationships =
                                                        match o.["relationships"] with
                                                        | null -> Relationships.empty
                                                        | n -> decodeRelationships n
                                                    Ok
                                                        { ActivityId = idNode.GetValue<string>()
                                                          ActivityTypeId = typeIdNode.GetValue<string>()
                                                          ProjectId = projectIdNode.GetValue<string>()
                                                          Description = o.["description"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                                                          BusinessPurpose = o.["businessPurpose"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                                                          Outcome = o.["outcome"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                                                          TagIds = tagIds
                                                          EntryMethod = entryMethod
                                                          ReconstructionReason = optString o "reconstructionReason"
                                                          StartedAt = DateTimeOffset.Parse(startedAtNode.GetValue<string>())
                                                          EndedAt = DateTimeOffset.Parse(endedAtNode.GetValue<string>())
                                                          ClientTimestamp = optInstant o "clientTimestamp"
                                                          ServerReceivedAt = DateTimeOffset.Parse(serverReceivedAtNode.GetValue<string>())
                                                          Voided = o.["voided"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<bool>()) |> Option.defaultValue false
                                                          Superseded = o.["superseded"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<bool>()) |> Option.defaultValue false
                                                          Evidence = evidence
                                                          History = history
                                                          Relationships = relationships
                                                          Version = versionNode.GetValue<int64>()
                                                          UpdatedAt = DateTimeOffset.Parse(updatedAtNode.GetValue<string>()) }

    // --- DailyAttestation -----------------------------------------------------------

    let private encodeAttestation (attestation: DailyAttestation) : JsonNode =
        let o = JsonObject()
        o.["attestationId"] <- JsonValue.Create(attestation.AttestationId)
        o.["date"] <- JsonValue.Create(attestation.Date.ToString("yyyy-MM-dd"))
        o.["statement"] <- JsonValue.Create(attestation.Statement)
        o.["attestedSequence"] <- JsonValue.Create(attestation.AttestedSequence)
        o.["attestedAt"] <- JsonValue.Create(attestation.AttestedAt.ToString("O"))
        o :> JsonNode

    let private decodeAttestation (node: JsonNode) : Result<DailyAttestation, string> =
        let o = node.AsObject()
        requireField o "attestationId"
        >>= fun idNode ->
            requireField o "date"
            >>= fun dateNode ->
                match DateOnly.TryParse(dateNode.GetValue<string>()) with
                | false, _ -> Error $"invalid attestation date '{dateNode.GetValue<string>()}'"
                | true, date ->
                    requireField o "attestedSequence"
                    >>= fun seqNode ->
                        requireField o "attestedAt"
                        >>= fun attestedAtNode ->
                            Ok
                                { AttestationId = idNode.GetValue<string>()
                                  Date = date
                                  Statement = o.["statement"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>()) |> Option.defaultValue ""
                                  AttestedSequence = seqNode.GetValue<int64>()
                                  AttestedAt = DateTimeOffset.Parse(attestedAtNode.GetValue<string>()) }

    // --- LedgerDocument -----------------------------------------------------------

    let encode (document: LedgerDocument) : string =
        let root = JsonObject()
        root.["schemaVersion"] <- JsonValue.Create(document.SchemaVersion)

        let activities = JsonArray()
        for activity in document.Activities do
            activities.Add(encodeActivity activity)
        root.["activities"] <- activities

        let attestations = JsonArray()
        for attestation in document.Attestations do
            attestations.Add(encodeAttestation attestation)
        root.["attestations"] <- attestations

        root.["eventSequence"] <- JsonValue.Create(document.EventSequence)
        root.ToJsonString()

    let decode (json: string) : Result<LedgerDocument, string> =
        try
            let node = JsonNode.Parse(json)
            let o = node.AsObject()

            let activitiesResult =
                match o.["activities"] with
                | null -> Ok []
                | arrayNode -> arrayNode.AsArray() |> Seq.map decodeActivity |> sequence

            activitiesResult
            >>= fun activities ->
                let attestationsResult =
                    match o.["attestations"] with
                    | null -> Ok []
                    | arrayNode -> arrayNode.AsArray() |> Seq.map decodeAttestation |> sequence

                attestationsResult
                >>= fun attestations ->
                    Ok
                        { SchemaVersion = o.["schemaVersion"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<int>()) |> Option.defaultValue 1
                          Activities = activities
                          Attestations = attestations
                          EventSequence = o.["eventSequence"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<int64>()) |> Option.defaultValue 0L }
        with ex ->
            Error $"malformed stored document: {ex.Message}"
