# Gap-closure listener continuation - 2026-10-17

This continues the [goal-wait checkpoint](gap-closure-goals-2026-10-17.md). The development candidate is
`0.4.0-gapclosure.20261017.10`; preview `.9` is archived without changing its bytes. This is not a stable release.
[ADR-032](../architecture/adr-032-session-listener-lifetimes.md) specifies the lifecycle and diagnostic boundaries.

## Changes

Late lease acquisition now stops before status reads or replication. Shutdown retains ownership of the callback and
its returned lease until both can be joined/disposed. Already-lost leases cannot authorize a run. Null denial and normal
disposal remain ordinary outcomes, and unrelated provider cancellation remains an error.

Coordinator hint replacements wait for their predecessors, including iterator cleanup; shutdown joins the full chain.
Queued replacements check coordinator shutdown directly, closing a race during asynchronous linked-token propagation.
Completed predecessor references and cancellation sources are released before starting the next listener. Session and
shared listeners reject late items; a canceled shared listener cannot alter fallback capability after an account switch.

The stop counter adds fixed `session_lease`, `session_hints` and `coordinator_hints` operations, with no identity tags
or implicit deadline. Hint diagnostics cover a lifetime, including reconnect backoff, rather than each retry. These
signals overlap phase/run observations and must not be summed as unique sync failures. Public APIs and persistence
formats are unchanged.

## Validation

Evidence and scripts live in `artifacts/gap-closure/listeners`. The
[validation manifest](gap-closure-listeners-validation-2026-10-17.json) preserves results, failures and exact hashes.

Seventeen new cases cover late granted/denied/lost leases, cooperative cancellation, ordinary lease outcomes, unrelated
provider cancellation, late hints, iterator disposal, unsupported streams, injected-time backoff, queued listener
shutdown, and successful account-switch handoff. They run against source and the exact candidate package.

| Check | Result |
|---|---|
| Full final source core suite, including API and process coverage | 874 passed; no failures/skips (`core-final.trx`) |
| Exact `.10` package consumers, including all seventeen new cases | 165 passed; no failures/skips (`package-final.trx`) |
| Published `.10` browser regression smoke | Six passed across Chromium, Firefox and WebKit; no failures/skips (`browser-final.trx`) |
| Published `.10` web smoke | Blob round trip, package readiness, workflow and sync succeeded |
| Package symbols/source | Six assembly/PDB pairs, 211 embedded source checksums, zero remote checks |
| Consumed package verification | Six archive/cache hashes and packaged READMEs match; three browser assets match source/package/publish |

The first focused run passed 32 cases and failed three new assertions that assumed cancellation propagation was
synchronous. The tests now wait for the callback's cancellation signal before releasing it. The second run passed 34
and failed one queued-listener case, exposing admission during coordinator shutdown before linked cancellation arrived.
The coordinator now checks its own shutdown token directly. The focused final run passed 35 cases; two further account
handoff cases are included in the full suite. An initial unreachable-code compiler warning in the test iterator was
removed. Both full core runs passed 874 cases; the final run includes the predecessor-reference cleanup change.
All intermediate TRXs remain recorded rather than being replaced by their passing successors.

Six selected behavioral regressions fail against immutable `.9` (`preview9-regressions.trx`): three late acquisition
outcomes still read storage; a canceled session hint consumes the next iterator step; and both old-listener item and
unsupported variants admit a replacement before the old provider finishes. Tests link the same source fixtures and
use public APIs already available in `.9`, with unchanged assertions. No old package is rebuilt or relabeled.

Host: Windows 10 x64, SDK 10.0.402 and default host runtime 10.0.13; browser publish uses the installed .NET WASM
10.0.12 toolchain. Browser checks are existing encryption/repair regression smoke, not browser-specific listener tests.

## Remaining boundaries

Callbacks/iterator disposal that ignore cancellation can delay shutdown indefinitely. Hosts must avoid callbacks that
block on the shutdown joining them. Manually managed session replication may continue while a shared hint replacement
waits; these changes serialize hint providers rather than all cross-account transport work. The existing coordinator-wide
unsupported-stream fallback policy is unchanged.

Idle lease-loss signals outside acquisition/runs, nested root-cause propagation, arbitrary host callbacks and durable
sub-object verification remain open. This slice adds no SQL Server, SQLCipher, real object-store, mobile-device,
independent crypto, performance or ten-run release qualification. The broader B4/B8 items remain unchecked.
