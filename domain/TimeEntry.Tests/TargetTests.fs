/// The monthly tracking target the person using the ledger sets
/// (DF-TE-0015, resolving OQ-9).
///
/// One suite across four tiers, because the feature's whole point is a value
/// that crosses all of them without anything inventing a default on the way.
/// The cases that matter most are the absences: a ledger with no target set,
/// a preferences file that is not there, and a preferences file that cannot be
/// read must be three different outcomes and not one.
module TimeEntry.Tests.TargetTests

open System.Text.Json.Nodes
open Xunit
open TimeEntry.Semantic.Duration
open TimeEntry.Semantic.Preferences
open TimeEntry.Projection.Projection
open TimeEntry.Persistence
open TimeEntry.Persistence.Documents
open TimeEntry.GitHub.Store
open TimeEntry.GitHub.Interpreter
open TimeEntry.Tests.Helpers
open TimeEntry.Tests.FakeStore

let private target hours = TrackingTarget.ofHours hours |> expect

// ---------------------------------------------------------------------------
// Tier 1 — what a target is
// ---------------------------------------------------------------------------

[<Fact>]
let ``a target is held in six-minute units, ten to the hour`` () =
    Assert.Equal(800, TrackingTarget.units (target 80))

[<Fact>]
let ``a target of no time at all is refused`` () =
    // Zero is the encoding of "no target set" in the stored document, so it
    // must be unrepresentable as a target: otherwise the file could not tell
    // the two apart.
    match TrackingTarget.ofHours 0 with
    | Error(TargetNotPositive units) -> Assert.Equal(0, units)
    | other -> failwithf "expected TargetNotPositive, got %A" other

[<Fact>]
let ``a negative target is refused`` () =
    match TrackingTarget.ofHours -8 with
    | Error(TargetNotPositive _) -> ()
    | other -> failwithf "expected TargetNotPositive, got %A" other

[<Fact>]
let ``an absurd target is refused rather than stored`` () =
    match TrackingTarget.ofUnits (MaximumTargetUnits + 1) with
    | Error(TargetExceedsMaximum(units, maximum)) ->
        Assert.Equal(MaximumTargetUnits + 1, units)
        Assert.Equal(MaximumTargetUnits, maximum)
    | other -> failwithf "expected TargetExceedsMaximum, got %A" other

[<Fact>]
let ``a target converts to hours and minutes without rounding`` () =
    // 125 units is 12.5 hours: the conversion has to produce 12h 30m, not
    // 12h and a remainder someone has to interpret.
    Assert.Equal((12, 30), TrackingTarget.toHoursAndMinutes (TrackingTarget.ofUnits 125 |> expect))

// ---------------------------------------------------------------------------
// Tier 3 — how a recorded period compares to a target
// ---------------------------------------------------------------------------

/// A month's worth of entries totalling a known number of units.
let private monthOf (unitsEach: int) (count: int) =
    [ for n in 1..count ->
          persistedEntryOn
              (sprintf "e%d" n)
              (millis (int64 unitsEach * MillisecondsPerBillableUnit))
              (sprintf "sha-%d" n)
              (onDate 2026 9 (n % 28 + 1)) ]

let private summaryOf entries = PeriodSummary.ofEntries RoundUp entries

[<Fact>]
let ``progress is reported as whole per-cent of the target`` () =
    // 9 entries of 60 units = 540 units = 54h, against an 80h (800-unit)
    // target. 540 * 100 / 800 = 67.
    let progress = TargetProgress.against (target 80) (summaryOf (monthOf 60 9))
    Assert.Equal(67, progress.PercentRecorded)
    Assert.Equal(800, progress.TargetUnits)

[<Fact>]
let ``progress truncates rather than rounds, so it never overstates`` () =
    // 799 of 800 units is 99.875%. Rounding would report 100% — a target
    // reached that was not.
    let recorded = monthOf 799 1
    let progress = TargetProgress.against (target 80) (summaryOf recorded)
    Assert.Equal(99, progress.PercentRecorded)
    Assert.False progress.Reached

[<Fact>]
let ``the remainder is stated in hours and minutes and never goes negative`` () =
    let progress = TargetProgress.against (target 80) (summaryOf (monthOf 546 1))
    // 800 - 546 = 254 units = 25h 24m, which is the figure month.html shows.
    Assert.Equal(254, progress.RemainingUnits)
    Assert.Equal((25, 24), (progress.RemainingDisplayHours, progress.RemainingDisplayMinutes))

