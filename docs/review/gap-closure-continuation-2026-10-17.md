# Gap-closure continuation — 2026-10-17

This record supersedes the initial repair-only checkpoint in
[gap-closure-2026-10-17.md](gap-closure-2026-10-17.md). Work remains an uncommitted development tree based on published
commit `e4286fe2141d0d1e836cfc88dbfa5dfd82154d37`. No stable release version, immutable tag or completed release
qualification is asserted. The [plan](../../Bsync-0.4.0-Gap-Closure-Implementation-Plan.md) now distinguishes checked
implementation from open acceptance gates.

## Implementation

| Area | Result |
|---|---|
| Repair | Same-length/missing/truncated plaintext recovery, stable readers/pins, promotion authorization recheck, physical retired-file accounting, replayable terminal cleanup, bounded verification buffers and content-free attempt metrics |
| Chunk encryption | Explicit experimental v2, dedicated native/browser chunks, 64 KiB..1 MiB chunks, 1024-chunk/1 GiB ceiling, authenticated ranges, durable partials, resumable rotation, v1 source-preserving migration, explicit recovery quarantine and bounded GC |
| Composition/protection | Named account resources and fresh work scopes; one cooperative deadline through disposal; key resources deleted last; explicit payload policy, AES-GCM protection preserving atomic CAS/fences; permanent provider version barrier |
| Maintenance | Separate owned read-model namespace, physical generation lifetimes, retention refusal/usage reporting, bounded cleanup, application pin resolution/dry-run, paged native/browser inventory and cache reconciliation |
| Native hosts | UI-independent durable bootstrap plus Android Application/Activity and Apple AppDelegate host source; foreground reconciliation; manual complete-host CI build jobs |
| Integration | Receiver deduplication/status/reconciliation policy, dispatch-time authorization, durable external-file operation/mapping bridge implementing the actual Bsync blob protocol |
| Compatibility | Actual published 0.4.0 document/orchestration/v1-blob/read-model fixtures; actual old SQLite and JS downgrade probes; source-generated serializers and updated public API baselines |

Design and operator details: [ADR-026](../architecture/adr-026-verified-content-repair.md),
[ADR-027](../architecture/adr-027-account-ownership-and-protected-metadata.md),
[ADR-028](../architecture/adr-028-experimental-chunked-encryption.md),
[native host boundaries](../patterns/native-background.md), [receiver policy](../patterns/intents.md),
[external-file bridge](../patterns/external-file-bridge.md), and [read-model retention](../patterns/local-read-models.md).

Implementation choices are explicit: continuous physical content ownership replaces the plan's proposed provider
generation CAS for repair; `Promoting` is a durable intent before the provider's combined verify/switch; interrupted
full verification rehashes rather than trusting a serialized digest checkpoint. V2 recovery is host-authorized and
retains unreadable generations permanently. B7 grouped preparation was not selected because no supported workload
requiring it was declared. Individual package semantics and the 256-item limit remain unchanged.

## Environment and retained evidence

Windows 10 x64, .NET SDK 10.0.401/runtime 10.0.12; Node 26.1.0 generates independent test-only AES-GCM format vectors.
Raw commands, logs, TRX, fixture capture and upgrade probes are under `artifacts/gap-closure/continuation` and
`artifacts/gap-closure/fixture-capture`. The [machine-readable continuation inventory](gap-closure-continuation-validation-2026-10-17.json)
records their hashes and outcomes.
Dependency auditing stays enabled, including audit-service failures. No required skip is counted as a passing gate.

The first complete continuation core run passed 781 tests. Subsequent targeted tests expanded coverage for resumed
rotation, failed-reader recovery, explicit policy registration, bounded inventory and an independent Node format vector.
A later 785-case run passed 784 and failed an existing wall-clock assumption in `SyncGoalTests.OneShotHelper`: under
load no page completed in its 150 ms budget. The test now establishes committed progress before applying the short
budget, then checks retention and eventual completion. The failed TRX/log is retained, not replaced by the corrected run.
An earlier targeted build also caught a duplicate local variable in the crash-test host (CS0136); it was renamed before
the successful format/maintenance run. That failed build produced no test result and is not counted as a passing run.

