/// Tier 1 — Semantic Model. Comparing the device's clock to the server's.
///
/// TE-R-008 requires a warning when device time and server time differ
/// materially. Two things had to be settled before it could be built, and
/// they are settled here and in `DF-TE-0017` rather than assumed:
///
/// **Where server time comes from.** The backing repository is reached over
/// HTTP, and every HTTP response carries a `Date` header — the time the
/// message was originated, which RFC 9110 requires a server to send. So the
/// server clock is already arriving with every read; nothing new has to be
/// requested for it. Tier 1 knows none of that: it receives two instants.
///
/// **What "materially" means.** No document says. It is therefore a parameter
/// with a stated default rather than a constant buried here — see
/// `DefaultToleranceMilliseconds` for the reasoning behind the default, and
/// note that a caller may pass its own.
///
/// This module performs no effects and reads no clock (TE-R-093). Both
/// instants are arguments, which is also what makes the comparison testable
/// at every offset rather than at whatever the machine happens to say.
module TimeEntry.Semantic.Clock

open TimeEntry.Semantic.Values

/// One billable unit, in milliseconds.
///
/// Chosen because it is the smallest quantity this ledger distinguishes: a
/// six-minute unit is the resolution of every figure it reports, so a
/// disagreement smaller than one unit cannot change any number a person sees
/// in a total. Anything at or beyond a unit can.
///
/// It is a default and not a rule. No repository document states a tolerance,
/// so this is a derived choice rather than a stated requirement, and
/// `assess` takes it as an argument so a caller that learns better can say so
/// without editing this file.
[<Literal>]
let DefaultToleranceMilliseconds = 360000L

/// How far apart the two clocks are, and whether that matters.
type ClockComparison =
    { /// Signed: positive means the device is AHEAD of the server. The sign is
      /// kept because the two directions have different consequences — a
      /// device running ahead can stamp an entry into a day that has not
      /// started, and one running behind can stamp it into a day already
      /// reviewed — and a caller that wants the magnitude can take it.
      DifferenceMilliseconds: int64
      ToleranceMilliseconds: int64
      IsMaterial: bool
      DeviceAhead: bool }

/// Compare a device instant against a server instant.
///
/// Total: any two instants have a comparison, and a difference of zero is a
/// perfectly ordinary answer rather than a special case. There is no
/// `Result` because nothing here can fail — which is the point of taking the
/// instants as arguments instead of reading them.
let assess (toleranceMilliseconds: int64) (device: Instant) (server: Instant) : ClockComparison =
    let difference = Instant.epochMilliseconds device - Instant.epochMilliseconds server
    let magnitude = abs difference

    { DifferenceMilliseconds = difference
      ToleranceMilliseconds = toleranceMilliseconds
      // At the tolerance exactly, NOT material. The tolerance reads as "up to
      // this much is tolerated", and a boundary that refused the value it
      // names would make the default read as 5 minutes 59 seconds.
      IsMaterial = magnitude > toleranceMilliseconds
      DeviceAhead = difference > 0L }

/// The same comparison under the default tolerance.
let assessDefault (device: Instant) (server: Instant) : ClockComparison =
    assess DefaultToleranceMilliseconds device server
