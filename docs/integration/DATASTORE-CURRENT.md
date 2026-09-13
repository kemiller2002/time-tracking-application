# CHR-INT-001 — Current GitHub Datastore Structure

Documents what exists today, before any integration-related datastore
paths are added. Source of truth: `f-sharp/src/Ledger.Engine/GitHubSync.fs`.

## Scoping unit: `<Owner>/<Repo>/<Folder>`, not "organizations"

GitHub sync is configured per browser (More screen → "GitHub sync"):
`Owner`, `Repo`, `Branch`, a `Folder` inside that repo (defaulting to
`GitHubSync.defaultFolder = "time-tracking-data"` when blank), and a
personal access token. There is no multi-tenant "organization" concept
anywhere in the domain or the datastore layout — one `<Folder>` is the
entire scope of one deployment's data. Several people can point their own
browser's sync settings at the *same* `<Owner>/<Repo>/<Folder>`, and each
gets their own subfolder (see below), but the folder itself is not
subdivided by organization.

## Per-person files

```
<Folder>/<Login>/ledger.json      — that person's LedgerDocument (activities + attestations)
<Folder>/<Login>/metadata.json    — write-only profile info (login, display name, last synced) for humans browsing the repo
<Folder>/<Login>/settings.json    — that person's preferences (reportFormat, timezone) — round-tripped
```

`<Login>` is always resolved from `GET /user` against the saved token
(`GitHubSync.buildWhoAmIEffect`) — never typed by the user, so it cannot
collide or be spoofed by a mistyped name. `GitHubSync.dataFilePath`,
`metadataFilePath`, `settingsFilePath` build these three paths.

`ledger.json`/`metadata.json` are committed together as one atomic write
via GitHub's Git Data API (5-step chain: read ref → read base tree →
build a new tree with just those two blobs replaced → create a commit →
move the branch ref onto it — `GitHubSync.buildRefGetEffect` through
`buildRefUpdateEffect`). `settings.json` alone still goes through a plain
Contents API PUT, since it is never written at the same moment as the
ledger.

## Shared, folder-root file

```
<Folder>/reference.json
```

Added by the "data-driven Projects/Activity Types/Tags" work (PR #22/#23,
same session as this document). Lives at the folder root, **not** under
any one person's subfolder, because the catalog it carries (`{projects,
activityTypes, tags}`, each `{id, name, active}`) is shared by everyone
syncing to that `<Folder>` — whoever last saves an edit via the admin page
(More screen → "Manage projects, activity types & tags") writes the same
file. `GitHubSync.referenceFilePath` builds this path.

## What does not exist yet

- No `organizations/` hierarchy of any kind.
- No inbound-integration inbox, no processing receipts, no time
  "candidates" distinct from an authoritative `Activity`.
- No path partitioning by date/year/month.
- No archival convention.

`INTEGRATION-CONTRACT.md` documents where the new integration-related
paths this specification calls for (`observations/inbox`,
`observation-receipts`, candidates) are proposed to live, adapted to this
existing `<Folder>`-scoped convention rather than the specification's
illustrative `organizations/<org>/projects/<project>/...` hierarchy.

## Persistence contract shapes already established

`GitHubSync.fs` already has generic vocabulary this work reuses rather
than reinventing:

- `parseGetResponse : body -> Result<{| Sha: string; DocumentJson: string |}, string>` —
  reads a Contents API GET response (base64 `content` + blob `sha`).
- `putBody : sha:string option -> branch -> message -> content -> string` —
  builds a Contents API PUT body; `sha = None` means "create," `Some sha`
  means "update this exact version," and GitHub itself rejects a stale
  `sha` with 409 (Contents API) or 422 (the Git Data API's ref-update
  step) — this is GitHub's own optimistic-concurrency mechanism, already
  relied on everywhere sync writes today.
- `parsePutResponse : body -> Result<string, string>` — reads the
  resulting blob's `sha` back out of either shape of PUT/PATCH response.
- `errorMessage : body option -> string` — extracts GitHub's own `message`
  field from an error response.

Any new persistence code for observations/candidates/receipts should
reuse these functions rather than duplicating GitHub response parsing.
