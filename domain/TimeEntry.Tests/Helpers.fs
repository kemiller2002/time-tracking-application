/// Test builders. Kept in one place so a test reads as the behavior under
/// scrutiny rather than as construction noise.
module TimeEntry.Tests.Helpers

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.EntryState
open TimeEntry.Transitions.Commands
open TimeEntry.Transitions.Effects

/// Unwrap a smart-constructor Result in test setup. Fails loudly rather than
/// silently substituting a default, so a broken builder cannot make a test
/// vacuously pass.
let expect (result: Result<'a, 'e>) : 'a =
    match result with
    | Ok value -> value
    | Error e -> failwithf "test setup produced an invalid value: %A" e

let entryId id = EntryId.create id |> expect
let revisionId id = RevisionId.create id |> expect
let projectId id = ProjectId.create id |> expect
let activityTypeId id = ActivityTypeId.create id |> expect
let userId id = UserId.create id |> expect
let deviceLabel label = DeviceLabel.create label |> expect
let version token = VersionToken.create token |> expect
let reason text = Reason.create text |> expect
let description text = Description.create text |> expect
let seconds n = Duration.ofSeconds n |> expect
let millis (n: int64) = Duration.ofMilliseconds n |> expect
let minutes n = Duration.ofMinutes n |> expect
let onDate year month day = EntryDate.ofYearMonthDay year month day |> expect
let instant epochMilliseconds = Instant.ofEpochMilliseconds epochMilliseconds

let defaultDate = onDate 2026 9 10

let catalogueName raw = CatalogueName.create raw |> expect

let project id status : Project =
    { Id = projectId id
      Name = catalogueName id
      Status = status
      ProjectionVersion = version "catalogue-v1" }

let activityType id status : ActivityType =
    { Id = activityTypeId id
      Name = catalogueName id
      Status = status
      ProjectionVersion = version "catalogue-v1" }

/// The catalogue the suites operate against. Carries an archived project and
/// an archived activity type so DF-TE-0007's refusals have something real to
/// be refused against.
let catalogue =
    Catalogue.ofLists
        [ project "echelon-foundry" Available
          project "northline" Available
          project "retired-client" Archived ]
        [ activityType "research" Available
          activityType "marketing" Available
          activityType "retired-activity" Archived ]

let facts (duration: Duration) =
    { Project = projectId "echelon-foundry"
      ActivityType = activityTypeId "research"
      Date = defaultDate
      Duration = duration
      Description = Some(description "Reviewed composition evidence.")
      Origin = Timed
      Evidence = [] }

let attribution (revision: string) =
    { Actor = userId "km"
      Device = deviceLabel "iPhone"
      OccurredAt = instant 1789000000L
      NewRevisionId = revisionId revision }

/// An entry that has been persisted once, so it has a version and can be the
/// target of a mutation.
let persistedEntry (id: string) (duration: Duration) (versionToken: string) =
    let entryFacts = facts duration

    { Id = entryId id
      State = Active
      Effective = entryFacts
      History =
        [ { Id = revisionId (id + "-r1")
            Change = Created
            Facts = entryFacts
            RecordedAt = instant 1788000000L
            RecordedBy = userId "km"
            Device = deviceLabel "iPhone" } ]
      Version = Some(version versionToken) }

/// An entry on a specific ledger day, for cross-day cases.
let persistedEntryOn (id: string) (duration: Duration) (versionToken: string) (date: EntryDate) =
    let entry = persistedEntry id duration versionToken

    { entry with
        Effective = { entry.Effective with Date = date }
        History =
            entry.History
            |> List.map (fun r -> { r with Facts = { r.Facts with Date = date } }) }

let mergeSource (id: string) (versionToken: string) : MergeSource =
    { EntryId = entryId id
      ExpectedVersion = version versionToken
      NewRevisionId = revisionId (id + "-merged") }

let splitChild (id: string) (duration: Duration) =
    { NewEntryId = entryId id
      NewRevisionId = revisionId (id + "-r1")
      Duration = duration
      Project = projectId "echelon-foundry"
      ActivityType = activityTypeId "research"
      Description = Some(description "Split part.")
      ReassignedEvidence = [] }

// --- outcome assertions ----------------------------------------------------
// Shared so split/merge/transition suites cannot drift apart on what
// "accepted" means.

let accepted (outcome: Outcome) =
    match outcome with
    | Accepted(entries, effects) -> entries, effects
    | Rejected rejection -> failwithf "expected Accepted, got Rejected %A" rejection

let rejection (outcome: Outcome) =
    match outcome with
    | Rejected r -> r
    | Accepted _ -> failwith "expected Rejected, got Accepted"

let single (entries: TimeEntry list) =
    if List.length entries <> 1 then
        failwithf "expected exactly one entry, got %d" (List.length entries)

    List.head entries

let correctionRequest (entry: TimeEntry) (newDuration: Duration) (versionToken: string) : CorrectEntryRequest =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      CorrectedFacts = { entry.Effective with Duration = newDuration }
      Reason = reason "Forgot to stop timer"
      Attribution = attribution "e1-r2" }

let voidRequest (entry: TimeEntry) (versionToken: string) : VoidEntryRequest =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      Reason = reason "Duplicate of the timer entry"
      Attribution = attribution "e1-r2" }

let splitRequest (entry: TimeEntry) (versionToken: string) (children: SplitChild list) : SplitEntryRequest =
    { EntryId = entry.Id
      ExpectedVersion = version versionToken
      Children = children
      Attribution = attribution "e1-r2" }

let mergeRequest (sources: MergeSource list) : MergeEntriesRequest =
    { NewEntryId = entryId "m1"
      Sources = sources
      Project = projectId "echelon-foundry"
      ActivityType = activityTypeId "research"
      Description = Some(description "Combined research block.")
      Evidence = []
      Reason = reason "Same task split across two timer runs"
      Attribution = attribution "m1-r1" }

// ---------------------------------------------------------------------------
// Sign-in tokens
// ---------------------------------------------------------------------------

/// Mint an ID token the kernel will decode.
///
/// The signature segment is the literal `not-checked`, which is honest: the
/// application does not verify signatures, and a test that signed its tokens
/// would be asserting a property the code does not have. See
/// `TimeEntry.Semantic.Identity` for what is and is not claimed by that.
let idToken (provider: string) (audience: string) (subject: string) (expiresAtSeconds: int64) =
    let issuer =
        match provider with
        | "google" -> "https://accounts.google.com"
        | "apple" -> "https://appleid.apple.com"
        | other -> other

    let encode (raw: string) =
        raw
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Convert.ToBase64String
        // Base64url: the kernel restores these before decoding, so a token
        // that still carried `+`, `/` or `=` would not exercise that path.
        |> fun s -> s.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let payload =
        sprintf
            """{"iss":"%s","sub":"%s","aud":"%s","exp":%d,"email":"person@example.invalid","name":"A Person"}"""
            issuer
            subject
            audience
            expiresAtSeconds

    sprintf "%s.%s.not-checked" (encode """{"alg":"RS256","typ":"JWT"}""") (encode payload)

/// The audience every test signs in against.
let testAudience = "1234.apps.googleusercontent.com"

/// A signed-in Google identity that has not expired for any `occurredAtMs`
/// this suite uses. Expressed in seconds, as `exp` is.
let signedIn = idToken "google" testAudience "1099" 4102444800L
