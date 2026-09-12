/// The device clock against the repository's (TE-R-008, DF-TE-0017).
///
/// The requirement — "a warning MUST be shown when device time and server
/// time differ materially" — was the one row in the traceability table marked
/// NOT IMPLEMENTED, because two things were unstated: where server time comes
/// from, and what "materially" means. Both are settled by DF-TE-0017, and
/// these tests pin the parts of that decision that could otherwise drift.
module TimeEntry.Tests.ClockTests

open System.Text.Json.Nodes
open Xunit
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.Clock
open TimeEntry.GitHub.Interpreter
open TimeEntry.Persistence
open TimeEntry.Tests.Helpers
open TimeEntry.Tests.FakeStore

// ---------------------------------------------------------------------------
// Tier 1 — the comparison
// ---------------------------------------------------------------------------

[<Fact>]
let ``clocks that agree are not material`` () =
    let comparison = assessDefault (instant 1789000000L) (instant 1789000000L)
    Assert.Equal(0L, comparison.DifferenceMilliseconds)
    Assert.False comparison.IsMaterial

[<Fact>]
let ``the difference keeps its sign, because the two directions differ`` () =
    // A device running ahead can stamp an entry into a day that has not
    // started; one running behind can stamp it into a day already reviewed.
    // Reporting only the magnitude would lose which of those is happening.
    let ahead = assessDefault (instant 1789600000L) (instant 1789000000L)
    let behind = assessDefault (instant 1789000000L) (instant 1789600000L)

    Assert.Equal(600000L, ahead.DifferenceMilliseconds)
    Assert.True ahead.DeviceAhead
    Assert.Equal(-600000L, behind.DifferenceMilliseconds)
    Assert.False behind.DeviceAhead

[<Fact>]
let ``a difference below the tolerance is not material`` () =
    // One second under a six-minute unit.
    let comparison = assessDefault (instant (1789000000L + 359000L)) (instant 1789000000L)
    Assert.False comparison.IsMaterial

[<Fact>]
let ``a difference exactly at the tolerance is not material`` () =
    // The tolerance reads as "up to this much is tolerated". A boundary that
    // refused the value it names would make a stated default of six minutes
    // behave as five minutes fifty-nine.
    let comparison =
        assessDefault (instant (1789000000L + DefaultToleranceMilliseconds)) (instant 1789000000L)

    Assert.False comparison.IsMaterial

[<Fact>]
let ``one millisecond beyond the tolerance is material`` () =
    let comparison =
        assessDefault
            (instant (1789000000L + DefaultToleranceMilliseconds + 1L))
            (instant 1789000000L)

    Assert.True comparison.IsMaterial

[<Fact>]
let ``a device running behind is material at the same magnitude`` () =
    // Symmetry is the property: a clock an hour behind is exactly as wrong as
    // one an hour ahead, and an asymmetric threshold would be a rule nobody
    // stated.
    let comparison =
        assessDefault
            (instant 1789000000L)
            (instant (1789000000L + DefaultToleranceMilliseconds + 1L))

    Assert.True comparison.IsMaterial
    Assert.False comparison.DeviceAhead

[<Fact>]
let ``the tolerance is a parameter, not a constant baked in`` () =
    // No document states a tolerance, so the default is a derived choice and
    // a caller must be able to override it (DF-TE-0017). A test, rather than
    // a comment, is what keeps that true.
    let device = instant (1789000000L + 60000L)
    let server = instant 1789000000L

    Assert.False((assess 120000L device server).IsMaterial)
    Assert.True((assess 30000L device server).IsMaterial)

[<Fact>]
let ``the default tolerance is one billable unit`` () =
    // Not an arbitrary number: it is the smallest quantity this ledger
    // distinguishes, so a disagreement below it cannot change any figure a
    // person sees in a total.
    Assert.Equal(TimeEntry.Semantic.Duration.MillisecondsPerBillableUnit, DefaultToleranceMilliseconds)

// ---------------------------------------------------------------------------
// Tier 4 — where server time comes from
// ---------------------------------------------------------------------------

[<Fact>]
let ``a store nobody has talked to reports no server time`` () =
    // Absence, not agreement. "The repository has not told us the time" and
    // "the clocks match" are different facts, and collapsing them would
    // produce a reassurance nothing checked.
    let fake = Fake([])
    Assert.Equal(None, observedServerTime fake.Store)

