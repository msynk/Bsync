# Capabilities hardening through B08 — 26 October 2026

This continues the [24 October implementation](capabilities-implementation-2026-10-24.md) and the
[capabilities plan](../../bsync-capabilities-implementation-plan.md). It closes additional implementation and local
validation gaps; **it does not declare all eight workstreams production-qualified**. Source remains uncommitted on
`7a5169b3df212a99f128f4d55ecb80bc483ababc`, targeting unreleased 0.6.0. Nothing was tagged, pushed or published.

The latest immutable local preview is **0.6.0-capabilities.20261024.4**. Earlier `.2` and `.3` artifacts were preserved.
Six package/symbol pairs, independent archive hashes, dirty-tree provenance and symbol verification are in
`artifacts/capabilities-closure/preview-0.6.0-4/`. Verification checks six assembly/PDB pairs and 254 embedded source
checksums. This establishes archive consistency, not clean release provenance. Tests below use Windows/.NET 10,
SQL Server 2025 LocalDB 17.0.4025.3 and desktop Playwright engines; external deployments and physical devices are separate.

## Changes delivered

- **B03:** `partialLength` reads keys and capacity records without loading partial payloads. The browser qualification
  runner supports an exact finite ciphertext budget, counts ledger admission reads and rejects payload inventory
  cursor scans. The optional browser workflow now covers finite/unlimited and interpreter/AOT combinations.
- **B05:** bounded session sweeping shares upload ownership, reconciles exact-key lost-initiation orphans, refuses live
  authorization horizons, retains unknown/corrupt state and retries failed cleanup. Verified completion remains
  replayable after cleanup; abort tombstones remain durable. The real-service runner now exercises multipart recovery,
  scope isolation, corrupt-content refusal and CORS/ETag behavior. It accepts isolated SeaweedFS or MinIO services.
- **B06:** real process-kill fixtures cover root staging and activation before/after commit. Added quota/corruption,
  published-binary migration and three-engine reload/revocation/storage-loss checks. Shared content is queued, pinned,
  verified and unpinned once per hash within a child while item-level status remains intact. This removes the repeated
  I/O that initially caused the large browser fixture to time out.
- **B07:** `AddBsyncWorkflowWorker` provides serial bounded callbacks, a fresh asynchronous DI scope per batch,
  configurable intervals/backoff, safe exception-type logging and worker metrics. Stop cancels and joins the active
  callback before scope disposal, including a canceled host stop-wait token. Callbacks must cooperate with cancellation.
  Added real EF execution-strategy tests around pre-commit failure/lost commit replies and concurrent command retries.
- **B08:** `GetHealthAsync` exposes published/current/min-valid versions, pending batch age/key count and explicit
  recovery reasons without creating or advancing stream state. Added four real process-kill cases, related-row
  reverse-map/membership updates, schema-change refusal, competing-worker fencing, an invalid-retention-boundary
  guard and a real LocalDB backup/restore with an externally changed epoch and catch-up.
- **Qualification wiring:** package consumers exercise worker registration and health. CI enables the capabilities
  consumer tests, preserves published migration fixtures and adds a SQL Server 2022 package-only workflow job.
  Optional object-store jobs include pinned SeaweedFS and pinned-source MinIO. Three stale 0.5.0 preview references
  were updated. Workflow definitions were parsed locally; this is not evidence of hosted execution.

## Executed evidence

Paths in this table are relative to `artifacts/capabilities-closure/`. Historical attempts remain alongside corrected runs.

