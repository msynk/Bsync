# Remaining implementation and qualification - 2026-10-18

This continues the [nested-stop record](gap-closure-nested-stops-2026-10-17.md). Candidate
`0.4.0-gapclosure.20261018.2` uses new package bytes; `0.4.0-gapclosure.20261017.12` remains an immutable archive.
This is development evidence, not a completed stable-release qualification.

## Implemented

- Durable v2 chunk scrubbing with time, byte and chunk allowances; authenticated generation-bound checkpoints resume
  after interruption. Caller cancellation, expiry, bad keys and tampering preserve data. Full SHA-256 checks retain their
  separate whole-pass contract; chunk scrubs do not claim simultaneous health or a newly computed full hash.
- Generation key journals written atomically before chunks. Bounded observations retain obligations through partials,
  rotation, old readers and quarantine; legacy missing journals report unknown keys. Physical collection removes key and
  scrub journals only after readers release the generation. Host key deletion still includes other stores and backups.
- Parent stop causes through session execution, coordinated admission, coordinator hints and session transport aliases.
  The transport wrapper was a real missing link: its lease-loss regression fails against immutable preview `.12`.
- Native file/SQLite measurement tooling for all five proposed object sizes, including raw append timing percentiles,
  allocation/heap observations, payload write counts, OS logical I/O and cancellation observations. The tool clearly
  separates these measurements from physical-media, device and browser-memory qualification.

[ADR-035](../architecture/adr-035-generation-integrity-maintenance.md) defines the storage, key and freshness contract.
The plan, API baseline, compatibility notes, support matrix, release notes and managed-content examples were updated.
No document wire change, implicit migration, key deletion, stable tag or package publication was introduced.

## Validation

Evidence is under `artifacts/gap-closure/completion`; the
[manifest](gap-closure-completion-validation-2026-10-18.json) records hashes, exact commands and retained failures.

| Check | Result |
|---|---|
| Full source core suite | 914 passed, no failures/skips |
| Focused integrity/lifecycle/process coverage | 51 passed |
| Public API baseline update/review | Six packages passed; additive core types/methods |
| Immutable `.12` lifecycle baseline | One expected lease-cause failure; lifecycle-cancellation control passed |
| Exact `.2` package consumers | 203 passed, no failures/skips |
| Published browser and web checks | Six browser cases passed across Chromium/Firefox/WebKit, including scrub page reload; web blob/package/workflow/sync smoke passed |
| Package symbols and consumed bytes | Six assembly/PDB pairs; 213 embedded checksums; six archive/cache hashes and READMEs match; three browser assets match source/package/publish |
| Native measurements | Five exact archived `.1` cases at 1/16/17/256/1,024 MiB; 15 exploratory source runs retained separately |
| Android full-host build | Blocked: XA5300, Android SDK directory unavailable |

