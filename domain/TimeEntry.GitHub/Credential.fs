/// Tier 4 — Host. How a request proves it may act on the repository.
///
/// This is a *port*, like `GitHubStore`, and for the same reason: the way the
/// application authenticates is expected to change, and a change of mechanism
/// must not be a change to the transport, the interpreter, or anything below
/// them.
///
/// Today the browser holds a token (DF-TE-0011). That is one implementation of
/// this port and is named as such — `Credential.token` — rather than being the
/// shape everything else is written against. The alternatives that were
/// considered and may yet be chosen each fit without the transport changing:
///
/// | Mechanism | What it returns | What changes elsewhere |
/// |---|---|---|
/// | Token in hand (today) | `AuthorizationHeader("Bearer", t)` | nothing |
/// | OAuth device flow | the same, refreshed on expiry | nothing |
/// | GitHub App installation token | the same, minted per hour | nothing |
/// | Same-origin proxy holding the session | `AmbientAuthority` | nothing |
///
/// Three design choices make that true, and each is a decision rather than a
/// convenience:
///
/// 1. **Asked per request, not once per session.** A token that never expires
///    could be set as a default header at construction. A device-flow or
///    installation token cannot: it expires, and the refresh has to happen
///    somewhere. Asking every time costs one function call and is the only
///    shape that admits both.
///
/// 2. **"No header" is a case, not an empty string.** A proxy that carries a
///    session cookie needs the request sent with no `Authorization` at all.
///    Expressing that as `AmbientAuthority` rather than as an empty token
///    means the transport cannot accidentally send `Authorization: Bearer `
///    and get a confusing 401.
///
/// 3. **Failing to obtain a credential is its own outcome.** "Not signed in"
///    and "signed in but refused" are different facts and lead to different
///    UI. Collapsing them into one 401 from the server would lose that, and
///    would require a network round trip to discover something the client
///    already knows.
module TimeEntry.GitHub.Credential

/// How a single request proves its right to act.
type Authorization =
    /// An `Authorization: <scheme> <parameter>` header. GitHub accepts
    /// `Bearer` for both personal access tokens and installation tokens.
    | AuthorizationHeader of scheme: string * parameter: string
    /// Send no `Authorization` header. The right travels some other way —
    /// a same-origin proxy's session cookie, a runner's ambient identity, or
    /// a public repository read.
    | AmbientAuthority

/// Why a credential could not be produced.
///
/// These are all *client-side* facts, discovered before any request is sent.
/// A server's 401 remains `StoreError.Unauthorized`; the two are deliberately
/// not the same type, because "I have nothing to send" and "what I sent was
/// refused" call for different responses.
type CredentialError =
    /// Nothing is configured. The user has not signed in, or the host was
    /// started without a token.
    | CredentialUnavailable of detail: string
    /// Something is configured but is past its usable life, and acquiring a
    /// replacement is either impossible here or has itself failed.
    | CredentialExpired of detail: string
    /// A refresh or exchange was attempted and the provider refused it.
    | CredentialRefused of detail: string

/// The port.
///
/// `Describe` exists so that a diagnostic can say which mechanism is in use
/// without any caller pattern-matching on the implementation — and, more
/// importantly, so that nothing is tempted to log the credential itself in
/// order to answer that question. It must never include secret material.
///
/// `NoEquality`/`NoComparison` for the same reason `GitHubStore` carries them:
/// the field is a function.
[<NoEquality; NoComparison>]
type CredentialSource =
    { /// A short, non-secret name for the mechanism, e.g. "token".
      Describe: string
      Acquire: unit -> Async<Result<Authorization, CredentialError>> }

/// The mechanism in use today: a token supplied to the host, sent as a bearer
/// credential on every request (DF-TE-0011).
///
/// The token is captured rather than re-read, because a static token has
/// nothing to re-read from. A mechanism that does — device flow, an
/// installation token — supplies its own `CredentialSource` instead of
/// changing this one.
let token (value: string) : CredentialSource =
    { Describe = "token"
      Acquire =
        fun () ->
            async {
                // Checked here rather than at the call site so that an empty
                // or whitespace token becomes a typed, client-side answer
                // instead of a 401 the user has to interpret.
                if System.String.IsNullOrWhiteSpace value then
                    return Error(CredentialUnavailable "no token was supplied")
                else
                    return Ok(AuthorizationHeader("Bearer", value.Trim()))
            } }

/// No credential of our own: the surrounding context carries the right.
///
/// Used for reads of a public repository, and the shape a same-origin proxy
/// would take.
let ambient (describe: string) : CredentialSource =
    { Describe = describe
      Acquire = fun () -> async { return Ok AmbientAuthority } }

/// Build a source from an arbitrary asynchronous acquisition.
///
/// This is the extension point: a device flow, an installation-token minter,
/// or a credential read from a keychain is `ofAsync "device-flow" (fun () ->
/// ...)`. Nothing in the transport needs to know which.
let ofAsync (describe: string) (acquire: unit -> Async<Result<Authorization, CredentialError>>) =
    { Describe = describe
      Acquire = acquire }

/// Render a credential failure for a person.
///
/// In one place so the wording of "you are not signed in" does not drift
/// between the browser and a command line.
let describeError (error: CredentialError) =
    match error with
    | CredentialUnavailable detail -> sprintf "No credential is available: %s." detail
    | CredentialExpired detail -> sprintf "The stored credential has expired: %s." detail
    | CredentialRefused detail -> sprintf "The credential was refused: %s." detail
