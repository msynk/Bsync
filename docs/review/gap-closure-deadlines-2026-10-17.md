# Gap-closure deadline continuation — 2026-10-17

This continues the [ownership checkpoint](gap-closure-ownership-2026-10-17.md). The development candidate is
`0.4.0-gapclosure.20261017.7`; earlier previews are preserved without byte/version reuse. This is not a qualified
stable release. [ADR-030](../architecture/adr-030-execution-budgets-and-verification.md) defines the behavior and limits.

## Implementation

Workflow ownership acquisition now consumes the original budget, and interruption returns last observed durable
progress without issuing an unbudgeted recovery read. Read-model rebuilding shares the monotonic deadline helper.
Transfer attempts check the remaining budget after their final callback before acknowledging completion, preserving
replayable queued jobs after expiry even when a provider ignores cancellation.

`bsync.execution.stops` distinguishes caller cancellation, lease loss and budget expiry for bounded invocations,
including work that returns late. Tags contain only fixed operation/reason values. Normal lease disposal is excluded;
simultaneous-signal precedence and nested-operation interpretation are documented. This is a signal counter, not a
claim that no work committed.

Full-content verification now reads at most the declared plaintext length plus an EOF probe with a 64 KiB working
buffer, supports short reads and rejects inconsistent stream lengths. The new budgeted overload rejects oversized
content before opening storage and includes joined disposal in its deadline. `ContentLimitExceeded` means deferral,
never corruption or repair authorization. Full hashes restart after interruption; provider-internal I/O/allocations
are outside the logical byte allowance.

## Validation and provenance

Commands, logs and all passing/failing TRXs are retained under `artifacts/gap-closure/deadlines`. Exact artifacts,
sources and results are recorded in the [validation manifest](gap-closure-deadlines-validation-2026-10-17.json).

| Check | Result |
|---|---|
| Final source core suite, including API baselines and process tests | 832 passed, zero failures/skips (`core-final.trx`) |
| Focused budget/verification/API cases | 28 passed, zero failures/skips (`budgets-api-fixed.trx`) |
| Exact `.7` package consumers | 123 passed, zero failures/skips (`package-final.trx`) |
| Published `.7` web smoke | Blob round trip, package preparation, workflow and sync succeeded |
| Published `.7` browser cases | Six passed across Chromium, Firefox and WebKit, zero failures/skips; includes the new verification overload (`browser-final.trx`) |
| Symbols/source verification | Six assembly/PDB pairs; 211 embedded source checksums; zero remote source checks |
| Consumed artifacts | All six archives match the isolated consumer cache and current packaged README; three checked browser assets match source and package |

Pack provenance records Windows 10 x64, SDK 10.0.402 and host runtime 10.0.13; browser publishing uses the installed
10.0.12 browser runtime/toolchain packs. The prior ownership report's inherited
SDK/runtime summary labels were corrected to its own embedded pack provenance; its artifacts/results were unchanged.

Both deadline defects reproduce against actual immutable `.6` packages: workflow acquisition exhausts the original
budget but still executes a step, and a late final size callback still acknowledges a transfer. The expected failures
are retained in `preview6-regressions-final.trx`. An intermediate transfer test double omitted its required readable
content; its `NotSupportedException` failures are retained and were corrected before the final reproduction and tests.

This slice does not rerun SQL Server/SQLCipher suites or add real object-store, mobile, signed/AOT, device or performance
qualification. No stable tag, commit or remote publication is created.

## Remaining work

General session/network-loop cancellation diagnostics and nested root-cause propagation remain outside the new
bounded-invocation metric. Durable verification progress across inventories and broader legacy byte/item/time limits
remain open; the new API does not serialize partial SHA state. B4's broad checkboxes therefore remain open while the
two concrete improvements are checked separately. Key-retirement policy, independent crypto review, B3 native hosts
and device lifecycle, B6 server integration coverage, measured performance, real external services and immutable
release qualification remain as recorded in the plan. Optional B7 grouped packages remain unselected.
