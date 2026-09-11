/// Verifies DF-TE-0002: exact duration is authoritative, billable units are a
/// derived projection, and no authoritative arithmetic is floating point.
module TimeEntry.Tests.DurationTests

open Xunit
open TimeEntry.Semantic.Duration
open TimeEntry.Tests.Helpers

// --- constructor guards: illegal durations are unrepresentable -------------

[<Theory>]
[<InlineData(0)>]
[<InlineData(-1)>]
[<InlineData(-3600)>]
let ``a non-positive duration is refused`` (value: int) =
    match Duration.ofSeconds value with
    // The error reports milliseconds, the authoritative unit (DF-TE-0009).
    | Error(DurationNotPositive actual) -> Assert.Equal(int64 value * MillisecondsPerSecond, actual)
    | other -> failwithf "expected DurationNotPositive, got %A" other

[<Fact>]
let ``a duration beyond the maximum is refused`` () =
    match Duration.ofMilliseconds (MaximumDurationMilliseconds + 1L) with
    | Error(DurationExceedsMaximum _) -> ()
    | other -> failwithf "expected DurationExceedsMaximum, got %A" other

[<Fact>]
let ``ofMinutes rejects a minute count that would overflow before multiplying`` () =
    // Guards before the multiply, so an overflowed product can never be
    // mistaken for a valid small duration.
    match Duration.ofMinutes (int (MaximumDurationMilliseconds / 60000L) + 1) with
    | Error(DurationExceedsMaximum _) -> ()
    | other -> failwithf "expected DurationExceedsMaximum, got %A" other

// --- TE-R-005: elapsed = end - start - paused -----------------------------

[<Fact>]
let ``elapsed time subtracts paused intervals`` () =
    // Epoch milliseconds: 5_000_000 - 1_000_000 - 400_000 = 3_600_000 ms.
    let duration = Duration.ofInterval 1000000L 5000000L 400000L |> expect
    Assert.Equal(3600000L, Duration.milliseconds duration)
    Assert.Equal(3600, Duration.seconds duration)

[<Fact>]
let ``an interval ending before it starts is refused`` () =
    match Duration.ofInterval 5000L 1000L 0L with
    | Error IntervalEndsBeforeStart -> ()
    | other -> failwithf "expected IntervalEndsBeforeStart, got %A" other

[<Fact>]
let ``an interval fully consumed by pauses is refused`` () =
    match Duration.ofInterval 1000L 2000L 1000L with
    | Error(DurationNotPositive _) -> ()
    | other -> failwithf "expected DurationNotPositive, got %A" other

// --- TE-R-002/TE-R-003: units are a projection, six minutes each ----------

[<Fact>]
let ``one unit is six minutes and ten units is one hour`` () =
    Assert.Equal(6, MinutesPerBillableUnit)
    Assert.Equal(10, BillableUnitsPerHour)

    let oneHour = minutes 60
    let units = BillableUnits.ofDuration RoundDown oneHour
    Assert.Equal(10, BillableUnits.units units)

[<Theory>]
// exact multiples project identically under every policy
[<InlineData(360, 1)>]
[<InlineData(3600, 10)>]
let ``exact unit multiples are policy independent`` (secondsValue: int, expectedUnits: int) =
    let duration = seconds secondsValue

    for policy in [ RoundUp; RoundDown; RoundNearest ] do
        Assert.Equal(expectedUnits, BillableUnits.units (BillableUnits.ofDuration policy duration))

[<Theory>]
[<InlineData(1, 1)>] // any started unit rounds up
[<InlineData(359, 1)>]
[<InlineData(361, 2)>]
let ``RoundUp charges any started unit`` (secondsValue: int, expectedUnits: int) =
    let units = BillableUnits.ofDuration RoundUp (seconds secondsValue)
    Assert.Equal(expectedUnits, BillableUnits.units units)

[<Theory>]
[<InlineData(359, 0)>]
[<InlineData(719, 1)>]
let ``RoundDown discards a partial unit`` (secondsValue: int, expectedUnits: int) =
    let units = BillableUnits.ofDuration RoundDown (seconds secondsValue)
    Assert.Equal(expectedUnits, BillableUnits.units units)

