# Offline implementation evidence, 2026-10-16

This records the earlier implementation checkpoint and continued work based on
`d796e0d17cbad8019244c142b0de981dfb023015`. The earlier checkpoint began at `ad46c3b`; its changes are now committed.
The continuation remains uncommitted and uses the user-selected 0.3.0 version prefix.
It does **not** mark the improvement plan or a supported release complete.

Environment: Windows 10 x64 build 19045, Intel Core Ultra 7 255H, .NET SDK 10.0.401/runtime 10.0.12,
`wasm-tools` 10.0.112, `maui-windows` 10.0.20. Native tests select SQLitePCLRaw plain 2.1.12 or SQLCipher 2.1.11 in
separate processes. SQL Server uses a disposable LocalDB database per fixture. At the earlier checkpoint Android/iOS workloads and physical
devices were unavailable. The continuation installed the Android workload; device execution remains unavailable. Later qualification provisioned PostgreSQL 17.11 and Moto 5.2.4 in isolated task-owned
directories on loopback; early runs without them are explicitly historical below. No production service was used.
Playwright engine versions recorded by the full browser run: Chromium 153.0.8010.12, Firefox 155.0, WebKit 26.6.

Earlier checkpoint source counts: core **713**, SQL Server **87**, PostgreSQL **44**, SQLCipher **31**, browser/WPF
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

## Continued implementation after d796e0d

- Optional bounded AES-256-GCM blob envelopes on native and Web Crypto, complete/partial atomic mapping,
  account/identity/mode authenticated data, copy-and-verify migration, key rotation and journaled orphan cleanup.
  Managed budgets include ciphertext overhead and coexistence during replacement. Ten core encryption tests include
  real process termination on both sides of rotation commit; six browser encryption/interop cases passed.
- Opt-in credential-isolated download tickets, one expiry refresh, authority proxy fallback, range/oversize rejection;
  ReadyToPush now has a durable upload prerequisite helper and the Tasks browser uses managed pinned storage.
- Candidate preparation reports required/optional failures separately from the still-usable active snapshot.
  Shared browser/WPF content library survives interrupted revision preparation and offline restart.
- Tasks saves immutable completion requests and separates local persistence, transport and execution UI outcomes.
  Credential-waiting uploads now conditionally resume after sign-in without overriding paused/canceled jobs; the UI
  identifies credential-required attachments and downloads use durable jobs. All 17 transfer tests passed again.
- Explicit EF execution-strategy create recipe with fresh transactions and lost-commit-response recovery;
  optional related-ACL projection with transactional grants/removals and bounded complete repair.
- Fixed direct Get/List membership enforcement in all authorities, with filtering before page limits.
- Optional native scheduler sources plus a common fresh-account runner; its deadline covers account opening,
  execution and cancellation. Platform compilation/device evidence is recorded separately below.
- Captured a real published 0.2.0 IndexedDB fixture; all three browser engines preserve prepared operations,
  groups, conflicts, rejections and unknown fields. See [fixture provenance](published-020-store-fixture.md).

### Current executed checks

| Check | Result |
|---|---|
| Full core after native download recovery | 740 passed, zero skipped; `final-core.trx` (earlier continuation: 735) |
| Full PostgreSQL 17.11 | 45 passed, zero skipped; `continuation-postgres.trx` |
| Full SQLCipher | 31 passed, zero skipped; `continuation-sqlcipher.trx` |
| Full solution | Zero warnings/errors; `final-solution.log` |
| Full SQL Server, including Moto 5.2.4 | 94 passed, zero skipped; `final-sqlserver.trx`; includes ownership, interrupted/ranged transfers and expanded abandoned-staging/grace-period/ownership-record collection assertions |
| Full source browser/WPF checkpoint | 129 passed, zero skipped; `continuation-browser.trx` (before the final credential UI regression additions) |
| Final Tasks attachments, immutable actions, account isolation and expired credentials | 12 passed across Chromium/Firefox/WebKit; `final-tasks-browser.trx` |
| Published IndexedDB preservation | Three passed; `published-020-browser.trx` |
| Encrypted browser content plus browser/WPF library restart | Nine passed; `encrypted-content-library.trx` |
| Exact preview 0.3.0-offline.20261016.8 | Six package/symbol pairs; 177 embedded-source checksums; five archive tests; 55 package-only tests, 75 packaged browser cases and published web smoke passed |
| Actual 0.2.0/current HTTP processes | Four passed: legacy transfer in both version directions, current ticket transfer, explicit old-server 404 retaining partial bytes; `scripts/check_http_compatibility.py` |
| Native callback inbox plus common worker | Nine passed; `native-download-inbox.trx`; five inbox cases plus four runner cases. iOS delegate also compiles against 26.5.10318 bindings. |
| Native scheduler reference compilation | Android 36.1.69 / Work Runtime 2.11.2.1 and iOS 26.5.10318: zero warnings/errors via `scripts/check_native_bindings.py`; not full platform builds |

