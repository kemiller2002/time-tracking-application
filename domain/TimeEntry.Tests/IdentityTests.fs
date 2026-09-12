/// Who a change is attributed to (DF-TE-0016, resolving OQ-6).
///
/// Two halves. Tier 1 decides whether a token's claims belong to this
/// application and what actor they name; the browser kernel decodes the token
/// and refuses a command that carries no identity at all. The second half is
/// the behavioural change: before this, an unattributed change was recorded
/// against the literal `browser`.
///
/// What no test here asserts is that attribution is *trustworthy*. Signatures
/// are not checked, and a suite that pretended otherwise would be worse than
/// one that says so. See `TimeEntry.Semantic.Identity`.
module TimeEntry.Tests.IdentityTests

open System.Text.Json.Nodes
open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Identity
open TimeEntry.Semantic.EntryState
open TimeEntry.Persistence
open TimeEntry.Tests.Helpers

let private claims =
    { Issuer = "https://accounts.google.com"
      Subject = "1099"
      Audience = [ testAudience ]
      ExpiresAt = instant 1789000001L
      Email = Some "person@example.invalid"
      Name = Some "A Person" }

let private now = instant 1789000000L

// ---------------------------------------------------------------------------
// Tier 1 — accepting a token's claims
// ---------------------------------------------------------------------------

[<Fact>]
let ``a Google token for this application is accepted`` () =
    match Identity.accept Google testAudience now claims with
    | Ok identity ->
        Assert.Equal(Google, Identity.provider identity)
        Assert.Equal("1099", Identity.subject identity)
    | Error e -> failwithf "expected an accepted identity, got %A" e

[<Fact>]
let ``a token from another issuer is refused even when the page says Google`` () =
    // The reason `provider` is not simply trusted from the request: a token is
    // accepted only if it says it came from the provider that was named.
    match Identity.accept Google testAudience now { claims with Issuer = "https://evil.invalid" } with
    | Error(IdentityIssuerMismatch(expected, found)) ->
        Assert.Equal("https://accounts.google.com", expected)
        Assert.Equal("https://evil.invalid", found)
    | other -> failwithf "expected IdentityIssuerMismatch, got %A" other

[<Fact>]
let ``an Apple token presented as a Google one is refused`` () =
    let apple = { claims with Issuer = IdentityProvider.issuer Apple }

    match Identity.accept Google testAudience now apple with
    | Error(IdentityIssuerMismatch _) -> ()
    | other -> failwithf "expected IdentityIssuerMismatch, got %A" other

[<Fact>]
let ``an Apple token presented as an Apple one is accepted`` () =
    let apple = { claims with Issuer = IdentityProvider.issuer Apple; Subject = "001122.aa.bb" }

    match Identity.accept Apple testAudience now apple with
    | Ok identity ->
        Assert.Equal(Apple, Identity.provider identity)
        Assert.Equal("001122.aa.bb", Identity.subject identity)
    | Error e -> failwithf "expected an accepted identity, got %A" e

[<Fact>]
let ``a token minted for another application is refused`` () =
    match Identity.accept Google testAudience now { claims with Audience = [ "someone-else" ] } with
    | Error(IdentityAudienceMismatch expected) -> Assert.Equal(testAudience, expected)
    | other -> failwithf "expected IdentityAudienceMismatch, got %A" other

[<Fact>]
let ``an audience list containing this application is accepted`` () =
    // `aud` may be an array. One of its entries matching is enough, which is
    // what OIDC says and what a token issued to several clients looks like.
    let several = { claims with Audience = [ "another-client"; testAudience ] }
    Assert.True(Result.isOk (Identity.accept Google testAudience now several))

[<Fact>]
let ``a token with no subject is refused, and says so as such`` () =
    // Not reported as a malformed identifier: "no subject" is a fact about
    // the token, and the other message would describe the symptom.
    match Identity.accept Google testAudience now { claims with Subject = "  " } with
    | Error IdentitySubjectMissing -> ()
    | other -> failwithf "expected IdentitySubjectMissing, got %A" other

[<Fact>]
let ``an expired token is refused against the instant the host supplied`` () =
    let expired = { claims with ExpiresAt = instant 1788999999L }

    match Identity.accept Google testAudience now expired with
    | Error(IdentityExpired(expiredAt, at)) ->
        Assert.Equal(1788999999L, expiredAt)
        Assert.Equal(1789000000L, at)
    | other -> failwithf "expected IdentityExpired, got %A" other

[<Fact>]
let ``a token expiring exactly now is refused`` () =
    // No leeway for clock skew. A tolerance nobody stated would be invented,
    // and the skew question is separately open (TE-R-008).
    match Identity.accept Google testAudience now { claims with ExpiresAt = now } with
    | Error(IdentityExpired _) -> ()
    | other -> failwithf "expected IdentityExpired, got %A" other

// ---------------------------------------------------------------------------
// Tier 1 — the actor an identity names
// ---------------------------------------------------------------------------

let private actorOf provider claims' =
    Identity.accept provider testAudience now claims'
    |> expect
    |> Identity.actor
    |> expect
    |> UserId.value

[<Fact>]
let ``the actor is the provider and the subject, not the email`` () =
    // The subject survives an email change; an email does not survive being
    // reassigned. Recording the email would let one person's old revisions
    // read as another person's.
    Assert.Equal("google:1099", actorOf Google claims)

[<Fact>]
let ``two providers with the same subject are two different actors`` () =
    // `sub` is only unique within one issuer. Without the prefix these two
    // would share a history.
    let apple = { claims with Issuer = IdentityProvider.issuer Apple }
    Assert.NotEqual<string>(actorOf Google claims, actorOf Apple apple)

