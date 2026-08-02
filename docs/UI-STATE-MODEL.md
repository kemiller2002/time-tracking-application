# UI state model

Timer states are `unknown → idle → running ↔ paused → stopped`. Starting is disabled conceptually until current state is resolved. Elapsed time is derived from start, pause, and server timestamps rather than interval counts.

Commands move through `local-only → sending → saved`, or to `needs-attention` / `conflict`. The request ID is created before submission and reused for safe retries. A 409 opens version comparison; no newer correction is silently overwritten.

Activity state separates original record, effective record, amendments, void/restore status, relationships, evidence links, and report inclusion. Draft forms and route/dialog state are local UI state, not server state.
