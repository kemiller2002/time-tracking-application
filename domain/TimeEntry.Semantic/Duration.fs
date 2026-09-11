/// Tier 1 — Semantic Model. Time measurement.
///
/// Implements DF-TE-0002 (exact elapsed time is authoritative; six-minute
/// units are a derived projection) and DF-TE-0009 (the unit of storage is
/// milliseconds, matching the `exact_duration_ms` contract the repository's
/// existing persistence layer already established).
///
/// No floating-point arithmetic appears anywhere in this module: billable time
/// must never be authoritatively computed in floats (TE-R-007). Milliseconds
/// are held as `int64` because 31 days in milliseconds exceeds `Int32.MaxValue`.
module TimeEntry.Semantic.Duration

/// Why a supplied duration was refused.
type DurationError =
    /// A recorded entry must cover a positive span of time.
    | DurationNotPositive of milliseconds: int64
    /// Guards against overflow and obviously nonsensical spans.
    | DurationExceedsMaximum of milliseconds: int64 * maximumMilliseconds: int64
    /// An interval whose end precedes its start.
    | IntervalEndsBeforeStart

[<Literal>]
let MillisecondsPerSecond = 1000L

[<Literal>]
let SecondsPerMinute = 60

/// TE-R-003: one billing unit is six minutes.
[<Literal>]
let MinutesPerBillableUnit = 6

/// 6 * 60 * 1000.
[<Literal>]
let MillisecondsPerBillableUnit = 360000L

/// TE-R-003: ten units is one hour.
[<Literal>]
let BillableUnitsPerHour = 10

/// A single entry may not exceed 31 days. Chosen to bound arithmetic, not to
/// express a business rule; no repository requirement states a maximum.
[<Literal>]
let MaximumDurationMilliseconds = 2678400000L

/// Exact elapsed time, in whole milliseconds. THE AUTHORITATIVE QUANTITY
/// (TE-R-001, DF-TE-0009). Constructed only through the smart constructors
/// below, so a non-positive or absurd duration cannot be represented
/// (TE-R-096).
type Duration =
    private
    | Duration of milliseconds: int64

    member this.Milliseconds = let (Duration ms) = this in ms

/// Six-minute billing units. A DERIVED PROJECTION of `Duration`, never a
/// store of what happened (DF-TE-0002). Because it is only ever produced by
/// projecting a `Duration` through a total rounding function, a fractional
/// unit is unrepresentable (TE-R-004).
type BillableUnits =
    private
    | BillableUnits of units: int

    member this.Units = let (BillableUnits u) = this in u

/// How exact elapsed time is projected onto six-minute units.
///
/// The policy is explicit rather than implicit in a formatter because
/// repository authority does not state one (DF-TE-0002, "Revisit when"). The
/// split invariant holds under any of these because it is enforced on
/// `Duration`, not on units.
type RoundingPolicy =
    /// Always round up to the next started unit. Conventional for billing.
    | RoundUp
    /// Round to the nearest unit; exact halves round up.
    | RoundNearest
    /// Discard any partial unit.
    | RoundDown

module Duration =

    /// The only way to build a Duration from milliseconds.
    let ofMilliseconds (milliseconds: int64) : Result<Duration, DurationError> =
        if milliseconds <= 0L then Error(DurationNotPositive milliseconds)
        elif milliseconds > MaximumDurationMilliseconds then
            Error(DurationExceedsMaximum(milliseconds, MaximumDurationMilliseconds))
        else
            Ok(Duration milliseconds)

    let ofSeconds (seconds: int) : Result<Duration, DurationError> =
        // Widen before multiplying so an overflowed product can never be
        // mistaken for a valid small duration.
        ofMilliseconds (int64 seconds * MillisecondsPerSecond)

    let ofMinutes (minutes: int) : Result<Duration, DurationError> =
        ofMilliseconds (int64 minutes * int64 SecondsPerMinute * MillisecondsPerSecond)

    /// TE-R-005: elapsed = end - start - paused, in epoch milliseconds.
    /// Callers supply the instants; this module never reads a clock (Tier 1
    /// performs no effects). Epoch milliseconds match `Date.now()` and the
    /// existing `started_at`/`paused_ms` arithmetic.
    let ofInterval
        (startEpochMilliseconds: int64)
        (endEpochMilliseconds: int64)
        (pausedMilliseconds: int64)
        : Result<Duration, DurationError> =
        if endEpochMilliseconds < startEpochMilliseconds then Error IntervalEndsBeforeStart
        elif pausedMilliseconds < 0L then Error(DurationNotPositive pausedMilliseconds)
        else
            ofMilliseconds (endEpochMilliseconds - startEpochMilliseconds - pausedMilliseconds)

    /// The authoritative accessor.
    let milliseconds (Duration ms) = ms

    /// Derived whole seconds. **Lossy** — truncates any sub-second remainder.
    /// Present for display and for comparison with second-granularity data;
    /// never use it to check an invariant (DF-TE-0009).
    let seconds (Duration ms) = int (ms / MillisecondsPerSecond)

    /// Total of many durations, in milliseconds. Used for day and month
    /// totals, which are therefore exact integer sums (TE-R-007).
    let sum (durations: Duration list) : int64 =
        durations |> List.sumBy milliseconds

    /// Whether a list of parts exactly reproduces a whole. The split invariant
    /// (TE-R-040) is enforced here, on the authoritative quantity, in
    /// milliseconds — checking truncated seconds would accept a split that
    /// loses sub-second time (DF-TE-0009).
    let partsPreserve (whole: Duration) (parts: Duration list) : bool =
        not parts.IsEmpty && sum parts = milliseconds whole

module BillableUnits =

    /// Project exact elapsed time onto six-minute units under an explicit
    /// policy. Integer arithmetic only.
    let ofDuration (policy: RoundingPolicy) (duration: Duration) : BillableUnits =
        let total = Duration.milliseconds duration
        let whole = total / MillisecondsPerBillableUnit
        let remainder = total % MillisecondsPerBillableUnit

        let units =
            match policy with
            | RoundDown -> whole
            | RoundUp -> if remainder > 0L then whole + 1L else whole
            | RoundNearest ->
                if remainder * 2L >= MillisecondsPerBillableUnit then
                    whole + 1L
                else
                    whole

        BillableUnits(int units)

    let units (BillableUnits u) = u

    /// TE-R-085: presentation-boundary conversion. Returns (hours, minutes);
    /// e.g. 23 units -> (2, 18).
    let toHoursAndMinutes (BillableUnits u) : int * int =
        let totalMinutes = u * MinutesPerBillableUnit
        totalMinutes / 60, totalMinutes % 60
