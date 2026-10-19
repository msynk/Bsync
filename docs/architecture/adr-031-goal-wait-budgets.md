# ADR-031: Goal-wait deadlines and observation ownership

Status: implemented, unreleased. Invariants: I08, I15, I16, I24. No document wire or persisted schema change.

## Decision

Each `SyncSession.SyncAsync` goal wait owns one monotonic `CooperativeDeadline` using the session's `TimeProvider`.
`LocalSyncCollection.SyncAsync` starts that same budget before resolving the account. Resolution, lifecycle gate
admission, opening/attaching the replica, goal reads, and synchronous progress callbacks consume it. A zero budget
returns unreached without resolving an account or opening storage/network resources. Caller cancellation throws.
Unrelated provider cancellation is not translated into budget exhaustion.

The shared replication loop only records completed run counts and signals waiters. Each caller performs its own
cancellable observations and progress callbacks. Expiry/cancellation of one wait does not cancel shared replication,
another wait, or an opening owned by another caller. Run notifications coalesce while accumulated work counts are
retained. An already-satisfied local acceptance goal may complete on its initial observation without a new run;
catch-up goals still require a run started after registration.

Before and after each provider read the wait checks cancellation. After a non-cooperative callback returns, expiry
cannot begin the next read or claim success. No fresh evaluation begins after the budget expires. Instead the result
is the last completed observation with `Reached=false`; this can be stale and its separate reads are not a database
snapshot. Interrupted run work may not be reflected in that observation. The store's checkpoints remain authoritative.

`SyncGoalResult.HasObservation` is additive and defaults to true for existing manually constructed results. False
means no full goal observation completed: `Pending=0` is unknown, requested revisions are unconfirmed, and the wait
is unreached. `Combine` preserves false if any input is unknown; its pending sum then covers only observed inputs.
Consumers must check this flag before treating zero as evidence of an empty queue. The goal's requested revision
list is copied at invocation so caller mutation cannot weaken an active wait.

## Resource lifetime

Started callbacks are joined, including providers that ignore cancellation. Replica opening that returns after its
token is canceled closes the newly returned store before constructing the transport. A late lifecycle attachment is
disposed through normal session shutdown. Account shutdown/switch cancels and joins registered goal observations
before disposing the replica store. Shutdown can therefore exceed a budget while a callback remains active; the
library does not abandon it or claim hard interruption.

Progress now executes in its caller's wait rather than inside the shared session loop. A synchronous progress callback
exception faults that wait only. Hosts must not synchronously block a callback on shutdown/account switching of its
own session: shutdown joins the callback. An asynchronously dispatched `Progress<T>` handler has its own host-owned
lifetime and is outside the synchronous reporting boundary.

## Diagnostics and compatibility

`bsync.execution.stops` adds the fixed operation `sync_goal`, with explicit caller cancellation taking precedence over
budget expiry. Session account shutdown alone ends the wait unreached and emits no goal deadline/caller signal;
network-run lifecycle and lease signals remain classified separately as `session_run`. A wait survives temporary
execution lease loss and may complete when shared replication resumes.

The public method signatures and positional result constructor remain intact. Result snapshots after timeout and
zero-budget behavior change as described above. `HasObservation` changes record equality and may appear in
application serialization, but these local results are not document protocol messages. No migration is required.
[ADR-032](adr-032-session-listener-lifetimes.md) extends lease acquisition/hint-listener diagnostics.
Resumable sub-object verification and external qualification remain open.

The new fake-clock, provider-gate and shared-wait tests run both against source and exact preview packages. Separate
regressions against immutable preview `.8` demonstrate that zero/expired account-resolution budgets previously still
opened a replica. The execution report preserves validation failures and identifies the candidate tested.
