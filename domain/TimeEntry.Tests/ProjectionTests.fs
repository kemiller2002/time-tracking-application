/// Verifies that projection — filtering, sorting, search, totals — is owned by
/// F# and is deterministic (TE-R-080, TE-R-081).
module TimeEntry.Tests.ProjectionTests

open Xunit
open TimeEntry.Semantic.Identifiers
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Values
open TimeEntry.Semantic.EntryState
open TimeEntry.Projection.Query
open TimeEntry.Projection.Projection
open TimeEntry.Tests.Helpers

let private noExtraObligations (_: TimeEntry) = []

let private run query entries =
    Projection.project RoundUp noExtraObligations query entries

let private dayQuery = EntryQuery.forDay defaultDate

[<Fact>]
let ``totals exclude voided and superseded entries`` () =
    let active = persistedEntry "e1" (minutes 30) "sha-1"

    let voided =
        { persistedEntry "e2" (minutes 60) "sha-1" with State = Void(reason "dup", instant 1L) }

    let superseded =
        { persistedEntry "e3" (minutes 90) "sha-1" with
            State = Superseded(SupersededBySplit [ entryId "c1" ]) }

    let result = run dayQuery [ active; voided; superseded ]

    // Only the 30-minute active entry counts.
    Assert.Equal(1800000L, result.TotalMilliseconds)
    Assert.Equal(1, result.CountedEntries)
    Assert.Equal(5, result.TotalBillableUnits)
    Assert.Equal((0, 30), (result.TotalDisplayHours, result.TotalDisplayMinutes))

[<Fact>]
let ``the default day view hides non counting entries but discloses how many`` () =
    let active = persistedEntry "e1" (minutes 30) "sha-1"

    let voided =
        { persistedEntry "e2" (minutes 60) "sha-1" with State = Void(reason "dup", instant 1L) }

    let countingOnly = run dayQuery [ active; voided ]
    Assert.Equal(1, List.length countingOnly.Entries)
    // The "discloses how many" half of this test's own name. It was missing,
    // and its absence hid a real defect: visibility was folded into the
    // selection filter, so a hidden entry was never counted as excluded and
    // the default day view reported zero — silently unable to tell anyone
    // that a removed entry existed (TE-R-030).
    Assert.Equal(1, countingOnly.ExcludedEntries)
    Assert.Equal(1, countingOnly.CountedEntries)
    Assert.Equal(1800000L, countingOnly.TotalMilliseconds)

    let withVoided = run { dayQuery with Visibility = IncludeVoided } [ active; voided ]
    Assert.Equal(2, List.length withVoided.Entries)
    // Shown, but still not counted.
    Assert.Equal(1800000L, withVoided.TotalMilliseconds)
    Assert.Equal(1, withVoided.ExcludedEntries)

[<Fact>]
let ``totals sum exact seconds rather than per entry rounded units`` () =
    // Ten 3-minute entries: 30 minutes = 5 units. Summing per-entry RoundUp
    // units would give 10 units, inflating billable time by 100%.
    let entries =
        [ 1..10 ]
        |> List.map (fun i -> persistedEntry (sprintf "e%d" i) (minutes 3) "sha-1")

    let result = run dayQuery entries

    Assert.Equal(1800000L, result.TotalMilliseconds)
    Assert.Equal(5, result.TotalBillableUnits)
    // Each row individually still rounds up, which is correct per row.
    Assert.True(result.Entries |> List.forall (fun e -> e.BillableUnits = 1))

[<Fact>]
let ``an empty result is reported as empty with zero totals`` () =
    let result = run dayQuery []

    Assert.True(result.IsEmpty)
    Assert.Equal(0L, result.TotalMilliseconds)
    Assert.Equal(0, result.TotalBillableUnits)
    Assert.Empty(result.Entries)

[<Fact>]
let ``a day with only voided entries is not reported as empty`` () =
    // The entries matched the day; they simply do not count. Reporting the day
    // as empty would hide history (TE-R-030).
    let voided =
        { persistedEntry "e1" (minutes 60) "sha-1" with State = Void(reason "dup", instant 1L) }

    let result = run { dayQuery with Visibility = IncludeVoided } [ voided ]

    Assert.False(result.IsEmpty)
    Assert.Equal(0L, result.TotalMilliseconds)
    Assert.Equal(1, result.ExcludedEntries)

[<Fact>]
let ``filtering by project excludes other projects`` () =
    let mine = persistedEntry "e1" (minutes 30) "sha-1"

    let other =
        let e = persistedEntry "e2" (minutes 30) "sha-1"

        { e with Effective = { e.Effective with Project = projectId "northline" } }

    let result =
        run { dayQuery with Project = Some(projectId "northline") } [ mine; other ]

    Assert.Equal(1, List.length result.Entries)
    Assert.Equal(entryId "e2", (List.head result.Entries).Id)

