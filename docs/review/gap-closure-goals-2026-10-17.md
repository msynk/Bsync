# Gap-closure goal-wait continuation - 2026-10-17

This continues the [session checkpoint](gap-closure-session-2026-10-17.md). The development candidate is
`0.4.0-gapclosure.20261017.9`; preview `.8` is archived without changing its bytes. This is not a stable release.
[ADR-031](../architecture/adr-031-goal-wait-budgets.md) specifies behavior and cooperative limits.

## Changes

Session goal waits now use one monotonic deadline from account resolution through replica opening, local observations
and synchronous progress callbacks. Zero budget performs no I/O. Expiry returns the last completed observation with
`Reached=false`, without starting fresh reads. `SyncGoalResult.HasObservation=false` identifies an unknown pending
count when no full observation completed; aggregate results preserve that distinction.

Each wait owns its reads and progress reporting. Its cancellation or progress exception cannot cancel shared replication
or another wait. Account shutdown joins goal observations before disposing storage, including a provider that ignores
cancellation. Late replica opening disposes the returned store before constructing a transport. The fixed `sync_goal`
metric classifies explicit caller cancellation and budget expiry; account shutdown alone emits no goal stop reason.

Two full test runs exposed SQLite connection failures in different fixtures. Their teardown called `ClearAllPools`,
affecting other databases while tests ran in parallel. Teardown now releases each fixture's own database pools, and
the full suite passes with parallel execution retained. There is no production SQLite change in this continuation.

## Validation

Evidence and reproducible scripts live in `artifacts/gap-closure/goals`. The
[validation manifest](gap-closure-goals-validation-2026-10-17.json) records results, failures, source and package hashes.

Thirteen new tests cover zero/expired account resolution, late opening, lifecycle gate admission, conservative last
observations, cooperative and non-cooperative reads, caller/budget metrics, joined shutdown, independent waits,
progress exceptions, copied revision lists, unrelated cancellation and unknown aggregate observations. They run
against source and the exact candidate package. Existing API and goal tests also pass.

| Check | Result |
|---|---|
| Full source core suite, including API and process coverage | 857 passed; no failures/skips (`core-final.trx`) |
| Exact `.9` package consumers, including all thirteen new cases | 148 passed; no failures/skips (`package-final.trx`) |
| Published `.9` browser regression smoke | Six passed across Chromium, Firefox and WebKit; no failures/skips (`browser-final.trx`) |
| Published `.9` web smoke | Blob round trip, package readiness, workflow and sync succeeded |
| Package symbols/source | Six assembly/PDB pairs, 211 embedded source checksums, zero remote checks |
| Consumed package verification | Six archive/cache hashes and packaged READMEs match; three browser assets match source/package/publish |

Both independent opening regressions fail against the actual immutable `.8` package: zero budget and account
resolution that consumes the entire budget still open the replica (`preview8-regressions.trx`, two expected failures).
The baseline project links the shared store/transport fixture and uses public APIs available in `.8`; assertions are
unchanged. The old archive/cache hashes are checked separately. No earlier package is rebuilt or relabeled.

The first two full core runs each passed 856 cases and failed one SQLite case, preserved in `core-first.trx` and
`core-second.trx`. A targeted 28-case SQLite follow-up passed between them. After fixture teardown isolation, the
final full run passed all 857 cases. An earlier compile required a test type-alias correction; it produced no TRX.
These checks establish the recorded outcomes, not repeated-run stability qualification.

Host: Windows 10 x64, SDK 10.0.402 and default host runtime 10.0.13; browser publish uses the installed .NET WASM
10.0.12 toolchain. Browser cases are existing encryption/repair regression smoke, not browser-specific goal tests.

## Remaining boundaries

Lease acquisition/hint-listener diagnostics, nested stop-cause propagation and durable sub-object verification remain
open; broad B4 completion is not claimed. Started callbacks must finish before shutdown can dispose their resources.
Synchronous progress callbacks must not block on their own session's shutdown/account switch. Asynchronously
dispatched progress handlers are host-owned. No SQL Server, SQLCipher, real object-store, mobile-device, independent
crypto or performance qualification is added here. Clean-tag publication and the ten-run release gate remain open.
