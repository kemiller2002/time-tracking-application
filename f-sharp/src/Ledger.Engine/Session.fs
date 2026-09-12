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

    /// Saved GitHub sync settings. `Token` is a fine-grained personal access
    /// token the user pastes in directly — per the explicit decision to keep
    /// this a browser-embedded app with no server component, it is sent
    /// straight from the browser to the GitHub REST API and never echoed
    /// back into the view (see Projections.fs).
    type GitHubSyncConfig = { Owner: string; Repo: string; Path: string; Branch: string; Token: string }

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
          AttestationStatement: string option
          GitHubOwner: string option
          GitHubRepo: string option
          GitHubPath: string option
          GitHubBranch: string option
          GitHubToken: string option }

    module Draft =
        let empty =
            { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None
              StartedAt = None; EndedAt = None; ReconstructionReason = None; TagIds = Set.empty; Reason = None
              EvidenceType = None; EvidenceUri = None; EvidenceNote = None; EvidenceLabel = None
              SplitParts = Map.empty; MergeSourceIds = Set.empty; AttestationStatement = None
              GitHubOwner = None; GitHubRepo = None; GitHubPath = None; GitHubBranch = None; GitHubToken = None }

    type State =
        { Environment: Environment
          Document: LedgerDocument
          TimerState: TimerState
          Draft: Draft
          SelectedDate: DateOnly
          SelectedMonth: string
          ActiveActivityId: string option
          ReportFormat: string
          /// UI-only navigation state ("today" | "track" | "month" | "more") —
          /// which screen of the app shell is showing. Not a business
          /// decision; see `.sde/architecture/FOUR-TIER-ARCHITECTURE.md`'s
          /// note that presentation-only routing is still Tier 3's to own.
          CurrentScreen: string
          /// Keyed "create"/"amend"/"void"/"restore"/"split"/"merge"/"evidence"/"timer"/"attest"/"githubConfig"/"githubSync".
          Errors: Map<string, string>
          PersistenceError: string option
          /// None until `SaveGitHubConfig` succeeds. Persisted to its own
          /// localStorage key (never the synced document) so it survives a
          /// reload without the token ever entering GitHub-tracked content.
          GitHubSync: GitHubSyncConfig option
          /// The Contents API blob `sha` from the last successful pull or
          /// push — GitHub's own optimistic-concurrency token, reused as the
          /// next write's `sha` exactly the way `docs/DOMAIN-REQUIREMENTS.md`'s
          /// persistence contract asks a caller to state the version it last
          /// read. `None` means "never synced" (the next push creates the file).
          GitHubDocumentSha: string option
          /// "idle" | "pulling" | "pushing" | "synced" | "conflict" | "unknown" | "error"
          GitHubSyncStatus: string
          GitHubLastSyncedAt: DateTimeOffset option }

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
          CurrentScreen = "today"
          Errors = Map.empty
          PersistenceError = None
          GitHubSync = None
          GitHubDocumentSha = None
          GitHubSyncStatus = "idle"
          GitHubLastSyncedAt = None }

    let mutable current: State = initial ()
