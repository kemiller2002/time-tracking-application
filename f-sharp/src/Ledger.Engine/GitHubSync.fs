namespace Ledger.Engine

open System
open System.Text
open System.Text.Json.Nodes
open Ledger.Domain
open Ledger.Engine.Protocol
open EchelonFoundry.Chrona.Integration

/// SDE Tier 3: translates the GitHub REST API — a third party's Public
/// Integration Contract — into this app's own closed `HttpEffect`/`HttpResult`
/// vocabulary. Unlike Protocol.fs's internal WASM<->browser Host Contract
/// (hand-rolled tagged JSON, because this app owns both ends of it), GitHub's
/// JSON shape belongs to GitHub; it is parsed here with `System.Text.Json.Nodes`
/// rather than reinvented. Still performs no I/O itself — it only builds
/// effect requests and parses effect results. The actual `fetch()` call
/// happens in Tier 4 (`web/dom-bindings.js`), exactly as it does for `Storage`.
module GitHubSync =

    let private apiBase = "https://api.github.com"

    /// Used whenever a saved config's `Folder` is blank — the target
    /// repository is never assumed to be dedicated to this app, so a
    /// concrete, namespaced default is used rather than falling back to
    /// the repo root.
    let defaultFolder = "time-tracking-data"

    /// The single fixed filenames inside a person's folder. Not
    /// user-configurable: letting the user name the files too would reopen
    /// the door to pointing this app at an existing, unrelated file.
    let private dataFileName = "ledger.json"
    let private metadataFileName = "metadata.json"
    let private settingsFileName = "settings.json"
    let private referenceFileName = "reference.json"

    let private folderPath (config: Session.GitHubSyncConfig) =
        (if String.IsNullOrWhiteSpace config.Folder then defaultFolder else config.Folder).Trim('/')

    /// `<Folder>/<Login>` — the repository is never assumed to belong to
    /// this app alone (hence `Folder`), and that folder is never assumed to
    /// belong to one person alone either (hence `Login`): several people
    /// can point the same repo/folder at this app and each still gets a
    /// folder only they write to.
    let private personFolder (config: Session.GitHubSyncConfig) (login: string) = $"{folderPath config}/{login}"

    let dataFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{dataFileName}"
    let metadataFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{metadataFileName}"
    let settingsFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{settingsFileName}"

    /// Lives at the folder root, not under any one person's subfolder — the
    /// project/activity-type/tag catalog is shared by everyone pointing
    /// their config at this repo/folder, not owned by whoever happens to
    /// read or write it.
    let referenceFilePath (config: Session.GitHubSyncConfig) = $"{folderPath config}/{referenceFileName}"

    /// GitHub's Contents API path segments are percent-encoded individually —
    /// `Uri.EscapeDataString` on the whole path would also encode the `/`
    /// separators the folder/login/file split needs to keep.
    let private encodedPath (path: string) = path.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"

    let private headers (token: string) =
        [ "Authorization", $"Bearer {token}"
          "Accept", "application/vnd.github+json"
          "X-GitHub-Api-Version", "2022-11-28"
          "Content-Type", "application/json; charset=utf-8" ]

    let private contentsUrl (config: Session.GitHubSyncConfig) (path: string) =
        $"{apiBase}/repos/{Uri.EscapeDataString config.Owner}/{Uri.EscapeDataString config.Repo}/contents/{encodedPath path}"

    /// Resolves who the saved token belongs to. Never user-entered — see
    /// `Session.GitHubSyncConfig.Login`'s doc comment for why.
    let buildWhoAmIEffect (config: Session.GitHubSyncConfig) : EffectRequest =
        HttpEffect("github-whoami", "GET", $"{apiBase}/user", headers config.Token, None, 15000)

    let private ledgerGetEffect (correlationId: string) (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        let url = $"{contentsUrl config (dataFilePath config login)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect(correlationId, "GET", url, headers config.Token, None, 15000)

    let buildGetEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest = ledgerGetEffect "github-pull" config login

    /// Fetches the ledger that just won a push conflict (GitHub's own 409 —
    /// this sync's `sha` was stale) so `Dispatch.fs` can merge it with the
    /// local document (`LedgerDocument.merge`) and retry the push, instead of
    /// surfacing the conflict and making the user pull-then-redo their edit.
    /// A distinct correlation id from `"github-pull"` because the two results
    /// are handled differently: an explicit pull replaces the document
    /// outright, this one merges into it.
    let buildConflictPullEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        ledgerGetEffect "github-push-conflict-pull" config login

    /// Fetches the ledger after a push whose outcome came back `Unknown`
    /// (the request timed out or the connection dropped after GitHub may
    /// already have received it) so `Dispatch.fs` can classify what actually
    /// happened — reusing `Ledger.Domain.Services`'s dormant
    /// `ReconciliationStatus` vocabulary (Applied/NotApplied/
    /// ReconciliationConflict/StillUnknown) — instead of leaving the user to
    /// manually pull and check. A distinct correlation id from both
    /// `"github-pull"` and `"github-push-conflict-pull"` because the
    /// classification differs from either: it compares the fetched document
    /// against what was attempted, not just against the local document.
    let buildReconciliationPullEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        ledgerGetEffect "github-push-reconcile-pull" config login

    let private putBody (sha: string option) (branch: string) (message: string) (content: string) =
        let body = JsonObject()
        body.["message"] <- JsonValue.Create(message)
        body.["content"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes content))
        body.["branch"] <- JsonValue.Create(branch)
        match sha with
        | Some value -> body.["sha"] <- JsonValue.Create(value)
        | None -> ()
        body.ToJsonString()

    // --- Atomic multi-file commit (ledger.json + metadata.json together) via
    // GitHub's Git Data API — a 5-step chain replacing what used to be two
    // independent Contents API PUTs (see the removed `buildPutEffect`/
    // `buildMetadataPutEffect`, and `docs/DOMAIN-REQUIREMENTS.md`'s
    // previously-documented scope): read the branch's current commit (1),
    // read that commit's tree (2), build a new tree with just those two
    // blobs replaced (3), create a new commit on it (4), then move the
    // branch ref to that commit (5) — a non-fast-forward move (someone/
    // something else advanced the branch first) fails with 422, GitHub's
    // equivalent of the Contents API's 409, so `Dispatch.fs` can drive it
    // through the same auto-merge/reconciliation paths built for that.
    // `settings.json` is deliberately not part of this chain: it is never
    // written at the same moment as the ledger, so a single Contents API
    // PUT (`buildSettingsPutEffect`, below) is already atomic for it. -------

    let private gitDataUrl (config: Session.GitHubSyncConfig) (segment: string) =
        $"{apiBase}/repos/{Uri.EscapeDataString config.Owner}/{Uri.EscapeDataString config.Repo}/git/{segment}"

    let private refUrl (config: Session.GitHubSyncConfig) = gitDataUrl config $"refs/heads/{Uri.EscapeDataString config.Branch}"

    /// Step 1: the commit the branch currently points at — becomes both the
    /// new commit's parent (step 4) and the source of the tree it's built on
    /// top of (step 2).
    let buildRefGetEffect (config: Session.GitHubSyncConfig) : EffectRequest =
        HttpEffect("github-commit-ref", "GET", refUrl config, headers config.Token, None, 15000)

    /// Step 2: that commit's tree — its `sha` is the `base_tree` the new
    /// tree (step 3) is built on, so every file this push doesn't touch
    /// stays exactly as it was.
    let buildCommitGetEffect (config: Session.GitHubSyncConfig) (commitSha: string) : EffectRequest =
        HttpEffect("github-commit-base", "GET", gitDataUrl config $"commits/{commitSha}", headers config.Token, None, 15000)

    let private treeEntry (path: string) (content: string) : JsonNode =
        let o = JsonObject()
        o.["path"] <- JsonValue.Create(path: string)
        o.["mode"] <- JsonValue.Create("100644")
        o.["type"] <- JsonValue.Create("blob")
        o.["content"] <- JsonValue.Create(content: string)
        o

    /// Step 3: a new tree replacing only `ledger.json` and `metadata.json` —
    /// GitHub accepts a tree entry's content inline, so no separate
    /// blob-creation call is needed for either file.
    let buildTreeCreateEffect (config: Session.GitHubSyncConfig) (login: string) (baseTreeSha: string) (ledgerJson: string) (metadataJson: string) : EffectRequest =
        let tree = JsonArray()
        tree.Add(treeEntry (dataFilePath config login) ledgerJson)
        tree.Add(treeEntry (metadataFilePath config login) metadataJson)
        let body = JsonObject()
        body.["base_tree"] <- JsonValue.Create(baseTreeSha)
        body.["tree"] <- tree
        HttpEffect("github-commit-tree", "POST", gitDataUrl config "trees", headers config.Token, Some(body.ToJsonString()), 15000)

    /// Step 4: the new commit object itself — not yet reachable from any
    /// branch until step 5 moves the ref onto it.
    let buildCommitCreateEffect (config: Session.GitHubSyncConfig) (treeSha: string) (parentSha: string) : EffectRequest =
        let parents = JsonArray()
        parents.Add(JsonValue.Create(parentSha: string))
        let body = JsonObject()
        body.["message"] <- JsonValue.Create("Update the business activity ledger and profile metadata")
        body.["tree"] <- JsonValue.Create(treeSha)
        body.["parents"] <- parents
        HttpEffect("github-commit-create", "POST", gitDataUrl config "commits", headers config.Token, Some(body.ToJsonString()), 15000)

    /// Step 5, the chain's terminal step — correlation id deliberately kept
    /// as `"github-push"` so `Dispatch.fs`'s existing success/conflict/
    /// failure/`Unknown` handling for that id (built for the old single-file
    /// PUT, then reused for merge-on-conflict and reconciliation) keeps
    /// working unchanged: `force: false` means a non-fast-forward move —
    /// someone/something else advanced the branch since step 1 read it —
    /// fails with 422 rather than overwriting it, GitHub's equivalent of the
    /// Contents API's 409.
    let buildRefUpdateEffect (config: Session.GitHubSyncConfig) (newCommitSha: string) : EffectRequest =
        let body = JsonObject()
        body.["sha"] <- JsonValue.Create(newCommitSha)
        body.["force"] <- JsonValue.Create(false)
        HttpEffect("github-push", "PATCH", refUrl config, headers config.Token, Some(body.ToJsonString()), 15000)

    /// The commit sha a `git/refs/heads/{branch}` GET response points at
    /// (step 1's response) — distinct from `parseGetResponse`'s Contents API
    /// blob `sha`, a different GitHub API family with a different shape.
    let parseRefResponse (body: string) : Result<string, string> =
        try
            Ok(JsonNode.Parse(body).AsObject().["object"].AsObject().["sha"].GetValue<string>())
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// The base tree sha out of a `git/commits/{sha}` GET response (step 2's
    /// response).
    let parseCommitBaseTreeResponse (body: string) : Result<string, string> =
        try
            Ok(JsonNode.Parse(body).AsObject().["tree"].AsObject().["sha"].GetValue<string>())
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// A bare top-level `sha` field — both `git/trees` (step 3) and
    /// `git/commits` (step 4) POST responses carry the new object's id there.
    let parseShaResponse (body: string) : Result<string, string> =
        try
            Ok(JsonNode.Parse(body).AsObject().["sha"].GetValue<string>())
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// Round-tripped, unlike `metadata.json`: pulled on identity resolution
    /// (fresh save or a cached config reload) and applied to the session, so
    /// a preference set on one device follows the person to another.
    let buildSettingsGetEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        let url = $"{contentsUrl config (settingsFilePath config login)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-settings-pull", "GET", url, headers config.Token, None, 15000)

    let buildSettingsPutEffect (config: Session.GitHubSyncConfig) (login: string) (sha: string option) (settingsJson: string) : EffectRequest =
        let body = putBody sha config.Branch "Update settings" settingsJson
        HttpEffect("github-settings-push", "PUT", contentsUrl config (settingsFilePath config login), headers config.Token, Some body, 15000)

    /// A missing `reference.json` (a fresh repo/folder no one has written
    /// one into yet) is expected, not an error: `Dispatch.fs` treats a 404
    /// here exactly like a missing `settings.json`, keeping the fixture
    /// defaults (`Session.fs`'s `fixtureEnvironment`) rather than surfacing
    /// a failure.
    let buildReferenceGetEffect (config: Session.GitHubSyncConfig) : EffectRequest =
        let url = $"{contentsUrl config (referenceFilePath config)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-reference-pull", "GET", url, headers config.Token, None, 15000)

    let private referenceItemJson (item: ReferenceItem) : JsonNode =
        let o = JsonObject()
        o.["id"] <- JsonValue.Create(item.Id)
        o.["name"] <- JsonValue.Create(item.Name)
        o.["active"] <- JsonValue.Create(item.Active)
        o

    /// The full catalog — active and inactive items alike, so deactivating
    /// one item in the admin page (More screen) never drops another item's
    /// entry from the file. Sorted by name for a stable, human-readable
    /// diff when the file is browsed directly on GitHub.
    let buildReferenceJson (projects: ReferenceItem list) (activityTypes: ReferenceItem list) (tags: ReferenceItem list) : string =
        let arrayOf (items: ReferenceItem list) =
            let arr = JsonArray()
            items |> List.sortBy (fun i -> i.Name) |> List.iter (fun i -> arr.Add(referenceItemJson i))
            arr
        let o = JsonObject()
        o.["projects"] <- arrayOf projects
        o.["activityTypes"] <- arrayOf activityTypes
        o.["tags"] <- arrayOf tags
        o.ToJsonString()

    /// Whoever last saved an admin edit becomes the current shared catalog
    /// — unlike the per-person `ledger.json`/`settings.json`, there is no
    /// per-writer segregation here (`referenceFilePath` lives at the folder
    /// root), matching the catalog's own "shared by everyone pointing their
    /// config at this repo/folder" design.
    let buildReferencePutEffect (config: Session.GitHubSyncConfig) (sha: string option) (referenceJson: string) : EffectRequest =
        let body = putBody sha config.Branch "Update the shared project/activity-type/tag catalog" referenceJson
        HttpEffect("github-reference-push", "PUT", contentsUrl config (referenceFilePath config), headers config.Token, Some body, 15000)

    let private parseReferenceItems (node: JsonNode) : ReferenceItem list =
        match node with
        | null -> []
        | array ->
            array.AsArray()
            |> Seq.map (fun item ->
                let o = item.AsObject()
                let active = match o.["active"] with null -> true | v -> v.GetValue<bool>()
                { Id = o.["id"].GetValue<string>(); Name = o.["name"].GetValue<string>(); Active = active; Version = "v1" })
            |> List.ofSeq

    /// `{ "projects": [{id,name,active}], "activityTypes": [...], "tags": [...] }`
    /// — hand-authored or generated externally, so every list defaults to
    /// empty when its key is absent rather than failing the whole parse;
    /// `active` defaults to `true` when omitted, matching a person who just
    /// wants to list active items without typing the field every time.
    let parseReferenceJson (json: string) : Result<{| Projects: ReferenceItem list; ActivityTypes: ReferenceItem list; Tags: ReferenceItem list |}, string> =
        try
            let node = JsonNode.Parse(json).AsObject()
            Ok
                {| Projects = parseReferenceItems node.["projects"]
                   ActivityTypes = parseReferenceItems node.["activityTypes"]
                   Tags = parseReferenceItems node.["tags"] |}
        with ex ->
            Error $"reference.json could not be read: {ex.Message}"

    /// The GET response's `content` is base64 with embedded newlines every 60
    /// characters — `Convert.FromBase64String` tolerates embedded whitespace,
    /// so no pre-processing is needed before decoding.
    let parseGetResponse (body: string) : Result<{| Sha: string; DocumentJson: string |}, string> =
        try
            let node = JsonNode.Parse(body).AsObject()
            let sha = node.["sha"].GetValue<string>()
            let documentJson = Encoding.UTF8.GetString(Convert.FromBase64String(node.["content"].GetValue<string>()))
            Ok {| Sha = sha; DocumentJson = documentJson |}
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// Reads the resulting blob's sha out of a Contents API PUT response
    /// (`content.sha` — used by `settings.json`'s own PUT) or, when that
    /// shape isn't there, out of a `git/refs/{ref}` PATCH response instead
    /// (`object.sha` — the atomic multi-file commit's step 5, still reported
    /// under the `"github-push"` correlation id both shapes share).
    let parsePutResponse (body: string) : Result<string, string> =
        try
            let node = JsonNode.Parse(body).AsObject()
            match node.["content"], node.["object"] with
            | null, null -> Error "GitHub's response had neither a content nor an object sha."
            | null, objectNode -> Ok(objectNode.AsObject().["sha"].GetValue<string>())
            | contentNode, _ -> Ok(contentNode.AsObject().["sha"].GetValue<string>())
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// GitHub's `/user` response carries many fields; only identity is read.
    /// `name` (the display name) is optional at the account level, unlike
    /// `login`, and is `null` for many accounts.
    let parseWhoAmIResponse (body: string) : Result<{| Login: string; Name: string option |}, string> =
        try
            let node = JsonNode.Parse(body).AsObject()
            let login = node.["login"].GetValue<string>()
            let name = match node.["name"] with null -> None | n -> (try Some(n.GetValue<string>()) with _ -> None)
            Ok {| Login = login; Name = name |}
        with ex ->
            Error $"GitHub's response could not be read: {ex.Message}"

    /// The full content of `metadata.json` — this app's only write into
    /// that file; nothing in-app ever reads another person's metadata back,
    /// it exists to make a shared repository's `<folder>/<login>/` entries
    /// self-describing for a human (or other tooling) browsing it.
    let buildMetadataJson (login: string) (displayName: string option) (lastSyncedAt: DateTimeOffset) : string =
        let o = JsonObject()
        o.["login"] <- JsonValue.Create(login)
        o.["displayName"] <- (match displayName with Some n -> JsonValue.Create(n) :> JsonNode | None -> null)
        o.["lastSyncedAt"] <- JsonValue.Create(lastSyncedAt.ToString "O")
        o.ToJsonString()

    /// `settings.json`'s content — user preferences, not business data (see
    /// `Session.State.Timezone`'s doc comment). Unlike `metadata.json`, this
    /// file is read back by this app itself (`parseSettingsJson`), so both
    /// fields are optional on decode: a partially-written or older file
    /// should update only the preferences it actually has an opinion about.
    let buildSettingsJson (reportFormat: string) (timezone: string option) : string =
        let o = JsonObject()
        o.["reportFormat"] <- JsonValue.Create(reportFormat)
        o.["timezone"] <- (match timezone with Some tz -> JsonValue.Create(tz) :> JsonNode | None -> null)
        o.ToJsonString()

    let parseSettingsJson (json: string) : Result<{| ReportFormat: string option; Timezone: string option |}, string> =
        try
            let node = JsonNode.Parse(json).AsObject()
            let optionalString (key: string) = match node.[key] with null -> None | n -> (try Some(n.GetValue<string>()) with _ -> None)
            Ok {| ReportFormat = optionalString "reportFormat"; Timezone = optionalString "timezone" |}
        with ex ->
            Error $"Saved settings could not be read: {ex.Message}"

    /// GitHub's Contents API error responses carry a JSON `message` field;
    /// fall back to the raw body when it doesn't parse as that shape.
    let errorMessage (body: string option) : string =
        let raw = body |> Option.defaultValue "no response body"
        try
            JsonNode.Parse(raw).AsObject().["message"].GetValue<string>()
        with _ ->
            raw

    // --- Inbound integration observations (see docs/integration/) -----------
    //
    // Reading what a producer (ROS) has written to the shared inbox, and
    // persisting the resulting candidates/receipts — CHR-INT-011/012/013.
    // Exactly like every other function in this file: builds effect
    // requests and parses effect results, performs no I/O itself. The
    // orchestration that calls these during WASM startup reconciliation
    // is deferred (CHR-INT-015) — nothing here is wired into `Dispatch.fs`
    // yet, so none of it runs today.

    let private orInvalidPath (result: Result<string, StorageConvention.PathError>) =
        match result with
        | Ok path -> path
        | Error(StorageConvention.BlankSegment field) -> failwith $"internal error: blank {field} while building an integration storage path"

    /// The public, producer-facing convention (`Chrona.Integration.
    /// StorageConvention`), scoped to this config's `Folder` exactly like
    /// every other datastore path in this file.
    let observationInboxDirectory (config: Session.GitHubSyncConfig) (projectId: string) : string =
        StorageConvention.observationInboxDirectory (folderPath config) projectId |> orInvalidPath

    /// Chrona-internal — deliberately not part of the public
    /// `Chrona.Integration` package, since a producer never needs to know
    /// where Chrona keeps its own candidates (specification §51). Still
    /// reuses `StorageConvention.safeSegment` rather than reimplementing
    /// path-safety, since `CandidateId` (`"candidate:<observationId>"`,
    /// see `Integration.TimeCandidate.candidateId`) contains a `:`.
    let candidatePath (config: Session.GitHubSyncConfig) (candidateId: string) : string =
        $"{folderPath config}/integration/candidates/{StorageConvention.safeSegment candidateId}.json"

    /// Chrona-internal, for the same reason as `candidatePath`.
    let observationReceiptPath (config: Session.GitHubSyncConfig) (observationId: string) : string =
        $"{folderPath config}/integration/observation-receipts/{StorageConvention.safeSegment observationId}.json"

    /// Lists the observations a producer has written for one project —
    /// the GitHub Contents API returns a JSON *array* (rather than the
    /// single object every other GET in this file parses) when `path`
    /// names a directory rather than a file.
    let buildObservationsListEffect (config: Session.GitHubSyncConfig) (projectId: string) : EffectRequest =
        let url = $"{contentsUrl config (observationInboxDirectory config projectId)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-observations-list", "GET", url, headers config.Token, None, 15000)

    /// One entry from a directory listing — only `name`/`path` are ever
    /// read; a listing never carries file content (specification §35's
    /// "list observation files" is a separate operation from "read
    /// observation" — `buildObservationGetEffect`, below, is still needed
    /// per file). Non-`.json` entries and subdirectories are filtered out
    /// here rather than left for the caller to notice.
    let parseObservationListing (body: string) : Result<{| Name: string; Path: string |} list, string> =
        try
            JsonNode.Parse(body).AsArray()
            |> Seq.choose (fun item ->
                let o = item.AsObject()
                let entryType = match o.["type"] with null -> "" | v -> v.GetValue<string>()
                let name = match o.["name"] with null -> "" | v -> v.GetValue<string>()
                if entryType = "file" && name.EndsWith ".json" then Some {| Name = name; Path = o.["path"].GetValue<string>() |} else None)
            |> List.ofSeq
            |> Ok
        with ex ->
            Error $"GitHub's directory listing could not be read: {ex.Message}"

    /// Reads one already-located observation file — reuses
    /// `parseGetResponse` (the same Contents-API single-file GET shape
    /// every other read in this file already parses).
    let buildObservationGetEffect (config: Session.GitHubSyncConfig) (path: string) : EffectRequest =
        let url = $"{contentsUrl config path}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-observation-pull", "GET", url, headers config.Token, None, 15000)

    /// Checks whether a candidate already exists for a given (deterministic)
    /// id — a 404 response means it does not (specification §23's "does a
    /// candidate already exist for this ObservationId?").
    let buildCandidateGetEffect (config: Session.GitHubSyncConfig) (candidateId: string) : EffectRequest =
        let url = $"{contentsUrl config (candidatePath config candidateId)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-candidate-pull", "GET", url, headers config.Token, None, 15000)

    /// Create-only: always `sha = None`. A candidate/receipt file is
    /// written exactly once and never updated (specification §26: "create-
    /// only files such as deterministic receipts should fail safely if
    /// another client already created them") — GitHub's Contents API
    /// rejects a create-with-no-`sha` PUT against a path that already
    /// exists (409/422), which is exactly the fail-safe two clients racing
    /// to process the same observation need (specification §43/44) rather
    /// than one silently overwriting the other's candidate.
    let buildCandidatePutEffect (config: Session.GitHubSyncConfig) (candidateId: string) (candidateJson: string) : EffectRequest =
        let body = putBody None config.Branch "Create a time candidate from an inbound observation" candidateJson
        HttpEffect("github-candidate-push", "PUT", contentsUrl config (candidatePath config candidateId), headers config.Token, Some body, 15000)

    /// Checks whether a receipt already exists for an observation id —
    /// the other half of §23's idempotency check, alongside
    /// `buildCandidateGetEffect`.
    let buildReceiptGetEffect (config: Session.GitHubSyncConfig) (observationId: string) : EffectRequest =
        let url = $"{contentsUrl config (observationReceiptPath config observationId)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-receipt-pull", "GET", url, headers config.Token, None, 15000)

    /// Create-only, for the same reason as `buildCandidatePutEffect` —
    /// per specification §22, this must never be sent before the
    /// corresponding candidate write has already succeeded.
    let buildReceiptPutEffect (config: Session.GitHubSyncConfig) (observationId: string) (receiptJson: string) : EffectRequest =
        let body = putBody None config.Branch "Record an observation-processing receipt" receiptJson
        HttpEffect("github-receipt-push", "PUT", contentsUrl config (observationReceiptPath config observationId), headers config.Token, Some body, 15000)

    // --- localStorage persistence of the saved config (Owner/Repo/Folder/
    // Branch/Token/Login/DisplayName) — a separate cache key from the ledger
    // document itself, so the token never enters GitHub-tracked content and
    // a reload doesn't force the user to re-enter their settings or re-run
    // the identity lookup. Hand-rolled JSON, matching Protocol.fs's own
    // Host Contract convention, since this is this app's own shape, not a
    // third party's. ---------------------------------------------------------

    let encodeConfig (config: Session.GitHubSyncConfig) : string =
        let o = JsonObject()
        o.["owner"] <- JsonValue.Create(config.Owner)
        o.["repo"] <- JsonValue.Create(config.Repo)
        o.["folder"] <- JsonValue.Create(config.Folder)
        o.["branch"] <- JsonValue.Create(config.Branch)
        o.["token"] <- JsonValue.Create(config.Token)
        o.["login"] <- (match config.Login with Some l -> JsonValue.Create(l) :> JsonNode | None -> null)
        o.["displayName"] <- (match config.DisplayName with Some n -> JsonValue.Create(n) :> JsonNode | None -> null)
        o.ToJsonString()

    let decodeConfig (json: string) : Result<Session.GitHubSyncConfig, string> =
        try
            let node = JsonNode.Parse(json).AsObject()
            let optionalString (key: string) = match node.[key] with null -> None | n -> (try Some(n.GetValue<string>()) with _ -> None)
            Ok
                { Owner = node.["owner"].GetValue<string>()
                  Repo = node.["repo"].GetValue<string>()
                  Folder = node.["folder"].GetValue<string>()
                  Branch = node.["branch"].GetValue<string>()
                  Token = node.["token"].GetValue<string>()
                  Login = optionalString "login"
                  DisplayName = optionalString "displayName" }
        with ex ->
            Error $"saved GitHub sync settings could not be read: {ex.Message}"
