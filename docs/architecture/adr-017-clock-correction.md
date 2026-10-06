# ADR-017: Correcting a device clock that is ahead of the server

- **Status:** Part 1 accepted and implemented (0.2.0, unreleased); part 2 **proposed**, not implemented.
- **Invariants:** I12, I20
- **Related:** improvement plan task D2, ADR-003 (origin timestamps), protocol §2.3

## Context

Writes carry an origin HLC timestamp. The authority rejects a write whose timestamp is more than `MaxClockSkew`
(default five minutes) ahead of its own clock, with `clock-skew` (I12: a fast clock must not poison last-writer-wins
for everyone). A device whose clock is two hours fast therefore has every write parked until the user fixes the clock.

`HybridLogicalClock` is strictly monotonic per node: every timestamp is greater than the last one it issued.

## Decision, part 1 (implemented)

- Authorities advertise their time in every pull response: member `serverTime` (Unix milliseconds, a digit string like
  other 64-bit values) with feature `server-time`. The in-memory, PostgreSQL and SQL Server authorities send it.
- The engine estimates the server's clock at the midpoint of each pull. When the device is **ahead** by a second or more,
  it sets `HybridLogicalClock.PhysicalOffset` to the difference; otherwise to zero. A device that is behind is left alone:
  the server accepts its writes and origin timestamps remain the device's own.
- The offset only changes the physical input of the HLC. Timestamps never go backwards: after a correction the clock
  continues from its last timestamp until the corrected physical time passes it.

Consequence: a device that has pulled once stamps acceptable timestamps for every later write, even with a clock hours
ahead (`ClockSkewTests.AheadClockConverges`).

## Problem left open

A write made **before** the first pull (for example offline, right after install) was stamped with the fast clock, and
the HLC's last timestamp is now hours ahead. Monotonicity keeps every later timestamp at least as far ahead, so:

- the rejected write cannot be re-stamped with an acceptable time, and
- later writes are stamped from the advanced last timestamp until real time catches up.

These writes stay parked with `clock-skew` until then, or until the user retries them after the clock caught up.

## Proposal, part 2 (not implemented)

Allow a one-time rewind of timestamps that **no peer has seen**:

1. On a `clock-skew` rejection, with a known server time, the engine computes a floor: the greatest timestamp this
   replica has received from the server or had accepted by it (the store already tracks observed versions and bases).
2. It rewinds the HLC to `max(floor, corrected now)`, re-stamps every local record whose pending or current timestamp is
   above the server bound, and resends them once as new operations.

Why this preserves the guarantees that matter: timestamps above the bound were never accepted, so no other replica and
no server state contains them; last-writer-wins order between this node's own edits is preserved because all of them
are re-stamped in their original order.

What needs deciding before implementing: whether rewinding a node's HLC is acceptable for applications that use
`UpdatedAt` as an ordering key outside Bsync, how a rewind interacts with export/import of local work (ADR-013), and
whether the store needs a persisted "rewind generation". Until then, part 2 stays a proposal.
