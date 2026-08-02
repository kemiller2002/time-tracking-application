# Echelon Business Activity Ledger

A mobile-first, audit-friendly business activity tracker. The current repository contains a functional frontend prototype and a Cloudflare-only API boundary; it does not contain a production Cloudflare service.

```bash
npm run dev
npm test
npm run check
```

Open `http://localhost:4173`. Data is synthetic and persists locally for demonstration. Use browser storage reset to restore the sample day.

## Implemented prototype flows

- Persistent start, pause, resume, and stop timer using authoritative timestamps
- One-tap favorites and six-minute manual entry
- Daily ledger, effective activity detail, correction, void/restore, split validation, evidence, and history
- Daily review/attestation, monthly summary, synchronization and stale-conflict demonstrations
- Installable offline shell and Shortcut-friendly endpoint guide

All production reads and writes are designed to use `/api/v1` through Cloudflare with secure cookies, idempotency keys, version conflicts, timeouts, and actionable errors. No browser code talks to GitHub or contains credentials.

See [UI architecture](docs/UI-ARCHITECTURE.md), [runbook](docs/UI-RUNBOOK.md), and [known limitations](docs/UI-VISUAL-REVIEW.md).
