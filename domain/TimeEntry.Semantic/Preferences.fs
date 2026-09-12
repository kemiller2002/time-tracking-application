/// Tier 1 — Semantic Model. What the person using the ledger has chosen.
///
/// Preferences are not ledger facts. Nothing here is a record of something
/// that happened, no revision is appended when one changes, and no capability
/// depends on one. They are kept in Tier 1 anyway because they are domain
/// values with domain constraints — a tracking target has a legal range, and
/// that rule belongs where every other legality rule lives, not in a form
/// handler.
///
/// The distinction that matters: a preference may be absent. `month.html`
/// shows "80h target" and no document says where 80 comes from (OQ-9), so an
/// unset target is represented as absence rather than as a default, and a
/// caller must decide what to show when there is nothing set. That is the
/// whole reason this is an option and not an int.
module TimeEntry.Semantic.Preferences

open TimeEntry.Semantic.Duration

/// Everything the person has set, in one value.
///
/// One field today. A record rather than a bare `TrackingTarget option` so
/// that `settings.html`'s other preferences — appearance, larger controls,
/// reduced motion, quick starts — have somewhere to arrive without changing
/// the signature of everything that carries preferences around. None of them
/// is invented here: the record has one field because one has been asked for.
type Preferences =
    { MonthlyTarget: TrackingTarget option }

module Preferences =

    /// Nothing set. Distinct from "not loaded", which a caller represents by
    /// not having a `Preferences` at all: a failed read must not present
    /// itself as a deliberate absence of a target.
    let none: Preferences = { MonthlyTarget = None }

    let withMonthlyTarget (target: TrackingTarget option) (preferences: Preferences) =
        { preferences with MonthlyTarget = target }
