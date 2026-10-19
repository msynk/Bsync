# Gap-closure execution checkpoint, 2026-10-17

Historical first-slice record. The [continuation record](gap-closure-continuation-2026-10-17.md) documents the
subsequent account, protection, chunked encryption, maintenance, host and integration implementation and final checks.

This records a **partial M0/M1 repair slice**, not completion of the full
[implementation plan](../../Bsync-0.4.0-Gap-Closure-Implementation-Plan.md).
Source base: `e4286fe2141d0d1e836cfc88dbfa5dfd82154d37`, with uncommitted implementation changes.
No release, commit, tag or publication was performed. The next stable version remains a maintainer decision.

## Implemented and preserved

- Structured full-content verification with conservative key/authentication/I/O outcomes.
- Optional provider replacement capability; `IBlobCache` and wire contracts remain unchanged.
- Durable repair scheduling for completed active/candidate content, isolated partial replacement, full hash
  validation, physical content ownership, fenced acknowledgement and restart reconciliation.
- Native atomic replacement and browser immutable async-reader snapshots. Existing logical pins survive.
- A single cooperative preparation/upload-prerequisite deadline including ownership waits; canceled work
  remains durable. Native verification is cancellable; already-started browser completion is joined.
- Durable terminal-transfer cleanup and bounded reconciliation, including enqueue/release replay.
- Source-generated serializers, additive public API baselines, operator documentation and publication-status corrections.

The baseline already implements account lifetime locks, generation-safe background scheduling, common headless
scope creation with opening deadlines, encrypted v1 and transaction/publication recipes. Those were preserved,
not described as newly implemented. [ADR-026](../architecture/adr-026-verified-content-repair.md) explains the
smaller Promoting journal state and continuous physical ownership in place of a new provider generation CAS.

## Executed evidence

Environment: Windows 10 x64 `10.0.19045`, .NET SDK `10.0.401`, runtime `10.0.12`,
MSBuild `18.9.11`. Installed workloads include `wasm-tools 10.0.112`, `android 36.1.69`,
and `maui-windows 10.0.20`; their presence is not device execution evidence.
Dependency auditing remained enabled, including transitive dependencies and audit-service errors.

Local result files are under `artifacts/gap-closure/`. The [machine-readable evidence manifest](gap-closure-validation-2026-10-17.json) records
checksums and test counters. Failed attempts are retained.

| Run | Result |
|---|---|
| Unmodified source baseline, `Bsync.Tests` | 740 passed, 0 skipped |
| New repair reproducer before implementation | Missing content passed; truncation and same-length corruption failed (expected reproduction) |
| Published 0.4.0 package consumer + same reproducer | 56 passed, 2 failed (the same two defects), 0 skipped; original 55 cases passed |
| Initial repair-focused attempt | 25 passed, 1 failed: oversized-body exception type changed; preserved `InvalidDataException` and retained the failure log |
| Corrected focused recovery/encryption/transfer/package suite | 44 passed, 0 skipped |
| Full core after repair, including before/after-promotion real process kills | 753 passed, 0 skipped |
| Additional cancellation/staging/restart case | 1 passed; no redownload and cached readiness invalidated |
| API baseline update/check | 6 passed; reviewed additive capability/verification/repair/cleanup API |
| Exact preview package consumer | 58 passed, 0 skipped, including all three native repair cases |
| Exact preview web smoke | Blob roundtrip, package readiness, workflow and document sync passed |
| Preview package manifest and symbols | 12 archives; 6 assembly/PDB pairs; 187 embedded checksums; 0 remote-source checksums |
| First source browser run | Agent interrupted after 66 passes, 0 failures; aborted result retained, not qualification evidence |
| Exact preview browser repair cases | 3 passed: Chromium, Firefox and WebKit; each covers missing, truncated and same-length content after reload, plus reader preservation |
| Release-tool and packaged-symbol fault tests | 9 and 5 passed respectively |
| Complete source browser rerun | 78 passed, 0 skipped across Chromium/Firefox/WebKit; 4.67 minutes |

The locally packed version is `0.4.0-gapclosure.20261017.1`. It is a dirty-tree development artifact,
not a candidate with clean remote-source provenance. Archive and consumer-cache hashes are retained.
No ten-consecutive-designated-qualification gate was attempted.

Commands (repository root):

```powershell
dotnet test src/Tests/Bsync.Tests -c Release --nologo --verbosity minimal `
  --logger "trx;LogFileName=gap-closure-baseline.trx" --results-directory artifacts/gap-closure/baseline
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.4.0 `
  --logger "trx;LogFileName=published-040.trx" --results-directory artifacts/gap-closure/baseline
dotnet test src/Tests/Bsync.Tests -c Release --no-build --nologo --verbosity minimal `
  --logger "trx;LogFileName=core-recovery.trx" --results-directory artifacts/gap-closure
dotnet test src/Tests/Bsync.Tests.Browser -c Release --filter FullyQualifiedName~IndexedDbBrowserTests `
  --logger "trx;LogFileName=browser-recovery.trx" --results-directory artifacts/gap-closure
# Each of the six projects under src/Bsync (see pack-*.log):
dotnet pack src/Bsync/Bsync -c Release -o artifacts/packages `
  -p:VersionSuffix=gapclosure.20261017.1 -p:ContinuousIntegrationBuild=true
