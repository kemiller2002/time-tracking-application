# Echelon Shared Application Foundations

Status: **Required source requirement for migration into Chrona**

This repository is a source requirement corpus for Chrona. The following obligations MUST be preserved during migration and reconciliation.

- **Aegis:** required for unexpected operational failures at GitHub/network/storage/browser-WASM/file/import-export and other external boundaries. Expected time-entry/domain outcomes remain typed domain/Ordo outcomes.
- **Forma:** required for interactive browser UI. Existing Forma patterns/components/tokens are used before local equivalents, with shared responsive/accessibility contracts preserved.
- **Folio:** required for printable/PDF/paginated time reports, summaries, exports, approval records, or other paper-oriented artifacts. Existing Folio primitives are used before local print implementations.
- **Composition:** Aegis fault intent flows through application/Limen state to Forma fault UI. Interactive UI uses Forma; printable document composition uses Folio.
- **Dependency discipline:** shared dependencies are pinned to released versions or immutable artifacts; shared capability gaps are recorded in the owning shared repository rather than silently forked.
- **Verification:** completion requires actual-use evidence and relevant Aegis, Forma, and Folio tests.

Chrona's current requirements may strengthen these rules but MUST NOT weaken them silently.
