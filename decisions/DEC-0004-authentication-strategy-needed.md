# Decision — Authentication strategy required before Capability 2

## Status

Proposed

## Context

Capability 2 requires secure single-owner sign-in, session cookies, CSRF protection, refresh/logout, expiration recovery, and optional reauthentication. The repository has no identity provider, Cloudflare Access application, domain, account binding, or passkey policy.

## Decision

Do not invent a production identity system. Recommend Cloudflare Access in front of the Worker with an application session mapped to a short-lived HttpOnly, Secure, SameSite session and CSRF token for state-changing commands. A deterministic development identity adapter may be added only after the production boundary is accepted.

## Alternatives Considered

Passkeys owned directly by the Worker; email one-time codes; static password; localStorage bearer token. Passkeys remain viable but require credential lifecycle/recovery design. Static passwords and localStorage bearer tokens are rejected.

## Evidence

Execution-contract security requirements and absence of repository identity/deployment configuration.

## Assumptions

The application remains single-owner and same-origin.

## Consequences

Capability 2 cannot honestly complete until the owner accepts an identity boundary and provides deployment context.

## Risks

Cloudflare Access availability and UX may not fit all Shortcut/PWA flows; passkeys may later supersede it.

## Revisit When

The owner chooses Cloudflare Access, Worker-managed passkeys, or another explicit identity provider.

## Related Capabilities

Capabilities 2, 3, 12, 15, 18.