| Check | Result | Evidence |
|---|---|---|
| Full core suite after shared-content correction | 943 passed | `tests/core-full-sequential.trx` |
| Full SQL suite at its checkpoint | 129 passed, 1 skipped: unconfigured external-service test | `tests/sqlserver-full-corrected.trx` |
| Final capture/S3 subset, including later restore/worker/retention/sweep additions | 9 passed | `tests/capture-sweep-final.trx` |
| SQLCipher | 31 passed | `tests/sqlcipher-closure.trx` |
| Public API baselines | 6 passed; reviewed additive worker/health surface | `tests/api-reviewed-final.trx` |
| SQLite concurrency diagnostic repetitions | 20 passed; original root cause remains unestablished | `tests/sqlite-concurrency-*.trx` |
| Scoped worker shutdown | 1 passed | `tests/worker-lifetime.trx` |
| Package-set process death | 4 passed | `tests/package-set-process-kill.trx` |
| Capture process death plus initial S3 cleanup tests | 7 passed | `tests/capture-kill-sweep.trx` |
| Exact `.4` package capability consumer | 15 passed | `tests/packaged4-capabilities.trx` |
| Exact `.4` package workflow consumer | Passed ledger replay, projection, capture, health and joined DI worker | `package4-workflows.log` |
| Exact `.4` full browser/WPF suite | 135 passed, 5 unconfigured environment tests skipped | `tests/packaged4-browser-full.trx` |
| SQL-backed browser sample checks with LocalDB configured | 12 passed, zero skipped | `tests/packaged4-browser-tasks-localdb.trx` |
| Exact `.4` two-tab last-capacity race and reopen | 3 passed; exactly one chunk admitted, durable bytes retained | `tests/packaged4-browser-capacity-race-corrected.trx` |
| Exact `.4` packaged browser set/ledger checks | 9 passed across Chromium, Firefox, WebKit | `tests/packaged4-browser-set-ledger.trx` |
| Published 0.5.0 → exact `.4` → old-reader refusal → new-reader recheck | Passed; refused open left fixture bytes unchanged | `migration4-reproducible/result.json`, logs and `published-fixture/` |
| Exact `.4` native memory profile | Five sizes, 1/16/17/256/1024 MiB, one fresh process each; all ten stages passed under a 31 MiB managed GC cap | `native-memory4/manifest.json` and per-case JSON/logs |
| `.3` finite browser budget | Twelve cases, 1/16/17/256 MiB in three engines, passed | `finite-packaged/result.json` |
| Exact `.4` finite browser large-object profile | 17 and 1024 MiB in Chromium and Firefox, all four passed | `finite-packaged4-large/result.json` |
| Exact `.4` finite WebKit profile | 17 and 256 MiB, both passed within the recorded 1000 MiB origin quota | `finite-packaged4-webkit/result.json` |
| Exact `.4` real local SeaweedFS 4.49 | 17 checks passed, including multipart and all three browser engines | `seaweed-packaged4-final/qualification/result.json` |
| Release tooling and symbol negative tests | 9 and 5 passed | `release-tooling.log`, `symbols4-tests.log` |
| Browser physical lock-release regression | Passed | `node scripts/test_browser_lock_release.mjs` output |

The full browser run skipped the PostgreSQL fixture and four SQL-backed theory groups without server configuration.
The latter groups were then executed against LocalDB: all 12 expanded cases passed. PostgreSQL remained unconfigured.

The `.3` finite-budget results record `Managed.FiniteBudget=true`, `Managed.PayloadInventoryScans=0`, and one capacity
admission read per appended chunk. The `.4` Chromium/Firefox 1 GiB cases also passed with zero scans. WebKit reports a 1000 MiB origin quota, so its
recorded workload ceiling remains 256 MiB; no 1 GiB pass is invented. Raw whole-object operations remain outside
the bounded large-content profile.
Browser counters sample managed/JS/WASM/visible crypto buffers; they do not measure all native/IndexedDB allocation
or physical flash writes. Native hard-limit results constrain the entire managed heap, not total process memory or
an attributed cache-only instantaneous peak. Some correctness/measurement runs overlapped other local tests; their
timings are not isolated performance benchmarks. One native sample per size is not a repeated distribution claim.

The SeaweedFS run uses random ephemeral credentials, a generated bucket and loopback HTTP. `LocalServiceQualified=true`
and `ExternalServiceQualified=false` are intentional. Multipart tests close/reopen both SQLite stores after losing a
service-accepted part reply; completed parts are not resent. They also lose a verified completion reply, retry it,
reject cross-scope/corrupt content and verify ETag exposure in all engines. This is not a physical service restart,
cloud IAM/TLS or account-deletion deployment qualification.

The backup/restore test is explicitly LocalDB-only: the database process shares the test user's disposable filesystem.
Remote/container jobs report it skipped rather than attempting an inaccessible client-side backup path. The retention
guard test injects a cursor below SQL's real minimum-valid boundary; it does not claim a natural retention-cleanup drill.

## Failures and limitations retained

- An initial browser package-set run exceeded its unchanged 60-second harness deadline in WebKit and later stalled.
  Its process tree was stopped; the log and abort note remain. Removing duplicate shared-content I/O corrected the
  workload; the exact `.4` nine-case run passed in 34 seconds. No timeout or correctness assertion was relaxed.
