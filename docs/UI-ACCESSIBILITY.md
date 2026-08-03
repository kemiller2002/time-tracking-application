# UI accessibility

Baseline target is WCAG 2.2 AA where applicable; full conformance is not claimed. The prototype uses landmarks, native buttons/fields, logical headings, stable DOM order, visible focus, explicit labels, 44 px targets, tabular timer numerals, color-independent states, reduced motion, forced colors, and zoom/reflow support. Complex workflows use full in-page sections rather than modals.

Timer transitions use a polite live region and should not announce each second. In-page workspaces move focus to Back and restore the invoking control. Required manual checks: current VoiceOver on iPhone, 200% text/zoom, keyboard-only order, focus visibility, forced colors, long localized content, error association, and contrast measurement in both themes.
