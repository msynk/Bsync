# Gap-closure session continuation — 2026-10-17

This continues the [publication checkpoint](gap-closure-publication-2026-10-17.md). The new development package
candidate is `0.4.0-gapclosure.20261017.8`; preview `.7` is archived without changing its bytes. This is not a stable
release. [ADR-030](../architecture/adr-030-execution-budgets-and-verification.md) specifies behavior and limits.

## Changes

Paused sessions previously interpreted the infinite wait sentinel as an already elapsed delay, continuously querying
storage. They now sleep until a trigger or cancellation; resume and shutdown wake the wait.

`SyncForAsync` now uses `SyncOptions<TDocument>.TimeProvider` for one monotonic deadline, including gate admission
and joined callbacks. Zero budget causes no I/O. A late successful return conservatively reports remaining work while
retaining completed run counts. Caller cancellation and unrelated provider cancellation propagate; only actual budget
expiry returns remaining work. Partial-run counts can underreport committed pages; durable checkpoints remain valid.

Stop diagnostics now include `sync_for` and `session_run`, with fixed operation/reason tags. Session runs distinguish
lifecycle/coordinator cancellation from execution lease loss, exclude normal lease disposal, and check the token after
the engine returns before publishing successful progress. No implicit session-run time limit is added.

## Validation

Evidence and reproducible scripts live in `artifacts/gap-closure/session`. The
[validation manifest](gap-closure-session-validation-2026-10-17.json) records exact results, source and package hashes.

The focused source run passed 18 cases: 12 new execution tests plus six public API baselines. Coverage includes idle
paused storage reads, resume/stop, zero-budget caller precedence, unrelated cancellation, replication gate expiry,
fake-clock network expiry, late diagnostic callbacks, normal completion/run limits, lease loss and ordinary disposal.
The initial build needed a missing test namespace import; no test result was produced by that failed compile.

| Check | Result |
|---|---|
| Full source core suite, including API and process coverage | 844 passed; no failures/skips (`core-final.trx`) |
| Exact `.8` package consumers, including all twelve new cases | 135 passed; no failures/skips (`package-final.trx`) |
| Published `.8` browser regression smoke | Six passed across Chromium, Firefox and WebKit; no failures/skips (`browser-final.trx`) |
| Published `.8` web smoke | Blob round trip, package readiness, workflow and sync succeeded |
| Package symbols/source | Six assembly/PDB pairs, 211 embedded source checksums, zero remote checks |
| Consumed package verification | Six archive/cache hashes and packaged READMEs match; three browser assets match source/package/publish |

Both regressions were reproduced against the actual immutable `.7` package (`preview7-regressions.trx`, two expected
failures). During the paused observation window its count calls increased from 96 to 109,928; this is fault evidence,
not a performance benchmark. The unrelated cancellation case returned rather than throwing. The baseline fixture
copies the new tests with only the unavailable `SyncOptions.TimeProvider` initializers removed for compilation;
the two selected cases do not depend on fake time and their assertions are unchanged. Original `.7` archive/cache
hashes are checked separately. No earlier package is rebuilt or relabeled.

Host: Windows 10 x64, SDK 10.0.402 and default host runtime 10.0.13. Browser publish uses the installed .NET WASM
10.0.12 toolchain. These are local correctness checks, not performance/device or ten-run release qualification.

## Remaining boundaries

Session lease acquisition, hint-listener lifetime and goal waits are still outside stop classification. Goal-wait budget
semantics and durable sub-object integrity progress remain open; the broad B4 items are not marked complete. Started
host callbacks are joined and cannot be forcibly interrupted. No SQL Server, real object-store, mobile-device or
independent crypto qualification is added by this slice. The earlier publication report retains its own SQL evidence.
