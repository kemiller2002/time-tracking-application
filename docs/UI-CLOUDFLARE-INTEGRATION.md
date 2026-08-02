# Cloudflare integration

`LedgerClient` targets environment-configurable `/api/v1`. It uses `credentials: include`, JSON runtime guards, cancellation, an eight-second timeout, request/correlation IDs, and idempotency headers. It maps 409, 429, timeout, offline, and general service errors to calm actionable messages.

Expected endpoint families are config/projects/activity types; current timer commands; day/month/activity projections; activity create/correct/void/restore/split/merge; evidence attach/remove; attestation; and reports. Production must add CSRF validation, response schemas for every DTO, optimistic version headers, retry policy, and signed evidence upload. GitHub is exclusively behind Cloudflare.
