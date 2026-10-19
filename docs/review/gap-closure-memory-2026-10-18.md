# Native memory qualification continuation — 2026-10-18

This continuation reduces aligned append allocations and adds executable memory qualification to the B2 workstream.
It compares preview `0.4.0-gapclosure.20261018.2` with the new immutable local candidate
`0.4.0-gapclosure.20261018.3` on Windows 10 x64, .NET 10.0.13. The package is still experimental and the working tree
is dirty. Public APIs, storage formats and key/lifetime rules are unchanged. The
[previous implementation record](gap-closure-completion-2026-10-18.md) remains the historical record for `.2`.

## Implementation

Aligned full chunks now pass their stable caller memory directly to authenticated encryption. A private, zeroed
plaintext buffer is allocated only when assembling a partial chunk. This removes one chunk-sized plaintext allocation
per aligned append without changing ciphertext ownership, commit ordering or caller-buffer lifetime. Four regression
cases cover aligned/mixed/tail/empty appends, caller-buffer preservation and reuse after completion. The exploratory
source run completed 1 GiB append, full verification, reads, ranges and scrubbing under the 31 MiB cap; it then failed
during rotation with a retained reader. That source profile remains failed. A separate exact `.3` package pilot completed
all ten stages under the same nominal cap. The subsequent full `.3` 31 MiB matrix passed twelve smaller-size samples
but failed during rotation in its first 1 GiB sample. The isolated pilot success is therefore not a repeatable guarantee.

Three 17 MiB append/completion measurements per exact package recorded median whole-process allocation totals of
74,706,336 bytes for `.2` and 56,856,160 bytes for `.3`: a reduction of 17,850,176 bytes (23.9%). The expected plaintext
copy removal is 17 MiB; remaining measurement variation includes runtime/test activity. These allocation observations
do not imply a latency SLO or live-memory peak. The raw files record the distinct library assembly hashes.

The [measurement tool](../../src/Tools/Bsync.ChunkBenchmark/README.md) now has an independent memory mode. Its Python
runner builds against the selected local package, compares both Bsync DLLs to their exact archives, and starts a fresh
process for every size/sample. Each child verifies its effective GC limit before touching storage. The runner removes
inherited per-heap limit overrides, explicitly sets memory conservation, preserves failures and refuses to overwrite
an evidence directory. Its manifest records commands, source/tool/archive hashes, runtime configuration and result hashes.

Each workload uses actual SQLite metadata, native chunk files and 1 MiB chunks. Ten stages assert append payload size,
full publication hash, sequential read hash, seeded cross-chunk ranges, bounded scrubbing, rotation to different key
bytes while an old reader remains alive, old-reader hash/key retention, physical collection after reader disposal, and
hash/range reads from the new generation. Fixed range seed: `90418`. No forced collection runs inside measured stages.
After a failure, a diagnostic collection records retained memory without retrying or converting that failure to success.

The new [optional Windows workflow](../../.github/workflows/chunk-memory.yml) packs unique local previews and retains
the full matrix and archives. It offers distinct 64 MiB and 31 MiB whole-managed-heap profiles. The YAML was checked
locally; no GitHub workflow execution, package publishing or release qualification is claimed.

## Evidence and the failed smaller cap

Raw evidence is in `artifacts/gap-closure/memory-qualification`; the
[validation manifest](gap-closure-memory-validation-2026-10-18.json) retains both passing and failed runs.

| Exact `.3` check | Result |
|---|---|
| Full source core suite, including four new cases | 918 passed, no failures/skips |
| Exact-package consumer suite | 207 passed, no failures/skips |
| Published browser harness | Six passed across Chromium, Firefox and WebKit |
| Published web smoke | Blob, package, workflow and sync checks passed |
| Distributed symbols | Six assembly/PDB pairs; 213 embedded source checksums; zero remote checksums |
| Guard checks | Missing/mismatched cap, invalid arguments and evidence overwrite rejected |
| 64 MiB managed cap | 15/15 `.3` runs passed, ten stages each; 15/15 archived `.2` baseline runs also passed |
| 31 MiB managed cap | `.3` matrix: twelve smaller runs passed, first 1 GiB rotation failed; separate 1 GiB pilot passed |

All six new archives/cache copies and READMEs and three browser assets are compared in the manifest. The previous `.2`
feed is preserved under `artifacts/gap-closure/preview-20261018-2-feed`; no version was reused. No public API baseline
change was needed. Four new caller-memory cases ran in both source and exact-package suites.

The original `.2` 31 MiB profile passed three samples each at 1, 16, 17 and 256 MiB. Its first 1 GiB append failed with
`OutOfMemoryException` while allocating the encrypted chunk in `PlatformBlobEncryption.SealAsync`. A diagnostic
reproduction failed after 720 acknowledged MiB appends. The post-unwind compacting collection reduced observed managed
bytes from 23,529,752 to 19,446,448; observed GC commit changed from 31,088,640 to 27,246,592 bytes. These are diagnostic
observations after failure, not a peak bound or a root-cause proof. The separate conservation-level-5 attempt also failed,
after 721 MiB. A local heap dump and its analysis are retained in ignored artifacts for further allocation/root analysis.

The [.NET heap hard limit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit)
includes the managed heap and GC bookkeeping. This experiment also includes caller/test buffers. Native SQLite, crypto
handles, JIT code, OS caches and browser copies lie outside that cap. Consequently the failed 31 MiB whole-process-managed
profile does not by itself prove that cache-owned live buffers exceed the plan's separate 32 MiB incremental target.
Conversely, a larger-cap pass cannot establish that target. Memory-conservation settings can alter GC behavior and pause
cost; they are separate measured configurations, not an automatic library fix.

Both 64 MiB profiles cover three fresh processes each at 1, 16, 17, 256 and 1,024 MiB. All final check counts are recorded
in the manifest. Per-stage timings are diagnostic under an
explicit cap; they must not be pooled with the unconstrained latency measurements in the previous report. Five-millisecond
heap samples can miss peaks and include garbage. No claim of cache-only allocation attribution is made.

## Remaining boundaries

The under-32-MiB cache-only live-buffer target remains open, with smaller-cap failures available for further investigation
and one passing pilot explicitly separated from the failed repeated profile. Browser JS/WASM duplication, physical-media
amplification, device latency/power, independent crypto review,
real object-store/CORS tests, Apple signing/devices and release provenance remain separate gates.

Additional official Android probes (older command-line tools, SDK catalog v1 and platform tools) again returned HTTP 404
from this environment. No SDK was installed and the full Android host remains unbuilt locally. The probe responses are
retained. No external device/service configuration was supplied during this continuation.
