# Recoverable multi-step work and background dispatch

Prefer one aggregate document when a user edit needs one atomic local commit. Collection dependency order is not a
cross-collection transaction. Server rules still validate every prerequisite independently.

For effects spanning collections and blobs, `RecoverableWorkflow` records a versioned step definition and progress in
the account's orchestration store before calling a registered step. The callback receives a stable account/workflow/
step operation identity. It must deduplicate that identity at its effect boundary. A crash after an effect but before
the progress commit repeats the step. Completed steps are skipped; dependency failures stop the drain; permanent
failures require `RetryFailedAsync` after a host decision. No automatic compensation or distributed rollback occurs.

Keep old registered definitions available until their work completes; changing the version or step order of an
existing workflow is refused. Store request payloads in the application's durable documents, keyed by workflow ID;
do not capture an in-memory UI object as the only copy of a step's input. Never turn this helper into a parallel
ordinary-document outbox. Its role is progress between independently idempotent actions.

```csharp
var workflow = new RecoverableWorkflow(accountMetadata, account);
await workflow.RunAsync(workId, "attachment-publication/1", steps,
    TimeSpan.FromSeconds(20), cancellationToken);
```

An external receiver that cannot deduplicate must be described as at-least-once. Canceling a local wait cannot recall
an accepted server action. Business rejection, protocol rejection and retryable dependency failures remain separate.
The current helper does not implement a transactional server command ledger; use the caller's database transaction
for domain mutation, command deduplication and result publication.

## Portable host scheduling

`BackgroundWorkDispatcher.RequestAsync` persists a request before calling `IBackgroundWorkScheduler`. Unsupported
constraints, deferral, cancellation or scheduler failure leave it pending. `Scheduled` means accepted by a host, not
executed. A new request arriving during a run cannot be acknowledged by that older run.

An OS-delivered worker and foreground/resume fallback call `RunAsync(account, budget, execute)`. The callback opens a
fresh account service lifetime, obtains available keys/credentials, performs bounded sync/transfer work and returns
an execution outcome. Never depend on a page/WebView remaining alive. The execution lease prevents competing runners
from independently draining the same request. Cancellation is cooperative: every callback must observe its token and
join its child work before returning.

The adapter reports headless, network and charging capabilities honestly. Scheduling constraints are host hints;
foreground reconciliation remains necessary. No Android WorkManager or iOS BGTaskScheduler adapter is yet qualified
by this implementation. Closed-tab browser execution remains unsupported. The portable dispatcher tests establish
durable request/ownership semantics, not OS task delivery, locked-device key access or physical-device behavior.

Workflow budgets include execution-lease acquisition. After budget expiry or lease loss, `RunAsync` returns the last
observed durable progress, or null when none was observed, without beginning a new storage read. This result can lag
the current record; explicitly call `GetAsync` when the host has a new read budget. A step that returns late is replayed
with the same operation identity instead of being newly acknowledged after its deadline. The fixed-tag
`bsync.execution.stops` metric separates observed stop reasons; see [observability](../operations/observability.md).
