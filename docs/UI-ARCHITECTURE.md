# UI architecture

## Scope

The first vertical slice is a dependency-light standards-based PWA. Semantic HTML and CSS provide structure and layout; small ES modules own domain calculations, persisted UI state, interaction orchestration, and the Cloudflare boundary. The prototype adapter is local and synthetic. The service remains authoritative in production.

## Layers

`index.html` is the application shell and semantic route content. Secondary workflows render as in-page detail workspaces, never modals. `src/app.js` binds feature flows. `src/domain.js` contains pure timer, duration, split, and request-ID logic. `src/state.js` separates persisted client state from presentation. `src/api/client.js` is the sole production network boundary. `service-worker.js` caches shell assets and explicitly excludes `/api/*`.

State is divided into server records, active timer, queued commands, and transient view/dialog state. Components are semantic CSS patterns rather than a framework runtime. This reduces bundle and interoperability risk while the product model is still being validated.

## Data flow

Browser → authenticated `/api/v1` Cloudflare service → command validation/idempotency → GitHub-backed append-only persistence and effective projections. The browser consumes effective records and never reconstructs the canonical ledger or contacts GitHub.

## Security boundaries

Credentials use secure HttpOnly SameSite cookies. Writes carry a stable request ID; secrets and business text are excluded from telemetry. File evidence needs a separate signed-upload and scanning design before production.