The first continuation core run had one expected public API baseline failure for EnsureUploadsAsync (733 other tests
passed); the baseline was reviewed/updated, an account-opening deadline test was added, and the full run above passed.
The first ACL tests exposed the direct-read membership omission; corrected in all authorities rather than hidden with
an extra sample CanRead policy. Browser fixture harness compilation needed the default ILocalStore issue-count contract;
its final three-engine run passed. No known product failure is being relabeled as infrastructure failure.

### Desktop performance checkpoint

[Complete observations and environment](offline-performance-2026-10-16.json); fixed seed 1729, 100?200 observations,
one excluded warmup, .NET 10.0.12/SDK 10.0.401, Windows 10 x64, Core Ultra 7 255H, WD PC SN5000S NVMe SSD.
Budgets were declared in docs/benchmarks.md before the measured run. Other local builds/downloads were active;
this is development evidence, not an idle-machine/mobile qualification or a network latency claim.

| Workload | p50 ms | p95 ms | p99 ms | p95 budget ms |
|---|---:|---:|---:|---:|
| sqlite-full-save-during-managed-staging | 0.91 | 1.38 | 6.48 | 50 |
| sqlite-indexed-page-50-of-10000 | 0.40 | 0.48 | 1.70 | 50 |
| package-stage-activate-one-mib | 4.91 | 6.21 | 7.18 | 250 |
| encrypted-16mib-open-authenticate-read-4k | 40.01 | 45.01 | 47.07 | 250 |
| sqlite-reconnect-1000-inprocess | 91.36 | 113.91 | 124.87 | 10000 |

All five latency budgets passed; process peak was 343 MiB against 1 GiB, including setup. Allocations include concurrent
staging for that workload. Encrypted range reads authenticate the complete 16 MiB envelope and allocated about 72 MiB
per observation; the bounded whole-envelope format is deliberately not suitable for unbounded large media files.
Reconnect uses 1,000 prepared writes and an in-process authority, without simulated elapsed days or network latency.

### Remaining qualification boundaries

Hosts must adopt account lifetimes for every custom worker/resource, register idempotent deletion callbacks and provide
real protected-key/account-catalog integration. Unknown legacy pins are preserved. The optional ACL projection requires
registration in every affected writer. Online large-feed rebuild and automatic capture remain explicitly gated/deferred;
the portable milestone uses controlled publication and bounded protected-snapshot repair. Domain-specific external
receivers remain host integrations, with no exactly-once external-effect claim. The optional URLSession download recipe
now persists account receipts and recovers callback files without the original UI; hosts still supply bootstrap,
protected storage, resource lifetimes and credential renewal. Its OS plaintext spool is outside encrypted-cache protection.
Background uploads and actual Apple callback/device qualification remain outside the demonstrated recipe.

External/release gates remain: physical-device execution/key tests, real supported S3 CORS, platform native-library
qualification, clean tagged release provenance, remote source checksum validation and ten consecutive designated
qualification workflow runs. Old 0.2.0 binary/source equivalence remains unresolved. Existing passing tests cannot
establish those gates. No commit, tag, push or package publication was performed by this continuation.

Android build limitation: the full SDK bootstrap installed Microsoft JDK 17.0.14 but failed fetching an Android SDK
archive (HTTP 404); the current official command-line-tools URL also returned 404. Android/iOS reference compilation
caught and corrected obsolete Android Preserve and nullable binding annotations, but no APK/app bundle, manifest,
entitlement, OS job delivery or physical locked-device behavior is claimed. The default solution target remains net10.0.


### Final local evidence (continuation through 2026-10-17)

The [validation manifest](offline-validation-2026-10-17.json) records commands, source fingerprint, toolchain,
native distributions, browser versions, TRX counts/checksums and package identity. Current sample/tooling additions do
not change the six library assemblies in preview .8. Native inbox tests exercise SQLite reopen and injected failure
after pinned cache import/before receipt commit; they are not OS process-relaunch delivery tests. Separate-process HTTP
fixtures restore actual published 0.2.0 packages and the exact .8 candidate with no source project references. CI/release
workflows now run those fixtures and preserve their logs. All four workflow YAML files parse locally; nine release-tool
unit tests pass. Remote workflow execution remains unverified.

Task-owned PostgreSQL and Moto services were stopped after validation; shared LocalDB was left running. Stable release
preflight was attempted and correctly refused the dirty checkout. No tag, push or publication was performed.
