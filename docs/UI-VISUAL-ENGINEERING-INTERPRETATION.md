# UI visual-engineering interpretation

## Source and scope

This interpretation was completed before interface design on 2026-08-02. It uses Visual Engineering context version `0.14.1`, source commit `9722e3f749490e1b5efbc5910123b0a490c395a9`, and the complete operational set: `UI-FOUNDATIONS.md`, `UI-DECISION-CHECKLIST.md`, `UI-ANTI-PATTERNS.md`, and `RESEARCH-INDEX.md`.

The product is a single-owner, interruption-prone, mobile business activity ledger. Its primary recognition task is “what is timing now?”; its primary action task is “start or stop work”; and its primary verification task is “is this day’s effective record accurate?” Corrections affect totals but preserve history, so their consequence must be understandable without exposing event-sourcing terminology.

## Governing principles

- Give every view one dominant task. On Today that is seeing the day; on Track it is controlling the current timer.
- Keep timer state and synchronization state visible across routes. Hidden status and recovery paths are unacceptable.
- Group by task relationship and chronology. Use a continuous ledger/timeline rather than a grid of interchangeable dashboard cards.
- Make first-glance recognition and deliberate verification distinct. Effective values lead; history, provenance, and raw details progressively disclose.
- Preserve DOM, reading, focus, and visual order. Mobile layouts recompose rather than reorder.
- Prefer semantic HTML and native controls. Add JavaScript only for bounded state and behavior.
- Use restrained semantic color with text, shape, and icon redundancy. High contrast is reserved for the active control and states requiring attention.
- Use typography, spacing, alignment, and a limited rule system together. Secondary text remains readable rather than fading into low contrast.
- Keep action placement stable and labels explicit. Critical actions are never gesture-only or icon-only.
- Treat recovery as part of information architecture: queued, sending, saved, needs-attention, and conflict states always offer a next action.

## Product-specific translation

The visual language is a quiet paper ledger with an “ink and mineral” palette, strong typographic numerals, hairline chronology rules, and a warm field rather than a generic white dashboard. The timer receives scale and position, not alarm color or animation. Records form one chronological composition; contained surfaces are reserved for bounded interactive tools such as the timer and conflict comparison.

Bottom navigation uses `Today`, `Track`, `Month`, and `More`. Four destinations reduce competing emphasis and leave labels large enough at 320 CSS pixels. A compact running-timer control appears above navigation everywhere except the Track view.

## Tensions and resolutions

- **Calm versus visible state:** persistent timer and sync state can add noise. Resolve with stable placement, restrained color, and text labels; raise contrast only for `Needs attention` or `Conflict`.
- **Fast entry versus audit completeness:** stopping must be immediate while metadata may be incomplete. Resolve by stopping first, then presenting a short completion sheet with optional evidence and a visible pending status.
- **Sparse orientation versus dense review:** Today benefits from breathing room, while monthly comparison benefits from compact alignment. Density changes by task rather than using one universal spacing value.
- **Native controls versus custom duration entry:** native date/time fields remain native; six-minute increments use a bounded button grid because it materially improves repeated entry.
- **Persistent controls versus 320 px content:** the timer and navigation are fixed but compact, with safe-area padding and content bottom spacing so nothing is obscured.

## Assumptions and unresolved questions

- The first deliverable uses a synthetic in-browser Cloudflare adapter because no service contract implementation or credentials exist. Production behavior remains unclaimed.
- Authentication is represented as a secure-cookie contract; the frontend does not store bearer credentials.
- One active timer is allowed. Server time is authoritative when available.
- Evidence upload storage, virus scanning, retention, file limits, and signed-upload protocol remain unresolved backend decisions.
- PWA share-target reliability and background behavior require testing on the intended iPhone/iOS versions.
- The target amount and legal/tax meaning of tracked work are configuration, not UI determinations.

## Accessibility and mobile adaptation

The implementation baseline is semantic landmarks, visible keyboard focus, 44 by 44 CSS-pixel targets, text-and-icon state cues, tabular numerals, reduced-motion support, forced-color overrides, zoom/reflow without horizontal scrolling, and live announcements only for timer transitions. Sheets use native dialogs where supported with focus restoration. Destructive actions require a reason and confirmation. Testing covers 320, 375, 390, and 430 CSS-pixel widths, keyboard order, 200% zoom/text scaling, reduced motion, forced colors, long content, and color-independent status recognition.

## Evidence status

The visual package contains a mixture of verified records, complete research packages, working drafts, and candidate theories. This interpretation treats the operational foundations as decision criteria, not universal laws or a copied style. Product-specific usability evidence has not yet been collected, so visual choices remain reversible and require screenshot review and task testing.
