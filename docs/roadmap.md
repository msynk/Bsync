# Roadmap

Phases follow the implementation plan. Each lists its status, the invariants it owns and the next
concrete work item. Release and verification status: [README](../README.md#status); evidence:
[support matrix](support-matrix.md). Test ids (T01–T60) refer to the adversarial catalogue; invariants (I01–I20) to
[`architecture/invariants.md`](architecture/invariants.md).

## v1 product focus

Authorized application documents replicated between durable client replicas (IndexedDB in browsers,
SQLite natively) and one ASP.NET Core authority backed by PostgreSQL, with offline local writes where code
runs on the device and equivalent contracts for server-connected and request-rendered hosts. Initial
workloads and targets: ADR-012. Explicit non-goals: see the README.

## Status

| Phase | Status | Summary |
|---|---|---|
| 0: Evidence and decisions | **Done** | `docs/review/baseline.md`, ADR-001–012, support matrix, compatibility policy, this roadmap. |
| 1: Immediate core safety | **Done** | All ten baseline scenarios fixed with regression tests; atomic store transforms; single-flight replication; validated options, ids and HLC; truthful `SyncResult`. |
| 2: Protocol v1 and durable-state contracts | **Done** | Metadata separation, immutable operations, receipts, per-operation outcomes, normative spec and fixtures, strict JSON, conformance suites, AOT-honest API, reset/resnapshot with generations and authority restore rules (`docs/protocol/v1.md` §6.1). |
| 3: Durable local storage (SQLite) | **Done for Windows** | `Bsync.Storage.Sqlite`: transactional store, schema versioning, replica identity/incarnation, conformance, process-kill tests, post-commit observation (`SyncEngine.Observe`). Schema 2 with an in-place, pending-work-preserving migration (Phase 8). Remaining: runs on Linux/macOS/mobile. |
| 4: Authority, PostgreSQL, HTTP | **Done (with stated gaps)** | `ISyncAuthority` shared by HTTP and in-process callers, scopes and read/write authorization hooks, `Bsync.Server.AspNetCore` endpoints, `HttpSyncTransport` client (now in `Bsync`); conformance over HTTP. PostgreSQL authority (`Bsync.Server.PostgreSql`): per-feed serialized allocation (ADR-005) with T27/T28/T30 tests, retention, epochs, scopes, cross-process hints via LISTEN/NOTIFY, dependency groups; conformance suite on PostgreSQL 17.6; two notes-server processes on one database in three browsers. Remaining: token refresh and cookie/CORS guidance with a real identity provider; throughput benchmarks on PostgreSQL; SQL Server. |
| 5: IndexedDB, WASM/PWA slice | **Done (with stated gaps)** | IndexedDB store (now in `Bsync.Blazor`; optimistic cross-tab commits, Web Locks lease, upgrade/unavailable handling), Playwright suite in Chromium/Firefox/WebKit (also against a WebAssembly AOT build), notes PWA sample (server + offline client) with offline reload, service-worker update and server-restart tests. Gaps: offline app-shell reload unverified in WebKit (Playwright limitation), no native Safari/mobile runs, no real quota-pressure test, OPFS not pursued (ADR-008). |
| 6: Blazor host integration | **Done for web hosts** | Client API in `Bsync` (namespace `Bsync.Client`: collection API, local session, local recipe; no UI dependency) and `Bsync.Blazor` (server-connected collection), explicit recipes incl. `AddBrowserSyncCollection`; Blazor Web App sample (static SSR, Interactive Server, WebAssembly, Auto) with Playwright tests; notes PWA moved onto the recipe. WPF and .NET MAUI (Windows) Blazor Hybrid samples with SQLite, tested through their UI. Remaining: Android/iOS/macOS device runs (workloads and devices not available here); authentication in a sample (account switching is unit-tested only). |
| 7: Session lifecycle and notifications | **Done for web hosts** | Single-flight loop; triggers (write, interval, request, SSE hints, browser `online`/visibility); exponential backoff with jitter; `Retry-After`; Web Locks lease and follower mode; status model and per-item status; account switching; pause/resume; one-shot credential renewal; hint stream endpoint and client with reconnect. Native pause/resume is wired in both Hybrid samples. Remaining: a server watermark in status, SignalR as an alternative hint transport (SSE only today). |
| 8: Conflicts, selective sync, retention | **Done (with stated gaps)** | Conservative default (`Defer`): durable unresolved conflicts in every store, list/resolve/discard in the engine and `ISyncCollection`, conflict UI in the samples' `NotesPanel`; field-level three-way merge and a merging handler; scope fingerprints in checkpoints with reset reasons (`epoch`, `scope-changed`, `expired`), device-side removal on revoke and return on regrant; tombstone retention horizon with `base-expired`; receipt expiry without double application; SQLite and IndexedDB schema 2 migrations keeping pending work; the default policy in the randomized convergence suite. Conflict UI verified end to end in three browsers (two devices, keep mine). Later the same day: dependency groups (protocol §4.1, both authorities, in the randomized suite), set and counter merges, and retention on PostgreSQL. |
| 9: Migrations and disaster recovery | **Done for clients (with stated gaps)** | Store schema migrations (SQLite, IndexedDB 1→2 keeping pending work); rolling domain-schema upgrade over HTTP; parked rejections listed, retried as new operations or reverted (engine and `ISyncCollection`, sample UI); export/import of local work preserving operation ids; SQLite check and rebuild that moves the damaged file aside and salvages readable local work page by page; operations runbook (`docs/operations/disaster-recovery.md`, ADR-013). Later the same day: a document upgrade hook (`DocumentUpgrade`), and a PostgreSQL restore drill (tables restored, new epoch with a version floor). Gaps: storage-level faults (torn writes, power loss) not exercised; no `pg_dump`/`pg_restore` drill script. |
| 10: Operational quality and release | **Done; CI not yet reliably green** | Traces and metrics in the core (`Bsync` source and meter: operations by outcome, conflicts, resets, run duration, queue depth and oldest pending age), session and server logs, server request/outcome metrics, a retryable 503 for authority failures, and a catch-all in the session loop; BenchmarkDotNet workloads with a recorded run (docs/benchmarks.md); package metadata and a pack of the six packages (`0.1.0` published to NuGet on 2026-09-28); public API baselines checked by tests; a GitHub Actions workflow. Later the same day: the OpenTelemetry SDK export is tested, and a 100,000-document benchmark was added. Since then (0.1.1, unreleased): the authority conformance suite is public in `Bsync.Testing`, CI builds and tests consumers of the packed packages, and a nightly preview pack runs. Gaps: CI not yet reliably green (see the support matrix); no multi-process throughput benchmark; benchmarks recorded on one development machine only. |
| 11: Evidence-selected extensions | Not started | |

## Handoff: next work items, in order

1. **Make CI reliably green.** CI has run since 2026-09-28: Linux and macOS unit tests, PostgreSQL on Linux and
   pack pass; the Windows unit tests and the Windows browser/WPF job failed intermittently. One cause is fixed (a
   timing-sensitive HTTP test); the browser job's failure has not been reproduced locally. Done when ten
   consecutive runs are green on Windows, Linux and macOS. Workstream B (SQL Server authority, write handler, publisher, server ergonomics) is implemented for
   0.2.0 and verified on SQL Server 2025 LocalDB; the relational sample (B5) is next.
2. **Mobile and desktop devices.** Build the MAUI sample for Android, iOS and Mac Catalyst (workloads and devices
   needed). Run the browser tests in native Safari and mobile browsers.
3. **Performance.**
   - Fewer JSON clones on the sync path (about 64 KB allocated per pushed document; see docs/benchmarks.md).
   - Indexed queries beyond id order.
   - A throughput benchmark on PostgreSQL with concurrent sessions.
4. **Identity.** A sample with real authentication (cookie or token renewal, account switching in the browser).
5. **Phase 11** — extensions only on evidence from real use (ADR-001): nothing selected yet.

Watch item: one run of `BlazorIntegrationTests` (out of about 80 runs on 2026-09-28) failed with a wait
timeout. It did not reproduce in 76 further runs. The wait helper now uses a 10-second wall-clock deadline and
reports the elapsed time.

## Known limitations today

- Durable authorities: PostgreSQL (verified on Windows with PostgreSQL 17.6) and SQL Server (unreleased; verified on SQL Server 2025 LocalDB); the in-memory authority is for tests and samples.
- SQLite is verified on Windows only; IndexedDB in Playwright's Chromium, Firefox and WebKit builds on Windows.
- Full-document replacement drops fields unknown to an older writer unless the document declares
  `[JsonExtensionData]` (I17 partial).
- With the default conflict policy, replicas converge only after kept conflicts are resolved or discarded;
  an app must surface them.
- Receipt retention is manual (`PurgeReceipts`); purging too early turns a replayed success into a conflict.
- The original demo (`Bsync.Demo`) is a single-tab simulation; the notes sample is the realistic one.
- Queries with a custom order load the whole collection into memory; default-ordered queries read bounded pages.
- CI runs on Windows, Linux and macOS but is not yet reliably green; browser, WPF and MAUI tests have run on
  Windows only.
