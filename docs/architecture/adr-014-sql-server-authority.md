# ADR-014: An authority on the application's SQL Server database

- **Status:** Accepted (2026-10-05). Implemented by `Bsync.Server.SqlServer` (0.2.0, unreleased); verified on SQL
  Server 2025 LocalDB (support matrix).
- **Invariants:** I04, I05, I06, I07, I10, I11, I14, I18, I19
- **Related:** ADR-005 (feed ordering), ADR-009 (controlled write service), ADR-010 (scopes), ADR-013 (recovery)

## Context

Applications that already own a relational system of record (SQL Server, usually through EF Core) cannot adopt
Bsync without a second database: the only durable authority is PostgreSQL with its own document tables, and no
domain logic can run inside the authority's transaction. They need:

- the protocol metadata next to their own tables, in the same database and the same transaction;
- a hook that runs their rules (computed fields, invariants, permissions on related rows) atomically with the
  acceptance of a replicated write;
- a way to put server-originated changes (API endpoints, jobs, imports) into the feed (B3, separate ADR section
  below).

## Decision

### Storage

- Protocol metadata lives in a schema (default `bsync`) on the **same database** as the application's tables:
  `meta` (schema version, epoch), `feeds` (one row per collection and scope: head version, retention horizon),
  `documents` (canonical JSON, version, tombstone flag, per feed), `receipts` (operation outcomes with request
  fingerprints, per feed).
- The application's relational tables remain the system of record. The `bsync` tables are the replication
  projection: what replicas see, at which version. The authority never reads or writes application tables
  itself; the write handler and the publisher do, in the same transaction.
- Keys: `feeds` has a surrogate `feed_id int IDENTITY` used only as a compact foreign key (SQL Server limits a
  clustered key to 900 bytes; collection, scope and id may each be 256 UTF-16 code units). It is never a feed
  position. Document ids and operation ids are stored as `varbinary` of their UTF-16BE bytes, so equality is exact
  and key order is UTF-16 ordinal order, as in the protocol.
- The schema is created and upgraded under `sp_getapplock` (exclusive, transaction-owned), versioned in `meta`;
  a database with a newer schema is refused and left untouched.

### Feed position (ADR-005 on SQL Server)

- The position is the protocol version, allocated from the feed head row read with
  `WITH (UPDLOCK, HOLDLOCK, ROWLOCK)` inside the push transaction and written back in the same transaction.
  A second writer of the same feed blocks on that row until the first commits or rolls back. Therefore version
  `n + 1` is assigned only after `n` committed (or was rolled back and is reused): a version is visible only after
  every lower version of its feed is decided, which is ADR-005's committed-prefix rule.
- Creating a feed row uses the same hints on the `(collection, scope)` unique index, so two first writers of a
  new feed serialize on the key range instead of racing.
- Why not `rowversion` or `IDENTITY` alone: both are assigned when a row is written, not when the transaction
  commits. A transaction that wrote `rowversion` 10 and is still open lets another commit 11; a reader that
  returns 11 and checkpoints there never sees 10 when it commits. `IDENTITY` has the same gap, and its values are
  also lost on rollback and can jump after a restart. Neither is a feed position (improvement plan non-goal).
  Timestamps and in-process locks fail for the same reason, or across processes.
- Pull reads `documents` with `version > @since ORDER BY version` under READ COMMITTED in one statement. Under
  READ_COMMITTED_SNAPSHOT the statement sees one committed snapshot. Under locking READ COMMITTED it blocks on
  uncommitted rows, so it cannot pass an undecided lower version; a row updated during the scan can be read at
  its old and new version, so the authority keeps only the highest version per document in a page. The
  checkpoint is the highest version in the window, which is a committed prefix in both modes.
- Cost: one writer at a time per feed (collection and scope). Writers of different feeds do not wait for each
  other.

### Transaction enlistment

- Every write API accepts an optional caller `DbTransaction` (its `Connection` is used). When given, the authority
  enlists in it, uses savepoints for its own partial rollbacks, and never commits or rolls it back. This is how
  an EF Core `SaveChanges` and a feed append commit atomically:
  `db.Database.BeginTransaction()`, then `SaveChanges`, then the authority or publisher with
  `db.Database.CurrentTransaction.GetDbTransaction()`, then `Commit`.