[<Fact>]
let ``text search matches description case insensitively`` () =
    let matching = persistedEntry "e1" (minutes 30) "sha-1"

    let notMatching =
        let e = persistedEntry "e2" (minutes 30) "sha-1"

        { e with
            Effective =
                { e.Effective with
                    Description = Some(description "Client proposal scope") } }

    let result =
        run { dayQuery with Text = Some "COMPOSITION" } [ matching; notMatching ]

    Assert.Equal(1, List.length result.Entries)
    Assert.Equal(entryId "e1", (List.head result.Entries).Id)

[<Fact>]
let ``whitespace only search text is treated as no filter`` () =
    // An empty search box must not blank the list.
    let entries = [ persistedEntry "e1" (minutes 30) "sha-1" ]

    Assert.Equal(1, List.length (run { dayQuery with Text = Some "   " } entries).Entries)
    Assert.Equal(1, List.length (run { dayQuery with Text = Some "" } entries).Entries)

[<Fact>]
let ``an entry with no description is excluded from text search rather than crashing`` () =
    let noDescription =
        let e = persistedEntry "e1" (minutes 30) "sha-1"
        { e with Effective = { e.Effective with Description = None } }

    let result = run { dayQuery with Text = Some "anything" } [ noDescription ]
    Assert.Empty(result.Entries)

[<Fact>]
let ``sorting is deterministic regardless of input order`` () =
    // TE-R-081: same state and query must give the same projection. Ties are
    // broken by id so list order cannot leak into the result.
    let a = persistedEntry "e-a" (minutes 30) "sha-1"
    let b = persistedEntry "e-b" (minutes 30) "sha-1"
    let c = persistedEntry "e-c" (minutes 30) "sha-1"

    let idsOf entries =
        (run dayQuery entries).Entries |> List.map (fun e -> EntryId.value e.Id)

    let expected = idsOf [ a; b; c ]

    Assert.Equal<string list>(expected, idsOf [ c; b; a ])
    Assert.Equal<string list>(expected, idsOf [ b; c; a ])

[<Fact>]
let ``longest first orders by exact seconds`` () =
    let short = persistedEntry "e1" (minutes 6) "sha-1"
    let long = persistedEntry "e2" (minutes 60) "sha-1"

    let result = run { dayQuery with Sort = LongestFirst } [ short; long ]
    Assert.Equal<string list>([ "e2"; "e1" ], result.Entries |> List.map (fun e -> EntryId.value e.Id))

[<Fact>]
let ``a row carries the capabilities of its state`` () =
    // TE-R-097: the browser reads these; it does not compute them.
    let active = persistedEntry "e1" (minutes 30) "sha-1"

    let voided =
        { persistedEntry "e2" (minutes 30) "sha-1" with State = Void(reason "dup", instant 1L) }

    let result = run { dayQuery with Visibility = IncludeVoided } [ active; voided ]

    let row id =
        result.Entries |> List.find (fun e -> e.Id = entryId id)

    Assert.Contains(TimeEntry.Semantic.Capabilities.CanCorrect, (row "e1").Capabilities)
    Assert.DoesNotContain(TimeEntry.Semantic.Capabilities.CanCorrect, (row "e2").Capabilities)
    Assert.Contains(TimeEntry.Semantic.Capabilities.CanRestore, (row "e2").Capabilities)

[<Fact>]
let ``a missing purpose surfaces as a badge and an obligation`` () =
    // Grounded in the recovered design: static-ui-screens/today.html renders
    // "Purpose missing" and review.html blocks attestation on it.
    let noDescription =
        let e = persistedEntry "e1" (minutes 30) "sha-1"
        { e with Effective = { e.Effective with Description = None } }

    let result = run dayQuery [ noDescription ]
    let row = List.head result.Entries

    Assert.Contains(PurposeMissing, row.Badges)
    Assert.Contains(TimeEntry.Semantic.Capabilities.BusinessPurposeMissing, row.Obligations)
    Assert.Contains(TimeEntry.Semantic.Capabilities.BusinessPurposeMissing, result.OpenObligations)

[<Fact>]
let ``display hours and minutes are computed in the projection`` () =
    // TE-R-085: the browser must never do time arithmetic.
    let entry = persistedEntry "e1" (millis (23L * MillisecondsPerBillableUnit)) "sha-1"
    let row = List.head (run dayQuery [ entry ]).Entries

    Assert.Equal(23, row.BillableUnits)
    Assert.Equal((2, 18), (row.DisplayHours, row.DisplayMinutes))

