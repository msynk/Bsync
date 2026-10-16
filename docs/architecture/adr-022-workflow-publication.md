# ADR-022: Recoverable workflows, command identity and controlled publication

Status: Implementation in progress, 2026-10-16.

## Local workflows and host execution

`RecoverableWorkflow` persists a versioned ordered step definition and progress before effects. It uses the account
metadata execution lease and fences progress commits. Effects receive a stable account/workflow/step identity and
must be idempotent at their own boundary. Crash-after-effect/before-progress is replay, not exactly-once execution.
Retryable dependencies stop the current drain; terminal failures require an explicit retry decision. Definition
changes are refused for existing work. Aggregate documents remain the default for an atomic editable unit.

`BackgroundWorkDispatcher` persists account requests before asking a host scheduler. Scheduling acceptance is a
different outcome from execution. A request generation prevents an older worker from clearing newer requested work.
Deferral, unsupported constraints, key/credential failures and cancellation preserve queued work. Foreground/resume
is the fallback. Android/iOS adapter implementation and physical-device qualification remain separate open work;
the portable runner cannot establish OS delivery guarantees.

## Command execution recipe

The portable `IntentRequest`/`IntentDefinition<TPayload>` contracts snapshot immutable requests and bind explicit
kind/version pairs to generated JSON metadata. `IntentExecutionResult` remains server-owned and separate;
`IntentStatus` distinguishes local persistence, transport and execution. These types add no replicated outbox or
automatic server dispatch. Tasks completion/rename use an adapter preserving their existing wire shape and fingerprint;
completion/acknowledgment request persistence is also tested across SQLite restart. Domain authorization, ledger
transactions, retention and external-effect delivery remain application responsibilities.

The Tasks sample now records scoped command identity, a fingerprint of immutable schema-1 request fields and the
authenticated actor, and the canonical result in `dbo.IntentExecutions`. It commits this row in the same SQL Server
transaction as the domain mutation and feed publication. This is independent of transport operation receipts.
Replays with changed request fields/actor fail; result fields submitted by clients are overwritten. Results expire
after 90 days, but compact identity/fingerprint rows must be retained indefinitely. Expired results refuse execution
with `intent-result-expired`; deleting ledger identities would invalidate the guarantee.

This is an application-owned SQL Server recipe tested with completion and rename actions, not a universal intent
framework. It does not promise exactly-once external side effects. External dispatch needs an application-transaction
outbox and a receiver idempotency key (or an explicit at-least-once contract). Existing deployments require a migration
and a reviewed backfill/replay policy for historical commands: missing historical execution identities cannot be
reconstructed from nothing. The sample's create-if-missing DDL is not a production EF migration.

An optional `ExternalEffectOutbox` now demonstrates the separate external-effect path: enqueue immutable work in
the caller transaction, dispatch outside transactions with a stable receiver idempotency key, then commit result
publication and fenced acknowledgement together. Thirty-second database-clock claims can overlap remote execution
after expiry; receiver deduplication remains mandatory for at-most-once effects. Five failed attempts quarantine work.
Completed identities are retained. This recipe requires an explicit host worker and result publisher and does not
automatically add notifications or external business behavior to Tasks. See the intent pattern for integration/retention.
The new ledger/outbox primary keys are nonclustered so the declared pair of 256-character Unicode IDs fits their index
key budget. Existing installations with clustered versions need an explicit index migration before using full-length
identities; create-if-missing does not silently rebuild live indexes. Legacy sample domain-table key limits are unchanged.

## Publication and lock ordering

`SqlServerSyncAuthority.AcquirePublicationLockAsync` exposes the existing feed lock in a caller transaction without
publishing or notifying. API writers acquire feed locks before domain rows, matching replication. The Tasks sample
uses intent feed, then task feed, then ledger/domain rows. Other applications must define a consistent total order
for every collection/scope they combine and retry the whole transaction on transient database failure.

The optional `TaskProjectionOutbox` recipe coalesces refresh requests by scope/task. It stores no stale projection
payload. A drain reads bounded candidates, locks the feed, conditionally consumes the selected request generation,
rebuilds current domain state and publishes/deletes in one transaction. Rollback leaves both request and feed intact;
a newer request cannot be acknowledged by an older enumerated generation. Notifications occur after commit and are
not correctness-critical. Five failed attempts quarantine an item; failures do not store exception/domain text.

This path is eventual domain-to-feed publication, unlike synchronous enlisted publication. It captures only explicit
requests from registered writers. It does not capture raw SQL, bulk writes, related-table visibility changes or CDC
automatically. `TaskProjectionReconciler` provides a bounded complete tenant repair: acquire the task feed lock before
reading domain rows/attachments, reject limit overflow, replace the complete projection and consume covered requests
in that same transaction. All domain writers must follow the feed-before-domain order. This exclusive boundary blocks
writers until commit, so no concurrent change can fall into a snapshot/catch-up gap. It reconciles absent/deleted tasks
and attachment changes, but is not an online large-feed rebuild or a per-user membership projector. Rollback tests inject
a failure after the first projection and verify that both feed and outbox remain unchanged; retry completes the repair.
Membership-aware projection samples, lag metrics and production execution-strategy integration remain open. Never
call ReplaceScopeAsync with an incomplete snapshot.
