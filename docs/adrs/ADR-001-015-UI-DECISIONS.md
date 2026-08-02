# ADR-001–015: first-slice UI decisions

Status: provisional. Date: 2026-08-02. These records are grouped because they form one reversible prototype boundary; split them when any decision becomes accepted or independently evolves.

| ADR | Decision | Alternatives / consequence |
|---|---|---|
| 001 | Standards-based HTML/CSS/ES modules | React/Vue/Svelte add runtime and build cost before component needs are proven. Revisit with measured complexity. |
| 002 | iPhone-first source order and bottom navigation | Desktop-first responsive tables conflict with the primary context. Desktop enhances without reordering. |
| 003 | Layered semantic tokens and durable patterns | Raw palette/component token proliferation rejected. CSS patterns remain framework-neutral. |
| 004 | One typed/runtime-validated Cloudflare client | Direct GitHub access prohibited. Same-origin `/api/v1` is the only browser service boundary. |
| 005 | Cloudflare Access/passkey-compatible HttpOnly secure cookie | Long-lived localStorage bearer tokens rejected. Backend must finalize CSRF and reauthentication. |
| 006 | Persist command IDs and ordered offline queue | Fire-and-forget and silent overwrite rejected. Binary evidence queue remains deferred. |
| 007 | Cache only PWA shell/GET assets initially | Aggressive service-worker caching risks stale authority. Installability retained; share target deferred to device testing. |
| 008 | Derive elapsed time from authoritative timestamps | JavaScript interval counting rejected. Reconcile server/device skew on connectivity. |
| 009 | Small observable store separated from server state/drafts | Global framework store rejected until complexity justifies it. |
| 010 | 409 opens current-versus-proposed comparison | Last-write-wins rejected. Apply-again performs a fresh version check. |
| 011 | Cloudflare-issued signed evidence upload with explicit confirmation | Repository/client direct upload rejected. Scanning, limits, retention, and resumability are unresolved. |
| 012 | Semantic/native baseline targeting WCAG 2.2 AA | Custom controls rejected without measurable benefit. Conformance awaits full testing. |
| 013 | Quiet chronological ledger visual model | Generic dashboard cards rejected. Effective state leads; history progressively discloses. |
| 014 | CSS bars/progress with exact text equivalents | Chart dependency and decorative gauges rejected for the first slice. |
| 015 | Shortcut-friendly API commands with service authentication | Credentials inside Shortcuts rejected. Exact iOS setup awaits backend auth decision. |

Evidence: Visual Engineering `UI-FOUNDATIONS`, `UI-DECISION-CHECKLIST`, `UI-ANTI-PATTERNS`, research index records on native-first components, product legibility, responsive meaning, typography, density, color redundancy, and first-glance versus verification. Validation: unit tests, semantic browser snapshots, mobile reflow and interaction review. Reversibility: all decisions are localized behind HTML patterns, CSS tokens, domain modules, and the API client boundary.
