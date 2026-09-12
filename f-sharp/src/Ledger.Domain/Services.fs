namespace Ledger.Domain

open System.Threading

/// A future extension point, not implemented here: this app's actual
/// persistence (in scope for this port) goes through the Engine layer's
/// simpler, synchronous Storage-effect mechanism against `localStorage`. This
/// port exists so a later real backend (a GitHub-backed ledger, per
/// docs/DOMAIN-REQUIREMENTS.md's persistence contract) has an obvious place to
/// land as a new adapter, without touching domain code.
type LoadedDocument = { SerializedDocument: string; PersistenceVersion: string }

type ReadResult<'value> =
    | Found of 'value
    | NotFound
    | KnownFailure of Diagnostic list
    | InvalidStoredDocument of Diagnostic list

type WriteResult =
    | ConfirmedSuccess of newPersistenceVersion: string
    | ConfirmedFailure of Diagnostic list
    | PersistenceConflict of currentPersistenceVersion: string * diagnostics: Diagnostic list
    | OutcomeUnknown of semanticOperationId: string

type ReconciliationStatus =
    | Applied
    | NotApplied
    | StillUnknown
    | ReconciliationConflict

type ReconciliationResult =
    { Status: ReconciliationStatus
      PersistenceVersion: string option
      Diagnostics: Diagnostic list }

type LedgerStore =
    { LoadDocument: CancellationToken -> Async<ReadResult<LoadedDocument>>
      SaveDocument: string -> string -> string -> CancellationToken -> Async<WriteResult>
      ReconcileWrite: string -> CancellationToken -> Async<ReconciliationResult> }
