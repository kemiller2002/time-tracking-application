# Echelon Business Activity Ledger

A mobile-first, audit-friendly business activity tracker. The business logic
is an F# domain compiled to WebAssembly, running entirely in the browser; a
thin JavaScript bridge (`web/dom-bindings.js`, `web/wasm-engine-transport.js`)
renders its view and forwards DOM events into it, but makes no business
decisions of its own — see [domain requirements](docs/DOMAIN-REQUIREMENTS.md)
for the rules and where each lives in the code.

```bash
npm run test:fsharp   # dotnet-run the F# spec suites
npm start              # dotnet publish the WASM engine, then serve statically
```

Open `http://localhost:4321/web/index.html`. Activities persist to this
browser's local storage through the engine's `Storage` effect — they survive
a reload, but are not synced anywhere else yet (a real backend is a planned
fast-follow; see the Persistence contract and Implementation sections of the
domain requirements doc).

## Implemented flows

- Persistent start, pause, resume, and stop timer with a 30-second discard
  threshold
- Manual activity entry, amendment, void/restore (with re-validation), split,
  and merge (same-date, contiguous sources only)
- Evidence attach/detach
- Daily ledger with a derived six-minute billing figure shown alongside the
  exact recorded time
- Daily review/attestation, with "amended after review" flagging
- Monthly summary and daily/monthly reports in JSON, Markdown, and CSV

## Requirements to run

- .NET SDK 10 with the `wasm-tools` workload (`dotnet workload install
  wasm-tools`)
- Python 3 (for the static file server `npm run serve` uses)
