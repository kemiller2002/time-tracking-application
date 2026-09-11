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
    Assert.Equal(1800, result.TotalSeconds)
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

    let withVoided = run { dayQuery with Visibility = IncludeVoided } [ active; voided ]
    Assert.Equal(2, List.length withVoided.Entries)
    // Shown, but still not counted.
    Assert.Equal(1800, withVoided.TotalSeconds)
    Assert.Equal(1, withVoided.ExcludedEntries)

[<Fact>]
let ``totals sum exact seconds rather than per entry rounded units`` () =
    // Ten 3-minute entries: 30 minutes = 5 units. Summing per-entry RoundUp
    // units would give 10 units, inflating billable time by 100%.
    let entries =
        [ 1..10 ]
        |> List.map (fun i -> persistedEntry (sprintf "e%d" i) (minutes 3) "sha-1")

    let result = run dayQuery entries

    Assert.Equal(1800, result.TotalSeconds)
    Assert.Equal(5, result.TotalBillableUnits)
    // Each row individually still rounds up, which is correct per row.
    Assert.True(result.Entries |> List.forall (fun e -> e.BillableUnits = 1))

[<Fact>]
let ``an empty result is reported as empty with zero totals`` () =
    let result = run dayQuery []

    Assert.True(result.IsEmpty)
    Assert.Equal(0, result.TotalSeconds)
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
    Assert.Equal(0, result.TotalSeconds)
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
    let entry = persistedEntry "e1" (seconds (23 * SecondsPerBillableUnit)) "sha-1"
    let row = List.head (run dayQuery [ entry ]).Entries

    Assert.Equal(23, row.BillableUnits)
    Assert.Equal((2, 18), (row.DisplayHours, row.DisplayMinutes))