python scripts/release.py manifest --version 0.4.0-gapclosure.20261017.1
dotnet run --project src/Tools/Bsync.PackageVerifier -c Release -- artifacts/packages e4286fe2141d0d1e836cfc88dbfa5dfd82154d37
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.1 `
  --logger "trx;LogFileName=package-recovery.trx" --results-directory artifacts/gap-closure
dotnet run --project src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release `
  -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.1 -- --smoke
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Browser -c Release `
  -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.1 -o artifacts/gap-closure/packaged-browser
$env:BSYNC_BROWSER_HOST = (Resolve-Path artifacts/gap-closure/packaged-browser/wwwroot).Path
dotnet test src/Tests/Bsync.Tests.Browser -c Release --no-build `
  --filter FullyQualifiedName~PackageAutomaticallyRepairsDamageAfterReloadWithStableReaders `
  --logger "trx;LogFileName=package-browser-recovery.trx" --results-directory artifacts/gap-closure
Remove-Item Env:\BSYNC_BROWSER_HOST
dotnet test src/Tests/Bsync.Tests.Browser -c Release --no-build --filter FullyQualifiedName~IndexedDbBrowserTests `
  --logger "trx;LogFileName=browser-recovery-complete.trx" --results-directory artifacts/gap-closure
python -m unittest discover -s scripts -p 'test_*.py' -v
python scripts/verify_symbols_test.py artifacts/packages -v
```

The published reproduction used the isolated `artifacts/package-cache` and downloaded baseline archive in
`artifacts/packages`. Published archives were then preserved under `artifacts/gap-closure/published040`,
leaving the candidate feed for the unique preview version. To rerun the baseline, restore those published
archives to a separate configured local feed; do not change bytes of any existing version.

## Publication inventory

All six 0.4.0 archives were freshly downloaded from NuGet's flat-container endpoint and their `.nuspec`
repository commits inspected. Each declares the baseline commit. Checksums are preserved in
[published-040-gap-closure-inventory.json](published-040-gap-closure-inventory.json).
The Bsync archive used by the published regression has SHA-256
`a5365067ebb4cedfa41da606d8458204c6dc04e7cfa5867e52caf39ab76667f6`.
This corrects stale README/changelog/support wording; it does not invent a historical tag or prove a clean build.

## Format and support inventory

| Format/capability | This implementation | Remaining boundary |
|---|---|---|
| Document/wire, package, transfer and cache formats | Existing contracts unchanged | Full published-0.4.0 upgrade fixture inventory still required |
| Repair journal | Schema 1, separate `repair/v1/` prefix | No provider generation counter or encrypted replacement policy |
| Terminal cleanup | Versioned prefix, payload `1` | Explicit bounded startup/maintenance invocation |
| Native plaintext replacement | Existing atomic provider operation, process-kill tests | Physical storage retained by old unlinked handles not independently inventoried |
| Browser plaintext replacement | IndexedDB transaction, immutable async-reader snapshot | Whole v1 object retained in JS memory; no large-object memory claim |
| Encrypted v1 | Existing 16 MiB format; conservative repair outcomes | No v2 writer, quarantine recovery, or new crypto stability claim |
| Protected orchestration/group format | Not introduced | Design and implementation outstanding |
| Native schedulers | Existing source recipes preserved | No new Android/iOS host or physical-device execution |

Older readers ignore the additional journal prefixes and preserve them but cannot finish their work. Do not
run mixed manager versions concurrently. Unknown encrypted schemas are refused without mutation. Plaintext
SHA-256 identity and server transfer semantics are unchanged.

## Remaining work by plan ID

| ID | State and next requirement |
|---|---|
| B1 | Partially implemented. Add explicit encrypted recovery/quarantine, complete retained-generation/capacity accounting, pre-promotion host policy hook, verification outside the global metadata lock, byte-budgeted verification continuation, repair duration/quarantine metrics, and additional tab-death coverage. |
| B2 | Not implemented. Requires reviewed chunk/manifest design, native/browser providers, migration/rotation and large-object performance evidence. The 16 MiB limit is unchanged. |
| B3 | Existing recipes only. Complete native hosts, callback/key/spool testing, real Android/iOS devices and supported SDK/build agents remain required. |
| B4 | Preparation/prerequisite deadline slice implemented. Account builder, complete maintenance/rotation budgets and protected orchestration payloads remain open. |
| B5 | Terminal-pin crash window closed with bounded cleanup. Read-model reader lifetimes/retention, application owner resolution, bounded inventory and staging quarantine remain open. |
| B6 | Existing application recipes preserved. Expanded generic receiver/publication/file-bridge examples and conformance scenarios remain open. |
| B7 | Optional and not implemented; no supported workload requiring grouped activation has been established. |
| B8 | Publication inventory, status corrections, package regression and local preview evidence added. Full published 0.4.0 state fixtures, clean tag/source verification, provider/device/object-store evidence, performance budgets and ten designated runs remain open. |

Portable/source/package success cannot substitute for the external gates. No platform, encrypted v2, full
account encryption, arbitrary-service exactly-once guarantee or release qualification is claimed here.
