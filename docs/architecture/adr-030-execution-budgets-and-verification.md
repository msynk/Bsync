# ADR-030: Execution stop diagnostics and bounded content verification

Status: implemented, unreleased. This closes specific deadline gaps, not every B4 integration or performance gate.

## Execution boundaries

Workflow lease acquisition now consumes the same monotonic `TimeProvider` budget as the steps. A workflow checks the
remaining budget and execution token after a callback returns, including callbacks that ignore cancellation. Expiry or
lease loss returns the last observed durable progress, or null if none was observed. It does not begin a new storage
read after stopping. This snapshot can lag another worker or a commit whose acknowledgement was interrupted; callers
that need current state must explicitly call `GetAsync`. Caller cancellation continues to throw.

Read-model rebuild uses the same deadline helper and checks cancellation between inventory pages and row checks.
Transfer attempts now check their deadline after the final provider/HTTP callback and before marking the job complete.
An expired attempt retains a queued job with the existing `interrupted` code; retry verifies/reconciles already completed
local or remote work. Cancellation cannot undo an external effect. Existing fences still prevent stale owners committing.

Started callbacks and resource disposal are joined. Arbitrary provider/host code cannot be forcibly interrupted.
No budget authorizes an expired physical account owner to take over a live resource, and no cancelled return proves
that a remote request was never executed. Atomic commits that finish during cancellation require normal replay.

## Diagnostics

`bsync.execution.stops` counts at most one observed stop signal per instrumented invocation. Tags are the fixed
`bsync.operation` and `bsync.reason` values documented in [observability](../operations/observability.md). It is not a
business-outcome counter: a commit may have succeeded before a deadline expired during joined disposal. Ordinary
execution-lease disposal cancels its `Lost` token, so observation registrations must end before lease disposal.

When several signals are present at return, precedence is caller cancellation, lease loss, then budget expiry. This is
a deterministic classification, not a claim about which signal happened first. Nested deadline tokens and registered
library aliases preserve their observed parent cause under [ADR-034](adr-034-nested-stop-causes.md). Unknown host-created
linked tokens remain caller-relative; session lifecycle/transport aliases preserve their known causes. Do not sum parent and child invocations as
distinct user operations. A callback's
unrelated `OperationCanceledException` without any of these signals does not manufacture a stop reason.

Instrumented operations: account work/maintenance, background dispatch, workflows, read-model rebuild/inventory,
package preparation, upload prerequisites, transfer drains/attempts, cache owner/inventory maintenance, budgeted chunk
rotation/migration and budgeted blob verification. The session continuation below adds one-shot sync and individual
session runs. [ADR-031](adr-031-goal-wait-budgets.md) adds goal waits, and
[ADR-032](adr-032-session-listener-lifetimes.md) adds lease acquisition and hint lifetimes. Arbitrary host callbacks remain outside this
coverage; the plan's all-path diagnostics checkbox remains open.

## One-shot sync and session runs

`SyncOptions<TDocument>.TimeProvider` supplies the monotonic clock and timer for `SyncEngine.SyncForAsync`. It does
not change the document HLC, server time, or the session's separate delay clock. The original budget includes waiting
for the engine's replication gate and joined callbacks/diagnostic collection. After a late successful return, completed
run counts are retained but `HasRemainingWork` is conservatively true. An interrupted run may have committed pages
not reflected in aggregate counts; its durable checkpoint remains the resumption boundary. A zero budget performs no
storage/network work and returns remaining work. Caller cancellation always throws, including with a zero budget.
An unrelated provider `OperationCanceledException` now propagates instead of being mislabeled as deadline exhaustion.

`sync_for` stop signals follow the same precedence as other bounded invocations. `session_run` observes an individual
engine run and coordinator admission wait, with `caller_canceled` for session shutdown/network suspension or the
coordinator's supplied cancellation token, and `lease_lost` for execution ownership loss. Session runs have no new
implicit time limit. A token check after the engine returns prevents publishing successful session progress after a
late stop; it cannot undo already committed local/remote effects. Observation ends before normal lease disposal.

Paused sessions now await the infinite trigger/cancellation wait instead of treating its negative sentinel as an
already elapsed delay. A paused session refreshes local status when explicitly triggered and resumes on `Resume`;
it does not continuously query storage while idle. `Pause` still allows the current run to finish.

## Verification limits

Both `BlobVerification.VerifyAsync` overloads hash with a 64 KiB working buffer and read at most the declared plaintext
size plus a one-byte EOF probe. A stream whose actual length differs from its reported length is rejected, including a
stream that continues producing data. Short reads are supported. The pooled plaintext buffer is cleared on return.

The new overload accepts a cooperative budget, maximum declared content bytes and optional `TimeProvider`. Oversized
content returns `ContentLimitExceeded` before storage opens. That is deferral, not corruption or permission to repair.
The deadline covers opening, reads and joined disposal; expiry and caller cancellation throw. Provider-internal reads,
decryption allocations and metadata are outside the byte allowance. This is not a physical-I/O or peak-memory claim.

No hash state is serialized and no partial hash establishes verification. An interrupted verification restarts, so the
host needs enough budget for a complete object. Durable progress across object inventories and bounded legacy full-list
APIs remain separate work; this API must not be advertised as resumable sub-object verification.

## Compatibility

No wire or persistent schema changes. The verification overload and `ContentLimitExceeded` enum value are additive;
only the new overload returns that value. Existing verification now rejects dishonest-length streams promptly. Existing
background/read-model/transfer result shapes are retained. Public API baselines and package consumer tests cover the
addition. The two deadline defects have executable reproducers against immutable preview `.6`.