[<Fact>]
let ``exceeding the target reports over a hundred per-cent and no remainder`` () =
    // Over-recording is a fact, not an error, and the remainder is zero
    // rather than negative: "minus four hours remaining" is not a remainder.
    let progress = TargetProgress.against (target 80) (summaryOf (monthOf 900 1))
    Assert.Equal(112, progress.PercentRecorded)
    Assert.Equal(0, progress.RemainingUnits)
    Assert.True progress.Reached

[<Fact>]
let ``an empty month reports no progress rather than failing`` () =
    let progress = TargetProgress.against (target 80) (summaryOf [])
    Assert.Equal(0, progress.PercentRecorded)
    Assert.Equal(800, progress.RemainingUnits)
    Assert.False progress.Reached

// ---------------------------------------------------------------------------
// Tier 4 — the stored document
// ---------------------------------------------------------------------------

let private roundTrip (preferences: Preferences) =
    preferences
    |> Mapping.preferencesToDocument
    |> Serialization.writePreferences
    |> Serialization.readPreferences
    |> expect
    |> Mapping.preferencesFromDocument
    |> expect

[<Fact>]
let ``a set target survives a round trip through the stored document`` () =
    Assert.Equal<Preferences>({ MonthlyTarget = Some(target 80) }, roundTrip { MonthlyTarget = Some(target 80) })

[<Fact>]
let ``an unset target survives a round trip as an unset target`` () =
    Assert.Equal<Preferences>(Preferences.none, roundTrip Preferences.none)

[<Fact>]
let ``a deleted key reads the same as no target set`` () =
    // A preferences file is reviewable JSON in a GitHub diff, so somebody may
    // well delete the key rather than zero it. Refusing that would turn a
    // readable file into an unreadable one over punctuation.
    let stored = """{ "schema_version": "1.0.0" }"""

    let read =
        Serialization.readPreferences stored |> expect |> Mapping.preferencesFromDocument |> expect

    Assert.Equal<Preferences>(Preferences.none, read)

[<Fact>]
let ``a hand-edited negative target is a corrupt file, not an absent one`` () =
    // The distinction the test exists for: reporting this as "no target set"
    // would hide a file that needs attention behind an invitation to set
    // something the person already set.
    let stored = """{ "schema_version": "1.0.0", "monthly_target_units": -40 }"""

    match Serialization.readPreferences stored |> expect |> Mapping.preferencesFromDocument with
    | Error(InvalidField("monthly_target_units", _)) -> ()
    | other -> failwithf "expected InvalidField, got %A" other

[<Fact>]
let ``a preferences file from a newer schema is refused, not guessed at`` () =
    let stored = """{ "schema_version": "9.9.9", "monthly_target_units": 800 }"""

    match Serialization.readPreferences stored |> expect |> Mapping.preferencesFromDocument with
    | Error(UnsupportedSchemaVersion("9.9.9", _)) -> ()
    | other -> failwithf "expected UnsupportedSchemaVersion, got %A" other

// ---------------------------------------------------------------------------
// Tier 4 — reading and writing against the store
// ---------------------------------------------------------------------------

let private storedPreferences (preferences: Preferences) =
    Layout.PreferencesPath,
    preferences |> Mapping.preferencesToDocument |> Serialization.writePreferences

[<Fact>]
let ``a repository with no preferences file reports no target set`` () =
    // Not an error. A ledger where nobody has set anything is the ordinary
    // first state, and nothing is refused for want of a preference. This is
    // the opposite of the catalogue, where absence IS an error.
    let fake = Fake([])

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesLoaded preferences -> Assert.Equal<Preferences>(Preferences.none, preferences)
    | other -> failwithf "expected PreferencesLoaded, got %A" other

[<Fact>]
let ``a stored target is read back`` () =
    let fake = Fake([ storedPreferences { MonthlyTarget = Some(target 80) } ])

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesLoaded preferences -> Assert.Equal(Some(target 80), preferences.MonthlyTarget)
    | other -> failwithf "expected PreferencesLoaded, got %A" other

