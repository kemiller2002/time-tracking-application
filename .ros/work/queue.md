# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ROS-UPGRADE-2-0-1 | ROS-UPGRADE-2-0-1 | complete |  |  |
| WI-0001 | Install and upgrade Echelon Foundry dependencies (ROS 2.0.1 greenfield, SDE 1.1.1, typescript-wasm-kernel 0.4.1) | complete | dependencies, mechanical | high |
| WI-0002 | TE-001 Repository and authority discovery: ROS, SDE, WASM kernel, requirements, source, tests, GitHub boundary, UI history | complete | discovery | high |
| WI-0003 | TE-002 Requirements normalization and traceability for Time Entry domain | complete | requirements | high |
| WI-0004 | TE-003 Domain state model: entry states, legal transitions, capabilities, obligations, effects | complete | domain | high |
| WI-0005 | TE-004 Time unit domain: exact elapsed duration plus six-minute billing units | complete | domain | high |
| WI-0006 | TE-005 GitHub persistence boundary: anti-corruption layer between F# domain and GitHub API | complete | persistence | high |
| WI-0007 | TE-006 WASM/browser boundary: F# WASM via typescript-wasm-kernel with minimal TS surface | complete | boundary | high |
| WI-0008 | TE-007 Historical UI recovery: establish authoritative HTML/CSS screen provenance | complete | ui | high |
| WI-0009 | TE-018 BLOCKED: .NET/F# toolchain unavailable - proxy denies all Microsoft distribution hosts | complete | blocker | high |
| WI-0010 | TE-008 Create entry vertical slice | complete | slice | high |
| WI-0011 | TE-009 Entry history/list projection: load, filter, sort, search, totals | complete | slice | high |
| WI-0012 | TE-010 Correction transition with auditable supersession | complete | slice | high |
| WI-0013 | TE-011 Split transition and total-preservation invariant | complete | slice | high |
| WI-0014 | TE-012 Void and restore transitions with audit behavior | complete | slice | high |
| WI-0015 | TE-013 Projects, activity types, and labels state and selection rules | active | slice | medium |
| WI-0016 | TE-014 GitHub concurrency: explicit version evidence and conflict handling | complete | slice | high |
| WI-0017 | TE-015 Accessibility and browser behavior verification | active | verification | medium |
| WI-0018 | TE-016 Heterogeneous verification: behavioral, boundary, structural, effect, serialization, adversarial | complete | verification | high |
| WI-0019 | TE-017 Final traceability and handoff | active | traceability | high |
| WI-0020 | TE-019 Implement merge transition: sources superseded into new entry per DF-TE-0006 | complete | domain | high |
| WI-0021 | TE-020 Implement archived-project policy per DF-TE-0007: reject new time, keep existing entries | captured | domain | medium |
| WI-0022 | TE-021 Rebuild app shell on static-ui-screens design system per DF-TE-0008 | captured | ui | medium |
| WI-0023 | TE-022 BLOCKED: wasm-tools workload unavailable via apt - only remaining toolchain gap | captured | blocker | high |
| WI-0024 | TE-023 Duration precision: store milliseconds to match exact_duration_ms contract | complete |  | medium |
| WI-0025 | TE-025 GitHub API adapter and effect interpreter: Git Data API for atomic multi-file split/merge writes | complete |  | medium |
| WI-0026 | TE-026 Project domain type with Active/Archived status and project storage per DF-TE-0007 | complete |  | medium |
| WI-0027 | TE-027 HTTP implementation of the GitHubStore port: Git Data API trees/commits/refs, auth, rate-limit and retry | blocked |  | medium |
| WI-0028 | TE-028 Verify the GitHub transport write path against a scratch branch (needs a write-capable token; agent proxy denies API writes) | captured |  | medium |
| WI-0029 | TE-029 Persist the project/activity catalogue and implement LoadProjects in the interpreter | complete |  | medium |
| WI-0030 | TE-030 Source-generated JSON serialization so the WASM host can be trimmed (reflection-based System.Text.Json blocks IL trimming) | captured |  | medium |
| WI-0031 | TE-031 Kernel command dispatch: browser submits commands, F# decides, effects returned as data | complete |  | medium |
| WI-0032 | TE-032 Kernel dispatch for the remaining transitions: correct, split, merge, restore, attach evidence | complete |  | medium |
| WI-0033 | TE-033 Perform effects: wire TimeEntry.GitHub's interpreter behind the browser so PersistNewEntry/PersistVoid actually reach the repository, with the version token returned to the page | complete |  | medium |
| WI-0034 | TE-034 Resolve OQ-6 (who the actor is on a browser change) — sign-in.html and settings.html exist but no document states where identity comes from; revisions are attributed to the literal 'browser' until it is settled | captured |  | medium |
| WI-0035 | TE-035 Correction, removal and restoration wired on the today screen, with kernel-decided visibility | complete |  | medium |
| WI-0036 | TE-036 Split on the today screen, with the kernel-computed preview TE-R-044 requires | complete |  | medium |
| WI-0037 | TE-037 Merge on the today screen: multi-entry selection and the kernel-computed combined-time preview | complete |  | medium |
| WI-0038 | TE-038 Evidence attachment on the today screen | complete |  | medium |
| WI-0039 | TE-039 Credential port: the browser's token is one implementation, so the mechanism can change (DF-TE-0011) | complete |  | medium |
| WI-0040 | TE-040 Read the ledger and catalogue from the repository on connect | complete |  | medium |
| WI-0041 | TE-041 Make the GitHub API base URL configurable so the browser check need not reach api.github.com | complete |  | medium |
| WI-0042 | TE-042 Conflict review: show the saved entry's values beside the proposed ones (TE-R-071's review step) | complete |  | medium |
| WI-0043 | TE-043 Conflict reconciliation on the today screen, and a stale-bundle guard for the browser harness | complete |  | medium |
| WI-0044 | TE-044 Month view: period summary in Tier 3, wired on the today screen | ready |  | medium |
