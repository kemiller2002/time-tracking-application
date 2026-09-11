/// Tier 1 — Semantic Model. Time measurement.
///
/// Implements DF-TE-0002: `Duration` (exact elapsed seconds) is authoritative;
/// `BillableUnits` (six-minute units) is a derived projection. Repository
/// authority requires exact elapsed time to be preserved and forbids
/// six-minute controls from replacing it (exec-contract §4, TE-R-001/TE-R-002);
/// the user instruction requires six-minute unit semantics with no fractional
/// units in authoritative state (TE-R-003/TE-R-004). Carrying both satisfies
/// both.
///
/// No floating-point arithmetic appears anywhere in this module: billable time
/// must never be authoritatively computed in floats (TE-R-007).
module TimeEntry.Semantic.Duration

/// Why a supplied duration was refused.
type DurationError =
    /// A recorded entry must cover a positive span of time.
    | DurationNotPositive of seconds: int
    /// Guards against overflow and obviously nonsensical spans.
    | DurationExceedsMaximum of seconds: int * maximumSeconds: int
    /// An interval whose end precedes its start.
    | IntervalEndsBeforeStart

[<Literal>]
let SecondsPerMinute = 60

/// TE-R-003: one billing unit is six minutes.
[<Literal>]
let MinutesPerBillableUnit = 6

[<Literal>]
let SecondsPerBillableUnit = 360 // MinutesPerBillableUnit * SecondsPerMinute

/// TE-R-003: ten units is one hour.
[<Literal>]
let BillableUnitsPerHour = 10

/// A single entry may not exceed 31 days. Chosen to bound arithmetic, not to
/// express a business rule; no repository requirement states a maximum.
[<Literal>]
let MaximumDurationSeconds = 2678400

/// Exact elapsed time, in whole seconds. THE AUTHORITATIVE QUANTITY
/// (TE-R-001). Constructed only through the smart constructors below, so a
/// non-positive or absurd duration cannot be represented (TE-R-096).
type Duration =
    private
    | Duration of seconds: int

    member this.Seconds = let (Duration s) = this in s

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

    /// The only way to build a Duration from seconds.
    let ofSeconds (seconds: int) : Result<Duration, DurationError> =
        if seconds <= 0 then Error(DurationNotPositive seconds)
        elif seconds > MaximumDurationSeconds then
            Error(DurationExceedsMaximum(seconds, MaximumDurationSeconds))
        else
            Ok(Duration seconds)

    let ofMinutes (minutes: int) : Result<Duration, DurationError> =
        // Guard before multiplying so an overflowed product can never be
        // mistaken for a valid small duration.
        if minutes <= 0 then Error(DurationNotPositive(minutes * SecondsPerMinute))
        elif minutes > MaximumDurationSeconds / SecondsPerMinute then
            Error(DurationExceedsMaximum(minutes * SecondsPerMinute, MaximumDurationSeconds))
        else
            ofSeconds (minutes * SecondsPerMinute)

    /// TE-R-005: elapsed = end - start - paused. Callers supply epoch seconds;
    /// this module never reads a clock (Tier 1 performs no effects).
    let ofInterval (startEpochSeconds: int64) (endEpochSeconds: int64) (pausedSeconds: int) =
        if endEpochSeconds < startEpochSeconds then Error IntervalEndsBeforeStart
        elif pausedSeconds < 0 then Error(DurationNotPositive pausedSeconds)
        else
            let span = endEpochSeconds - startEpochSeconds - int64 pausedSeconds

            if span <= 0L then Error(DurationNotPositive(int span))
            elif span > int64 MaximumDurationSeconds then
                Error(DurationExceedsMaximum(MaximumDurationSeconds, MaximumDurationSeconds))
            else
                ofSeconds (int span)

    let seconds (Duration s) = s

    /// Total of many durations. Used for day and month totals, which are
    /// therefore exact integer sums (TE-R-007).
    let sum (durations: Duration list) : int =
        durations |> List.sumBy seconds

    /// Whether a list of parts exactly reproduces a whole. The split invariant
    /// (TE-R-040) is enforced here, on the authoritative quantity.
    let partsPreserve (whole: Duration) (parts: Duration list) : bool =
        not parts.IsEmpty && sum parts = seconds whole

module BillableUnits =

    /// Project exact elapsed time onto six-minute units under an explicit
    /// policy. Integer arithmetic only.
    let ofDuration (policy: RoundingPolicy) (duration: Duration) : BillableUnits =
        let total = Duration.seconds duration
        let whole = total / SecondsPerBillableUnit
        let remainder = total % SecondsPerBillableUnit

        let units =
            match policy with
            | RoundDown -> whole
            | RoundUp -> if remainder > 0 then whole + 1 else whole
            | RoundNearest ->
                if remainder * 2 >= SecondsPerBillableUnit then
                    whole + 1
                else
                    whole

        BillableUnits units

    let units (BillableUnits u) = u

    /// TE-R-085: presentation-boundary conversion. Returns (hours, minutes);
    /// e.g. 23 units -> (2, 18).
    let toHoursAndMinutes (BillableUnits u) : int * int =
        let totalMinutes = u * MinutesPerBillableUnit
        totalMinutes / 60, totalMinutes % 60
