# ADR-034: Nested execution stop causes

Status: Implemented for cooperative deadlines and registered library aliases; broader B4 coverage remains open.

## Decision

Keep the observed caller, lease and budget signals when a nested bounded invocation receives a Bsync deadline token.
A private registry associates exact tokens with their active observation scopes. Workflow, background work, read-model
rebuild, transfer drain/attempt and goal-wait aliases register before invoking callbacks and unregister before disposing
their leases. Direct deadline tokens register automatically. Zero-budget sync and goal waits use the same lookup.
There is no ambient thread/account state or public token-provenance API.

Child scopes capture their parent's resolver at admission. Closing a scope freezes its observed flags and removes the
registry entry, so a child already admitted retains its cause without later interpreting normal lease disposal as loss.
A new invocation using an old token after scope closure gets ordinary caller-relative classification. Lease aliases
also read the live loss token so cancellation callback ordering cannot misclassify a nested operation.

Several observed signals still use caller cancellation, lease loss, then budget expiry precedence. This classifies
signals at the observation boundary, not their chronological order. A child-only expiry does not invent a parent stop.
Parallel unrelated tokens do not inherit another invocation's cause. The counter and its fixed tags are unchanged.

This affects diagnostics only: existing return values, exception behavior, durable progress and cooperative joining
remain unchanged. A background operation can still return `Canceled` for its canceled input token while its diagnostic
reason identifies the enclosing budget expiry. Parent and child counts can describe one event; do not sum them as
unique user operations.

## Boundaries and evidence

Application-created linked tokens are opaque. Session run, coordinated admission, coordinator listener and session
transport aliases now register active scopes too. Locally canceled aliases with no observed parent signal retain
caller-relative classification. Passing a token alone does not introduce
a hard deadline or forcibly interrupt callbacks. Durable chunk authentication is addressed by [ADR-035](adr-035-generation-integrity-maintenance.md).

Twenty new source/exact-package cases cover precedence, three-level propagation, lease aliases, zero budgets, scope
cleanup, unrelated parallel calls and unchanged outcomes. Three regressions reproduce against immutable preview `.11`.
See the [execution report](../review/gap-closure-nested-stops-2026-10-17.md) for results and qualification limits.
