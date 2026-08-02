# UI accessibility

Baseline target is WCAG 2.2 AA where applicable; full conformance is not claimed. The prototype uses landmarks, native buttons/fields/dialog, logical headings, stable DOM order, visible focus, explicit labels, 44 px targets, tabular timer numerals, color-independent states, reduced motion, forced colors, and zoom/reflow support.

Timer transitions use a polite live region and do not announce each second. The dialog restores focus through native behavior. Required manual checks: current VoiceOver on iPhone, 200% text/zoom, keyboard-only order, focus visibility, forced colors, long localized content, error association, and contrast measurement in both themes.
