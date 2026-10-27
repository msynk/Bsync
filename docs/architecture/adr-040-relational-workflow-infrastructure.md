# ADR-040: SQL Server workflow helpers with caller-owned transactions

Status: experimental public integration with LocalDB and package-consumer coverage (B07, 2026-10-24).

Use the existing optional `Bsync.Server.SqlServer` package and `Bsync.Server.SqlServer.Workflows` namespace. Portable
Bsync gains no EF, hosting or database dependency. The existing authority's `DbTransaction` overloads remain the
publication/enlistment contract; helpers accept the exact caller `SqlConnection`/`SqlTransaction` and never commit it.
EF hosts use `Database.GetDbConnection()` and `CurrentTransaction.GetDbTransaction()` inside their execution strategy,
with a fresh unit of work on retry. Retry the same immutable command identity after a lost commit response.

Acquire all affected feed locks in deterministic order before domain, ledger and outbox rows. Callbacks perform
database work only in the supplied transaction. Application authorization, canonical payload serialization, membership,
projection reads, credentials, business refusal and receiver policy remain host-owned.

`SqlServerCommandLedger` binds scope/command ID, actor and payload fingerprint. Concurrent calls range-lock identity;
domain execution and the durable result share a transaction. Expired results retain immutable identity and never
reexecute. Result/payload bounds are 1 MiB UTF-8 each. A callback failure rolls back to a savepoint; if the transaction
is doomed, the host must abandon it. Transport receipt expiry does not expire this ledger.

`SqlServerProjectionOutbox` coalesces requests with a generation GUID in the domain transaction. A worker acquires
publication locks, conditionally consumes that generation, rereads canonical state and publishes in one transaction.
Failure restores work. Concurrent replacement invalidates stale failure updates. Five failed attempts poison only
that generation; later domain requests create a new generation. This queue never dispatches external effects.

The sample effect outbox/delivery policy now lives in the package. Attempts are durable before any remote call;
30-second claims fence acknowledgements, and a cooperative 20-second receiver budget bounds normal dispatch. No
external call runs inside a database transaction. Deduplication/status lookup are explicit receiver capabilities.
Without either, ambiguous delivery becomes reconciliation-required. Receiver identity is durably bound before the
first send; changing it requires reconciliation without contacting the replacement. A legacy unbound row already
attempted cannot safely be rebound. The delegate overload continues to require host-supplied durable deduplication.

Migrations are explicit deployment operations. New helpers install/check version 1 in `dbo.BsyncWorkflowSchema`;
unknown versions refuse runtime use and migration without deleting records. Install migrations with deployment
privileges, coordinate concurrent application versions, then run workers under minimal data privileges. Do not invoke
DDL from startup request paths. Earlier sample effect rows are retained by the explicit migration.

`Bsync.Server.AspNetCore` provides `AddBsyncWorkflowWorker`: one serial callback with a fresh asynchronous DI scope
per batch, explicit interval/failure backoff, and cancellation followed by joined disposal. Shutdown waits for the
active callback even if the host stop-wait token expires; callbacks must cooperate with cancellation. A noncooperative
external operation can therefore hold shutdown open. No worker starts merely because a package is referenced, and
registration never runs migrations. Errors log only exception type; payloads and signed URLs are not logged.
`Bsync.WorkflowWorkers` exposes `bsync.worker.batches` with configured non-secret worker name and outcome. `Bsync.RelationalWorkflows` exposes
`bsync.relational.operations` for committed projection/capture/effect outcomes, without scope/user labels. Operational
queries must also monitor pending age, attempts, retry deadlines, poisoned rows and reconciliation outcomes.

The package-only `Bsync.PackageConsumer.Workflows` program creates a private test database and exercises command
replay, projection publication, capture health and joined DI worker lifetime through packaged public APIs. It contains
no copied sample infrastructure. Real LocalDB tests cover EF execution-strategy retries before commit/after lost commit
reply and eight concurrent immutable-command retries. Broader deadlock, multi-feed and deployment qualification remain open; the existing application samples retain application-specific ledgers/projections.

See the [hardening continuation](../review/capabilities-closure-2026-10-26.md).
