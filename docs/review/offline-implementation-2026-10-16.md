# Offline implementation evidence, 2026-10-16

This records work in the uncommitted checkout based on `ad46c3b61f17d2304fd6c580d916a5242aca52d8`.
It does **not** mark the improvement plan or a supported release complete.

Environment: Windows 10 x64 build 19045, Intel Core Ultra 7 255H, .NET SDK 10.0.401/runtime 10.0.12,
`wasm-tools` 10.0.112, `maui-windows` 10.0.20. Native tests select SQLitePCLRaw plain 2.1.12 or SQLCipher 2.1.11 in
separate processes. SQL Server uses a disposable LocalDB database per fixture. Android/iOS workloads and physical
devices were unavailable. Later qualification provisioned PostgreSQL 17.11 and Moto 5.2.4 in isolated task-owned
directories on loopback; early runs without them are explicitly historical below. No production service was used.
Playwright engine versions recorded by the full browser run: Chromium 153.0.8010.12, Firefox 155.0, WebKit 26.6.

Final executed source counts: core **713**, SQL Server **87**, PostgreSQL **44**, SQLCipher **31**, browser/WPF
**114**, all with zero failures and zero skips. The solution built with zero warnings/errors. Exact preview .6 passed
53 package conformance cases, the published web smoke, 66 packaged-browser cases and a subsequent nine-case rerun
with corrected awaited lifetime probes. `artifacts/offline-validation-summary.json` identifies the corresponding TRX
files. Task-owned Moto and PostgreSQL services were stopped after qualification; no commit/tag/publication was made.

## Implemented source capabilities

- Account-scoped CAS metadata in SQLite/IndexedDB, bounded enumeration, schema refusal and fenced ownership.
- Coordinator offline opening, suspension, resume, pending-work inspection and journaled account-resource deletion.
- Shared account lifetimes/exclusive deletion across native processes and browser tabs, with pending-work recheck after acquiring exclusive access.
- Explicit key identity/version/availability contracts, native protected-store lookup recipe and browser recovery-key unlock UI.
- Durable transfer jobs, bounded retry/drain/concurrency, resume from persisted offsets and presigned upload refresh.
- Managed owner pins, conservative byte reservations, partial/staging accounting and protected-content capacity errors.
- Native exclusive cache locks and browser Web Locks; immutable manifests, staging, activation, readers and readiness.
- Physical lifetime locks for new readers/snapshots; reconciliation reclaims their pins after owner termination, preserving durable-work pins.
- Replayable local workflows and portable background request/runner contracts.
- Credential-required/migration-pending account outcomes; deadline cancellation cannot clear a background request.
- Full-reconciliation local read-model generations, atomic applied-row progress and immutable readers.
- Aggregate-only, bounded diagnostic export and transfer offset/verified-completion progress events.
- Cached package verification for age-labelled UI polling; bounded backlog/storage/background gauges and completed-publication lag.
- Query materialization/return counters and 10k/50k/100k indexed-page benchmark coverage.
- SQL Server publication lock ordering, application command ledger and coalesced projection-outbox recipes.
- Immutable typed intent requests/results and an optional transactionally enqueued, fenced external-effect outbox recipe.
- Verified corruption repair through managed reimport, preserving logical pins and existing native reader handles.
- Published 0.2.0 SQLite upgrade fixture and package-only smoke coverage of the new contracts.

The design and constraints are in ADRs [020](../architecture/adr-020-account-orchestration.md),
[021](../architecture/adr-021-managed-content.md), [022](../architecture/adr-022-workflow-publication.md).

## Earlier executed validation

These are checkpoints from successive implementation slices. The later table records the final source/package checks;
individual historical green runs do not establish the full release gate.

