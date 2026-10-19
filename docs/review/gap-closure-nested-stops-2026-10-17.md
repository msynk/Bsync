# Gap-closure nested-stop continuation - 2026-10-17

This continues the [idle-lease checkpoint](gap-closure-idle-lease-2026-10-17.md). The development candidate is
`0.4.0-gapclosure.20261017.12`; preview `.11` is archived without changing its bytes. This is not a stable release.
[ADR-034](../architecture/adr-034-nested-stop-causes.md) defines the diagnostic scope and remaining boundaries.

## Changes

Nested bounded operations preserve the observed caller, lease and budget flags from Bsync deadline tokens and known
library aliases. Workflow, background, read-model rebuild, transfer drain/attempt and goal-wait aliases are registered
for their active scopes. Zero-budget sync and goal waits also resolve their parent cause. Caller cancellation still
takes precedence over lease loss, then budget expiry when signals overlap; this is not first-event attribution.

Scope disposal freezes observations and removes token associations before normal lease disposal. Captured parent
resolvers support already-admitted children; unrelated tokens and later reuse of old tokens do not inherit a closed
scope. No ambient account/thread state or new public API is introduced. Public outcomes, exceptions, durable progress
and cooperative callback joining are unchanged; a canceled input may still produce a `Canceled` result while its
diagnostic reason is `budget_expired`.

## Validation

Evidence and scripts live in `artifacts/gap-closure/nested-stops`. The
[validation manifest](gap-closure-nested-stops-validation-2026-10-17.json) records exact source/package hashes and all TRXs.
Twenty new cases cover precedence, multilevel propagation, child-only expiry, opaque application aliases, closed
scopes, parallel unrelated tokens, transfer/read-model leases and zero-budget paths.

The initial focused run passed 40 cases and failed one: the parallel test's store used system time while its workflow
used fake time, making ownership expire before the callback ran. Sharing the fixture clock fixed the test setup.
Four additional coverage cases brought the final focused run to 45 passing cases. Both runs are preserved.

| Check | Result |
|---|---|
| Full source core suite, including API and process coverage | 902 passed; no failures/skips (`core-final.trx`) |
| Exact `.12` package consumers, including all twenty new cases | 193 passed; no failures/skips (`package-final.trx`) |
| Published `.12` browser regression smoke | Six passed across Chromium, Firefox and WebKit; no failures/skips (`browser-final.trx`) |
| Published `.12` web smoke | Blob round trip, package readiness, workflow and sync succeeded |
| Package symbols/source | Six assembly/PDB pairs, 211 embedded source checksums, zero remote checks |
| Consumed package verification | Six archive/cache hashes and packaged READMEs match; three browser assets match source/package/publish |

Three selected regressions fail against immutable `.11` (`preview11-regressions.trx`): direct deadline propagation,
a transfer attempt's nested child and a three-level workflow/background/sync call all report child `caller_canceled`
instead of `budget_expired`. The baseline links unchanged public-API tests and shared fixtures. Expected baseline
failures are retained separately from candidate validation; no earlier package is rebuilt or relabeled.
The archived `.11` core package matches both its consumed cache and the previous checkpoint's recorded hash.

Host: Windows 10 x64, SDK 10.0.402 and default runtime 10.0.13; browser publish uses the installed .NET WASM 10.0.12
toolchain. Existing browser cases exercise encryption/repair regression smoke, not browser-specific nested diagnostics.

## Remaining boundaries

Application-created linked tokens are opaque, and unregistered session/coordinator lifecycle aliases still classify
cancellation relative to their invocation. This is observed-signal attribution, not a global causal trace. Parent and
child observations can describe the same event and must not be summed as unique operations. Arbitrary noncooperative
callbacks can still delay completion and shutdown.

Durable sub-object verification and broader inventory budgets remain open. This slice adds no SQL Server, SQLCipher,
real object-store, mobile-device, independent crypto, performance or ten-run release qualification. The broader B4/B8
items remain unchecked.
