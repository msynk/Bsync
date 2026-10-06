# Architecture

- [Invariants I01–I20 and their current status](invariants.md)

## Decision records

| ADR | Topic | Status |
|---|---|---|
| [001](adr-001-build-versus-adopt.md) | Build a native protocol vs adopt Datasync, Dotmim.Sync, PowerSync, RxDB, Replicache/Zero, Fusion, Electric | Accepted |
| [002](adr-002-consistency-model.md) | Consistency model and scope | Accepted |
| [003](adr-003-metadata-separation.md) | Local revision, operation id, server version, checkpoint, origin HLC | Accepted |
| [004](adr-004-store-operations.md) | Atomic store operations, ownership and queue coalescing | Accepted; three stores implemented |
| [005](adr-005-feed-ordering.md) | Committed-prefix feed ordering and epochs | Accepted; reference and PostgreSQL implemented |
| [006](adr-006-conflict-model.md) | Conflict model, policies and defaults | Accepted (Phase 1, Phase 8: default `Defer`, three-way merge) |
| [007](adr-007-hosting-and-lifecycle.md) | Hosting profiles, application API, DI and lifecycle | Accepted; every profile has a tested sample |
| [008](adr-008-browser-storage.md) | IndexedDB baseline, OPFS/SQLite only on evidence | Accepted; IndexedDB implemented |
| [009](adr-009-server-integration.md) | Controlled write service, PostgreSQL first, capture coverage | Accepted; PostgreSQL authority implemented |
| [010](adr-010-auth-and-scope.md) | Authentication, scope identity, revocation and account switching | Accepted; implemented for the reference and PostgreSQL authorities |
| [011](adr-011-compatibility.md) | Wire, store and domain schema compatibility | Accepted; headers and store migrations implemented |
| [012](adr-012-packaging-and-support.md) | Packaging, support tiers, target framework, workloads | Accepted |
| [013](adr-013-recovery.md) | Recovery of local work: retry, revert, export/import, SQLite rebuild, schema roll-out | Accepted |
| [015](adr-015-membership-and-removals.md) | Read membership in the feed; removals without a resnapshot | Accepted; in-memory and SQL Server |
| [017](adr-017-clock-correction.md) | Correcting a device clock that is ahead of the server | Part 1 implemented; part 2 proposed |
| [016](adr-016-encryption-at-rest.md) | Encryption at rest and replica wipe | Wipe and persistence implemented; encryption proposed |
| [018](adr-018-local-secondary-indexes.md) | Declared secondary indexes in the local stores (task E2) | Proposed; not implemented |
| [014](adr-014-sql-server-authority.md) | SQL Server authority on the application's database: feed lock, transaction enlistment, write handler, publisher | Accepted; implemented (0.2.0, unreleased) |

Operations: [disaster recovery and stuck replicas](../operations/disaster-recovery.md).

## Current shape (after Phase 10)

```
 component ──► ISyncCollection<T> ─┬─► LocalSyncCollection ──► SyncSession (loop, triggers, backoff, lease, hints)
                                   │                                │
                                   │                                ▼
                                   │   local writes (atomic) ──► SyncEngine ──► ILocalStore.UpdateAsync (compare-and-transform)
                                   │                                │            InMemory | SQLite (native) | IndexedDB (browser)
                                   │                                │            records: Current, Base, BaseVersion, LocalRevision,
                                   │                                │            Pending, Rejection, Observed, Conflict, Generation
                                   │                                │            + cursor (checkpoint, generation, resnapshot) + clock high-water
                                   │                                ▼
                                   │                         ISyncTransport ── HTTP (POST pull/push, GET hints SSE) or in-process
                                   │                                │
                                   └─► ServerSyncCollection ────────┤  (server-rendered hosts call the authority in-process)
                                                                    ▼
                                            MapSyncCollection endpoints ──► ISyncAuthority (InMemorySyncServer today)
                                              scope from auth claims          per-scope feed, versions, receipts,
                                              problem details + codes         scope fingerprint in checkpoints,
                                                                              retention horizon, epochs/version floor
```

- Pull: opaque checkpoint → page of (document, version); `reset-required` with a reason starts a new generation.
- Push: operations (id, base version, payload) → one outcome per id: accepted, conflict, rejected (incl.
  `base-expired`), retry-later; duplicates are replayed from receipts.
- Conflicts: kept by default (`Defer`), or merged field by field, or decided by a policy (ADR-006).
- Diagnostics: `Bsync` activity source and meter, `Bsync.Server` meter, `ILogger` in session and
  endpoints (docs/operations/observability.md).
