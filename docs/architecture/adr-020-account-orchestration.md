# ADR-020: Account lifecycle and versioned orchestration persistence

- **Status:** Implementation in progress (2026-10-16)
- **Scope:** L1 and the persistence foundation for B2/P3/I4/Q6/H7.

## Decision

Keep document replication's existing pending operations authoritative. Orchestration metadata describes
transfers, pins, candidate/active content revisions, workflow progress and execution ownership; it never
duplicates the document outbox. Feature payloads carry their own schema version and use source-generated JSON.

`AccountScope` gives application, environment, account and optional organization separate stable boundaries.
Its storage key is a SHA-256 of length-framed identifiers. It is a local partition key, not authorization or
encryption. Servers must continue deriving identity and membership from authenticated callers.

`IOrchestrationStore` provides bounded ordinal keyset pages and atomic compare-and-write batches. Revisions
are opaque unique tokens, including after deletion/recreation. A failed revision check commits nothing.
Keys use a bounded ASCII alphabet so .NET, SQLite and IndexedDB ordering agree. Pagination is live, not a
snapshot; use an immutable generation when a feature requires snapshot semantics.

SQLite uses independent `bs_orchestration_schema`/`bs_orchestration_records` tables at version 1, without
changing replicated-document `user_version`. Account rows may share a file. IndexedDB uses a distinct
account database at version 1. Newer schemas are refused. Metadata transactions never perform network I/O.
SQLite uses WAL/synchronous FULL; IndexedDB requests strict durability. Browser origin eviction, missing
keys, disk failure and physical power loss are not equated with tested process termination.

`ExecutionLease` stores an expiring ownership token and renews within a bounded 1-second to 5-minute
lease lifetime. The same token survives renewal; takeover creates a different token. Every scheduler-owned
metadata commit passes a fence checked inside the storage transaction. Cancellation alone is insufficient:
a paused/crashed worker cannot overwrite successor progress even if an in-flight request eventually completes.
Time comes from the host UTC clock; a clock discontinuity may shorten/lengthen takeover latency. It cannot
allow two generations to pass the same transactional fence. Hosts must not share mutable credentials across
accounts; retries remain protected by existing document operation IDs and server receipts.

The existing `SyncCoordinator` owns coordinated account opening/suspension/closing/deletion. Sessions can
open with networking disabled and suspend network work while retaining local access. The host registers
additional blob/scheduler/key resources using stable names and idempotent callbacks. A deletion journal is
kept in a durable application catalog outside the resources being deleted. Each step is replayable; progress
is committed after successful deletion. A completed tombstone makes repeated deletion idempotent until a
subsequent explicit open. Account deletion defaults to refusing pending work; export/discard are explicit.
Registered resources also report pending work through `InspectPendingAsync`. A missing/incomplete inspection is
counted as unknown and default deletion refuses it, even with no pending documents. Hosts can explicitly report zero
for key-only/empty-cache resources. Export must preserve all registered work, including workflows/transfers; exceptions
or unknown counts never authorize erasure.
Applications must quiesce UI writes before exporting/deleting and preserve resource registrations during
interrupted deletion. This API does not invent a distributed transaction with file bytes or protected key stores.

Configure `SyncCoordinatorOptions.AccountResourceLock` in every participant to protect whole-resource lifetimes.
`FileAccountResourceLock` uses stable shared/exclusive file handles outside deleted directories; the browser provider
uses shared/exclusive Web Locks. Open replicas hold shared access, allowing local readers/writers while a separate
replication execution lease is owned elsewhere. Deletion closes this coordinator, acquires exclusive access without
waiting, and reopens locally to recheck pending work under that boundary. This recheck closes the race where another
owner writes between shared release and exclusive acquisition. Even preserve refusal leaves this coordinator closed;
reopen explicitly. A competing lifetime makes deletion throw `AccountResourcesInUseException`, with no journal created
or resources deleted by that attempt; opening during exclusive work returns `InUse`. Interrupted deletion also requires
exclusive access before resuming. Stable native lock files must never be unlinked. Process/tab termination releases
physical ownership; timer expiry does not. Providers and their acquired handles must outlive the opened resources.

This is opt-in for compatibility: legacy per-session opens, unmanaged file access and workers that omit the shared
lock remain the host's responsibility. All blob/scheduler/key workers must hold shared account access for their resource
lifetime too. Locks do not forcibly revoke another tab or authorize server access. Hosts must stop all their UI writes
and release resources before deletion; a live uncooperative owner blocks deletion rather than permitting data loss.

## Verification and remaining boundaries

Tests cover compare-and-write races, all-or-nothing failure, expiration and takeover, account isolation,
restart, newer-schema refusal, offline opening and deletion recovery. Browser implementation requires real
browser tests before claiming parity. Native physical-device background execution is a separate H7 gate.
Local key access remains host-provided and independent of `CredentialRenewal`. Locked/unavailable outcomes
must retain existing storage; no empty-store fallback is permitted.

`ILocalKeyProvider` now makes this boundary explicit with an account/key/version reference and available/locked/unavailable
outcomes. `LocalKeyAccess` owns a disposable 256-bit copy, zeroed on disposal; imported engine keys and managed strings
remain outside that guarantee. See [local unlock](../patterns/local-unlock.md) for the browser recovery-key example,
native protected-store lookup and threat model. Provisioning and rotation are separate host operations.

`SyncCoordinatorOptions.CheckNetworkCredentialsAsync` checks or renews credentials for the exact account only after
previous network work has stopped. Offline opens do not call it. False returns `CredentialsRequired` from a network-enabled
open while keeping local replicas readable; resume throws `AccountCredentialsUnavailableException` with data retained.
The host must still use account-bound transports and handle expiry during requests. A replica factory can report
`ReplicaMigrationPendingException` to defer opening with `Migrating`; no empty replacement is created.