| Command / artifact | Result |
|---|---|
| `dotnet build src/Bsync.slnx -c Release` | Passed, zero warnings/errors, including trimmed browser/sample publishing |
| `dotnet test src/Tests/Bsync.Tests -c Release` | 705 passed, zero skipped; `offline-core-latest.trx` (earlier runs: 681, 692, 694, 695, 704) |
| `dotnet test src/Tests/Bsync.Tests.SqliteEncrypted -c Release --no-build` | 31 passed, zero skipped; `offline-encrypted.trx` |
| `dotnet test src/Tests/Bsync.Tests.SqlServer -c Release --no-build` | 75 passed, one skipped (S3 absent); `offline-sqlserver.trx` |
| Browser metadata reload tests | Three passed: Chromium, Firefox, WebKit |
| Browser prepared-package reload/reader tests | Three passed: Chromium, Firefox, WebKit |
| Full browser suite | 91 passed, one skipped (PostgreSQL absent); `offline-browser.trx` |
| Browser cross-tab cache lock/process termination tests | Three passed: Chromium, Firefox, WebKit |
| Metadata process-kill durability/native lock release | One passed; `metadata-process-kill.trx` |
| Reader/snapshot process-kill reclamation | Both process-kill tests passed; `reader-process-kill.trx`; nonblocking cleanup may require another pass until OS handle release |
| Browser reader/snapshot reclamation and package readers | Six passed across Chromium, Firefox and WebKit; `reader-browser-recovery.trx` |
| Lifetime-lock checkpoint | Full core 694 passed before the final progress test; all six browser reader/package cases passed again in `reader-lifetime-browser-final.trx` |
| Read-model recovery and public API baselines | Eleven passed (five recovery tests plus six API baselines); `read-model.trx` |
| Browser read-model interrupted rebuild/reload | Three passed: Chromium, Firefox, WebKit; `read-model-browser.trx` |
| SQL Server Change Tracking design spike | One passed; `change-tracking-spike.trx`; does not qualify a capture adapter |
| Projection outbox and bounded repair | Five passed in targeted `projection-reconciliation.trx`; six cases including oldest-request age now included in the full SQL Server run |
| Full SQL Server after reconciliation/lag/key-length changes | 81 passed, one unconfigured S3 skip; `offline-sqlserver-latest.trx`; `BSYNC_SQLSERVER=Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true` |
| Local key ownership/API contracts | Nine passed; `local-key-contract.trx`; dispose zeroes owned bytes without modifying the caller's original |
| Account lifetimes, key contracts and API baselines | 17 passed; `account-resource-locks.trx`; cross-coordinator refusal and pending-write transition race |
| Account/package/process recovery | 28 passed; `account-package-recovery.trx`; includes killed-process shared lifetime release |
| Browser offline recovery-key UI | Two passed in `offline-unlock-browser.trx`: Chromium/Firefox cold offline reload, wrong-key retention and lock |
| Resource pending-work inspection/API baselines | 15 passed; `account-resource-pending.trx`; default deletion retains workflow/transfer work and unknown counts, even with no dirty documents |
| Corrected browser account/package/unlock/PWA cases | 11 passed, zero skipped; `offline-browser-corrections.trx`; PWA route checks stop the actual server and bypass the browser HTTP cache |
| Published package-only WebAssembly harness | 63 passed, zero skipped in Chromium/Firefox/WebKit; `offline-package-browser.trx`, `0.2.0-offline.20261016.4`; publish `Bsync.PackageConsumer.Browser` then set `BSYNC_BROWSER_HOST` to its published wwwroot and run `IndexedDbBrowserTests` with `--no-build` |
| MAUI protected-store recipe | Windows MAUI build passed with zero warnings/errors; protected storage was not executed on a locked physical device |
| Credential/migration lifecycle and coordinator/API tests | 20 passed; `account-credentials.trx` |
| Redacted export and API tests | Eight passed; `offline-diagnostics.trx`; browser source-generated export also passed in all three engines |
| Transfer progress, HTTP blob routes and API baselines | 28 passed; `transfer-progress.trx` |
| `PublishedSchemaMigrationTests` | Published 0.1.0 and 0.2.0 SQLite fixtures pass; see [fixture evidence](published-020-store-fixture.md) |
| `python -m unittest discover -s scripts -p 'test_*.py' -v` | Nine passed |
| All six library packs, `VersionSuffix=offline.20261016.1` | Twelve package/symbol artifacts; `artifacts/offline-packages/provenance.json` verified checksums/metadata/build maps |
| Package-only consumer tests, `BsyncPackageVersion=0.2.0-offline.20261016.1` | 53 passed; auditing/source mapping enabled |
| Published package-only web consumer `--smoke` | SQLite/HTTP/blob transfer, managed package readiness and workflow completion passed |
| Published consumer static assets | IndexedDB, blobs, orchestration and cache-lock JS present, including compressed variants |
| Intermediate six-package preview `0.2.0-offline.20261016.2` | Twelve artifacts; `artifacts/offline-final-packages/provenance.json`; 53 package tests and published web smoke including read-model/diagnostic APIs passed |
| Distributed PDB/Source Link verification | Six DLL/PDB pairs passed; four positive/negative archive tests passed; `symbols-verification.json` |
| Extended distributed source verification | Six pairs and 167 embedded-source checksums passed on preview .3; five archive tests include altered embedded-checksum refusal. Remote mode was attempted and refused unavailable source (HTTP 404); no successful remote-source claim |
| Intermediate six-package preview `0.2.0-offline.20261016.3` | Twelve artifacts; `artifacts/offline-reader-packages/provenance.json`; six PDB pairs, four verifier tests, 53 package-only tests and published web smoke passed |
| Account/key/metrics preview `0.2.0-offline.20261016.4` | Twelve artifacts in `artifacts/offline-account-packages`; six PDB pairs, 170 embedded-source checksums, five verifier tests, 53 package-only tests and published web smoke passed |
| Resource-pending preview `0.2.0-offline.20261016.5` | Twelve artifacts in `artifacts/offline-qualified-packages`; six PDB pairs, 170 embedded-source checksums, five verifier tests, 53 package-only tests and published web smoke including resource-pending semantics passed |
| Latest complete solution build | Passed with zero warnings/errors after the lifetime-lock/progress changes and verifier integration |
| `git diff --check` | Passed |