- The first full core run had 942 passes and one SQLite `ConcurrentInstances` failure, reporting a nested transaction.
  The focused repeat, subsequent complete 943-test run and 20 additional focused repetitions passed. No speculative SQLite production change was made.
  The original failure remains evidence requiring continued concurrency monitoring, not a silently discarded pass.
- The first SQL full-suite build collided with DLLs loaded by a concurrent core test process. Retained build logs show
  the file-lock failure; the corrected sequential build/run passed. This was not a passing SQL execution.
- The first real multipart run passed its service checks but failed temporary-directory cleanup because native SQLite
  pools retained handles. Explicit pool closure/database deletion corrected cleanup; all 17 checks subsequently passed.
- The initial last-capacity race also attempted the first protected-format upgrade concurrently in two tabs.
  WebKit correctly refused that migration; the fixture now performs the required exclusive format setup before racing
  writes. No migration guard was weakened. The initial two-pass/one-failure TRX is retained; the corrected three-engine run passed.
- Android dependency installation still failed HTTP 404 after acquiring a verified Microsoft OpenJDK 21.0.8.
  The full Android host has not compiled successfully here. Logs/binlogs and the verified JDK identity are retained.
- Official MinIO Windows binary/archive requests returned HTTP 410. Pinned source was obtained, but attempted official
  Go downloads returned 403/404. The pinned-source hosted workflow is prepared; **no MinIO qualification pass is claimed**.

## Remaining gates

| Stream | Remaining gate or scope limit |
|---|---|
| B01 | Authenticated failing GitHub logs/artifacts, published 0.5.0 symbol/source closure, clean immutable release provenance, complete hosted matrix and ten consecutive designated green runs. `gh auth status` was unauthenticated. |
| B02 | Independent cryptographic review against an immutable revision; physical-device/total-process/physical-I/O qualification and final repeated target matrix. Key-use ceilings remain a host-wide lifetime obligation across devices/purposes. |
| B03 | Termination during accounting commits, broader external-interference and constrained-device matrices; the last-capacity two-tab race now passes. Raw unencrypted whole-object snapshots/import/completion are not a general bounded streaming provider. |
| B04 | Working Android SDK and complete conditional host build; macOS/Apple signing/build infrastructure and physical Android/Apple/iPadOS lifecycle, key and deletion tests. Maintained recipes remain unqualified on those targets. |
| B05 | Real MinIO, deployment TLS/IAM, physical service restart and revocation/deletion matrix. S3 remains an optional SDK recipe, not a newly supported provider package. Hosts schedule cleanup and retain replay identities. |
| B06 | Broader device/deployment qualification; the profile deliberately supports flat required children only. Nested/optional-child semantics are not implemented or claimed. |
| B07 | Hosted SQL Server 2022/cloud and broader multi-feed/deadlock/operations qualification. Domain rules, current authorization, publication lock order and external receiver policy remain host-owned. |
| B08 | Hosted SQL Server 2022/cloud, natural retention-expiry and remote restore drills, production monitoring/privileges and mixed-publisher qualification. Composite keys, cross-database publication, unbounded isolated-generation builders and CDC remain outside this bounded same-database profile. |

These gates cannot be certified by local success or by the existence of a workflow. No independent review, physical
device, authenticated hosted result, missing service run or larger unsupported adapter is relabeled complete.

## Reproduction

Use a new unique preview suffix for any changed library bytes. Never overwrite `.2`, `.3` or `.4` archives.
Set `NUGET_PACKAGES` to an isolated directory for package consumers. The reproducible migration command is:

```powershell
python scripts/check_capabilities_migration.py --version <exact-preview> --output <new-evidence-directory>
```

It verifies published archives against the committed B01 SHA-256 inventory, checks consumed DLLs against exact
packages, preserves the pre-upgrade fixture, and compares every fixture file before/after unsupported old-reader open.
Worker/health usage is executable in `src/Tests/PackageConsumers/Bsync.PackageConsumer.Workflows`.
The browser qualification runner accepts `--finite-budget`; native memory qualification accepts `--heap-limit-mib 31`.
See their READMEs for instrumentation limits. Run provider tests with a newly generated test bucket and retained
service identity; never infer deployment support from the loopback profile.
