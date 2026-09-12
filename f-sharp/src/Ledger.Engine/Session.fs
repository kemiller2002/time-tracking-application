namespace Ledger.Engine

open System
open Ledger.Domain

/// Session-scoped application state. This is deliberately the *only* mutable
/// state anywhere in the WASM module (`Dispatch.handle` is the sole writer,
/// called once per browser round-trip).
module Session =

    type SplitPartDraft =
        { DurationText: string option
          ActivityTypeId: string option
          ProjectId: string option
          Description: string option
          BusinessPurpose: string option
          Outcome: string option }

    module SplitPartDraft =
        let empty =
            { DurationText = None; ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None }

    /// Accumulates one field at a time as the browser flushes each changed form
    /// control before the form's own submit event arrives.
    type Draft =
        { ActivityTypeId: string option
          ProjectId: string option
          Description: string option
          BusinessPurpose: string option
          Outcome: string option
          StartedAt: string option
          EndedAt: string option
          ReconstructionReason: string option
          TagIds: Set<string>
          Reason: string option
          EvidenceType: string option
          EvidenceUri: string option
          EvidenceNote: string option
          EvidenceLabel: string option
          SplitParts: Map<int, SplitPartDraft>
          MergeSourceIds: Set<string>
          AttestationStatement: string option }

    module Draft =
        let empty =
            { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None
              StartedAt = None; EndedAt = None; ReconstructionReason = None; TagIds = Set.empty; Reason = None
              EvidenceType = None; EvidenceUri = None; EvidenceNote = None; EvidenceLabel = None
              SplitParts = Map.empty; MergeSourceIds = Set.empty; AttestationStatement = None }

    type State =
        { Environment: Environment
          Document: LedgerDocument
          TimerState: TimerState
          Draft: Draft
          SelectedDate: DateOnly
          SelectedMonth: string
          ActiveActivityId: string option
          ReportFormat: string
          /// Keyed "create"/"amend"/"void"/"restore"/"split"/"merge"/"evidence"/"timer"/"attest".
          Errors: Map<string, string>
          PersistenceError: string option }

    /// Seed data mirrors `worker/src/handler.js`'s `defaultBindings` fixtures —
    /// real project/activity-type/tag loading is a fast-follow once a real
    /// persistence adapter exists (see `Ledger.Domain/Services.fs`).
    let private fixtureEnvironment () : Environment =
        let refItem id name active = { Id = id; Name = name; Active = active; Version = "v1" }
        let counter = ref 0
        let newId () =
            counter.Value <- counter.Value + 1
            Guid.NewGuid().ToString "N"
        { Projects =
            [ refItem "echelon-foundry" "Echelon Foundry" true
              refItem "visual-engineering" "Visual Engineering" true
              refItem "helixnote" "HelixNote" true
              refItem "general" "General" true
              refItem "archived-initiative" "Archived Initiative" false ]
            |> List.map (fun p -> p.Id, p)
            |> Map.ofList
          ActivityTypes =
            [ refItem "research" "Research" true
              refItem "linkedin-marketing" "LinkedIn marketing" true
              refItem "software-development" "Software development" true
              refItem "administration" "Administration" true
              refItem "meeting" "Meeting" true
              refItem "retired-type" "Retired type" false ]
            |> List.map (fun t -> t.Id, t)
            |> Map.ofList
          Tags =
            [ refItem "billable" "Billable" true
              refItem "client-facing" "Client-facing" true
              refItem "internal-only" "Internal only" true
              refItem "legacy" "Legacy" false ]
            |> List.map (fun t -> t.Id, t)
            |> Map.ofList
          NewId = newId
          Clock = fun () -> DateTimeOffset.UtcNow }

    let initial () : State =
        let today = DateOnly.FromDateTime(DateTime.UtcNow)
        { Environment = fixtureEnvironment ()
          Document = LedgerDocument.empty
          TimerState = NoTimer
          Draft = Draft.empty
          SelectedDate = today
          SelectedMonth = today.ToString "yyyy-MM"
          ActiveActivityId = None
          ReportFormat = "json"
          Errors = Map.empty
          PersistenceError = None }

    let mutable current: State = initial ()
