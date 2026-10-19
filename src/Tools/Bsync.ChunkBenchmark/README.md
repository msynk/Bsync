# Native chunk storage measurements

Run from the repository root:

```powershell
dotnet run --project src/Tools/Bsync.ChunkBenchmark -c Release -- artifacts/chunk-measurements.json 1,16,17,256,1024 3
```

To measure exact locally packed bytes, add `-p:BsyncPackageVersion=<unique-preview-version>` before `--`.
This selects package references and the isolated package-consumer feed/cache instead of project references.

The tool uses actual native chunk files and SQLite metadata, one-MiB sequential appends and a stable caller buffer.
It verifies publication, resumes bounded scrubs and records timed scrub cancellation. Each case uses a fresh temporary
directory that is removed after closing SQLite. Output includes raw runs, runtime/OS, library hash, per-chunk append
p50/p95/p99, allocations, sampled managed-heap increase, stored ciphertext and chunk payload writes. The declared
payload write-ratio target is at most 1.001. No network, credentials, application data or production cache is used.

Windows OS logical write-byte counters include filesystem/SQLite writes; they are not physical-media or flash
amplification. The five-millisecond memory sampler includes GC/runtime behavior and can miss peaks. It cannot certify
the separate target of less than 32 MiB cache-only live working buffers. Caller bytes are allocated before sampling.
Per-chunk percentiles are descriptive, not a population latency SLO. Device/browser memory duplication, power,
physical storage and signed/AOT-host measurements remain separate qualification work.

## Constrained native memory qualification

Use a fresh evidence directory and an existing immutable local preview:

```powershell
python src/Tools/Bsync.ChunkBenchmark/run-memory.py --package-version 0.4.0-gapclosure.20261019.1 --output artifacts/chunk-memory --heap-limit-mib 31
```

This runs all five sizes, three times each, in separate 64-bit processes with an explicit **managed GC heap hard limit**.
The command above selects the default 31 MiB profile. Earlier candidates reproduced a 1 GiB append OOM on Windows/.NET
10.0.13 with preview `.2`. An exploratory optimized source run failed later during rotation. A fresh exact `.3` pilot
completed all stages, but its repeated 31 MiB matrix failed during rotation. All fifteen `.3` cases passed at 64 MiB.
The [envelope memory continuation](../../../docs/review/gap-closure-envelope-memory-2026-10-19.md) records the new
candidate's fifteen passing 31 MiB cases after removing unnecessary Base64 escaping in internal storage JSON. A whole-heap cap alone
does not supply the attribution required by the plan's less-than-32-MiB cache-only working-buffer gate.
The tool checks the effective runtime setting and refuses an unconstrained or mismatched run. The runner removes inherited per-heap limit
overrides in each child, verifies the two consumed Bsync DLLs against the selected archives, and preserves hashes,
commands, raw results and failed logs. Existing evidence directories/results are never overwritten. For a shorter
check use `--sizes 1,17 --samples 1`.

Each case verifies append, publication, full sequential SHA-256, seeded cross-chunk ranges, bounded scrubbing, rotation
to different key bytes with an old reader retained, old-reader content/key retention, retired collection, and new-reader
hash/ranges. The caller/test buffers are included in the cap. There is no forced GC during these operations.
Five-millisecond samples and stage-end observations describe managed memory and GC commit; they are not peak proofs.
Successful completion under the enforced cap supplies separate bounded-managed-heap evidence for this workload.
On failure, a post-unwind diagnostic compaction records retained memory without retrying the workload or converting
the failure into success. `--conserve-memory 0..9` selects a separate, explicitly recorded GC configuration (default 0).
Set `BSYNC_MEMORY_DIAGNOSTIC_PAUSE=1` only when collecting a local heap dump; a failed process then prints its ID and
waits 30 seconds after the diagnostic collection. Dumps contain process memory and belong in local ignored artifacts.
For a live dump, `BSYNC_MEMORY_PAUSE_WRITE=<positive-write-count>` inserts a 30-second pause after that ciphertext write.
The wrapper is absent when this option is unset. The runner records diagnostic pause settings; instrumented dump runs
are separate diagnostic evidence, not ordinary qualification or latency samples.

The [.NET hard limit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit)
covers managed heap and GC bookkeeping, not all process memory. Native SQLite, crypto/JIT allocations, OS caches,
browser JS/WASM copies and device behavior require separate measurements. This deliberately constrained run is not a
latency benchmark and does not establish an aggregate budget for multiple concurrent streams.

The optional [Windows workflow](../../../.github/workflows/chunk-memory.yml) packs unique local artifacts and uploads
the full evidence. It never publishes packages. Its default 64 MiB profile and selectable 31 MiB reproducer are distinct
from the release/device qualification gates; a new workflow is not evidence that GitHub has executed it.

Reports use source-generated JSON metadata, including GC configuration and failure diagnostics. A self-contained
Windows publish can therefore be exercised with `-r win-x64 --self-contained true -p:PublishTrimmed=true
-p:ILLinkTreatWarningsAsErrors=true`. Use distinct output/intermediate directories and a new evidence filename.
Trimming changes assembly bytes: retain both the exact input package hashes and the final trimmed executable/library
hashes instead of asserting that a transformed DLL is byte-identical to the original archive. Windows trimming is
separate from native AOT, browser AOT, and signed Android/Apple release builds.

With the Windows C++ toolchain and SDK installed, publish the same exact-package consumer using
`-r win-x64 --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true
-p:ILLinkTreatWarningsAsErrors=true` into separate intermediate/output directories. Run its `.exe` with the same
benchmark or `--memory` arguments; the latter still requires a matching fresh-process GC limit. Native AOT and
single-file publications embed assemblies, so their reports set `LibraryAssemblySha256` to null and record
`ExecutableSha256` instead. Retain the input package hashes and publication logs separately; an executable hash
must never be presented as the original managed DLL hash. This qualifies this benchmark consumer, not a signed
Android/Apple host or every application composition.
The optional [native AOT workflow](../../../.github/workflows/chunk-native-aot.yml) reproduces the five sizes and
five fresh-process 31 MiB-cap cases on Windows, retaining package/executable hashes and results. Its presence does
not imply remote execution or completion of the designated release matrix.
