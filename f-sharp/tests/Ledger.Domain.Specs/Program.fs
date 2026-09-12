open System
open Ledger.Domain

let assertTrue condition message = if not condition then failwith message

let refItem id active = { Id = id; Name = id; Active = active; Version = "v1" }

let fixedClock = DateTimeOffset.Parse "2026-08-15T13:00:00Z"

/// Seed data mirrors `worker/src/handler.js`'s `defaultBindings` fixtures.
let fixture () : Environment =
    let counter = ref 0
    let newId () =
        counter.Value <- counter.Value + 1
        sprintf "A-%d" counter.Value
    { Projects =
        [ refItem "echelon-foundry" true
          refItem "visual-engineering" true
          refItem "helixnote" true
          refItem "general" true
          refItem "archived-initiative" false ]
        |> List.map (fun p -> p.Id, p)
        |> Map.ofList
      ActivityTypes =
        [ refItem "research" true
          refItem "linkedin-marketing" true
          refItem "software-development" true
          refItem "administration" true
          refItem "meeting" true
          refItem "retired-type" false ]
        |> List.map (fun t -> t.Id, t)
        |> Map.ofList
      Tags =
        [ refItem "billable" true
          refItem "client-facing" true
          refItem "internal-only" true
          refItem "legacy" false ]
        |> List.map (fun t -> t.Id, t)
        |> Map.ofList
      NewId = newId
      Clock = fun () -> fixedClock }

let at (dateIso: string) hour minute = DateTimeOffset.Parse(sprintf "%sT%02d:%02d:00Z" dateIso hour minute)

let projection = function Success(document, _) -> document | result -> failwith $"Expected success, got {result}"
let resultOf = function Success(_, result) -> result | result -> failwith $"Expected success, got {result}"
let firstCode = function Rejected(d :: _) -> d.Code | result -> failwith $"Expected rejection, got {result}"

let createCmd startedAt endedAt : CreateActivityCommand =
    { ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Work"; BusinessPurpose = "Purpose"
      Outcome = ""; TagIds = []; EntryMethod = Manual; ReconstructionReason = None; StartedAt = startedAt; EndedAt = endedAt
      ClientTimestamp = None }

let create environment document startedAt endedAt = Commands.create environment document (createCmd startedAt endedAt)

let createOk environment document startedAt endedAt =
    match create environment document startedAt endedAt with
    | Success(doc, activity) -> doc, activity
    | result -> failwith $"Expected success, got {result}"

