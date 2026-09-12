/// Tier 1 — Semantic Model. Who is making a change.
///
/// Every revision records an actor (TE-R-031, TE-R-052). Until the user
/// settled OQ-6 there was nowhere for one to come from, so the browser kernel
/// attributed changes to the literal string `browser` — a placeholder, and
/// documented as one. The answer is **Google and Apple sign-in**
/// (DF-TE-0016), and this is the domain's side of it.
///
/// ## What this module does and does not claim
///
/// It turns an OIDC ID token's *claims* into an actor, and refuses claims that
/// do not belong to this application: a token from another issuer, a token
/// minted for another audience, an expired token, or one with no subject.
/// That catches misconfiguration and mistakes, which is most of what goes
/// wrong in practice.
///
/// It does NOT make attribution cryptographically trustworthy, and nothing
/// here should be read as saying it does. The signature is not checked, and
/// checking it in the browser would be close to theatre: the page that would
/// do the checking is the same page that could skip it, and it already holds a
/// repository token that can write anything. The verifiable authority for a
/// change is the **commit** — GitHub authenticates whoever wrote it — and the
/// actor recorded inside the file is the *claimed* signed-in identity beside
/// it. Making the recorded actor independently verifiable needs a check
/// somewhere the page cannot reach, which is tracked as separate work rather
/// than implied here.
module TimeEntry.Semantic.Identity

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values

/// The providers the user named. A closed union rather than a string, so
/// adding one is a compiler-checked decision with an issuer attached, and a
/// typo in a request cannot invent a provider.
type IdentityProvider =
    | Google
    | Apple

module IdentityProvider =

    /// The `iss` claim each provider mints. These are the values in each
    /// provider's published OIDC discovery document, and they are the whole
    /// reason `provider` is not simply trusted from the request: a token is
    /// accepted only if it says it came from the provider the page named.
    let issuer provider =
        match provider with
        | Google -> "https://accounts.google.com"
        | Apple -> "https://appleid.apple.com"

    /// The stable name recorded in an actor id, and the name a request uses.
    /// Lowercase and unpunctuated so it survives into an identifier unchanged.
    let name provider =
        match provider with
        | Google -> "google"
        | Apple -> "apple"

    /// How the provider is written in a sentence a person reads.
    let label provider =
        match provider with
        | Google -> "Google"
        | Apple -> "Apple"

    let ofName (raw: string) =
        match raw with
        | "google" -> Some Google
        | "apple" -> Some Apple
        | _ -> None

/// The claims this application reads out of an ID token.
///
/// A plain record of already-decoded values: Tier 1 does not know what a JWT
/// is, or base64, or JSON (TE-R-095). Decoding the token into this shape is
/// the browser boundary's job; deciding whether these claims are acceptable is
/// this tier's.
type IdentityClaims =
    { Issuer: string
      Subject: string
      /// `aud` is a string or an array of strings in OIDC, so it arrives as a
      /// list with one element in the common case.
      Audience: string list
      ExpiresAt: Instant
      /// `email` and `name` are optional in both providers' tokens — Apple
      /// omits the name after the first authorization, and may substitute a
      /// private relay address for the email.
      Email: string option
      Name: string option }

/// Why a set of claims was not accepted.
type IdentityRejection =
    /// The token was not minted by the provider the request named.
    | IdentityIssuerMismatch of expected: string * found: string
    /// The token was minted for a different application.
    | IdentityAudienceMismatch of expected: string
    | IdentitySubjectMissing
    | IdentityExpired of expiredAtMs: int64 * nowMs: int64
    | IdentityProviderUnknown of found: string

/// An identity that has been accepted for this application.
///
/// Private constructor: the only way to hold one is to have passed the checks
/// in `accept`, so no code path can attribute a change to a subject nobody
/// validated (TE-R-096).
type SignedInIdentity =
    private
    | SignedInIdentity of provider: IdentityProvider * subject: string * display: string option

module Identity =

    /// Accept a token's claims, or say why not.
    ///
    /// `now` is passed in rather than read: Tier 1 performs no effects, so
    /// expiry can only be checked against a clock somebody else read
    /// (TE-R-093). There is no leeway for clock skew — adding one would be
    /// inventing a tolerance nobody stated, and the device/server skew
    /// question is separately open (TE-R-008).
    ///
    /// The subject is checked for emptiness here rather than left to
    /// `UserId.create`: "no subject" is a fact about the token, and reporting
    /// it as a malformed identifier would describe the symptom instead.
    let accept
        (provider: IdentityProvider)
        (audience: string)
        (now: Instant)
        (claims: IdentityClaims)
        : Result<SignedInIdentity, IdentityRejection> =
        let expected = IdentityProvider.issuer provider

        if claims.Issuer <> expected then
            Error(IdentityIssuerMismatch(expected, claims.Issuer))
        elif not (List.contains audience claims.Audience) then
            // The audience is NOT reported back. It is the application's own
            // client id, and echoing a mismatched one into a message a person
            // reads puts configuration into a sentence about signing in.
            Error(IdentityAudienceMismatch audience)
        elif System.String.IsNullOrWhiteSpace claims.Subject then
            Error IdentitySubjectMissing
        elif Instant.epochMilliseconds claims.ExpiresAt <= Instant.epochMilliseconds now then
            Error(
                IdentityExpired(
                    Instant.epochMilliseconds claims.ExpiresAt,
                    Instant.epochMilliseconds now
                )
            )
        else
            // Name first, then email, then nothing. A display name is for a
            // person to recognise themselves by; an email is a reasonable
            // second best, and Apple's private relay address is a poor one but
            // still better than a subject nobody can read.
            let display =
                match claims.Name, claims.Email with
                | Some name, _ when not (System.String.IsNullOrWhiteSpace name) -> Some name
                | _, Some email when not (System.String.IsNullOrWhiteSpace email) -> Some email
                | _ -> None

            Ok(SignedInIdentity(provider, claims.Subject.Trim(), display))

    let provider (SignedInIdentity(p, _, _)) = p
    let subject (SignedInIdentity(_, s, _)) = s

    /// What a person sees: their name if the token carried one, otherwise the
    /// provider they signed in with. Never the subject — it is an opaque
    /// provider-scoped string that would tell them nothing.
    let display (SignedInIdentity(p, _, d)) =
        match d with
        | Some text -> text
        | None -> sprintf "Signed in with %s" (IdentityProvider.label p)

    /// The actor recorded on a revision: `google:<sub>` or `apple:<sub>`.
    ///
    /// The **subject**, not the email. `sub` is stable and provider-scoped:
    /// Google's survives an email change, and Apple's is the only stable
    /// handle when the email is a per-application relay address. Recording an
    /// email would mean a person's history stopped being theirs the day they
    /// changed it, and — worse — could make one person's old revisions read as
    /// another person's if an address were ever reassigned.
    ///
    /// Prefixed with the provider because `sub` is only unique within one
    /// issuer. Without the prefix, a Google subject and an Apple subject could
    /// in principle collide and two people would share a history.
    let actor (identity: SignedInIdentity) : Result<UserId, IdentifierError> =
        let (SignedInIdentity(p, subject, _)) = identity
        UserId.create (sprintf "%s:%s" (IdentityProvider.name p) subject)
