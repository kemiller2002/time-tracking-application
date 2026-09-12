/// Tier 1 — Semantic Model. Domain values other than time and identity.
module TimeEntry.Semantic.Values

open System

type TextError =
    | TextEmpty
    | TextTooLong of length: int * maxLength: int

/// Free text describing work performed (TE-R-027).
///
/// Per DF-TE-0005 a description is optional at creation and required for
/// attestation, so requiredness lives at the use site (`Description option`)
/// rather than in this type. The constraints here are the minimum the evidence
/// supports: reject whitespace-only text, and bound length to prevent
/// unbounded payloads. No repository requirement states a length, so 4000 is
/// an engineering bound, not a rule.
type Description =
    private
    | Description of string

    member this.Value = let (Description v) = this in v

[<Literal>]
let MaxDescriptionLength = 4000

module Description =
    let create (raw: string) : Result<Description, TextError> =
        if String.IsNullOrWhiteSpace raw then Error TextEmpty
        elif raw.Length > MaxDescriptionLength then
            Error(TextTooLong(raw.Length, MaxDescriptionLength))
        else
            Ok(Description(raw.Trim()))

    let value (Description v) = v

/// A stated reason for a state change. Mandatory for correction (TE-R-051),
/// void (TE-R-061), restore (TE-R-062), and merge (TE-R-026).
///
/// This is a distinct type from `Description` precisely so a transition cannot
/// accept a work description where an audit reason is required (TE-R-096).
type Reason =
    private
    | Reason of string

    member this.Value = let (Reason v) = this in v

[<Literal>]
let MaxReasonLength = 1000

module Reason =
    let create (raw: string) : Result<Reason, TextError> =
        if String.IsNullOrWhiteSpace raw then Error TextEmpty
        elif raw.Length > MaxReasonLength then Error(TextTooLong(raw.Length, MaxReasonLength))
        else Ok(Reason(raw.Trim()))

    let value (Reason v) = v

/// The calendar day an entry is recorded against, as a proleptic Gregorian
/// ordinal. Deliberately not `DateTime`: a ledger day carries no time zone or
/// time-of-day, and permitting one invites the class of bug where an entry
/// silently moves between days.
type EntryDate =
    private
    | EntryDate of dayNumber: int

    member this.DayNumber = let (EntryDate d) = this in d

type DateError =
    | DateOutOfRange of year: int

module EntryDate =
    let ofYearMonthDay (year: int) (month: int) (day: int) : Result<EntryDate, DateError> =
        if year < 1 || year > 9999 then Error(DateOutOfRange year)
        elif month < 1 || month > 12 then Error(DateOutOfRange year)
        elif day < 1 || day > DateTime.DaysInMonth(year, month) then Error(DateOutOfRange year)
        else
            // Bound to a local first: accessing a member on a freshly
            // constructed struct triggers FS0052 (implicit copy), which is an
            // error here because warnings are errors.
            let date = DateTime(year, month, day)
            Ok(EntryDate(int (date.Ticks / TimeSpan.TicksPerDay)))

    let dayNumber (EntryDate d) = d

    /// Rebuild a date from a previously persisted day number. Guards the range
    /// so a corrupt record cannot produce an out-of-range date.
    let ofDayNumber (day: int) : Result<EntryDate, DateError> =
        if day < 0 || day > 3652058 then Error(DateOutOfRange day) else Ok(EntryDate day)

    /// The inverse of `ofYearMonthDay`, so a date can be persisted in a
    /// human-readable form and read back exactly. The ledger's files are meant
    /// to be reviewable in GitHub, so the stored form is a calendar date
    /// rather than an opaque integer.
    let toYearMonthDay (EntryDate d) : int * int * int =
        let date = DateTime(int64 d * TimeSpan.TicksPerDay)
        date.Year, date.Month, date.Day

    let compare (a: EntryDate) (b: EntryDate) = compare (dayNumber a) (dayNumber b)

/// An instant, as epoch **milliseconds**. Tier 1 never reads a clock; instants
/// are always supplied by a caller that has one (Tier 3/4).
///
/// Milliseconds for the same reason as `Duration` (DF-TE-0009): the browser and
/// the existing persistence layer both work in `Date.now()` milliseconds, and a
/// seconds/milliseconds mismatch at the boundary is exactly the silent-defect
/// class that decision exists to remove.
type Instant =
    private
    | Instant of epochMilliseconds: int64

    member this.EpochMilliseconds = let (Instant ms) = this in ms

module Instant =
    let ofEpochMilliseconds (ms: int64) = Instant ms
    let epochMilliseconds (Instant ms) = ms

/// Opaque external concurrency evidence (TE-R-073).
///
/// In the GitHub-backed deployment this carries a blob or commit SHA, but Tier
/// 1 must not know that (TE-R-094/TE-R-095): the domain only needs to compare
/// tokens for equality and refuse a write whose expected token no longer
/// matches. Keeping it opaque is the anti-corruption boundary.
type VersionToken =
    private
    | VersionToken of string

    member this.Value = let (VersionToken v) = this in v

module VersionToken =
    let create (raw: string) : Result<VersionToken, TextError> =
        if String.IsNullOrWhiteSpace raw then Error TextEmpty else Ok(VersionToken(raw.Trim()))

    let value (VersionToken v) = v

    let matches (expected: VersionToken) (actual: VersionToken) = expected = actual

/// A link or attachment supporting an entry (TE-R-033, system-prompt §8.12).
type EvidenceRef =
    { Uri: string
      Label: Description option
      AttachedAt: Instant }

/// Why a manual entry was recorded rather than timed (system-prompt §8.4,
/// "reason for manual entry").
type EntryOrigin =
    | Timed
    | Manual of reason: Reason
