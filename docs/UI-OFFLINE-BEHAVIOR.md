# UI offline behavior

The shell and last confirmed projections may render offline. An active timer continues from persisted timestamps. Writes receive a request ID before queuing and retain it until the Cloudflare service confirms the command.

Queue order is preserved for dependent commands. Safe retries reuse the same idempotency key. The UI says `Local only`, `Sending`, `Saved`, `Needs attention`, or `Conflict`; it never calls a local-only record saved. A 409 pauses the affected command and opens current-versus-proposed comparison. Evidence binaries require resumable upload support and are not yet production-ready.
