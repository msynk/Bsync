# ADR-033: Admitted lease loss between session runs

Status: implemented, unreleased. No public API, wire or persisted schema changes.

## Decision

After admitting a live `ISyncExecutionLease`, a session registers one observer on its `Lost` token. Loss requests a
sync through the existing coalescing trigger and records a fixed `session_ownership` operation on `bsync.execution.stops`.
This covers idle intervals and paused sessions as well as active runs. A lease already lost at admission is rejected
and classified by `session_lease`; it never starts an admitted ownership lifetime.

An ordinary idle wait wakes without advancing the clock. Before the next acquisition, the loop joins the observer and
disposes the old lease. If acquisition is denied, normal follower polling applies. Pause is preserved: a loss can refresh
paused status once but cannot start replication or acquisition. The canceled token does not repeatedly wake the loop.
The lost lease is retired on resume or shutdown. Server `Retry-After` waits continue to ignore triggers until their
original delay completes, including the ownership-loss trigger. Loss observation is prompt, but takeover/network work
remains subject to pause and server backoff.

All session-owned lease release paths detach and join the observer before initiating lease disposal. Lease providers
may cancel `Lost` during ordinary disposal; this must not count as involuntary ownership loss or wake a later account.
An observer already running is joined before lease/resource disposal. Plain `IAsyncDisposable` leases without a loss
token retain their existing behavior.

## Diagnostics

`session_ownership` records one callback signal per admitted lease. It uses only the existing `bsync.operation` and
`bsync.reason` tags; there are no identity, account, token or endpoint tags. The reason is `lease_lost`, or
`caller_canceled` if the session's network cancellation is already requested when the signal is recorded. A normally
released lease emits neither; this is not a count of all lease lifetimes or session shutdowns. There is no implicit
deadline and no `budget_expired` outcome.

An active run may also report `session_run=lease_lost`; acquisition and ownership signals can overlap at an admission
race. These are separate observations, not unique business failures. Filter by operation rather than summing them.
Existing run-specific tests now filter their listener to the run/one-shot operations they assert; dedicated ownership
tests verify the new phase and its overlap with a run.

## Limits and evidence

The observer is synchronous, performs no storage I/O, and only schedules existing session work plus the metric signal.
Metric listeners must not block on shutdown of the operation delivering their callback. Lease disposal and other host
callbacks remain cooperative and can delay shutdown. Nested deadline/registered-alias causes are addressed separately
by [ADR-034](adr-034-nested-stop-causes.md); opaque host aliases remain caller-relative.
Durable chunk scrubbing is covered by [ADR-035](adr-035-generation-integrity-maintenance.md); external qualification remains open.

Eight new source/exact-package tests cover idle reacquisition/denial, pause without polling, fake-clock `Retry-After`,
normal shutdown/suspension, active-run overlap and already-lost admission. Two idle-wakeup cases reproduce against
immutable preview `.10` without advancing the fake clock. See the execution report for exact results and artifact hashes.
