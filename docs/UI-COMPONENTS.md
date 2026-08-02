# UI components

Foundations use semantic color, type, spacing, radius, border, elevation, focus, and motion tokens in `src/styles.css`. Light/dark themes follow system preference; forced colors, reduced motion, and 320 px recomposition are explicit.

Implemented primitives: labeled button, icon button, text input, textarea, native select, checkbox, status label, toast, progress bar, and native dialog sheet. Patterns: persistent timer, timer display, favorite activity, six-minute picker, chronological timeline item, activity detail, history item, correction note, warning/review row, evidence form, conflict comparison, monthly bars with exact-value labels, loading/empty language, and bottom navigation.

Controls own bounded behavior; pages own meaningful labels and hierarchy. Critical actions use text. State is never color-only. Tap targets are 44 px or larger.
