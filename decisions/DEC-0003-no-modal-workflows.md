# Decision — No modal workflows

## Status

Accepted

## Context

The initial prototype used native modal dialogs for entry, detail, correction, evidence, review, and conflict workflows. The user explicitly directed that modals not be used.

## Decision

All secondary workflows render as full in-page detail workspaces in normal document flow. Opening a workflow hides the primary route content, moves focus to an explicit Back control, and restores the prior route and focus on return.

## Alternatives Considered

Native dialogs, modal bottom sheets, and popovers were rejected. Expanding every complex form inline inside the timeline would damage chronology and scanning.

## Evidence

Explicit user instruction and the need for stable mobile hierarchy and predictable assistive-technology navigation.

## Assumptions

The application may later use real URL routes for detail workspaces without changing this interaction model.

## Consequences

Workflows have more space, browser semantics are simpler, and no focus trap/backdrop is needed. Deep-link/history support should be added when routing is upgraded.

## Risks

Hiding the parent route without URL history can surprise browser Back behavior in the prototype.

## Revisit When

Never for modal use unless the user supersedes this constraint. Revisit only the routing mechanism.

## Related Capabilities

Capabilities 1 and 5–17.