SQL Server tests used a dedicated disposable LocalDB instance `BsyncGapClosure20261017`, engine 17.0.4025.3; they
created/dropped only test databases. The initial integration selection passed 23 tests; the expanded receiver/file
bridge selection passed 8. These selections overlap and are not 31 distinct tests. The real S3/service environment
was not configured. No consumer database or production bucket was used.

Native large-object tests exercised 17, 256 and 1024 MiB, exact full plaintext SHA, maximum physical chunk write size,
range intersections, physical quota, retained readers, interruption/resume and real process kills on both sides of
manifest publication. Independent checked-in vectors were written with Node's crypto API, not the Bsync writer, and
are consumed by native and Web Crypto implementations. This is interoperability evidence, not independent design review.

| Final validation | Result |
|---|---|
| Complete source core suite | 785 passed; later retention/policy/API selection 16 passed, including one new regression (786 distinct core cases covered across these runs) |
| Source IndexedDB browser selection | 81 passed across Chromium, Firefox and WebKit |
| SQL Server integration selection | 24 passed; isolated LocalDB instance stopped after testing |
| SQLCipher suite | 31 passed in its separate native-library process |
| Exact `.3` package consumers | 72 passed, including native 1 GiB and read-model retention regression |
| Exact `.3` packaged browser | 6 passed; repair, large chunk storage, independent vector, paged inventory, protected payload/fence and published-JS downgrade checks in all three engines |
| Exact `.3` package web smoke | Passed: sync, upload/download, package preparation and workflow |
| Artifacts and symbols | Six archive/cache pairs match; six DLL/PDB pairs; 205 embedded-source checksums; zero remote-source checksums |
| Release-tool tests | 9 unit and 5 symbol integration tests passed; actual stable preflight correctly refused the dirty tree |

The 81-case source browser run preceded the final inventory/vector/policy additions; those additions were executed in
the six packaged-browser cases against the final archive assets. No source-only result is relabeled as package evidence.
The final preview is `0.4.0-gapclosure.20261017.3`. Preview `.2` passed its then-existing 71 cases; a newly added regression
then reproduced the latest aborted read-model generation retaining all 128 rows at the ceiling. That expected failure
is retained as `preview2-retention-regression.trx`. Preview `.3` fixes it by retiring abandoned staging only after
acquiring the build fence. Its new 72-case suite passed. Preview `.2` was archived, not overwritten.

Final source/package/browser results and archive/symbol checks are recorded in the continuation JSON inventory.
Historical `.1` preview evidence applies only to that earlier repair slice. Each new pack uses a unique preview version;
changed bytes are never repacked as published `0.4.0` or an earlier preview.

Representative executed commands (run from the repository root; use a **new** suffix if packing again):

```powershell
dotnet test src/Tests/Bsync.Tests -c Release -m:2 -nodeReuse:false
dotnet test src/Tests/Bsync.Tests -c Release -m:2 -nodeReuse:false --filter 'FullyQualifiedName~OwnedReadModelTests|FullyQualifiedName~ProtectedOrchestrationStoreTests|FullyQualifiedName~PublicApi'
dotnet test src/Tests/Bsync.Tests.SqliteEncrypted -c Release -m:2 -nodeReuse:false
dotnet test src/Tests/Bsync.Tests.Browser -c Release --filter FullyQualifiedName~IndexedDbBrowserTests
# Integration run used the isolated LocalDB connection named above and the seven test classes in integration-final.log.
# pack-preview3.ps1 retains the exact six pack commands, manifest command and symbol verification.
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -m:2 -nodeReuse:false -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.3
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Browser -c Release -m:2 -nodeReuse:false -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.3 -o artifacts/gap-closure/continuation/preview3-browser
$env:BSYNC_BROWSER_HOST=(Resolve-Path artifacts/gap-closure/continuation/preview3-browser/wwwroot).Path
dotnet test src/Tests/Bsync.Tests.Browser -c Release --no-build --filter 'FullyQualifiedName~ChunkedEncryptionResumesLargeObjectsAndProtectsAtomicPayloads|FullyQualifiedName~PackageAutomaticallyRepairsDamageAfterReloadWithStableReaders'
Remove-Item Env:BSYNC_BROWSER_HOST
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release -m:2 -nodeReuse:false -p:BsyncPackageVersion=0.4.0-gapclosure.20261017.3 -o artifacts/gap-closure/continuation/preview3-web
dotnet artifacts/gap-closure/continuation/preview3-web/Bsync.PackageConsumer.Web.dll --smoke
python -m unittest discover -s scripts -p 'test_*.py' -v
python scripts/verify_symbols_test.py artifacts/packages -v
```