Preview provenance explicitly records `dirty: true`. These are local validation artifacts, not clean tagged releases.
No commit, tag or package publication was performed. Targeted tests added after a full suite run are recorded separately;
they do not change that full run's historical count. The earlier R0-only runs remain separate historical evidence.

Regression fixes found during implementation: preserve ordinary session transport-factory timing while deferring
explicit offline opens; retain coordinator parent-first behavior; isolate process-wide blob metric assertions from
unrelated concurrent transfer tests; do not return already expired ownership after a slow lease-acquisition write;
join browser write transactions before releasing physical cache locks.

The first expanded browser run was aborted after 90 passes, two cache-test assertion failures and one PostgreSQL skip.
The assertions had incorrectly treated HTTP-cache responses as worker-cache responses; the revised test uses no-store
fetches, actual server shutdown and bounded cancellation. The targeted 11-case rerun passed. Stopping the aborted run
left its sample server holding publish output files; only that identified test-owned process was stopped before rerun.
SQL lag instrumentation initially used the wrong reader type for `datetimeoffset`; this was fixed before the 80-pass
SQL Server run. An earlier SQL invocation omitted `BSYNC_SQLSERVER` and failed setup; it is not product failure evidence.

## Preliminary performance

Command: `dotnet run --project src/Tests/Bsync.Benchmarks -c Release -- --filter '*LocalQuery.IndexedPage*'
--job short --artifacts artifacts/benchmarks/offline-plan`.

| Documents | In-memory p95 | SQLite FULL p95 |
|---|---:|---:|
| 10,000 | 0.188 ms | 0.441 ms |
| 50,000 | 0.176 ms | 0.719 ms |
| 100,000 | 0.249 ms | 1.703 ms |

This is a short three-measurement desktop run of a 50-item indexed page, with concurrent development activity. It is
not p99 qualification or a mobile/browser performance guarantee. Managed allocations were about 458–460 KiB in memory
and 896–898 KiB on SQLite per operation. The existing 50 ms p95 desktop target is met in this preliminary run only.

## Remaining implementation and qualification

### Later intent and repair slice

Immutable request snapshots and explicit generated payload registrations now separate request, execution and delivery.
The Tasks adapter preserves its existing wire shape and command hashes. Completion/acknowledgment requests survive
SQLite restart. Managed reimport verifies existing bytes and repairs corruption through verified atomic replacement,
preserving pins and prior native reader handles; capacity includes old and staged bytes. Automatic download repair
and encrypted-cache migration remain open.