// ---------------------------------------------------------------------------
// Period summary
// ---------------------------------------------------------------------------

[<Fact>]
let ``decimal hours are exact tenths, never a float`` () =
    // A billable unit is six minutes, which is exactly one tenth of an hour.
    // So decimal hours need no rounding at all — and computing them as a
    // float would introduce error into a figure that has none (TE-R-007).
    //
    // 52 + 30 exact minutes = 82 minutes, which bills as 14 units: 1.4 hours.
    let summary =
        PeriodSummary.ofEntries
            RoundUp
            [ persistedEntry "e1" (millis 3120000L) "sha-1"
              persistedEntry "e2" (minutes 30) "sha-2" ]

    Assert.Equal(14, summary.TotalBillableUnits)
    Assert.Equal(1, summary.DecimalHoursWhole)
    Assert.Equal(4, summary.DecimalHoursTenths)

[<Fact>]
let ``a period groups its days and counts only the active ones`` () =
    let summary =
        PeriodSummary.ofEntries
            RoundUp
            [ persistedEntry "e1" (minutes 30) "sha-1"
              persistedEntry "e2" (minutes 30) "sha-2"
              persistedEntryOn "e3" (minutes 60) "sha-3" (onDate 2026 9 11) ]

    Assert.Equal(2, summary.ActiveDays)
    Assert.Equal(2, List.length summary.Days)
    // Chronological, so a caller need not sort — and cannot sort differently.
    Assert.Equal(defaultDate, (List.head summary.Days).Date)
    Assert.Equal(3600000L, (List.head summary.Days).TotalMilliseconds)
    Assert.Equal(2, (List.head summary.Days).CountedEntries)

[<Fact>]
let ``a period reports how much of itself was entered by hand`` () =
    // A ledger meant for review should be able to say how much of itself was
    // reconstructed rather than timed (system-prompt 8.4).
    let timed = persistedEntry "e1" (minutes 30) "sha-1"

    let manual =
        let entry = persistedEntry "e2" (minutes 60) "sha-2"

        { entry with
            Effective =
                { entry.Effective with
                    Origin = Manual(reason "Worked from notes.") } }

    let summary = PeriodSummary.ofEntries RoundUp [ timed; manual ]

    Assert.Equal(1800000L, summary.TimedMilliseconds)
    Assert.Equal(3600000L, summary.ManualMilliseconds)
    // The two halves account for the whole: no entry is both or neither.
    Assert.Equal(summary.TotalMilliseconds, summary.TimedMilliseconds + summary.ManualMilliseconds)

[<Fact>]
let ``a removed entry is counted as removed and excluded from the total`` () =
    let active = persistedEntry "e1" (minutes 30) "sha-1"

    let removed =
        { persistedEntry "e2" (minutes 60) "sha-2" with
            State = Void(reason "Recorded twice.", instant 1L) }

    let summary = PeriodSummary.ofEntries RoundUp [ active; removed ]

    Assert.Equal(1800000L, summary.TotalMilliseconds)
    Assert.Equal(1, summary.RemovedEntries)
    Assert.Equal(1, summary.CountedEntries)
    // And it is not an active day of its own.
    Assert.Equal(1, summary.ActiveDays)

[<Fact>]
let ``an empty period is zero rather than an error`` () =
    // Zero elapsed time is not a valid `Duration`, which is correct — but it
    // is a perfectly valid TOTAL, and a month with no entries must summarise
    // rather than fail.
    let summary = PeriodSummary.ofEntries RoundUp []

    Assert.Equal(0L, summary.TotalMilliseconds)
    Assert.Equal(0, summary.DecimalHoursWhole)
    Assert.Equal(0, summary.DecimalHoursTenths)
    Assert.Equal(0, summary.ActiveDays)
    Assert.Empty(summary.Days)

[<Fact>]
let ``a month total sums exact time rather than per-day rounded units`` () =
    // Three days of 3 exact minutes each is 9 minutes — 2 units when the
    // month is projected once. Projecting each DAY and adding would give 3,
    // inflating the month by 50%. The same drift the day view avoids, one
    // level up.
    let entries =
        [ for day in 1..3 ->
            persistedEntryOn (sprintf "e%d" day) (minutes 3) "sha" (onDate 2026 9 day) ]

    let summary = PeriodSummary.ofEntries RoundUp entries

    Assert.Equal(540000L, summary.TotalMilliseconds)
    Assert.Equal(2, summary.TotalBillableUnits)
    Assert.Equal(3, summary.ActiveDays)
    // Each day on its own rounds up to 1 unit, so the days sum to 3.
    Assert.Equal(3, summary.Days |> List.sumBy (fun d -> d.BillableUnits))
