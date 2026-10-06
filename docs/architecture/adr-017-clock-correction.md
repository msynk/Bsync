# ADR-017: Correcting a device clock that is ahead of the server

- **Status:** Part 1 accepted and implemented (0.2.0, unreleased); part 2 accepted and implemented (2026-10-06, 0.3.0,
  unreleased).
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

## Decision, part 2 (implemented)

Allow a one-time rewind of timestamps that **no peer has seen**:

1. On a `clock-skew` rejection, with a known server time, the engine computes a floor: the greatest timestamp this
   replica has received from the server or had accepted by it (the store already tracks observed versions and bases).
2. It rewinds the HLC to `max(floor, corrected now)`, re-stamps every local record whose pending or current timestamp is
   above the server bound, and resends them once as new operations.

Why this preserves the guarantees that matter: timestamps above the bound were never accepted, so no other replica and
no server state contains them; last-writer-wins order between this node's own edits is preserved because all of them
are re-stamped in their original order.

Decisions taken when implementing it:

- **What is re-stamped:** records rejected with `clock-skew`, and members of their dependency groups parked with
  `group-failed`, in the order of their original timestamps, as new operations (the rejected operation ids keep their
  receipts). Each keeps its local revision, so a `SyncGoal.Accepted(id, revision)` wait completes when the re-stamped
  write is accepted. A record edited again meanwhile is left alone: the new edit already has a new timestamp.
- **The floor** is the newest timestamp among the stored documents that are not being re-stamped and are not above the
  server's time plus the servers' default allowance (five minutes); the clock is moved back to it and continues from
  `max(floor, corrected now)`.
- **No rewind generation is persisted.** Instead the store's clock high-water mark, which seeds the clock after a
  restart, is set explicitly to the newest timestamp still stored (`ILocalStore.ResetClockHighWaterAsync`, implemented by
  the in-memory, SQLite and IndexedDB stores; other stores keep their higher mark, and only a restart before real time
  catches up is affected).
- **Opt-out:** `SyncOptions.RestampSkewedWrites` (default on). Applications that copied a local document's
  `UpdatedAt` elsewhere before it was uploaded should turn it off; the re-stamped value is the one every peer sees.
- **Export/import (ADR-013):** exported work carries the timestamps it had at export; if they are still too far ahead
  after import, the same re-stamp happens on its first push.
- The engine re-stamps right after the push that got the rejections and pushes again in the same run, so
  `SyncAsync` reports the writes as pushed (`ClockSkewTests.OfflineWritesAreRestamped`).
