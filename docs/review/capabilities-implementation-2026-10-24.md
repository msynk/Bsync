# Capabilities implementation continuation through B08 — 2026-10-24

The [26 October hardening record](capabilities-closure-2026-10-26.md) supersedes the remaining-work table below;
this document preserves the earlier implementation and `.2` candidate evidence.

This is an experimental implementation continuation, not completion of every acceptance gate in the
[plan](../../bsync-capabilities-implementation-plan.md). The branch now targets unreleased **0.6.0**. Published 0.5.0
archives remain unchanged. Work is uncommitted on baseline `7a5169b3df212a99f128f4d55ecb80bc483ababc`; a dirty-tree
preview cannot establish clean release provenance or ten consecutive green hosted runs.

## Implemented profiles and remaining work

| Stream | Implemented or exercised in this continuation | Requirements still open |
|---|---|---|
| B01 | Existing baseline inventory/CI artifact correction retained; API baselines reviewed; new uniquely versioned preview packages, isolated consumers and symbol verifier exercised | Authenticated failing-run logs/artifacts; published 0.5.0 symbol/source closure; clean tagged candidate; complete hosted/provider matrix and ten green runs |
| B02 | Durable key-use ceiling based on actual key material, not aliases; reservation before encryption; shared-purpose/concurrent/failed-operation and SQLite-reopen regressions | Independent crypto review; device qualification; expanded final-candidate large-object/process-memory/kill matrix; hosts must coordinate aggregate use across devices and retain the meter for the key's lifetime |
| B03 | IndexedDB schema-2 capacity ledger; atomic payload/accounting writes; constant-record stored-byte reads; paged resumable reconciliation; write refusal during uncertainty; old-writer reopen refusal; partial replacement generation checks | Raw unencrypted whole-object operations still materialize payloads; broad finite-budget scaling and physical-memory/I/O qualification, external-interference/low-capacity matrices |
| B04 | Maintained-recipe decision recorded; complete Android build and dependency-install attempted | Build failed missing SDK (XA5300); installer failed HTTP 404. Full Android/Apple builds and physical lifecycle/key/deletion qualification remain open; no new native platform support claim |
| B05 | Existing queue gains resumable multipart transport, authenticated HTTP control routes, durable reconciliation/abort, refreshed URL handling and bounded replies; optional S3 SDK recipe verifies snapshots before scoped grants | S3 is still a recipe; real MinIO plus second target, multipart browser CORS/TLS/IAM and restart/revocation matrix; autonomous metadata retention/orphan operations and provider packaging decisions |
| B06 | Flat immutable roots over 1–32 exact child packages; 300-item regression; shared-byte accounting; single-pointer activation; active/candidate repair status; live-reader-safe retirement; feature barrier 3 | Cross-process kill matrix; published-old-binary upgrade/downgrade fixture; browser set, quota, revocation and corruption qualification; nested/optional-child semantics intentionally unsupported |
| B07 | Public command ledger, coalesced projection outbox and extracted effect/receiver infrastructure; explicit versioned migrations, stable receiver binding, fenced acknowledgements and worker metrics; package-only consumer | Generic DI/worker convenience registration; broader EF execution-strategy/deadlock/multi-feed/recovery matrix and production operations qualification. Host-owned worker lifecycle/registration remains documented |
| B08 | Same-database bounded SQL Change Tracking adapter: prerequisite validation, snapshot boundaries, sealed replay, source/schema epoch binding, atomic publication/marker/cursor, truncate guard and explicit full-resnapshot transition | SQL Server 2022/cloud, forced process death, related-row fan-out, retention/restore drills, production lag/health monitoring. Composite keys, cross-database adapters, large isolated-generation builders and CDC are outside the implemented profile |

Design contracts are in [ADR-036](../architecture/adr-036-encryption-admission-and-browser-capacity.md),
[ADR-037](../architecture/adr-037-maintained-native-background-recipes.md),
[ADR-038](../architecture/adr-038-durable-multipart-upload.md),
[ADR-039](../architecture/adr-039-atomic-package-sets.md),
[ADR-040](../architecture/adr-040-relational-workflow-infrastructure.md) and
[ADR-041](../architecture/adr-041-bounded-change-tracking-capture.md).

## Evidence

Environment: Windows, .NET 10, SQL Server 2025 LocalDB **17.0.4025.3**, Playwright Chromium/Firefox/WebKit.
Exact SDK/runtime details are retained in `artifacts/capabilities/dotnet-info.txt`; LocalDB details in
`artifacts/capabilities/localdb-info.txt`. These results are local, not Linux/macOS/cloud/mobile qualification.

