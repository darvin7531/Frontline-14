# Follow-up proposal: category-specific production lines (not implemented)

## Current boundary

`FrontlineFactorySystem` validates and pays for jobs on the server. One ordered
`FrontlineFactoryComponent.Jobs` list is scheduled through `ProcessingSlots`.
The UI's recipe `Category` is optional presentation metadata only; changing a
category cannot change price, scheduling, access or a paid job's entitlement.
Factory outputs remain the actual sealed crate prototypes, not loose weapons.

Strategic Persistence stores a stable factory identity, ordered recipe/remaining
job claims, physical inputs and retained crate entitlements. There are no player
job owners, production-line identifiers or client-selected physical input IDs.

## Separate task for approval

Before implementation, agree whether a line is a recipe category, a mapper-owned
machine configuration or another stable domain identifier. Do not use translated
UI labels as persisted keys. Agree on the following rules explicitly:

- Does each line have one active job or configurable processing slots?
- Are waiting jobs FIFO per line? Can different lines advance simultaneously?
- Does a selected recipe lock the line until completion, or only during a job?
- May any nearby player submit to a busy public line, or is reservation required?
  Public shared FIFO and owner/reservation-based access are alternative policies,
  not rules this UI change has chosen.
- How are simultaneous requests ordered and funded? Reuse the existing server
  validation/debit guard; no client ownership or available-material assertions.
- Is cancellation/refunding supported? What happens on machine destruction?
  Queue expiry/two-hour timers and migration behavior are unresolved and need
  explicit approval; this task adds none of them.

## Persistence and acceptance work after approval

Add stable line identity and ordering only if the agreed scheduler requires it.
Version the strategic snapshot and define how existing paid FIFO claims migrate
without losing, duplicating or repricing goods. Preserve remaining progress,
quarantined invalid claims, input entities and retained crate entitlements across
restart; reject unsupported versions rather than silently discarding claims.

Prove concurrent funded submissions, line conflicts and parallel/FIFO scheduling
through native server requests, followed by restart/load and failure-conservation
tracers. Expose the agreed schedule through existing BUI snapshots. Keep the
presentation category filter usable regardless of whether gameplay lines exist.

No queue, timer, access, persistence or economic changes are part of the current
three-window redesign.
