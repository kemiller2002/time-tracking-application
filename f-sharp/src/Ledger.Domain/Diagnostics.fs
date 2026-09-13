namespace Ledger.Domain

/// SDE Tier 1 (Semantic Model): stable failure-reason codes. Pure values —
/// must not know about JSON, WASM, browser, or storage (`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`).
module DiagnosticCode =
    [<Literal>]
    let ActivityTypeRequired = "ACTIVITY_TYPE_REQUIRED"
    [<Literal>]
    let ProjectRequired = "PROJECT_REQUIRED"
    [<Literal>]
    let DescriptionRequired = "DESCRIPTION_REQUIRED"
    [<Literal>]
    let BusinessPurposeRequired = "BUSINESS_PURPOSE_REQUIRED"
    [<Literal>]
    let InvalidTimeRange = "INVALID_TIME_RANGE"
    [<Literal>]
    let CrossesMidnight = "CROSSES_MIDNIGHT"
    [<Literal>]
    let ReconstructionReasonRequired = "RECONSTRUCTION_REASON_REQUIRED"
    [<Literal>]
    let OverlappingActivity = "OVERLAPPING_ACTIVITY"
    [<Literal>]
    let ProjectNotFound = "PROJECT_NOT_FOUND"
    [<Literal>]
    let ProjectInactive = "PROJECT_INACTIVE"
    [<Literal>]
    let ActivityTypeNotFound = "ACTIVITY_TYPE_NOT_FOUND"
    [<Literal>]
    let ActivityTypeInactive = "ACTIVITY_TYPE_INACTIVE"
    [<Literal>]
    let TagNotFound = "TAG_NOT_FOUND"
    [<Literal>]
    let TagInactive = "TAG_INACTIVE"
    [<Literal>]
    let ActivityNotFound = "ACTIVITY_NOT_FOUND"
    [<Literal>]
    let ActivityNotRecorded = "ACTIVITY_NOT_RECORDED"
    [<Literal>]
    let ActivityAlreadyVoided = "ACTIVITY_ALREADY_VOIDED"
    [<Literal>]
    let ActivityNotVoided = "ACTIVITY_NOT_VOIDED"
    [<Literal>]
    let ActivitySuperseded = "ACTIVITY_SUPERSEDED"
    [<Literal>]
    let StaleVersion = "STALE_VERSION"
    [<Literal>]
    let ReasonRequired = "REASON_REQUIRED"
    [<Literal>]
    let StatementRequired = "STATEMENT_REQUIRED"
    [<Literal>]
    let TooShortToSplit = "TOO_SHORT_TO_SPLIT"
    [<Literal>]
    let SplitRequiresTwoParts = "SPLIT_REQUIRES_TWO_PARTS"
    [<Literal>]
    let DurationInvariantFailed = "DURATION_INVARIANT_FAILED"
    [<Literal>]
    let MergeRequiresTwoSources = "MERGE_REQUIRES_TWO_SOURCES"
    [<Literal>]
    let MergeSourcesNotContiguous = "MERGE_SOURCES_NOT_CONTIGUOUS"
    [<Literal>]
    let InvalidEvidenceType = "INVALID_EVIDENCE_TYPE"
    [<Literal>]
    let EvidenceNotFound = "EVIDENCE_NOT_FOUND"
    [<Literal>]
    let TimerAlreadyRunning = "TIMER_ALREADY_RUNNING"
    [<Literal>]
    let TimerNotRunning = "TIMER_NOT_RUNNING"
    [<Literal>]
    let TimerNotPaused = "TIMER_NOT_PAUSED"
    [<Literal>]
    let TimerNotFound = "TIMER_NOT_FOUND"
    [<Literal>]
    let PersistenceInvalidDocument = "PERSISTENCE_INVALID_DOCUMENT"
    [<Literal>]
    let ReferenceItemNameRequired = "REFERENCE_ITEM_NAME_REQUIRED"
    [<Literal>]
    let ReferenceItemNotFound = "REFERENCE_ITEM_NOT_FOUND"

type Diagnostic =
    { Code: string
      Message: string
      Category: string
      SubjectId: string option
      Field: string option
      Context: Map<string, string> }

module Diagnostic =
    let domain code message =
        { Code = code; Message = message; Category = "domain"; SubjectId = None; Field = None; Context = Map.empty }

    let forSubject subjectId diagnostic = { diagnostic with SubjectId = Some subjectId }
    let forField field diagnostic = { diagnostic with Field = Some field }
    let withContext context diagnostic = { diagnostic with Context = context }
    let persistence code message = { domain code message with Category = "persistence" }
