/// Every typed outcome, in the words a person reads.
///
/// This module exists for two reasons that turn out to be the same reason.
///
/// **TE-R-053.** The main UI must not use technical language. Until now a
/// refused command reached the user as `sprintf "%A" rejection`, which renders
/// an F# union literally:
///
///     CatalogueRejected (ProjectIsArchived ProjectId "retired-client")
///
/// That is the same objection that made `Voided` read as "Removed from totals"
/// in the history view — it was simply less visible, because a rejection only
/// appears when something goes wrong.
///
/// **Trimming.** `%A` is implemented by `FSharp.Core`'s `sformat.fs`, which
/// calls `Type.InvokeMember`, and `printf.fs`, which calls
/// `MakeGenericMethod`. Neither can be statically analysed, so while any
/// user-visible string depends on `%A` the WebAssembly bundle cannot be
/// trimmed — and trimming it anyway would degrade those messages at run time,
/// in the browser and nowhere else.
///
/// So: every case is matched and worded here. The compiler enforces that a
/// new case gets wording, because these matches are exhaustive.
///
/// ## What the wording is for
///
/// A person reading a refusal needs to know what happened and what to do
/// about it. Where a case carries a value that helps with the second part —
/// which project, how much time, which day — it is included. Where it does
/// not, the sentence stays short rather than padded with identifiers that
/// mean nothing outside the code.
module TimeEntry.Browser.Wording

open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Catalogue
open TimeEntry.Semantic.EntryState
open TimeEntry.Semantic.Capabilities
open TimeEntry.Transitions.Effects
open TimeEntry.GitHub.Store
open TimeEntry.Persistence.Documents
open TimeEntry.Persistence.Serialization

/// Exact elapsed time, written out. Duplicated from `Kernel.displayExact`
/// rather than shared, because this module must not depend on the kernel that
/// depends on it. Both are three lines of integer arithmetic over the same
/// constants, and a test asserts they agree.
let private exactTime (milliseconds: int64) =
    let sign = if milliseconds < 0L then "-" else ""
    let magnitude = abs milliseconds
    let totalMinutes = magnitude / (MillisecondsPerSecond * int64 SecondsPerMinute)
    let hours = totalMinutes / 60L
    let minutes = totalMinutes % 60L

    if hours = 0L then
        sprintf "%s%dm" sign minutes
    else
        sprintf "%s%dh %02dm" sign hours minutes

// ---------------------------------------------------------------------------
// Machine names
// ---------------------------------------------------------------------------

/// The stable name a capability is known by on the wire.
///
/// NOT wording: the page keys on these to decide which controls to offer, so
/// they must not change when the prose does. Written out rather than taken
/// from `%A` for exactly that reason — under `%A` the wire contract would be
/// whatever the union case happened to be called.
let capabilityName (capability: EntryCapability) =
    match capability with
    | CanCorrect -> "CanCorrect"
    | CanSplit -> "CanSplit"
    | CanVoid -> "CanVoid"
    | CanRestore -> "CanRestore"
    | CanMerge -> "CanMerge"
    | CanAttachEvidence -> "CanAttachEvidence"

/// The same capability, as a person would name the action.
let capabilityAction (capability: EntryCapability) =
    match capability with
    | CanCorrect -> "correct this entry"
    | CanSplit -> "split this entry"
    | CanVoid -> "remove this entry from totals"
    | CanRestore -> "return this entry to totals"
    | CanMerge -> "merge this entry"
    | CanAttachEvidence -> "attach evidence to this entry"

// ---------------------------------------------------------------------------
// Domain values
// ---------------------------------------------------------------------------

let identifierError (error: IdentifierError) =
    match error with
    | IdentifierEmpty -> "an identifier is required"
    | IdentifierTooLong maxLength -> sprintf "an identifier may be at most %d characters" maxLength
    | IdentifierMalformed reason -> sprintf "that identifier is not valid: %s" reason

let textError (error: TextError) =
    match error with
    | TextEmpty -> "some text is required here"
    | TextTooLong(length, maxLength) ->
        sprintf "that text is %d characters; the limit is %d" length maxLength

let durationError (error: DurationError) =
    match error with
    | DurationNotPositive _ -> "a recorded entry must cover more than no time at all"
    | DurationExceedsMaximum(_, maximumMilliseconds) ->
        sprintf "a single entry may not exceed %s" (exactTime maximumMilliseconds)
    | IntervalEndsBeforeStart -> "that period ends before it starts"

let dateError (error: DateError) =
    match error with
    | DateOutOfRange _ -> "that is not a date this ledger can record"

/// Complete sentences, ending in a full stop.
///
/// `rejection` used to append one, which gave "…still count.." on the
/// archived cases — they are two sentences already. Each message owning its
/// own punctuation is the fix that cannot recur.
let catalogueError (error: CatalogueError) =
    match error with
    | ProjectNotInCatalogue projectId ->
        sprintf "There is no project called %s." (ProjectId.value projectId)
    | ProjectIsArchived projectId ->
        // DF-TE-0007's consequence, stated so the reader knows their existing
        // entries are safe — the common worry on seeing this.
        sprintf
            "%s is archived, so it cannot take new time. Entries already recorded against it still count."
            (ProjectId.value projectId)
    | ActivityTypeNotInCatalogue activityTypeId ->
        sprintf "There is no activity type called %s." (ActivityTypeId.value activityTypeId)
    | ActivityTypeIsArchived activityTypeId ->
        sprintf
            "%s is archived, so it cannot take new time. Entries already recorded against it still count."
            (ActivityTypeId.value activityTypeId)

let private supersessionCause (cause: SupersessionCause) =
    match cause with
    | SupersededBySplit children -> sprintf "it was split into %d records" (List.length children)
    | SupersededByMerge _ -> "it was merged into another record"