| Later check | Evidence |
|---|---|
| Complete core suite after intents/repair | 711 passed, zero skipped; `offline-core-intents-repair.trx`; later complete run with workflow process tests: 713 passed, `offline-core-final.trx` |
| Intent contracts/API baselines | Nine passed; `intent-contracts.trx` |
| SQL Server intent registration/ledger/end-to-end cases | Four passed; `intent-registrations.trx` |
| Cache/package/intent failure cases | 18 passed; `repair-intents.trx` |
| SQL Server with isolated Moto 5.2.4 S3 emulator | 83 passed, zero skipped; `offline-sqlserver-with-s3.trx`; not production S3 CORS qualification |
| Browser repair, lock release and server restart | Nine passed in Chromium/Firefox/WebKit; `browser-ownership-repair-restart.trx` |
| Intent preview `0.2.0-offline.20261016.6` | Twelve artifacts in `artifacts/offline-intent-packages`; six PDB pairs, 170 embedded checksums, five verifier tests, 53 package tests and published web smoke passed |
| Preview .6 packaged browser harness | 66 passed, zero skipped; `offline-intent-package-browser.trx`; later lock-probe corrections require their own rerun |
| Corrected awaited package lifetime probes | Nine passed, zero skipped against preview .6; `package-browser-awaited-probes.trx`; account locks, dead readers and corruption repair in all three engines |
| Workflow process death | Eight workflow/process cases passed; `workflow-process-kill.trx`, including two real child-process kills after durable effects and before progress acknowledgement; takeover clock advanced beyond the dead lease |
| Final-build PostgreSQL provider | 44 passed, zero skipped; `offline-postgres-final.trx`, isolated PostgreSQL 17.11 |
| Final-build SQLCipher | 31 passed, zero skipped; `offline-sqlcipher-final.trx` |
| External-effect recipe and full SQL Server suite | 87 passed, zero skipped with Moto; `offline-sqlserver-final.trx`; four new outbox cases cover lost replies, result rollback, stale claims and poison isolation |
| Final corrected full browser/WPF suite | 114 passed, zero skipped; `offline-browser-all-providers.trx`, SQL Server and PostgreSQL configured |

PostgreSQL's portable Windows archive came from the [EDB binary downloads page](https://www.enterprisedb.com/download-postgresql-binaries?lang=en)
(`postgresql-17.11-5-windows-x64-binaries.zip`), SHA-256
`80379b2c04d51c30225532e0ae04509899141e9957ed096fe749d7fd9df8f82f`.
It runs only on loopback with a new task-specific data directory; tests create/drop their own random databases.

The preceding complete browser run had 106 passes, two WebKit failures and one PostgreSQL skip
(`offline-browser-final.trx`). The lock test inferred ownership too early from tab closure; it now probes actual
release. The server-reset test could observe the initial aggregate zero before the new row's acceptance; it now waits
for that row to be clean and stops the real endpoint before creating unsynced work. Targeted corrections passed;
they do not rewrite the earlier run into a green result. CI now includes a separate packaged-WebAssembly job consuming
the exact pack-job artifact in all three engines. Its YAML parses locally; remote CI execution remains unverified.

A subsequent full run exposed a second lock-test issue: this installed Playwright driver checks the truthiness of an
async predicate's Promise before awaiting its value, so `WaitForFunctionAsync` did not repeat a false async probe.
Account-release and reader-reclamation checks now explicitly await each .NET probe with a bounded deadline. Earlier
passing async-probe cases alone are not proof of reclamation; the corrected probes require fresh execution evidence.
That full run finished with 113 passes, one account-probe failure and zero skipped cases, including PostgreSQL
multi-server scenarios (`offline-browser-qualified.trx`). The corrected awaited package probes subsequently passed.
The fresh complete source-browser run then passed all 114 cases with no skips. These fixes address observed test
boundaries; one green local run is not the plan's ten-consecutive-CI-run release qualification.

Required work remains: whole-account lock adoption by host-managed workers/resources; physical protected-key integration;
custom/legacy owner reconciliation; encrypted blob envelopes, rotation and migration; complete generic intent execution examples and external-service integration (transactional outbox recipe implemented); online large-feed
projection rebuild and membership reconciliation (bounded exclusive-boundary repair is implemented); automatic read-model retention and host snapshot integration; Android/iOS adapters; complete
generic samples and the full failure/performance matrix.

External/release gates also remain: physical-device execution/key tests, real S3 CORS,
platform native-library qualification, clean tagged release provenance,
remote source checksum validation and ten consecutive designated qualification workflow runs. Old 0.2.0 binary/source
equivalence remains unresolved. Existing passing tests must not be used to mark these gaps complete.