- Without a caller transaction (the protocol path: HTTP endpoints, `InProcessTransport`) the authority opens its own
  connection and transaction and commits it.

### Write handler

`ISyncWriteHandler<TDocument>` (core package, provider-neutral) runs inside the authority's transaction for every
operation that passed the protocol checks (clock skew, retention horizon, `CanWrite`, `Validator`, base
version) and would otherwise be accepted. It receives `SyncWriteContext<TDocument>`: the caller
(`SyncCallContext`), the operation (submitted document, base version), the current stored document and version,
and the connection and transaction. It returns a `SyncWriteDecision<TDocument>`:

| Decision | Effect |
|---|---|
| `Accept(canonical)` | The canonical document (it may differ from the submission, for example a recomputed field; same id) is stored, versioned and returned in the outcome. The handler's own writes commit with it. |
| `Conflict()` | The handler's writes are rolled back to a savepoint; the outcome is a conflict with the current stored document (only valid when one exists). |
| `Reject(errorCode, message)` | The handler's writes are rolled back to a savepoint; the rejection receipt is stored, so a replay returns it and the replica parks the record (I19). |
| `RetryLater(errorCode, message)` | The handler's writes are rolled back; no receipt is stored, so the same operation id can be decided later. |

- A replayed operation returns its stored outcome and never calls the handler again (I04, I11).
- Tombstones: the handler performs the application's delete (usually soft) and accepts a document with
  `Deleted = true`; the feed stores a tombstone. The authority never deletes application rows.
- The handler must not commit, roll back or close the connection, and must not hold the transaction across
  network I/O (the feed lock is held for the whole push): a handler that needs an outbound call returns
  `RetryLater` and does the call elsewhere.
- An exception from the handler rolls back the whole push request; the endpoint answers `unavailable` and the
  client resends the same operations.
- Without a handler, a pass-through handler accepts the submission unchanged. The conformance suite runs with it.
- Rejection arguments (a string dictionary next to `errorCode`) are deferred to task C5: they need a wire field
  and store persistence, and v1 peers would drop them silently. Until then a handler encodes everything a UI
  needs in a stable `errorCode`.

### Server-originated changes (publisher, task B3)

Server-originated writes (API endpoints, jobs, imports, projection rebuilds) go through a publisher in the same
database and transaction, which assigns versions under the same feed lock. They never use fake operation ids or
base versions. Specified with B3.

### Shared versus per-principal documents

A writable document shared by several principals lives once, in a group or tenant scope, so concurrent editors
get a real conflict. Per-principal copies of a document (fan-out) are allowed only for read-only projections.
Read access inside a shared scope is `CanRead` (and, later, the membership index of ADR-015).

### Restore

After restoring the database, call `BeginNewEpochAsync(versionFloor)`: a new epoch in `meta`, and every feed's head
raised to at least the floor, in one transaction. It mirrors the PostgreSQL authority;
`docs/operations/disaster-recovery.md` has the procedure.

### Hints across processes

In-process commits raise `Committed`. Other server processes learn about commits from a polling notifier in the
package that reads feed heads every few seconds (no Service Broker, no extra dependency), or from a host bridge:
the host calls `NotifyCommitted(scope)` from its own bus. Hints only speed things up; losing them never loses data
(I13).

## Consequences

- One `bsync` schema per application database; backups of the application include the replication state, so
  a restore keeps both consistent (still a new epoch: replicas may hold versions the backup lacks).
- Writes to synchronized documents that bypass the authority and the publisher remain unsupported (ADR-009).
- Throughput is bounded by one writer per feed. Measure before changing the algorithm (task H).

## Verification

Required before the package is supported (task B2): the public authority conformance suite; a delayed lower
version is never skipped (T27/T28 analogue); two instances, twelve writers, one reader, committed-prefix order
(T30 analogue); rollback after the handler wrote leaves no feed row, receipt or domain write; a duplicate
operation id replays without calling the handler; a canonical document that differs from the submission reaches
the replica while a local edit made during the round trip stays pending on the new base; a handler rejection parks
only that record; scope, retention, epoch restore, groups.
