# Gap-closure idle-lease continuation - 2026-10-17

This continues the [listener checkpoint](gap-closure-listeners-2026-10-17.md). The development candidate is
`0.4.0-gapclosure.20261017.11`; preview `.10` is archived without changing its bytes. This is not a stable release.
[ADR-033](../architecture/adr-033-idle-lease-observation.md) defines ownership observation and scheduling limits.

## Changes

An admitted execution lease now has one loss observer, covering idle and paused periods as well as active runs. Loss
wakes the existing coalescing trigger, so ordinary idle sessions can retire the lease and attempt acquisition without
waiting for the next interval. Pause stays paused, and server `Retry-After` still prevents early execution. The observer
is detached and joined before session-owned lease disposal, excluding the provider's normal disposal cancellation.

The fixed `session_ownership` stop operation reports the loss signal independently of acquisition/run observations.
It adds no identity tags or implicit deadline. Normal release emits no ownership signal; a lease already lost at
admission remains classified by acquisition only. Existing run-specific tests filter their listener to the operations
they assert, while new ownership tests cover overlap with active runs. Public APIs and persistence formats are unchanged.

## Validation

Evidence and scripts live in `artifacts/gap-closure/idle-lease`. The
[validation manifest](gap-closure-idle-lease-validation-2026-10-17.json) records results and exact source/package hashes.

Eight new tests cover idle reacquisition/denial, pause without polling, fake-clock server backoff, normal shutdown and
suspension, active-run diagnostic overlap and already-lost admission. The focused suite passed all 37 cases on its
first run. The full source suite also passed on its first run; there were no candidate test failures in this slice.

| Check | Result |
|---|---|
| Full source core suite, including API and process coverage | 882 passed; no failures/skips (`core-final.trx`) |
| Exact `.11` package consumers, including all eight new cases | 173 passed; no failures/skips (`package-final.trx`) |
| Published `.11` browser regression smoke | Six passed across Chromium, Firefox and WebKit; no failures/skips (`browser-final.trx`) |
| Published `.11` web smoke | Blob round trip, package readiness, workflow and sync succeeded |
| Package symbols/source | Six assembly/PDB pairs, 211 embedded source checksums, zero remote checks |
| Consumed package verification | Six archive/cache hashes and packaged READMEs match; three browser assets match source/package/publish |

Both idle-wakeup cases fail against immutable `.10` (`preview10-regressions.trx`, two expected timeouts): with the fake
clock held fixed, the session does not attempt reacquisition after loss. The baseline links the same public-API tests
and shared fixtures without changing assertions. The old archive/cache hashes are checked against the previous
checkpoint, and no earlier package is rebuilt or relabeled.

Host: Windows 10 x64, SDK 10.0.402 and default host runtime 10.0.13; browser publish uses the installed .NET WASM
10.0.12 toolchain. Browser cases are existing encryption/repair regression smoke, not browser-specific ownership tests.

## Remaining boundaries

Loss observation does not guarantee immediate takeover: pause, server backoff and acquisition denial still apply.
Disposal and arbitrary host callbacks remain cooperative; metric listeners must not synchronously block on shutdown
of the operation delivering their notification. Ownership and run signals can describe the same loss and must not be
summed as unique failures.

Nested stop-cause propagation and durable sub-object integrity progress remain open. This slice adds no SQL Server,
SQLCipher, real object-store, mobile-device, independent crypto, performance or ten-run release qualification. The
broader B4/B8 items remain unchecked.