I also checked the current [Android command-line tools distribution](https://developer.android.com/studio) and
[Microsoft JDK distribution](https://learn.microsoft.com/en-us/java/openjdk/download). The JDK download probe succeeded,
but both the Android tools archive and official SDK package catalog returned HTTP 404 from this environment. Those
probe responses are retained; changing a local SDK path alone cannot fix the missing downloadable components here.

The first compile exposed duplicate serializer-context attributes across partial declarations; placing the registrations
on the single context declaration fixed generation. A test compile then needed its key-exception namespace. The first
10-case focused execution passed nine and failed the session child-cause case, identifying the transport wrapper's
unregistered token. That wrapper was fixed, and the test also waits for the enclosing run observation before initiating
shutdown so the asserted boundary cannot race a second cancellation cause. Failure logs and the failed TRX are retained.

The first packed candidate, `0.4.0-gapclosure.20261018.1`, passed 202 package tests and failed one existing coordinator
shutdown case: root cancellation could precede its linked-token callback, suppressing the queued listener's diagnostic.
The final `.2` candidate restores the direct root observation; the regression now repeats each shutdown variant 25 times.
The failed package TRX/log and immutable `.1` feed are retained. Its storage benchmark remains valid evidence for those
exact archived bytes; `.2` changes only that coordinator diagnostic guard, not the storage implementation.

SQLite process-kill cases terminate on either side of the scrub checkpoint commit, reopen through a fresh process/provider,
and prove resumption respects the durable prefix. Browser coverage closes/reopens the page after the first scrub chunk,
then checks that bounded calls continue from the persisted prefix and that the key obligation survives. Web Crypto/
IndexedDB scrubs and key journals are also tested alongside rotation and reader lifetimes. Browser-engine execution
does not establish physical mobile behavior. The initial six passing browser cases are retained before the extra
reload boundary; the final browser run uses the rebuilt test assembly and newly published package-consumer harness.

The package benchmark used Windows x64, .NET 10.0.13, native chunk files and SQLite with 1 MiB appends. These are raw
development-machine observations under concurrent validation load, not isolated performance qualification:

| Object MiB | Append ms | Per-chunk p95 ms | Sampled managed-heap increase MiB |
|---|---:|---:|---:|
| 1 | 67.2 | 66.92 | 2.35 |
| 16 | 268.7 | 20.53 | 10.59 |
| 17 | 276.5 | 21.61 | 10.58 |
| 256 | 5,250.2 | 27.52 | 24.10 |
| 1,024 | 31,538.1 | 41.85 | 65.83 |

Every case wrote/stored exactly plaintext size plus 28 bytes per chunk: ratio 1.000026703, within the declared 1.001
chunk-payload target. OS logical append writes (including SQLite) ranged from 1,077,476 to 1,174,168,800 bytes; this
does not measure physical-media amplification. Timed scrub cancellation was observed in 0.34–1.56 ms for the four
larger cases; the 1 MiB case finished without an observed cancellation. Full raw data contains p50/p95/p99, allocations
and limits. The 65.83 MiB sampled heap increase includes garbage/runtime and **does not establish the requested
less-than-32-MiB cache-only working-buffer gate**. Browser copies and physical-device measurements remain open.

The retained 15-run exploratory source benchmark preceded the final joined-disposal check. The separate five-case
package measurement records the exact archived `.1` assembly hash; results are not pooled across these versions.

## Gates that cannot be closed by local implementation

| Plan area | Remaining requirement and reason |
|---|---|
| B2 key retirement | The host must account for v1, protected metadata, backups and every other key user under exclusive ownership before deleting any key. Library journals supply evidence; they cannot authorize host key deletion. |
| B2 crypto/performance | Independent design review; cache-only live-buffer proof; physical-media amplification; named mobile/browser memory duplication and latency measurements. Local native measurements are partial evidence only. |
| B3 Android | Install/configure a real Android SDK/JDK, then full and signed/AOT builds, WorkManager factory/upgrade coexistence and physical-device lifecycle/power tests. The current build fails before app compilation. |
| B3 Apple | A Mac/Xcode and signing/device environment, launch/expiration/locked-key tests, and host-approved URLSession plaintext spool/file-protection integration. The reference app does not enable URLSession downloads; source recipes are not device qualification. Background uploads remain explicitly unsupported. |
| B6 object store | A supported real service plus ticket issuance, credentials, CORS/orphan policies and deployment writer coverage. The existing simulated service cannot satisfy this gate. |
| B7 groups | Conditional extension remains unselected: the plan requires a supported workload needing multiple packages. The 256-item individual limit is unchanged. |
| B8 historical fixtures | Actual published 0.4.0 fixtures exist, but their capture cannot retroactively precede implementation. Preserve that chronology and obtain an explicit review disposition. |
| B8 wire compatibility | This continuation changes no wire protocol. Cross-version wire process tests remain conditional on a wire-affecting change. |
| B8 release | Maintainer-selected new stable version, clean immutable tag, remote source checksum verification and ten consecutive designated full qualification runs, including required external targets. Dirty-tree local passes do not count as that release gate. |

No physical devices, Mac signing environment or real object-store configuration was supplied for this run. These gates
remain unchecked in the plan; this record does not claim every acceptance item is complete.
