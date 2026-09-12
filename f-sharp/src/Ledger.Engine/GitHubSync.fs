namespace Ledger.Engine

open System
open System.Text
open System.Text.Json.Nodes
open Ledger.Engine.Protocol

/// SDE Tier 3: translates the GitHub Contents API — a third party's Public
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

    /// The single fixed filename inside that folder. Not user-configurable:
    /// letting the user name the file too would reopen the door to pointing
    /// this app at an existing, unrelated file in the target repo.
    let private fileName = "ledger.json"

    /// Always a folder, never the repo root and never a bare filename —
    /// this app's data stays confined to one folder it owns inside a
    /// repository that may hold other, unrelated content.
    let dataFilePath (config: Session.GitHubSyncConfig) =
        let folder = (if String.IsNullOrWhiteSpace config.Folder then defaultFolder else config.Folder).Trim('/')
        $"{folder}/{fileName}"

    /// GitHub's Contents API path segments are percent-encoded individually —
    /// `Uri.EscapeDataString` on the whole path would also encode the `/`
    /// separator the folder/file split needs to keep.
    let private encodedPath (path: string) = path.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"

    let private headers (token: string) =
        [ "Authorization", $"Bearer {token}"
          "Accept", "application/vnd.github+json"
          "X-GitHub-Api-Version", "2022-11-28"
          "Content-Type", "application/json; charset=utf-8" ]

    let private contentsUrl (config: Session.GitHubSyncConfig) =
        $"{apiBase}/repos/{Uri.EscapeDataString config.Owner}/{Uri.EscapeDataString config.Repo}/contents/{encodedPath (dataFilePath config)}"

    let buildGetEffect (config: Session.GitHubSyncConfig) : EffectRequest =
        let url = $"{contentsUrl config}?ref={Uri.EscapeDataString config.Branch}"
        HttpEffect("github-pull", "GET", url, headers config.Token, None, 15000)

    /// `sha` is the blob version to overwrite — `None` asks GitHub to create
    /// the file, which fails if one is already there (surfaced like any other
    /// non-2xx status; see `Dispatch.fs`'s handling of the "github-push" result).
    let buildPutEffect (config: Session.GitHubSyncConfig) (sha: string option) (documentJson: string) : EffectRequest =
        let content = Convert.ToBase64String(Encoding.UTF8.GetBytes documentJson)
        let body = JsonObject()
        body.["message"] <- JsonValue.Create("Update the business activity ledger")
        body.["content"] <- JsonValue.Create(content)
        body.["branch"] <- JsonValue.Create(config.Branch)
        match sha with
        | Some value -> body.["sha"] <- JsonValue.Create(value)
        | None -> ()
        HttpEffect("github-push", "PUT", contentsUrl config, headers config.Token, Some(body.ToJsonString()), 15000)

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

    /// GitHub's Contents API error responses carry a JSON `message` field;
    /// fall back to the raw body when it doesn't parse as that shape.
    let errorMessage (body: string option) : string =
        let raw = body |> Option.defaultValue "no response body"
        try
            JsonNode.Parse(raw).AsObject().["message"].GetValue<string>()
        with _ ->
            raw