[<Fact>]
let ``an unreadable preferences file is not reported as no target set`` () =
    let fake = Fake([ Layout.PreferencesPath, "{ this is not json" ])

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesUnreadable _ -> ()
    | other -> failwithf "expected PreferencesUnreadable, got %A" other

[<Fact>]
let ``an unreachable repository is not reported as no target set`` () =
    let fake = Fake([], TransportFailure "the network went away")

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesUnavailable(TransportFailure _) -> ()
    | other -> failwithf "expected PreferencesUnavailable, got %A" other

[<Fact>]
let ``setting a target writes the preferences file`` () =
    let fake = Fake([])

    let saved =
        savePreferences fake.Store { MonthlyTarget = Some(target 80) } |> Async.RunSynchronously

    Assert.True(Result.isOk saved)
    Assert.Contains(Layout.PreferencesPath, fake.Paths)

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesLoaded preferences -> Assert.Equal(Some(target 80), preferences.MonthlyTarget)
    | other -> failwithf "expected PreferencesLoaded, got %A" other

[<Fact>]
let ``clearing a target writes the file back with none set`` () =
    let fake = Fake([ storedPreferences { MonthlyTarget = Some(target 80) } ])

    savePreferences fake.Store Preferences.none
    |> Async.RunSynchronously
    |> Result.isOk
    |> Assert.True

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesLoaded preferences -> Assert.Equal<Preferences>(Preferences.none, preferences)
    | other -> failwithf "expected PreferencesLoaded, got %A" other

[<Fact>]
let ``a preferences write is a compare-and-swap on the file it read`` () =
    // The same optimistic concurrency every ledger write uses. A preference
    // is small and a refusal is cheap to recover from, but losing one of two
    // simultaneous writes silently is no more acceptable here than for an
    // entry.
    let fake = Fake([ storedPreferences { MonthlyTarget = Some(target 80) } ])

    fake.InterfereDuringCommit(fun () ->
        fake.ChangeBehindOurBack(
            Layout.PreferencesPath,
            snd (storedPreferences { MonthlyTarget = Some(target 40) })
        ))

    match savePreferences fake.Store { MonthlyTarget = Some(target 120) } |> Async.RunSynchronously with
    | Error(HeadMoved _)
    | Error(PreconditionFailed _) -> ()
    | other -> failwithf "expected the write to be refused, got %A" other

// ---------------------------------------------------------------------------
// The browser kernel's month view
// ---------------------------------------------------------------------------

let private monthRequest (preferences: string option) =
    let node = JsonObject()
    node.Add("year", JsonValue.Create 2026)
    node.Add("month", JsonValue.Create 9)
    let documents = JsonArray()

    for entry in monthOf 60 9 do
        documents.Add(JsonNode.Parse(Serialization.write (Mapping.toDocument entry)))

    node.Add("entries", documents)
    preferences |> Option.iter (fun p -> node.Add("preferences", JsonNode.Parse p))
    node.ToJsonString()

let private monthView (preferences: string option) =
    JsonNode.Parse(TimeEntry.Kernel.viewMonth (monthRequest preferences))

[<Fact>]
let ``a month with no target set reports its figures and no target`` () =
    // The restraint DF-TE-0015 is mostly about: month.html draws a bar
    // against "80h target", and since no document says where 80 comes from,
    // an unset target renders no bar rather than a bar against a guess.
    let view = monthView None
    Assert.True(view.["ok"].GetValue<bool>())
    Assert.Equal("54h 00m", view.["displayTotal"].GetValue<string>())
    Assert.True(isNull view.["target"])

[<Fact>]
let ``a preferences file with no target set is also no target`` () =
    let stored =
        Preferences.none |> Mapping.preferencesToDocument |> Serialization.writePreferences

    Assert.True(isNull (monthView (Some stored)).["target"])