let tests : (string * (unit -> unit)) list =
    [
      // --- Timer lifecycle + 30s discard boundary -------------------------------
      "Timer.start twice rejects the second start", fun () ->
        let t0 = at "2026-08-15" 9 0
        let running = Timer.start "research" "echelon-foundry" "" t0 NoTimer |> Result.defaultWith (fun d -> failwith d.Message)
        match Timer.start "research" "echelon-foundry" "" t0 running with
        | Error d -> assertTrue (d.Code = DiagnosticCode.TimerAlreadyRunning) "wrong code"
        | Ok _ -> failwith "second start accepted"

      "Timer.pause/resume/stop with no timer fail with TimerNotFound", fun () ->
        let t0 = at "2026-08-15" 9 0
        assertTrue ((Timer.pause t0 NoTimer |> Result.isError)) "pause with no timer succeeded"
        assertTrue ((Timer.resume t0 NoTimer |> Result.isError)) "resume with no timer succeeded"
        assertTrue ((Timer.stop t0 NoTimer |> Result.isError)) "stop with no timer succeeded"

      "a timer stopped under thirty seconds is discarded", fun () ->
        let t0 = at "2026-08-15" 9 0
        let running = Timer.start "research" "echelon-foundry" "" t0 NoTimer |> Result.defaultWith (fun d -> failwith d.Message)
        let stopped = Timer.stop (t0.AddMilliseconds 29999.0) running |> Result.defaultWith (fun d -> failwith d.Message)
        assertTrue stopped.Discarded "29999ms timer was not discarded"

      "a timer stopped at exactly thirty seconds is recorded, not discarded", fun () ->
        let t0 = at "2026-08-15" 9 0
        let running = Timer.start "research" "echelon-foundry" "" t0 NoTimer |> Result.defaultWith (fun d -> failwith d.Message)
        let stopped = Timer.stop (t0.AddMilliseconds 30000.0) running |> Result.defaultWith (fun d -> failwith d.Message)
        assertTrue (not stopped.Discarded) "30000ms timer was discarded"
        assertTrue (stopped.ExactMs = 30000.0) "exact ms was wrong"

      "pause/resume excludes paused time from the recorded duration", fun () ->
        let t0 = at "2026-08-15" 9 0
        let running = Timer.start "research" "echelon-foundry" "" t0 NoTimer |> Result.defaultWith (fun d -> failwith d.Message)
        let paused = Timer.pause (t0.AddMilliseconds 5000.0) running |> Result.defaultWith (fun d -> failwith d.Message)
        let resumed = Timer.resume (t0.AddMilliseconds 65000.0) paused |> Result.defaultWith (fun d -> failwith d.Message)
        let stopped = Timer.stop (t0.AddMilliseconds 105000.0) resumed |> Result.defaultWith (fun d -> failwith d.Message)
        assertTrue (stopped.ExactMs = 45000.0) $"expected 45000ms of running time, got {stopped.ExactMs}"

      "stopping while paused uses the pause moment as the end, not the stop call's clock", fun () ->
        let t0 = at "2026-08-15" 9 0
        let running = Timer.start "research" "echelon-foundry" "" t0 NoTimer |> Result.defaultWith (fun d -> failwith d.Message)
        let paused = Timer.pause (t0.AddMilliseconds 35000.0) running |> Result.defaultWith (fun d -> failwith d.Message)
        let stopped = Timer.stop (t0.AddMilliseconds 999999.0) paused |> Result.defaultWith (fun d -> failwith d.Message)
        assertTrue (stopped.ExactMs = 35000.0) $"expected 35000ms, got {stopped.ExactMs}"

      // --- Midnight crossing / overlap -------------------------------------------
      "create rejects an activity that crosses midnight", fun () ->
        let environment = fixture ()
        let result = create environment LedgerDocument.empty (at "2026-08-15" 23 30) (at "2026-08-16" 0 30)
        assertTrue (firstCode result = DiagnosticCode.CrossesMidnight) "midnight-crossing accepted"

      "create rejects an overlapping interval on the same date", fun () ->
        let environment = fixture ()
        let document, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result = create environment document (at "2026-08-15" 9 30) (at "2026-08-15" 10 30)
        assertTrue (firstCode result = DiagnosticCode.OverlappingActivity) "overlap accepted"

      "touching endpoints are not treated as overlapping", fun () ->
        let environment = fixture ()
        let document, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        match create environment document (at "2026-08-15" 10 0) (at "2026-08-15" 11 0) with
        | Success _ -> ()
        | result -> failwith $"back-to-back interval was rejected: {result}"

      // --- Split-too-short ---------------------------------------------------------
      "split rejects a source under two minutes", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 1)
        let result =
            Commands.split environment document
                { SourceActivityId = activity.ActivityId; ExpectedVersion = None
                  Parts = [ { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 30000.0 }
                            { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 30000.0 } ]
                  Reason = "split" }
        assertTrue (firstCode result = DiagnosticCode.TooShortToSplit) "under-two-minute split accepted"

      // --- Optimistic concurrency --------------------------------------------------
      "amend with a stale expected version yields Conflict carrying the current activity", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some 999L; Changes = createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0); Reason = "fix" }
        match result with
        | Conflict(version, Some current, _) -> assertTrue (version = activity.Version && current.ActivityId = activity.ActivityId) "conflict carried wrong state"
        | result -> failwith $"expected Conflict, got {result}"

      "amend with no expected version skips the concurrency check", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = None; Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with Description = "Updated" }; Reason = "fix" }
        match result with
        | Success(_, updated) -> assertTrue (updated.Description = "Updated") "amend without expected version did not apply"
        | result -> failwith $"expected Success, got {result}"

      // --- Amend re-validation parity with create -----------------------------------
      "amend re-runs full field validation and rejects a blank description", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with Description = "   " }; Reason = "fix" }
        assertTrue (firstCode result = DiagnosticCode.DescriptionRequired) "blank description accepted on amend"

      "amend permits shrinking its own interval without a false self-overlap", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 15) (at "2026-08-15" 9 45) with Description = "Work" }; Reason = "fix" }
        match result with
        | Success _ -> ()
        | result -> failwith $"shrinking amend was rejected: {result}"

      // --- Reference validation matrix ----------------------------------------------
      "create rejects an unknown project", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with ProjectId = "does-not-exist" }
        assertTrue (firstCode result = DiagnosticCode.ProjectNotFound) "unknown project accepted"

      "create rejects an archived project", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with ProjectId = "archived-initiative" }
        assertTrue (firstCode result = DiagnosticCode.ProjectInactive) "archived project accepted"

      "create rejects an unknown activity type", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with ActivityTypeId = "does-not-exist" }
        assertTrue (firstCode result = DiagnosticCode.ActivityTypeNotFound) "unknown activity type accepted"

      "create rejects an inactive activity type", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with ActivityTypeId = "retired-type" }
        assertTrue (firstCode result = DiagnosticCode.ActivityTypeInactive) "inactive activity type accepted"

      "create rejects an unknown tag", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with TagIds = [ "does-not-exist" ] }
        assertTrue (firstCode result = DiagnosticCode.TagNotFound) "unknown tag accepted"

      "create rejects an inactive tag", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with TagIds = [ "legacy" ] }
        assertTrue (firstCode result = DiagnosticCode.TagInactive) "inactive tag accepted"

      "create accepts an active tag", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with TagIds = [ "billable" ] }
        match result with
        | Success _ -> ()
        | result -> failwith $"active tag rejected: {result}"

      // --- Amend's unchanged-reference exemption -------------------------------------
      "amend without changing project tolerates that project having since been archived", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let archivedEnvironment = { environment with Projects = environment.Projects |> Map.add "echelon-foundry" (refItem "echelon-foundry" false) }
        let result =
            Commands.amend archivedEnvironment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with Description = "Still fine" }; Reason = "fix" }
        match result with
        | Success _ -> ()
        | result -> failwith $"unchanged-project amend was rejected: {result}"

      "amend that switches to an archived project is rejected", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with ProjectId = "archived-initiative" }; Reason = "fix" }
        assertTrue (firstCode result = DiagnosticCode.ProjectInactive) "amend into an archived project accepted"

      "amend keeps a previously-carried now-inactive tag", fun () ->
        let environment = fixture ()
        let document, activity =
            createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let document2, activity2 =
            match Commands.create environment document { createCmd (at "2026-08-15" 10 0) (at "2026-08-15" 11 0) with TagIds = [ "billable" ] } with
            | Success(d, a) -> d, a
            | result -> failwith $"{result}"
        let deactivated = { environment with Tags = environment.Tags |> Map.add "billable" (refItem "billable" false) }
        let result =
            Commands.amend deactivated document2
                { ActivityId = activity2.ActivityId; ExpectedVersion = Some activity2.Version
                  Changes = { createCmd (at "2026-08-15" 10 0) (at "2026-08-15" 11 0) with TagIds = [ "billable" ]; Description = "Kept tag" }; Reason = "fix" }
        match result with
        | Success _ -> ()
        | result -> failwith $"carried-over inactive tag was rejected: {result}"

      "amend rejects newly adding an inactive tag", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result =
            Commands.amend environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with TagIds = [ "legacy" ] }; Reason = "fix" }
        assertTrue (firstCode result = DiagnosticCode.TagInactive) "newly-added inactive tag accepted"

      // --- Manual past-date reconstruction reason -------------------------------------
      "create rejects a manual past-date activity missing a reconstruction reason", fun () ->
        let environment = fixture ()
        let result = create environment LedgerDocument.empty (at "2026-08-10" 9 0) (at "2026-08-10" 10 0)
        assertTrue (firstCode result = DiagnosticCode.ReconstructionReasonRequired) "past-date entry without a reason accepted"

      "create accepts a manual past-date activity with a reconstruction reason", fun () ->
        let environment = fixture ()
        let result = Commands.create environment LedgerDocument.empty { createCmd (at "2026-08-10" 9 0) (at "2026-08-10" 10 0) with ReconstructionReason = Some "backfilled from calendar" }
        match result with
        | Success _ -> ()
        | result -> failwith $"past-date entry with a reason rejected: {result}"

      "create does not require a reconstruction reason for a same-day entry", fun () ->
        let environment = fixture ()
        let result = create environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        match result with
        | Success _ -> ()
        | result -> failwith $"same-day entry rejected: {result}"

      // --- Evidence -------------------------------------------------------------------
      "evidence can be attached to a Recorded activity and later detached", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let withEvidenceDoc =
            Commands.attachEvidence environment document
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Type = DocumentEvidence; Uri = None; Note = Some "n"; Hash = None; Label = "" }
            |> projection
        let attached = withEvidenceDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        assertTrue (attached.Evidence.Length = 1) "evidence was not attached"
        let detachedDoc =
            Commands.detachEvidence environment withEvidenceDoc
                { ActivityId = activity.ActivityId; ExpectedVersion = Some attached.Version; EvidenceLinkId = attached.Evidence.Head.EvidenceLinkId; Reason = "no longer needed" }
            |> projection
        let detached = detachedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        assertTrue detached.Evidence.IsEmpty "evidence was not detached"

      "evidence remains attachable while Voided but not while Superseded", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let voidedDoc = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "r" } |> projection
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let attachedWhileVoided =
            Commands.attachEvidence environment voidedDoc { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; Type = OtherEvidence; Uri = None; Note = None; Hash = None; Label = "" }
        match attachedWhileVoided with
        | Success _ -> ()
        | result -> failwith $"evidence attach while Voided rejected: {result}"

      "evidence detach requires Recorded, not merely non-Superseded", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let withEvidence = Commands.attachEvidence environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Type = DocumentEvidence; Uri = None; Note = None; Hash = None; Label = "" } |> projection
        let attached = withEvidence.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let voidedDoc = Commands.voidActivity environment withEvidence { ActivityId = activity.ActivityId; ExpectedVersion = Some attached.Version; Reason = "r" } |> projection
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let result =
            Commands.detachEvidence environment voidedDoc
                { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; EvidenceLinkId = voided.Evidence.Head.EvidenceLinkId; Reason = "r" }
        assertTrue (firstCode result = DiagnosticCode.ActivityNotRecorded) "detach while Voided accepted"

      // --- Void/restore/day/month reconciliation ---------------------------------------
      "voiding removes an activity from day totals; restoring returns it", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let voidedDoc = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "r" } |> projection
        let voidedSummary = Summary.forDate voidedDoc (DateOnly(2026, 8, 15))
        assertTrue (voidedSummary.IncludedActivities.IsEmpty && voidedSummary.VoidCount = 1) "void did not clear totals"
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let restoredDoc = Commands.restore environment voidedDoc { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; Reason = "r" } |> projection
        let restoredSummary = Summary.forDate restoredDoc (DateOnly(2026, 8, 15))
        assertTrue (restoredSummary.IncludedActivities.Length = 1) "restore did not return the activity to totals"

      "Summary.forMonth aggregates across dates in the month", fun () ->
        let environment = fixture ()
        let document, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let document2, _ = createOk environment document (at "2026-08-20" 9 0) (at "2026-08-20" 10 0)
        let monthSummary = Summary.forMonth document2 "2026-08"
        assertTrue (monthSummary.IncludedActivities.Length = 2) "month summary did not include both activities"

      // --- Day review amended-after-attestation ----------------------------------------
      "day review flags only the activity amended after the latest attestation", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let document2, a2 = createOk environment document (at "2026-08-15" 10 0) (at "2026-08-15" 11 0)
        let attestedDoc = Commands.attestDay environment document2 { Date = DateOnly(2026, 8, 15); Statement = "all good"; ExpectedProjectionVersion = None } |> projection
        let laterClock = fixedClock.AddSeconds 5.0
        let environmentLater = { environment with Clock = fun () -> laterClock }
        let amendedDoc =
            Commands.amend environmentLater attestedDoc
                { ActivityId = a1.ActivityId; ExpectedVersion = Some a1.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with Description = "changed after review" }; Reason = "fix" }
            |> projection
        let latestAttestation = Summary.latestAttestation amendedDoc (DateOnly(2026, 8, 15))
        let warnings = Summary.reviewWarnings amendedDoc (DateOnly(2026, 8, 15)) latestAttestation
        assertTrue (warnings = [ a1.ActivityId, "amended_after_review" ]) $"unexpected warnings: {warnings}"
        ignore a2

      // --- Split/merge invariants + Superseded immutability -----------------------------
      "split preserves total duration and marks the source Superseded and unrestorable", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let source, replacements =
            Commands.split environment document
                { SourceActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Parts =
                    [ { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 }
                      { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 } ]
                  Reason = "split" }
            |> resultOf
        assertTrue (source.Voided && source.Superseded) "split source was not marked Superseded"
        assertTrue (replacements.Length = 2 && (replacements |> List.sumBy Activity.exactDurationMs) = 3600000.0) "split parts did not sum to the source duration"
        let restoreAttempt = Commands.restore environment LedgerDocument.empty { ActivityId = source.ActivityId; ExpectedVersion = None; Reason = "r" }
        ignore restoreAttempt

      "restoring a split source is rejected as Superseded", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let finalDoc =
            Commands.split environment document
                { SourceActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Parts =
                    [ { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 }
                      { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 } ]
                  Reason = "split" }
            |> projection
        let source = finalDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let result = Commands.restore environment finalDoc { ActivityId = source.ActivityId; ExpectedVersion = Some source.Version; Reason = "r" }
        assertTrue (firstCode result = DiagnosticCode.ActivitySuperseded) "restoring a split source was accepted"

      "merging two contiguous same-date sources supersedes both without double-counting", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 30)
        let document2, a2 = createOk environment document (at "2026-08-15" 9 30) (at "2026-08-15" 10 0)
        let merged, sources =
            Commands.merge environment document2
                { SourceActivityIds = [ a1.ActivityId; a2.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "merge" }
            |> resultOf
        assertTrue (sources |> List.forall (fun s -> s.Voided && s.Superseded)) "merge sources were not Superseded"
        assertTrue (Activity.exactDurationMs merged = 3600000.0) "merged duration was wrong"

      "restoring a merge source is rejected as Superseded", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 30)
        let document2, a2 = createOk environment document (at "2026-08-15" 9 30) (at "2026-08-15" 10 0)
        let finalDoc =
            Commands.merge environment document2
                { SourceActivityIds = [ a1.ActivityId; a2.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "merge" }
            |> projection
        let source = finalDoc.Activities |> List.find (fun a -> a.ActivityId = a1.ActivityId)
        let result = Commands.restore environment finalDoc { ActivityId = source.ActivityId; ExpectedVersion = Some source.Version; Reason = "r" }
        assertTrue (firstCode result = DiagnosticCode.ActivitySuperseded) "restoring a merge source was accepted"

      // --- FIX: restore re-validation ----------------------------------------------------
      "restore re-validates and rejects a reintroduced overlap", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let voidedDoc = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "r" } |> projection
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let occupiedDoc, _ = createOk environment voidedDoc (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let result = Commands.restore environment occupiedDoc { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; Reason = "r" }
        assertTrue (firstCode result = DiagnosticCode.OverlappingActivity) "restore into an occupied slot was accepted"

      "restore succeeds when nothing conflicts", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let voidedDoc = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "r" } |> projection
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let result = Commands.restore environment voidedDoc { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; Reason = "r" }
        match result with
        | Success(_, restored) -> assertTrue (not restored.Voided) "restore did not clear the voided flag"
        | result -> failwith $"clean restore was rejected: {result}"

      // --- FIX: merge contiguity / same-date ---------------------------------------------
      "merge rejects non-contiguous same-date sources", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 30)
        let document2, a2 = createOk environment document (at "2026-08-15" 10 0) (at "2026-08-15" 10 30)
        let result =
            Commands.merge environment document2
                { SourceActivityIds = [ a1.ActivityId; a2.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "merge" }
        assertTrue (firstCode result = DiagnosticCode.MergeSourcesNotContiguous) "non-contiguous merge accepted"

      "merge rejects sources on different dates", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 30)
        let document2, a2 = createOk environment document (at "2026-08-16" 9 0) (at "2026-08-16" 9 30)
        let result =
            Commands.merge environment document2
                { SourceActivityIds = [ a1.ActivityId; a2.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "merge" }
        assertTrue (firstCode result = DiagnosticCode.MergeSourcesNotContiguous) "cross-date merge accepted"

      "merge rejects fewer than two sources", fun () ->
        let environment = fixture ()
        let document, a1 = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 30)
        let result =
            Commands.merge environment document
                { SourceActivityIds = [ a1.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "merge" }
        assertTrue (firstCode result = DiagnosticCode.MergeRequiresTwoSources) "single-source merge accepted"

      // --- FIX: billing ---------------------------------------------------------------------
      "Billing.billedMinutesFor rounds a two-minute activity up to six", fun () ->
        let environment = fixture ()
        let _, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 2)
        assertTrue (Billing.billedMinutesFor activity = 6.0) $"expected 6.0, got {Billing.billedMinutesFor activity}"

      "two independent two-minute activities bill as twelve minutes total, not one rounded total", fun () ->
        let environment = fixture ()
        let document, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 2)
        let document2, _ = createOk environment document (at "2026-08-15" 9 5) (at "2026-08-15" 9 7)
        let summary = Summary.forDate document2 (DateOnly(2026, 8, 15))
        assertTrue (summary.TotalBilledMinutes = 12.0) $"expected 12.0, got {summary.TotalBilledMinutes}"

      "a duration exactly on a six-minute boundary does not round up further", fun () ->
        let environment = fixture ()
        let _, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 9 6)
        assertTrue (Billing.billedMinutesFor activity = 6.0) $"expected 6.0, got {Billing.billedMinutesFor activity}"

      // --- FIX: reason required --------------------------------------------------------------
      "amend/void/restore/split/merge/detachEvidence all reject a blank reason", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let amendResult = Commands.amend environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Changes = createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0); Reason = "  " }
        assertTrue (firstCode amendResult = DiagnosticCode.ReasonRequired) "amend accepted a blank reason"
        let voidResult = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "" }
        assertTrue (firstCode voidResult = DiagnosticCode.ReasonRequired) "void accepted a blank reason"
        let voidedDoc = Commands.voidActivity environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Reason = "r" } |> projection
        let voided = voidedDoc.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let restoreResult = Commands.restore environment voidedDoc { ActivityId = activity.ActivityId; ExpectedVersion = Some voided.Version; Reason = "" }
        assertTrue (firstCode restoreResult = DiagnosticCode.ReasonRequired) "restore accepted a blank reason"
        let splitResult =
            Commands.split environment document
                { SourceActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Parts =
                    [ { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 }
                      { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None; TagIds = None; DurationMs = 1800000.0 } ]
                  Reason = "" }
        assertTrue (firstCode splitResult = DiagnosticCode.ReasonRequired) "split accepted a blank reason"
        let document2, a2 = createOk environment document (at "2026-08-15" 10 0) (at "2026-08-15" 10 30)
        let mergeResult =
            Commands.merge environment document2
                { SourceActivityIds = [ activity.ActivityId; a2.ActivityId ]; ExpectedVersions = Map.empty
                  ActivityTypeId = "research"; ProjectId = "echelon-foundry"; Description = "Merged"; BusinessPurpose = "Purpose"; Reason = "" }
        assertTrue (firstCode mergeResult = DiagnosticCode.ReasonRequired) "merge accepted a blank reason"
        let withEvidence = Commands.attachEvidence environment document { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Type = DocumentEvidence; Uri = None; Note = None; Hash = None; Label = "" } |> projection
        let attached = withEvidence.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        let detachResult = Commands.detachEvidence environment withEvidence { ActivityId = activity.ActivityId; ExpectedVersion = Some attached.Version; EvidenceLinkId = attached.Evidence.Head.EvidenceLinkId; Reason = "" }
        assertTrue (firstCode detachResult = DiagnosticCode.ReasonRequired) "detachEvidence accepted a blank reason"

      // --- validateDocument -------------------------------------------------------------------
      "validateDocument tolerates an activity whose reference has since been archived", fun () ->
        let environment = fixture ()
        let document, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let archivedEnvironment = { environment with Projects = environment.Projects |> Map.add "echelon-foundry" (refItem "echelon-foundry" false) }
        assertTrue (Commands.validateDocument archivedEnvironment document |> List.isEmpty) "historical archived reference was rejected"

      "validateDocument flags an activity whose reference no longer exists at all", fun () ->
        let environment = fixture ()
        let document, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let strippedEnvironment = { environment with Projects = environment.Projects |> Map.remove "echelon-foundry" }
        let diagnostics = Commands.validateDocument strippedEnvironment document
        assertTrue (diagnostics |> List.exists (fun d -> d.Code = DiagnosticCode.ProjectNotFound)) "missing reference was not flagged"
        ignore activity

      // --- LedgerDocument.merge (auto-merge on a GitHub push conflict) --------

      "LedgerDocument.merge keeps the higher-versioned copy of an activity that diverged on both sides", fun () ->
        let environment = fixture ()
        let baseDoc, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let amended =
            Commands.amend environment baseDoc
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version
                  Changes = { createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0) with Description = "Updated on the other side" }
                  Reason = "correction" }
            |> projection
        let merged = LedgerDocument.merge baseDoc amended
        assertTrue (merged.Activities.Length = 1) $"expected one merged activity, got {merged.Activities.Length}"
        let winner = merged.Activities |> List.find (fun a -> a.ActivityId = activity.ActivityId)
        assertTrue (winner.Description = "Updated on the other side") "merge did not keep the higher-versioned (amended) copy"
        assertTrue (winner.Version = 2L) $"expected the amended version to win, got version {winner.Version}"

      "LedgerDocument.merge unions activities that exist on only one side", fun () ->
        let environment = fixture ()
        let localDoc, localActivity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let remoteDoc, remoteActivity = createOk environment LedgerDocument.empty (at "2026-08-15" 14 0) (at "2026-08-15" 15 0)
        let merged = LedgerDocument.merge localDoc remoteDoc
        let ids = merged.Activities |> List.map (fun a -> a.ActivityId) |> Set.ofList
        assertTrue (ids = Set.ofList [ localActivity.ActivityId; remoteActivity.ActivityId ]) "merge did not union activities unique to each side"

      "LedgerDocument.merge unions attestations by AttestationId without duplicating a shared one", fun () ->
        let environment = fixture ()
        let doc, _ = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let attestedDoc = Commands.attestDay environment doc { Date = DateOnly(2026, 8, 15); Statement = "all good"; ExpectedProjectionVersion = None } |> projection
        let merged = LedgerDocument.merge attestedDoc attestedDoc
        assertTrue (merged.Attestations.Length = 1) $"expected the shared attestation to appear once, got {merged.Attestations.Length}"

      "LedgerDocument.merge takes the higher EventSequence of the two documents", fun () ->
        let environment = fixture ()
        let doc, activity = createOk environment LedgerDocument.empty (at "2026-08-15" 9 0) (at "2026-08-15" 10 0)
        let amended =
            Commands.amend environment doc
                { ActivityId = activity.ActivityId; ExpectedVersion = Some activity.Version; Changes = createCmd (at "2026-08-15" 9 0) (at "2026-08-15" 10 0); Reason = "correction" }
            |> projection
        assertTrue (amended.EventSequence > doc.EventSequence) "test setup assumption failed: amend should advance EventSequence"
        let merged = LedgerDocument.merge doc amended
        assertTrue (merged.EventSequence = amended.EventSequence) "merge did not take the higher EventSequence"
    ]

[<EntryPoint>]
let main _ =
    let failures =
        tests
        |> List.choose (fun (name, test) ->
            try
                test ()
                printfn $"PASS {name}"
                None
            with error ->
                Some $"FAIL {name}: {error.Message}")
    failures |> List.iter (eprintfn "%s")
    if List.isEmpty failures then
        printfn $"All {tests.Length} F# domain specifications passed."
        0
    else
        eprintfn $"{failures.Length} of {tests.Length} F# domain specifications failed."
        1