[<Theory>]
[<InlineData(179, 0)>] // just under half a unit
[<InlineData(180, 1)>] // exactly half rounds up
[<InlineData(181, 1)>]
let ``RoundNearest rounds exact halves up`` (secondsValue: int, expectedUnits: int) =
    let units = BillableUnits.ofDuration RoundNearest (seconds secondsValue)
    Assert.Equal(expectedUnits, BillableUnits.units units)

// --- TE-R-001: projection never mutates the authoritative quantity --------

[<Fact>]
let ``projecting to units leaves the exact duration intact`` () =
    // 52 minutes is not a whole number of units; rounding must not write back.
    let duration = minutes 52
    BillableUnits.ofDuration RoundUp duration |> ignore
    BillableUnits.ofDuration RoundDown duration |> ignore
    Assert.Equal(3120, Duration.seconds duration)

// --- TE-R-085: presentation-boundary conversion ---------------------------

[<Fact>]
let ``twenty three units display as two hours eighteen minutes`` () =
    // The worked example from the execution instruction.
    let duration = millis (23L * MillisecondsPerBillableUnit)
    let units = BillableUnits.ofDuration RoundDown duration
    Assert.Equal(23, BillableUnits.units units)
    Assert.Equal((2, 18), BillableUnits.toHoursAndMinutes units)

// --- TE-R-040: the split invariant, on milliseconds -----------------------

[<Fact>]
let ``parts preserve a whole when their milliseconds sum exactly`` () =
    Assert.True(Duration.partsPreserve (minutes 60) [ minutes 36; minutes 24 ])

[<Fact>]
let ``under-allocated parts do not preserve the whole`` () =
    Assert.False(Duration.partsPreserve (minutes 60) [ minutes 36; minutes 23 ])

[<Fact>]
let ``over-allocated parts do not preserve the whole`` () =
    Assert.False(Duration.partsPreserve (minutes 60) [ minutes 36; minutes 25 ])

[<Fact>]
let ``an empty part list never preserves a whole`` () =
    Assert.False(Duration.partsPreserve (minutes 60) [])

[<Fact>]
let ``parts preserving rounded units but losing seconds are refused`` () =
    // Both sides are 1 unit under RoundUp, but the seconds differ. Enforcing
    // the invariant on units instead of seconds would wrongly accept this,
    // losing 60 real seconds (TE-R-001). This is the adversarial case for
    // DF-TE-0002.
    let source = seconds 300
    let parts = [ seconds 120; seconds 120 ]

    Assert.Equal(
        BillableUnits.units (BillableUnits.ofDuration RoundUp source),
        BillableUnits.units (BillableUnits.ofDuration RoundUp (seconds 240))
    )

    Assert.False(Duration.partsPreserve source parts)

[<Fact>]
let ``totals are an exact integer sum`` () =
    // Ten three-minute entries are exactly thirty minutes. Summing per-entry
    // rounded-up units would give ten units (one hour) instead of five.
    let durations = List.replicate 10 (minutes 3)
    Assert.Equal(1800000L, Duration.sum durations)

    let total = millis (Duration.sum durations)
    Assert.Equal(5, BillableUnits.units (BillableUnits.ofDuration RoundUp total))

// --- DF-TE-0009: sub-second precision survives ----------------------------

[<Fact>]
let ``sub-second time is preserved rather than truncated`` () =
    // The existing persistence contract records exact_duration_ms, so a
    // 1500 ms entry is real recorded time. Storing whole seconds would
    // silently discard 500 ms of it (TE-R-001).
    let duration = millis 1500L
    Assert.Equal(1500L, Duration.milliseconds duration)
    // The lossy accessor is lossy, which is why it must never guard an
    // invariant.
    Assert.Equal(1, Duration.seconds duration)

[<Fact>]
let ``a sub-second split is possible and exact`` () =
    // Under whole-second storage both children truncate to 0 ms, which the
    // constructor rejects, making a legitimate split of real recorded time
    // impossible. In milliseconds it simply works.
    let source = millis 1500L
    let parts = [ millis 750L; millis 750L ]
    Assert.True(Duration.partsPreserve source parts)

[<Fact>]
let ``a split losing a single millisecond is refused`` () =
    Assert.False(Duration.partsPreserve (millis 1500L) [ millis 750L; millis 749L ])