| Execution | Result | Retained evidence under `artifacts/capabilities/` |
|---|---|---|
| Full core suite, corrected | **937 passed**, zero skipped | `tests/core-full-corrected.trx` |
| Full SQL Server suite, final | **120 passed, 1 skipped** (unconfigured real S3 service) | `tests/sqlserver-full-final.trx` |
| Encrypted SQLite suite | **31 passed** | `tests/sqlcipher-final.trx` |
| Public API review/update | **6 passed**; additive changes in four package baselines | `tests/api-final.trx` |
| Focused orchestration/package/multipart routes | **28 passed** | `tests/focused-final.trx` |
| Final SQL workflow/capture/S3 model subset | **14 passed**, before the final added corrupt-content test subsequently included in the full SQL run | `tests/b05-b07-b08-final.trx` |
| Package-only key-budget/multipart/package-set tests | **14 passed** | `tests/package-capabilities-corrected.trx` |
| Package-only SQL workflow program | Passed command replay, projection publication and Change Tracking against private LocalDB database | `package-workflows-final.log` |
| Initial ledger upgrade/reconciliation tests | **3 passed**, one per browser engine | `tests/b03-browser-ledger.trx` |
| Full source browser/WPF suite | **129 passed, 5 skipped** (unconfigured PostgreSQL/SQL sample environments); 10 min 2 s | `tests/browser-full.trx` |
| Final packaged ledger/race checks | **6 passed**, Chromium/Firefox/WebKit against exact preview `.2` assets | `tests/packaged-browser-ledger-final.trx` |
| Release tooling unit tests | **9 passed** | `release-unit-tests.log` |
| Symbol-verifier integration tests | **5 passed**, including tampering/wrong-commit/missing-PDB refusals | `symbol-tests.log` |
| Actual preview assembly/PDB verification | **6 pairs**, **254 embedded source checksums**, zero remote source checksums | `preview-0.6.0-2/symbols-verification.json` |
| Browser lock-release regression | Passed `node scripts/test_browser_lock_release.mjs` | Tool execution output |

The final six-package preview is **0.6.0-capabilities.20261024.2**. Twelve package/symbol archives are retained in
`artifacts/capabilities/preview-0.6.0-2/`, with `provenance.json` recording dirty state and exact hashes. Independent
SHA-256 inventory is `artifacts/capabilities/package-hashes.json`. Preview `.1` was an earlier two-package consumer
checkpoint; it was not overwritten. Symbol checks prove local archive/DLL/PDB/checksum consistency, not equivalence
to a clean published commit. Nothing was published, tagged or pushed.

The broad browser run preceded the last partial-generation race correction. Both ledger scenarios were then executed
against final `.2` packaged assets in all three engines: legacy upgrade/reconciliation/no-inventory-scan behavior, and
discard/reappend under identical partial keys while digest verification is suspended. Stale completion refuses without
deleting replacement progress or publishing an old object. This focused final run does not imply the entire hosted
browser or device matrix passed on the preview.

## Failed attempts retained

- `tests/b08-capture.trx`: one failure, one pass. A truncate could leave minimum-valid-version equal to the saved cursor.
  Tracking begin-version/table metadata now forces resnapshot; corrected run passed both tests. Later full SQL passed.
- `tests/core-full.trx`: 934 passed, one failure. The unknown-orchestration-schema test still used now-supported version
  3. It now asserts version 4 is refused and retained; the corrected full suite passed.
- `tests/api-update.trx`: one snapshot failure caused by multiline SQL constant line endings. Migration SQL is now a
  public getter, avoiding embedding platform-dependent SQL text as a public constant; subsequent API checks passed.
- `tests/package-capabilities.trx`: five passed, nine failed because the newly added package consumer omitted its native
  SQLite bundle. Added the application-owned `SQLitePCLRaw.bundle_e_sqlite3` dependency; corrected run passed all 14.
  The library continues to leave native SQLite selection to the host.
- An overly broad Python discovery pattern also imported the standalone symbol integration runner with discovery
  arguments as its artifact directory (three failures/two errors after nine unit passes). Correct invocation uses
  `test_*.py` for unit tests and the symbol runner with an isolated six-package directory; both corrected runs passed.
- Android host build failed XA5300; dependency installer failed HTTP 404. Binlogs remain under this artifact directory.
- Early multipart/control-route compilation errors were corrected before passing tests. No failure was relabeled a pass
  or suppressed with an assertion/threshold reduction.

## Reproduction and operational boundaries

Run `dotnet test src/Tests/Bsync.Tests -c Release` and the analogous `Bsync.Tests.SqliteEncrypted` project.
For SQL, set `BSYNC_SQLSERVER` to an authorized test server where the test identity can create/drop databases, then run
`dotnet test src/Tests/Bsync.Tests.SqlServer -c Release`. Tests create random private databases and clean them up.
The local connection was `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`.

Pack all six libraries with a fresh suffix. Set `NUGET_PACKAGES` to an isolated cache and run:

```powershell
dotnet run --project src/Tests/PackageConsumers/Bsync.PackageConsumer.Workflows -c Release -p:BsyncPackageVersion=<preview>
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=<preview> -p:BsyncCapabilitiesTests=true --filter 'FullyQualifiedName~EncryptionUsageBudget|FullyQualifiedName~MultipartUpload|FullyQualifiedName~OfflinePackage'
```

The browser package consumer is published with the same preview; `BSYNC_BROWSER_HOST` points at its `wwwroot` when
executing packaged browser checks. Close older tabs/processes before upgrading blob schema or enabling package sets.
Do not manually lower feature versions, erase encrypted/unknown state, or discard old child references to enable downgrade.

SQL workflow migrations are explicit deployment operations. Never silently enable Change Tracking/snapshot isolation.
Configure minimal runtime privileges, feed-before-domain lock order, host-owned canonical/membership callbacks and an
external source restore epoch. Full resnapshot callbacks must atomically replace the complete destination scope,
including deletions. Monitor pending/poisoned/reconciliation work and retention lag. No arbitrary external receiver is
promised exactly-once execution. Independent review and physical/service/hosted evidence remain necessary before promotion.
