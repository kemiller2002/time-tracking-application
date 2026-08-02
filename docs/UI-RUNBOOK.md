# UI runbook

Start with `npm run dev`, then open `http://localhost:4173`. Run `npm run check` for syntax and unit tests. Run `./ros registry check && ./ros validate` for repository validation.

Configuration defaults to same-origin `/api/v1`; production should inject an approved environment base without secrets. Diagnose queued work from the synchronization view, preserve request IDs when retrying, and review 409 conflicts instead of overwriting. A service-worker shell issue can be rolled back by changing the cache version or unregistering it during diagnosis.

The current data adapter is a prototype. Do not use it for production records. Before release, connect real response schemas and authentication, run the full matrix in `UI-TEST-PLAN.md`, complete the privacy/threat review for evidence, and verify Cloudflare-to-GitHub persistence independently.