## Published fixture provenance

`src/Tests/Bsync.Fixtures.V040` consumes exact `[0.4.0]` packages in an isolated package cache, with no Bsync project
references. Capture happened during this continuation, after initial source implementation, using the published DLLs;
it is not represented as a pre-edit capture. The retained generator and fixture metadata record loaded assembly hashes.
Existing 0.2.0 fixtures remain unchanged.

| Artifact | SHA-256 |
|---|---|
| `replica-0.4.0.db` | `FED5125552B97F4B175E41D655D2E026E49E372C56F9FD5EB01C4383F5E7A6EA` |
| `published040.zip` | `3B0F95E1571EF52D6CF307F64CF94C362774F3FD741E84CDBCE2BBFCFB77475A` |
| Published loaded `Bsync.dll` | `7b5379bcce76e584a2f21e145ca56d6bb0f5f5e4f987de09ad7786f4b5b21997` |
| Published loaded `Bsync.Storage.Sqlite.dll` | `bc452effaf9a36b816edd4d8a7edff0605faa4ba9764d69700b361283a978bf7` |

The native downgrade probe upgrades a copy to feature header 2, invokes the actual published reader with
`--expect-unsupported`, and checks the database is byte-for-byte unchanged. Before/after SHA-256:
`FDE054EED89920CF296333ECDC87FCD8CE2ECC67BB14EA579C0F095FC359576C`.
Browser downgrade tests load the actual published `bsync-orchestration.js` fixture and require `VersionError`.
These barriers prevent new opens; hosts must close already-open old processes/tabs before upgrading.

## Unclosed qualification and follow-ups

This section records the preview `.3` checkpoint. The subsequent
[maintenance continuation](gap-closure-maintenance-2026-10-17.md) closes the quarantine metrics follow-up and adds
retired-file pagination and specific read-model process-death coverage. Its package/test evidence is separate.

- Independent cryptographic design review and reviewed aggregate per-key invocation policy. V2 remains explicitly
  experimental. Measured peak managed/JS/WASM duplication, write amplification and p50/p95/p99 on named targets are
  still required; maximum chunk allocation tests are not performance qualification.
- Full Android/Apple host builds, trimming/AOT, signing and physical lifecycle/key tests. Android build returned XA5300
  because no SDK directory was available. Dependency installation returned XAIAD7000/HTTP 404; a subsequent official
  command-line SDK download also returned 404. Logs remain. No Mac/Xcode environment was available. URLSession host
  integration/spool protection and background uploads are not enabled by the new Apple host; uploads are unsupported.
- Real external-service tickets, expiry/refresh, range/CORS behavior and orphan collection policy. The file bridge uses
  streamed fallback and simulated immutable service tests. Domain authorization, replay horizons and contributing-writer
  coverage remain deployment-specific responsibilities.
- Separate quarantine metrics gauge and full fixed-target performance dashboards. Quarantine/journal inventory and
  physical accounting exist; unknown legacy staging is preserved. Legacy full-list methods remain for compatibility.
- Clean immutable new-version tag, remote source checksums, all advertised provider/native runtime targets, and the
  existing ten-consecutive-designated-run gate. Local successful suites are not a substitute. The six-package release
  inventory remains unchanged because platform hosts are unpackaged source samples.

The implementation is reviewable and locally testable; the plan's entire definition of done is deliberately not checked
while these gates lack evidence. No new capability is represented as device-qualified or a stable published release.