let capabilityDenial (denial: CapabilityDenial) =
    match denial with
    | AlreadyVoid -> "it has already been removed from totals"
    | AlreadySuperseded cause -> supersessionCause cause
    | NotVoid -> "it has not been removed, so there is nothing to return"
    | BlockedByOpenQuestion questionId ->
        sprintf "how this should behave is not yet decided (%s)" questionId

// ---------------------------------------------------------------------------
// Rejections
// ---------------------------------------------------------------------------

/// Why a command was refused, for the person who made it.
let rejection (rejection: Rejection) =
    match rejection with
    | NotPermittedInState(attempted, denial) ->
        sprintf "You cannot %s: %s." (capabilityAction attempted) (capabilityDenial denial)

    | VersionConflict(_, actual) ->
        match actual with
        | Some _ ->
            "This entry changed somewhere else after you opened it. Nothing was saved."
        | None ->
            // No stored version at all: the entry was never persisted, or the
            // page never learned its version. Different remedy, so different
            // words.
            "This entry has not been saved yet, so there is no version to change. Nothing was saved."

    | SplitDoesNotPreserveTotal(sourceMilliseconds, childMilliseconds) ->
        let difference = sourceMilliseconds - childMilliseconds

        if difference > 0L then
            sprintf "The parts add up to %s, which leaves %s unaccounted for." (exactTime childMilliseconds) (exactTime difference)
        else
            sprintf "The parts add up to %s, which is %s more than the entry holds." (exactTime childMilliseconds) (exactTime (abs difference))

    | SplitNeedsAtLeastTwoChildren supplied ->
        sprintf "A split needs at least two parts; %d was given." supplied

    | SplitChildIdentityNotUnique duplicated ->
        sprintf "Two parts were given the same identity (%s)." (EntryId.value duplicated)

    | EntryNotLoaded entryId ->
        sprintf "The entry %s is not loaded, so nothing could be done to it." (EntryId.value entryId)

    | MergeNeedsAtLeastTwoSources supplied ->
        sprintf "A merge needs at least two entries; %d was given." supplied

    | MergeSourceIdentityNotUnique duplicated ->
        sprintf "The same entry was given twice (%s)." (EntryId.value duplicated)

    | MergeSpansMultipleDays days ->
        // The reason is stated, not just the refusal: this one surprises
        // people, and "the ledger is day-oriented" is the whole of it.
        sprintf
            "These entries fall on %d different days. Merging them would move time between days and change both days' totals."
            days

    | MergeDurationOutOfRange totalMilliseconds ->
        sprintf "Merging these would come to %s, which is more than one entry can hold." (exactTime totalMilliseconds)

    | CatalogueRejected error -> catalogueError error

    | TransitionUndefined questionId ->
        sprintf "How this should behave is not yet decided (%s), so it has not been guessed at." questionId

// ---------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------

let storeError (error: StoreError) =
    match error with
    | PreconditionFailed(path, _, _) ->
        sprintf "%s changed in the repository after it was read. Nothing was saved." path
    | HeadMoved _ ->
        "The repository changed while this was being saved. Nothing was saved."
    | NotFound path -> sprintf "The repository has no %s." path
    | Unauthorized detail -> sprintf "The repository refused the request: %s" detail
    | CredentialMissing detail -> detail
    | RateLimited(Some seconds) ->
        sprintf "The repository is rate limiting requests. Try again in %d seconds." seconds
    | RateLimited None -> "The repository is rate limiting requests. Try again shortly."
    | TransportFailure detail -> sprintf "The repository could not be reached: %s" detail
    | UnexpectedResponse(status, detail) ->
        sprintf "The repository answered unexpectedly (%d): %s" status detail

let documentError (error: DocumentError) =
    match error with
    | UnsupportedSchemaVersion(found, supported) ->
        sprintf "this record is version %s; this application reads %s" found supported
    | MissingField path -> sprintf "the stored record is missing %s" path
    | InvalidField(path, detail) -> sprintf "the stored record's %s is not valid: %s" path detail
    | UnknownStateKind found -> sprintf "the stored record has an unknown state (%s)" found
    | UnknownChangeKind found -> sprintf "the stored record has an unknown change (%s)" found
    | EmptyHistory -> "the stored record has no history, and every record has at least one entry"
    | StateFieldsInconsistent(stateKind, detail) ->
        sprintf "the stored record says %s but %s" stateKind detail

let decodeError (error: DecodeError) =
    match error with
    | MalformedJson detail -> sprintf "the stored file is not readable: %s" detail
    | EmptyContent -> "the stored file is empty"

// ---------------------------------------------------------------------------
// The generic seam
// ---------------------------------------------------------------------------

/// Word an error whose type is not known at the call site.
///
/// `CommandParsing` threads every smart-constructor failure through one
/// `Result.mapError`, and those constructors return five different error
/// types. Typing that seam would mean a wording function argument on forty
/// call sites; a type test on the boxed value costs nothing at run time, is
/// trim-safe — a type test reads no members — and keeps the call sites as
/// they are.
///
/// The fallback is a plain sentence rather than a dump. If a new error type
/// appears and is not listed here, a person sees "that value was refused"
/// instead of the name of an F# union — the wrong message, but not a leak of
/// the implementation into the interface.
let ofError (error: obj) : string =
    match error with
    | :? IdentifierError as e -> identifierError e
    | :? TextError as e -> textError e
    | :? DurationError as e -> durationError e
    | :? DateError as e -> dateError e
    | :? CatalogueError as e -> catalogueError e
    | :? DocumentError as e -> documentError e
    | :? DecodeError as e -> decodeError e
    | :? StoreError as e -> storeError e
    | :? Rejection as e -> rejection e
    | _ -> "that value was refused"