[<Fact>]
let ``the display name is the token's name when it has one`` () =
    let identity = Identity.accept Google testAudience now claims |> expect
    Assert.Equal("A Person", Identity.display identity)

[<Fact>]
let ``it falls back to the email, then to the provider`` () =
    let emailOnly = { claims with Name = None }
    let neither = { claims with Name = None; Email = None }

    Assert.Equal(
        "person@example.invalid",
        Identity.display (Identity.accept Google testAudience now emailOnly |> expect)
    )

    // Never the subject: it is an opaque provider-scoped string that would
    // tell a person nothing about themselves.
    Assert.Equal(
        "Signed in with Apple",
        Identity.display (
            Identity.accept Apple testAudience now { neither with Issuer = IdentityProvider.issuer Apple }
            |> expect
        )
    )

[<Fact>]
let ``only the two named providers are recognised`` () =
    Assert.Equal(Some Google, IdentityProvider.ofName "google")
    Assert.Equal(Some Apple, IdentityProvider.ofName "apple")
    Assert.Equal(None, IdentityProvider.ofName "Google")
    Assert.Equal(None, IdentityProvider.ofName "facebook")

// ---------------------------------------------------------------------------
// The browser kernel — decoding a token and refusing an unattributed change
// ---------------------------------------------------------------------------

let private createCommand (identity: string option) =
    let node = JsonObject()

    node.Add(
        "catalogue",
        JsonNode.Parse(Serialization.writeCatalogue (Mapping.catalogueToDocument catalogue))
    )

    node.Add("entries", JsonArray())
    node.Add("versions", JsonObject())

    let command =
        JsonNode.Parse
            """{ "kind": "create", "entryId": "e-new", "projectId": "echelon-foundry",
                 "activityTypeId": "research", "date": "2026-09-10",
                 "durationUnits": 5, "occurredAtMs": 1789000000 }"""

    identity |> Option.iter (fun raw -> command.AsObject().Add("identity", JsonNode.Parse raw))
    node.Add("command", command)
    JsonNode.Parse(TimeEntry.Kernel.dispatch (node.ToJsonString()))

let private identityJson (provider: string) (token: string) =
    sprintf """{ "provider": "%s", "audience": "%s", "idToken": "%s" }""" provider testAudience token

[<Fact>]
let ``a command with no identity is refused rather than attributed`` () =
    // The behavioural change DF-TE-0016 makes. Before it, this recorded a
    // revision against the literal `browser`.
    let answer = createCommand None
    Assert.False(answer.["ok"].GetValue<bool>())

    Assert.Equal(
        "sign in with Google or Apple before recording time",
        answer.["error"].GetValue<string>()
    )

[<Fact>]
let ``a signed-in command records the actor the token names`` () =
    let answer = createCommand (Some(identityJson "google" signedIn))
    Assert.True(answer.["ok"].GetValue<bool>())

    let stored = answer.["entries"].AsArray() |> Seq.head
    let entry = Serialization.read (stored.ToJsonString()) |> expect
    let mapped = Mapping.fromDocument None entry |> expect

    Assert.Equal<string>(
        "google:1099",
        UserId.value (mapped.History |> List.last |> fun r -> r.RecordedBy)
    )

[<Fact>]
let ``an expired token is refused in words, against the command's own clock`` () =
    // `occurredAtMs` is 1789000000, which is 1789000 seconds. A token
    // expiring a second earlier is expired for this command.
    let expired = idToken "google" testAudience "1099" 1788999L
    let answer = createCommand (Some(identityJson "google" expired))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("that sign-in has expired — sign in again", answer.["error"].GetValue<string>())

[<Fact>]
let ``a token for another application is refused without naming the client id`` () =
    let other = idToken "google" "someone-elses-client-id" "1099" 4102444800L
    let answer = createCommand (Some(identityJson "google" other))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.DoesNotContain(testAudience, answer.["error"].GetValue<string>())

[<Fact>]
let ``an unknown provider is refused by name`` () =
    let answer = createCommand (Some(identityJson "facebook" signedIn))
    Assert.False(answer.["ok"].GetValue<bool>())

    Assert.Equal(
        "'facebook' is not a sign-in method this ledger accepts",
        answer.["error"].GetValue<string>()
    )

[<Fact>]
let ``a token that is not three segments is refused without throwing`` () =
    let answer = createCommand (Some(identityJson "google" "not-a-token"))
    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("that sign-in token is not in the expected form", answer.["error"].GetValue<string>())

[<Fact>]
let ``a token whose payload is not base64 is refused without throwing`` () =
    let answer = createCommand (Some(identityJson "google" "aaa.!!!not-base64!!!.ccc"))
    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Contains("could not be read", answer.["error"].GetValue<string>())

[<Fact>]
let ``a base64url payload is decoded, padding and all`` () =
    // The tokens providers actually mint are base64url with the padding
    // stripped. A decoder that only handled standard base64 would fail on
    // roughly three quarters of real tokens, and only sometimes.
    let token = idToken "google" testAudience "a-subject-long-enough-to-need-padding" 4102444800L
    Assert.DoesNotContain("=", token)
    let answer = createCommand (Some(identityJson "google" token))
    Assert.True(answer.["ok"].GetValue<bool>())

[<Fact>]
let ``an identity missing a field is refused by naming the field`` () =
    let answer =
        createCommand (Some(sprintf """{ "provider": "google", "idToken": "%s" }""" signedIn))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("missing 'identity.audience'", answer.["error"].GetValue<string>())
