# ADR-032: Lease admission and joined hint-listener lifetimes

Status: implemented, unreleased. No public signature, wire or persisted schema changes.

## Decision

A session checks its network-lifetime cancellation token before and after `AcquireLease`. Any returned lease is retained
by the session even when the callback finishes late: shutdown joins the callback and disposes the lease before closing
the replica. A canceled acquisition cannot begin status reads or replication. An already-lost `ISyncExecutionLease`
causes follower behavior without starting an engine run. A null lease remains an ordinary follower outcome, and an
unrelated provider cancellation remains an error handled by the existing attention/retry path.

Session and multiplexed hint listeners check cancellation after each yielded item and on normal stream completion.
An item returned after cancellation cannot request another sync. Iterators are disposed and their asynchronous cleanup
is joined. No implicit timeout is added: hints remain optional acceleration, and interval reconciliation is authoritative.

When a coordinator switches hint accounts, it cancels the previous listener and chains the replacement behind its
completion. Shutdown joins the entire chain, including a retired provider that ignores cancellation. A queued listener
checks both its own token and the coordinator shutdown token before invoking the provider, so asynchronous linked-token
propagation cannot admit new work during shutdown. Completed predecessor references and cancellation sources are
released before the replacement listener runs. The current source is released after shutdown joins its task.

A canceled listener's `NotSupportedException` cannot set the shared fallback flag or start per-session listeners.
An active server's unsupported response still uses the existing per-session fallback. This does not reset the existing
coordinator-wide fallback policy on every account switch. Hosts using different server capabilities should scope their
coordinators accordingly. Session replication itself can start while a replacement shared hint listener waits; this
change serializes hint providers, not all network work across manually managed accounts.

## Stop diagnostics

`bsync.execution.stops` adds these fixed operation values, with only `bsync.operation` and `bsync.reason` tags:

| Operation | Observation boundary | Reasons |
|---|---|---|
| `session_lease` | One acquisition invocation, through post-return ownership checks | `caller_canceled`, otherwise `lease_lost` for a returned lost lease |
| `session_hints` | One listener lifetime, including reconnect backoff and iterator cleanup | `caller_canceled` on network suspension or account/session shutdown |
| `coordinator_hints` | One scheduled listener lifetime, including predecessor join, reconnects and iterator cleanup | `caller_canceled` on replacement, suspension or coordinator shutdown |

No stop is emitted for successful/denied acquisition, normal lease disposal, unsupported hints that finish while active,
or an unrelated provider cancellation. Cancellation is evaluated at the observation boundary; a concurrent shutdown
can legitimately classify a finishing listener. These operations have no implicit time budget and do not emit budget
expiry. Hints are independent of replication ownership, so execution lease loss does not terminate/classify a listener.
Acquisition and run signals are phase observations; do not sum them as unique user actions or business outcomes.

## Limits and validation

Callbacks and iterator disposal that ignore cancellation can delay shutdown indefinitely. They must not synchronously
wait on shutdown/account switching of the coordinator/session that is joining them.
[ADR-033](adr-033-idle-lease-observation.md) adds admitted ownership-loss observation between runs. Arbitrary host
callbacks, nested root-cause propagation and durable sub-object verification remain outside this change; the broad
B4 all-path item stays open.

Seventeen source/exact-package tests cover late/null/lost leases, ordinary outcomes, unrelated cancellation, hint
backoff with injected time, unsupported streams, late items, queued replacement shutdown and successful account handoff.
Six behavioral regressions are reproduced against immutable preview `.9`; the review report records failures and exact
candidate provenance. These are local correctness tests, not mobile, real-service or release qualification.
