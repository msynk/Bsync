# ADR-009: Server integration and change capture

- **Status:** Accepted; PostgreSQL authority implemented with Npgsql (`Bsync.Server.PostgreSql`, 2026-09-28)
- **Invariants:** I04, I05, I06, I18

## Context

The feed must include every change to synchronized data, however it was written: replication pushes,
ordinary API endpoints, background jobs, admin tools, raw SQL.

## Alternatives

1. **Controlled authoritative write service.** All writes to synchronized collections go through one
   application service that performs the conditional write, records the operation receipt and appends to
   the feed in one transaction.
2. **Database triggers** populating a change table. Captures raw SQL writes; logic lives in the database.
3. **CDC** (PostgreSQL logical decoding, SQL Server change tracking, Debezium outbox). Captures
   everything in commit order; heavy operational footprint.

## Decision

- v1 uses (1). The same service is called by the HTTP endpoints and by in-process callers (Blazor Server,
  background jobs), so authorization and validation are identical (I18).
- One relational provider first: **PostgreSQL**, chosen for transactional DDL, `xid8`/snapshot functions usable for the feed watermark (ADR-005),
  robust unique constraints for receipts, and wide hosting availability. SQL Server is the likely second
  provider. (This decision originally named an EF Core package, `Bsync.Server.EntityFrameworkCore`; it was never
  created. The provider uses Npgsql directly; see Implementation below.)
- Writes that bypass the service (raw SQL, bulk tools, other applications) are **unsupported** until a
  tested capture mechanism (2 or 3) exists; documentation says so explicitly.
- Correctness never relies on a process lock; concurrency is enforced by conditional `UPDATE … WHERE
  version = @base` and unique indexes on `(scope, operation_id)` and `(scope, document_id)`.

## Implementation (2026-09-28)

`PostgreSqlSyncAuthority<T>` is the controlled write service of alternative 1, on Npgsql rather than EF Core:
- The protocol needs a handful of statements (feed lock, conditional write, receipt insert). EF Core adds a
  dependency and change tracking without helping with the locking.
- Applications that keep their domain model in EF Core call the authority (in-process or over HTTP) for
  synchronized collections. Writes that bypass it stay unsupported, as decided above.

## SQL Server (2026-10-05)

ADR-014 adds the second provider, `Bsync.Server.SqlServer`, on the application's own database, with a write handler
in the authority's transaction and a publisher for server-originated writes (both also part of alternative 1).

Implementation details:
- Documents are stored as their exact JSON text, per (collection, scope, id).
- Receipts are keyed by (collection, scope, operation id).
- Ordering follows ADR-005.
- Commit hints use `LISTEN`/`NOTIFY`, so they cross server processes; the multi-process browser test runs two
  notes-server processes on one database.
- Retention and epochs: `PurgeTombstonesAsync`, `PurgeReceiptsAsync`, `BeginNewEpochAsync`.
- The schema is versioned in `bs_meta`, created under an advisory lock, and a newer schema is refused.

Not done: SQL Server; trigger- or CDC-based capture; a throughput benchmark on PostgreSQL.

## Consequences

Phase 4 cannot be completed on a machine without PostgreSQL. The EF Core in-memory provider is not an
acceptable substitute for concurrency and ordering tests.
