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
    ///
    /// `Folder`, not a free-form file path: the target repository is not
    /// assumed to belong to this app alone, so the ledger's data is never
    /// placed at the repo root or at a path the user could point at an
    /// unrelated existing file. It's also not assumed to belong to this
    /// *person* alone — the same repository/folder can be shared by
    /// several people, each getting their own `<Folder>/<Login>/` — so the
    /// data always lives at `<Folder>/<Login>/ledger.json` (see
    /// `GitHubSync.dataFilePath`), never at `<Folder>/ledger.json` directly.
    type GitHubSyncConfig =
        { Owner: string
          Repo: string
          Folder: string
          Branch: string
          Token: string
          /// Resolved once via GitHub's `/user` endpoint right after the
          /// token is saved — never typed by the user, so it can't collide
          /// or be mistyped the way a free-text name could. `None` until
          /// that resolves; Pull/Push are blocked until then, since the
          /// per-person folder path is built from it.
          Login: string option
          DisplayName: string option }

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
          GitHubFolder: string option
          GitHubBranch: string option
          GitHubToken: string option
          Timezone: string option }

    module Draft =
        let empty =
            { ActivityTypeId = None; ProjectId = None; Description = None; BusinessPurpose = None; Outcome = None
              StartedAt = None; EndedAt = None; ReconstructionReason = None; TagIds = Set.empty; Reason = None
              EvidenceType = None; EvidenceUri = None; EvidenceNote = None; EvidenceLabel = None
              SplitParts = Map.empty; MergeSourceIds = Set.empty; AttestationStatement = None
              GitHubOwner = None; GitHubRepo = None; GitHubFolder = None; GitHubBranch = None; GitHubToken = None
              Timezone = None }

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
          /// Keyed "create"/"amend"/"void"/"restore"/"split"/"merge"/"evidence"/"timer"/"attest"/"githubConfig"/"githubSync"/"githubSettings".
          Errors: Map<string, string>
          PersistenceError: string option
          /// A user preference — not business data, so it lives here rather
          /// than in `LedgerDocument`. `None` until `SaveSettings` sets it or
          /// a GitHub settings pull restores one. Mirrors `Model.Config`'s
          /// dormant `Timezone` field, which nothing else in the domain
          /// reads yet; storing and round-tripping the preference is in
          /// scope now, deeper timezone-aware behavior is not.
          Timezone: string option
          /// None until `SaveGitHubConfig` succeeds, or a cached config is
          /// found on `Initialize` (see `GitHubSync.encodeConfig`/`decodeConfig`
          /// and the "github-config-load"/"github-config-save" Storage keys
          /// in `Dispatch.fs`) — a separate localStorage key from the synced
          /// document, so the token never enters GitHub-tracked content.
          GitHubSync: GitHubSyncConfig option
          /// The ledger file's Contents API blob `sha` from the last
          /// successful pull or push — GitHub's own optimistic-concurrency
          /// token, reused as the next write's `sha` exactly the way
          /// `docs/DOMAIN-REQUIREMENTS.md`'s persistence contract asks a
          /// caller to state the version it last read. `None` means "never
          /// synced" (the next push creates the file).
          GitHubDocumentSha: string option
          /// The same, for `settings.json` (`ReportFormat`/`Timezone`) — the
          /// one file still written through a plain Contents API PUT, since
          /// it is never committed together with anything else.
          GitHubSettingsSha: string option
          /// The commit `ledger.json`/`metadata.json` are currently being
          /// committed on top of — the parent of the new commit the atomic
          /// multi-file push (`GitHubSync.buildRefGetEffect` through
          /// `buildRefUpdateEffect`) is building. Set when that chain's first
          /// step (reading the branch's ref) succeeds, and read again by its
          /// last step (creating the new commit) — two separate `handle`
          /// calls, so it has to live here rather than as a local value thread-
          /// ed through one function call. Stale between pushes is harmless:
          /// every push chain starts by overwriting it before ever reading it.
          GitHubCommitParentSha: string option
          /// "idle" | "identifying" | "pulling" | "pushing" | "merging" |
          /// "reconciling" | "synced" | "conflict" | "unknown" | "error"
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
          Timezone = None
          GitHubSync = None
          GitHubDocumentSha = None
          GitHubSettingsSha = None
          GitHubCommitParentSha = None
          GitHubSyncStatus = "idle"
          GitHubLastSyncedAt = None }

    let mutable current: State = initial ()
