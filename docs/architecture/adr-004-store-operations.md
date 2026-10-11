# ADR-004: Durable store operations and ownership

- **Status:** Accepted (2026-09-27); implemented by the in-memory, SQLite (Phase 3) and IndexedDB (Phase 5) stores, which
  pass `LocalStoreConformance`
- **Invariants:** I01, I02, I03, I15

## Context

`ILocalStore` exposed `GetAsync` + `UpsertAsync` + `SetCheckpointAsync`. The engine composed read/modify/
write sequences across awaits, so any concurrent local edit could be overwritten (S02, S03), and a pull
page and its checkpoint could be persisted separately.

## Alternatives

1. **Generic transaction object** (`BeginTransaction`, reads and writes, `Commit`). Rejected: cannot be
   held open across .NET/JS awaits in IndexedDB, and invites long transactions spanning network I/O.
2. **Many coarse commands** (`AcknowledgeAsync`, `ApplyPageAsync`, …). Clear but pushes protocol logic
   into every provider and multiplies conformance surface.
3. **One atomic multi-record compare-and-transform plus optional checkpoint.** Chosen.

## Decision

- `UpdateAsync(IReadOnlyList<RecordUpdate>, Checkpoint?)` applies pure transforms to the committed state
  of one or more records and optionally stores the checkpoint, atomically. Transforms receive copies,
  return a new record or `null` (no change), must not call the store, and may run more than once, which
  allows optimistic implementations (read, compute, conditional write, retry) for IndexedDB.
- All protocol state changes are expressed as transforms conditioned on local revision or pending
  operation id: local write, delete, operation preparation, acknowledgement, rejection, conflict
  resolution, pull page.
- Reads: `GetAsync`, `GetPendingAsync(limit, exclude)` (bounded, ordered by origin time then id),
  `CountDirtyAsync`, `QueryAsync`, `GetCheckpointAsync`, `GetClockHighWaterAsync`.
- The store maintains a clock high-water mark on commit (I12).
- Stores never alias application objects: values are copied on the way in and out.
- **Ownership:** one engine replicates a store at a time. Within one engine, replication is single-flight;
  local writes do not take the replication gate and never wait for network I/O. Multi-tab/multi-process
  ownership (leases, fencing) is a provider concern designed in Phase 5.
- **Queue coalescing:** a record has at most one pending operation. Unsent edits coalesce into the
  current state; once an operation is persisted it is immutable and resent unchanged until final. A later
  edit is sent as the next operation after the first is acknowledged.

## Providers (2026-09-27)

- `InMemoryLocalStore`: reference, not durable.
- `SqliteLocalStore` (`Bsync.Storage.Sqlite`): one `BEGIN IMMEDIATE` transaction per update, WAL,
  `synchronous=FULL` by default, JSON documents via `JsonTypeInfo<T>`, ordinal id ordering through a
  UTF-16BE key column, schema version in `PRAGMA user_version` (newer schemas refused), replica id and
  incarnation. Passes the shared conformance suite with one instance and with two instances on one file,
  and the process-kill tests.
- The contract gained a replica cursor (checkpoint + generation + resnapshot flag) and `GetStaleAsync`
  for the reset flow (ADR-005, protocol §6.1).
- `IndexedDbLocalStore` (`Bsync.Blazor`, namespace `Bsync.Blazor.IndexedDb`): optimistic read-compute-conditional-write per
  update in one readwrite transaction (ADR-008).
- Phase 8 (2026-09-28): stores persist `SyncRecord.Conflict` and list conflicts by id
  (`GetConflictsAsync(limit)`); `PurgeAsync(ids, generation)` physically removes clean records of older
  generations without a kept conflict, for scope-change and retention resets; the cursor carries a
  `PurgeMissing` flag so an interrupted purge sweep resumes with the same meaning. SQLite and IndexedDB are
  at schema 2; both upgrade schema 1 in place without touching existing rows (tested with a pending
  operation in each).
- Later on 2026-09-28:
  - Stores persist `SyncRecord.Group` and a pending operation's `Group`/`GroupSize` (dependency groups;
    SQLite schema 3 adds four columns, IndexedDB schema 3 adds fields only).
  - They list rejected records (`GetRejectedAsync`) and serve bounded pages in id order from an index
    (`QueryPageAsync`).
  - `LocalSyncCollection` uses these pages for default-ordered queries, so it never loads the whole collection
    for them.

## Consequences

- Durable providers must implement `UpdateAsync` as one database transaction (SQLite) or one readwrite
  IndexedDB transaction with conditional writes.
- `QueryAsync` is unbounded today; a bounded query subset is Phase 3/6 work.

## Migration impact

Breaking for custom `ILocalStore` implementations: `UpsertAsync`, `SetCheckpointAsync` and
`GetDirtyAsync` were replaced by `UpdateAsync`, `GetPendingAsync`, `CountDirtyAsync` and
`GetClockHighWaterAsync`. See `docs/compatibility.md`.

## Tests

`ThrowingTransformIsAtomic`, `CrashDuringPageApplyIsAtomic`, `CheckpointCommittedWithPage`,
`StoreSnapshotsDoNotAlias`, `S02`, `S03`. A shared provider conformance suite is Phase 2/3 work.