[<Fact>]
let ``a set target is reported with its progress, remainder and words`` () =
    let stored =
        { MonthlyTarget = Some(target 80) }
        |> Mapping.preferencesToDocument
        |> Serialization.writePreferences

    let target' = (monthView (Some stored)).["target"]
    Assert.Equal("80h 00m", target'.["displayTarget"].GetValue<string>())
    Assert.Equal(67, target'.["percentRecorded"].GetValue<int>())
    Assert.Equal(67, target'.["barPercent"].GetValue<int>())
    Assert.Equal("26h 00m", target'.["displayRemaining"].GetValue<string>())
    Assert.False(target'.["reached"].GetValue<bool>())
    Assert.Equal("Tracking target reached: No", target'.["headline"].GetValue<string>())
    Assert.Contains("Formal program determination: Not evaluated.", target'.["detail"].GetValue<string>())

[<Fact>]
let ``the bar is clamped at a hundred while the figure is not`` () =
    let stored =
        { MonthlyTarget = Some(target 40) }
        |> Mapping.preferencesToDocument
        |> Serialization.writePreferences

    // 540 units recorded against a 400-unit target.
    let target' = (monthView (Some stored)).["target"]
    Assert.Equal(135, target'.["percentRecorded"].GetValue<int>())
    Assert.Equal(100, target'.["barPercent"].GetValue<int>())
    Assert.True(target'.["reached"].GetValue<bool>())

[<Fact>]
let ``a corrupt preferences file fails the month view in words`` () =
    // Not silently ignored. A bar drawn from a file nobody could read would
    // be worse than a message saying the file needs attention.
    let view = monthView (Some """{ "schema_version": "1.0.0", "monthly_target_units": -40 }""")
    Assert.False(view.["ok"].GetValue<bool>())
    Assert.DoesNotContain("InvalidField", view.["error"].GetValue<string>())

// ---------------------------------------------------------------------------
// The browser kernel's write path
// ---------------------------------------------------------------------------

let private setRequest (body: JsonObject -> unit) =
    let repository = JsonObject()
    repository.Add("owner", JsonValue.Create "owner")
    repository.Add("repo", JsonValue.Create "ledger")
    repository.Add("branch", JsonValue.Create "main")
    let node = JsonObject()
    node.Add("repository", repository)
    node.Add("token", JsonValue.Create "a-token")
    body node
    node.ToJsonString()

let private setTarget (fake: Fake) (body: JsonObject -> unit) =
    JsonNode.Parse(
        TimeEntry.Kernel.setMonthlyTargetWith (fun _ _ -> fake.Store) (setRequest body)
        |> Async.RunSynchronously
    )

[<Fact>]
let ``setting a target in whole hours stores it and says so`` () =
    let fake = Fake([])
    let answer = setTarget fake (fun node -> node.Add("hours", JsonValue.Create 80))

    Assert.True(answer.["ok"].GetValue<bool>())
    Assert.Equal("The monthly tracking target is now 80h 00m.", answer.["outcome"].GetValue<string>())

    match readPreferences fake.Store |> Async.RunSynchronously with
    | PreferencesLoaded preferences -> Assert.Equal(Some(target 80), preferences.MonthlyTarget)
    | other -> failwithf "expected PreferencesLoaded, got %A" other

[<Fact>]
let ``clearing is a distinct instruction, not an hours of zero`` () =
    let fake = Fake([ storedPreferences { MonthlyTarget = Some(target 80) } ])
    let answer = setTarget fake (fun node -> node.Add("clear", JsonValue.Create true))

    Assert.True(answer.["ok"].GetValue<bool>())
    Assert.Equal("The monthly tracking target was removed.", answer.["outcome"].GetValue<string>())

[<Fact>]
let ``an hours of zero is refused in words a person can act on`` () =
    let fake = Fake([])
    let answer = setTarget fake (fun node -> node.Add("hours", JsonValue.Create 0))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("a tracking target must be more than no time at all", answer.["error"].GetValue<string>())

[<Fact>]
let ``a non-numeric target is refused rather than defaulted`` () =
    let fake = Fake([])
    let answer = setTarget fake (fun node -> node.Add("hours", JsonValue.Create "eighty"))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("'hours' must be a whole number of hours", answer.["error"].GetValue<string>())
    Assert.Empty(fake.Paths)

[<Fact>]
let ``a request with neither hours nor clear is refused`` () =
    let fake = Fake([])
    let answer = setTarget fake (fun _ -> ())

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.Equal("missing 'hours'", answer.["error"].GetValue<string>())

[<Fact>]
let ``a refused write is reported in words, not as a store error literal`` () =
    let fake = Fake([], Unauthorized "bad credentials")
    let answer = setTarget fake (fun node -> node.Add("hours", JsonValue.Create 80))

    Assert.False(answer.["ok"].GetValue<bool>())
    Assert.DoesNotContain("Unauthorized", answer.["error"].GetValue<string>())
