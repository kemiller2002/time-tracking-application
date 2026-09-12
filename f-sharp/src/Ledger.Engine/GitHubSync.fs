namespace Ledger.Engine

open System
open System.Text
open System.Text.Json.Nodes
open Ledger.Engine.Protocol

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

    /// `<Folder>/<Login>` — the repository is never assumed to belong to
    /// this app alone (hence `Folder`), and that folder is never assumed to
    /// belong to one person alone either (hence `Login`): several people
    /// can point the same repo/folder at this app and each still gets a
    /// folder only they write to.
    let private personFolder (config: Session.GitHubSyncConfig) (login: string) =
        let folder = (if String.IsNullOrWhiteSpace config.Folder then defaultFolder else config.Folder).Trim('/')
        $"{folder}/{login}"

    let dataFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{dataFileName}"
    let metadataFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{metadataFileName}"
    let settingsFilePath (config: Session.GitHubSyncConfig) (login: string) = $"{personFolder config login}/{settingsFileName}"

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

    let buildGetEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        let url = $"{contentsUrl config (dataFilePath config login)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-pull", "GET", url, headers config.Token, None, 15000)

    let private putBody (sha: string option) (branch: string) (message: string) (content: string) =
        let body = JsonObject()
        body.["message"] <- JsonValue.Create(message)
        body.["content"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes content))
        body.["branch"] <- JsonValue.Create(branch)
        match sha with
        | Some value -> body.["sha"] <- JsonValue.Create(value)
        | None -> ()
        body.ToJsonString()

    /// `sha` is the blob version to overwrite — `None` asks GitHub to create
    /// the file, which fails if one is already there (surfaced like any other
    /// non-2xx status; see `Dispatch.fs`'s handling of the "github-push" result).
    let buildPutEffect (config: Session.GitHubSyncConfig) (login: string) (sha: string option) (documentJson: string) : EffectRequest =
        let body = putBody sha config.Branch "Update the business activity ledger" documentJson
        HttpEffect("github-push", "PUT", contentsUrl config (dataFilePath config login), headers config.Token, Some body, 15000)

    let buildMetadataPutEffect (config: Session.GitHubSyncConfig) (login: string) (sha: string option) (metadataJson: string) : EffectRequest =
        let body = putBody sha config.Branch "Update profile metadata" metadataJson
        HttpEffect("github-metadata-push", "PUT", contentsUrl config (metadataFilePath config login), headers config.Token, Some body, 15000)

    /// Round-tripped, unlike `metadata.json`: pulled on identity resolution
    /// (fresh save or a cached config reload) and applied to the session, so
    /// a preference set on one device follows the person to another.
    let buildSettingsGetEffect (config: Session.GitHubSyncConfig) (login: string) : EffectRequest =
        let url = $"{contentsUrl config (settingsFilePath config login)}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-settings-pull", "GET", url, headers config.Token, None, 15000)

    let buildSettingsPutEffect (config: Session.GitHubSyncConfig) (login: string) (sha: string option) (settingsJson: string) : EffectRequest =
        let body = putBody sha config.Branch "Update settings" settingsJson
        HttpEffect("github-settings-push", "PUT", contentsUrl config (settingsFilePath config login), headers config.Token, Some body, 15000)

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

    let parsePutResponse (body: string) : Result<string, string> =
        try
            Ok(JsonNode.Parse(body).AsObject().["content"].AsObject().["sha"].GetValue<string>())
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