[<Fact>]
let ``an observed Date header becomes an Instant at the boundary`` () =
    // The store speaks in header milliseconds and the domain in `Instant`;
    // the conversion happens in the interpreter, the same place a blob SHA
    // becomes a VersionToken (TE-R-094).
    let fake = Fake([])
    fake.ServerSaysItIs 1789000000L
    Assert.Equal(Some(instant 1789000000L), observedServerTime fake.Store)

// ---------------------------------------------------------------------------
// The browser kernel — what the page is told
// ---------------------------------------------------------------------------

let private loadWith (fake: Fake) (deviceNowMs: int64 option) =
    let node = JsonObject()
    let repository = JsonObject()
    repository.Add("owner", JsonValue.Create "owner")
    repository.Add("repo", JsonValue.Create "ledger")
    repository.Add("branch", JsonValue.Create "main")
    node.Add("repository", repository)
    node.Add("token", JsonValue.Create "a-token")
    node.Add("date", JsonValue.Create "2026-09-10")
    deviceNowMs |> Option.iter (fun ms -> node.Add("deviceNowMs", JsonValue.Create ms))

    JsonNode.Parse(
        TimeEntry.Kernel.loadLedgerWith (fun _ _ -> fake.Store) (node.ToJsonString())
        |> Async.RunSynchronously
    )

/// A store holding a readable catalogue, so the load succeeds and gets as far
/// as reporting the clock.
let private loadableStore () =
    let fake =
        Fake(
            [ Layout.CataloguePath,
              Serialization.writeCatalogue (Mapping.catalogueToDocument catalogue) ]
        )

    fake

[<Fact>]
let ``a load with no device clock reports no assessment`` () =
    // A caller that does not say what time it thinks it is gets no
    // assessment, rather than one against zero.
    let fake = loadableStore ()
    fake.ServerSaysItIs 1789000000L
    let answer = loadWith fake None
    Assert.True(answer.["ok"].GetValue<bool>())
    Assert.True(isNull answer.["clock"])

[<Fact>]
let ``a load before the repository has said the time reports no assessment`` () =
    // Cannot happen through the real transport, which sees a `Date` on every
    // response — but the kernel must not depend on that, because a store that
    // answers from a cache would have no header to report.
    let fake = loadableStore ()
    let answer = loadWith fake (Some 1789000000L)
    Assert.True(answer.["ok"].GetValue<bool>())
    Assert.True(isNull answer.["clock"])

[<Fact>]
let ``agreeing clocks are reported with no warning to show`` () =
    let fake = loadableStore ()
    fake.ServerSaysItIs 1789000000L
    let clock = (loadWith fake (Some 1789000000L)).["clock"]

    Assert.False(clock.["isMaterial"].GetValue<bool>())
    // The sentence exists only when there is something to warn about, so a
    // page cannot render reassurance by accident.
    Assert.True(isNull clock.["warning"])

[<Fact>]
let ``a material skew is warned about, in words, with its direction`` () =
    let fake = loadableStore ()
    fake.ServerSaysItIs 1789000000L
    // Two hours ahead.
    let clock = (loadWith fake (Some(1789000000L + 7200000L))).["clock"]

    Assert.True(clock.["isMaterial"].GetValue<bool>())
    Assert.True(clock.["deviceAhead"].GetValue<bool>())
    Assert.Equal("2h 00m", clock.["displayDifference"].GetValue<string>())

    let warning = clock.["warning"].GetValue<string>()
    Assert.Contains("ahead of the repository's by 2h 00m", warning)
    // It warns about which DAY new time will be dated to, and does not claim
    // anything already recorded is wrong: durations were measured by one
    // clock and are internally consistent.
    Assert.Contains("dated to the wrong day", warning)
    Assert.DoesNotContain("invalid", warning)

[<Fact>]
let ``a device running behind is warned about as behind`` () =
    let fake = loadableStore ()
    fake.ServerSaysItIs (1789000000L + 7200000L)
    let clock = (loadWith fake (Some 1789000000L)).["clock"]

    Assert.False(clock.["deviceAhead"].GetValue<bool>())
    Assert.Contains("behind the repository's by 2h 00m", clock.["warning"].GetValue<string>())

[<Fact>]
let ``the tolerance is reported beside the verdict`` () =
    // So a reader can see what "materially" was taken to mean rather than
    // having to trust the boolean.
    let fake = loadableStore ()
    fake.ServerSaysItIs 1789000000L
    let clock = (loadWith fake (Some 1789000000L)).["clock"]
    Assert.Equal(DefaultToleranceMilliseconds, clock.["toleranceMilliseconds"].GetValue<int64>())
