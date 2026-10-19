# Encrypted envelope memory continuation — 2026-10-19

This continues the [native memory investigation](gap-closure-memory-2026-10-18.md) on Windows 10 x64,
.NET 10.0.13. The new local candidate is `0.4.0-gapclosure.20261019.1`. It remains experimental, with a dirty
working tree and separate device, external-service, cryptographic-review and release gates.

## Implementation

Internal chunk-state JSON now writes encrypted Base64 without HTML escaping. The dedicated writer context uses
`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`; it applies only to storage envelopes, including recovery's atomic
mutation. Plaintext manifest serialization, authenticated bytes, default readers, public APIs and server protocols
are unchanged. Stored JSON must not be embedded directly in HTML. Existing escaped Base64 remains valid JSON and
continues to deserialize through the default reader.

A regression exercises escaped ciphertext through partial resume, completion, reads, rotation and explicit recovery,
and checks the literal Base64 representation of newly saved envelopes. The measurement tool also supports an opt-in
live-dump pause; its wrapper is absent in ordinary runs, and the runner records any diagnostic pause settings.

## Diagnostic evidence

Evidence is retained under `artifacts/gap-closure/rotation-memory`. A diagnostic `.20261018.3` workload with a
31 MiB cap failed during append, confirming that its earlier isolated rotation failure was not a stable phase boundary.
A separate 64 MiB diagnostic workload paused after ciphertext write 700 and completed after the live heap dump.
The dump contained large character arrays, but root analysis found no roots for the sampled large arrays; it does not
prove they were retained. A cache-object root was successfully traced through the paused asynchronous write.

The [runtime JSON writer source](https://source.dot.net/System.Text.Json/System/Text/Json/Writer/Utf8JsonWriter.WriteValues.String.cs.html)
shows a pooled character buffer on the escaped-string path and a worst-case expansion allowance. This motivated
the scoped encoder change; the dump alone is not a causal proof. Qualification results are recorded below.

## Qualification

The 25 source chunk-cache cases and 208 exact-package consumer cases passed, including the new escape-compatibility
regression. Six published browser cases passed across Chromium, Firefox and WebKit, and the published web smoke passed.
All six assembly/PDB pairs were verified with 213 embedded source checksums; remote source verification remains zero.

The first full source-suite attempt recorded 918 passes and one timeout in the existing fifteen-collection HTTP
coordinator test while packing and memory work ran concurrently. Its failed TRX is retained; concurrent load is a
possible contributor, not a proven cause. An isolated full rerun is recorded separately below.

That isolated attempt recorded 918 passes and one process-death cleanup-test failure: ten immediate nonblocking
collection attempts removed 68 of the expected 130 retired rows. The test now retries zero-progress attempts with a
short delay within its original 30-second deadline. It still requires all 130 rows to be removed, preserves the active
generation and foreground write, and verifies live-reader protection before killing the child. No library cleanup
semantics or timing limits changed. The corrected full-suite result is recorded below.

Four negative harness checks confirmed rejection of missing/mismatched GC limits, invalid size and existing evidence.
The initial log checker expected usage text for the first two exceptions; inspection corrected the checker against
the retained runtime-limit exception logs without retrying those workloads. The original assessment is retained too.

The final exact-package 31 MiB matrix passed **15/15** fresh processes: three each at 1, 16, 17, 256 and 1,024 MiB,
with all ten stages in every process. This includes rotation to different key bytes while an old reader remains alive,
old-reader hash/key retention, collection after reader disposal and rotated hash/range reads. No diagnostic pause,
forced collection or workload retry ran inside these measurements. The requested and effective heap limit was
32,505,856 bytes in each child. Highest sampled managed memory was 30,915,760 bytes; highest observed GC committed
memory was 31,584,256 bytes. Samples may miss peaks; successful completion under the checked hard cap is separate
evidence. The three 1 GiB processes completed in 102.3, 94.2 and 86.2 seconds; these are diagnostic constrained-GC
durations, not latency-SLO measurements.

The exploratory source pilot also passed all ten stages at 1 GiB/31 MiB. It is separate from the package matrix and the
two old-candidate diagnostic runs. The previous `.20261018.3` archives are preserved under
`artifacts/gap-closure/preview-20261018-3-feed`; no package version was overwritten. The exact new core assembly SHA-256
is `9a0926645ffa4dd1fabd52ddd4438167f5c3c6037687632b933fb72d2e7887f6`.

The corrected full source suite passed **919/919**, with no failures or skips. All earlier failed TRX files remain
separate evidence. The [validation manifest](gap-closure-envelope-memory-validation-2026-10-19.json) records source,
package, log and result hashes, archived-package preservation, six current archive/cache/README comparisons and three
browser source/package/publish comparisons. Only the chunk-envelope implementation changed among library source files
since the preceding candidate; public API baselines are unchanged.

## Remaining gates

The plan's less-than-32-MiB incremental cache-owned buffer target includes attribution that a whole managed-heap cap
alone does not establish. Native allocations, browser JS/WASM duplication, mobile device latency/power and physical-media
amplification remain unqualified. Independent cryptographic review, real object-store tickets/CORS/orphan behavior,
Android SDK/device and Apple Mac/Xcode/signing/device evidence, host key-retirement decisions, and clean tagged release
provenance with ten designated qualification runs remain open. Grouped packages remain an unselected conditional extension.
