# Bsync implementation plan: from 0.6 to a production-grade 1.0

- **Prepared:** 2026-11-24, from the source at `7a49daf` (unreleased 0.6.0 on top of published 0.5.0).
- **Status:** active. This is the single plan for the repository. It replaces `docs/Improvement-Plan.md`,
  `docs/roadmap.md` and the dated execution records in `docs/review/`. Those files remain readable in the
  [history at `7a49daf`](https://github.com/msynk/Bsync/tree/7a49dafad358240c7d1fd1afe0bcd08e51bfafee/docs).
- **Inputs:** a full review of the source (engine, client session, stores, Blazor layer, server authorities,
  endpoints, blobs, encryption, orchestration) and a survey of comparable sync engines as of November 2026.
- **Release and verification status:** [README](../README.md#status). **Evidence:** [support matrix](guide.md#support-matrix).
  **Design decisions:** [architecture](architecture.md). **Invariants:** [I01–I29](architecture.md#invariants).

Item ids (`A1`, `P3`, …) are stable; tests and commits should reference them. Each item has a priority:
**P0** blocks any further release, **P1** is required for 1.0, **P2** is required to be competitive after 1.0, **P3** is
optional and taken on evidence (ADR-001).

---

## 1. Where Bsync stands

### 1.1 Strengths to keep

- A sound, documented replication core: atomic local write plus pending operation, immutable retry-safe operations
  with server receipts, per-document optimistic concurrency, a gap-free committed-prefix feed with epochs, resets by
  epoch, scope change and expiry, and kept (never silently overwritten) conflicts.
- Executable guarantees: invariants I01–I29, public store and authority conformance suites, wire fixtures, process-kill
  and seeded randomized convergence tests.
- First-class Blazor WebAssembly (IndexedDB, multi-tab lease) and native (SQLite/SQLCipher) replicas; PostgreSQL and
  SQL Server authorities on the application's own database; one `ISyncCollection<T>` across every render mode.
- No injection or blob path-traversal problems were found; feed ordering under concurrency is correct on both durable
  authorities.

### 1.2 Market position (November 2026)

| Option | Blazor WASM offline | Licence / hosting | Main weakness |
|---|---|---|---|
| PowerSync .NET SDK | No (MAUI/WPF/console only, beta) | FSL service, cloud or self-host | Writes need your own API; no browser .NET story |
| Couchbase Lite .NET | No | Commercial | Couchbase stack only |
| Ditto .NET | No (web SDK is in-memory) | Commercial | Proprietary cloud |
| Datasync Community Toolkit | Weak | MIT | Polling, per-table, no realtime/blobs/merge |
| Dotmim.Sync | No | MIT, one maintainer | Database-to-database polling, no reactive client |
| **Bsync** | **Yes** | MIT, self-hosted on Postgres/SQL Server | See section 2 |

Realm/Atlas Device Sync reached end of life in September 2025, Electric's hosted cloud is being shut down after the
Databricks acquisition, and Amplify DataStore ends in May 2027. "Open source, self-hosted on your own database" is
now a selling point. Bsync's lane is: **the first-class offline-first engine for Blazor WebAssembly, Blazor Hybrid/MAUI
and desktop .NET, on the application's own PostgreSQL or SQL Server**.

What leading engines now offer that Bsync lacks: declarative partial replication (PowerSync Sync Streams, Electric
shapes, Couchbase channels), query-driven sync (Zero, TanStack DB), real-time data push instead of hints plus polling,
reactive live queries over a real query engine, server-side mutators with optimistic rebase (Replicache/Zero),
OPFS/worker-based browser storage, client data migrations, devtools and an admin surface.

---

## 2. Summary of findings

| Area | Most important findings | Workstream |
|---|---|---|
| Security defaults | Endpoints open by default; membership derived from client-written JSON; plaintext accepted by an encrypted IndexedDB store | A |
| Correctness | PostgreSQL restore can reissue versions; poison operations block a replica forever; committed writes reported as `forbidden`; leaked browser lease stops all sync | A |
| Server scale | One writer per scope with N+1 round trips under the feed lock; no pull byte budget; hint fan-out per tenant; in-memory retention | P |
| Client performance | Triple JSON encoding across interop; quadratic index fallback; follower tabs re-query every 2 s; in-memory `Where`/`Order` queries | P |
| Attachments | Download appends per socket read; full cache scans; pins never released; large direct uploads livelock; transient errors become terminal | B |
| Developer experience | About 345 public types; manual wiring; no live queries or Blazor components; business rules only on SQL Server | D |
| Capabilities | No sync rules, no real-time data push, no cross-collection atomicity, whole-document writes only, no client migrations | C |
| Platforms | No OPFS/worker store, no BroadcastChannel, background sync only as samples, no device runs | E |
| Operations | Thin server telemetry, no admin API, no devtools, CI not reliably green, release provenance open | O, R |

---

## 3. Workstream A: security and correctness (P0)

Ship as 0.6.x before any new feature work. Every item needs a regression test that fails before the fix.

| Id | Problem | Where | Change | Done when |
|---|---|---|---|---|
| ~~A1~~ | Sync endpoints are open by default: `ResolveScope` returns `"default"` for every caller, and collection and blob routes do not require authorization (multipart routes do). | `SyncEndpointOptions.cs:28`; `SyncEndpoints.cs:141-227`; `SyncBlobEndpoints.cs:56` | Fail closed: require an explicit `ResolveScope`, refuse unauthenticated principals unless `AllowAnonymous = true`, and log a startup warning for a shared scope. Apply the same rule to every Bsync route. | An unconfigured mapping refuses anonymous requests; HTTP binding tests cover each route group. **Done** (0.6.0): `EndpointAdmissionTests`. |
| ~~A2~~ | `PostgreSqlSyncAuthority.BeginNewEpochAsync` raises only existing feeds and stores no version floor, so a feed created after the backup restarts at version 1 and an old `baseVersion` can match a different state. Violates protocol §6.1. | `PostgreSqlSyncAuthority.cs:909-919`, feed creation `:202`, `:558` | Store `version_floor` in `bs_meta` and create new feeds at it (as SQL Server does). | New conformance case "feed created after restore" passes on all authorities. **Done** (0.6.0): in-memory and HTTP pass; PostgreSQL and SQL Server runs pending a database. |
| A3 | Any unexpected authority exception becomes a retryable 503. A deterministic failure (validator bug, undeserializable stored document, constraint violation) makes the replica resend the same batch forever. | `SyncEndpoints.cs:532-537`; authorities' push loops | Per-operation savepoint; deterministic failures become `rejected` with stable codes (`server-error`, `invalid-document`); keep 503 for infrastructure faults; count repeated 503s per replica. | A throwing validator rejects one operation while the rest of the batch commits. |
| A4 | A write that removes the caller's own read access commits, but the outcome is rewritten to `rejected(forbidden)`, so the replica parks it and its group as failed. | PostgreSQL `:225,256,1047-1052`; SQL Server `:632,662,1284-1289` | Return `accepted` with the version and without a document (or a new `accepted-hidden` kind behind a feature flag); the replica removes its copy. | Reassigning a task away from oneself ends clean on the device and on the server. |
| A5 | Read membership is computed from submitted JSON. Without app checks, a reader can add principals or remove co-owners. Creating an existing unreadable id is an existence oracle. Check order differs between authorities. | PostgreSQL `:410-446`; SQL Server `:832-834` | Pass previous and new reader sets to `CanWrite`; default policy "only current readers with owner rights may change readers"; identical outcome for unreadable and absent ids; same order on both authorities. | Conformance cases for reader escalation and the oracle. |
| ~~A6~~ | Pull batches accept duplicate JSON property names and start one database pull per part, unbounded, and buffer the whole response. | `SyncEndpoints.cs:584-613` | Reject duplicates, cap parts at the mapped collection count, bound concurrency, stream the response. | A request with repeated parts is refused with 400. **Done** (0.6.0): `PullBatchHardeningTests`. |
| A7 | Cancelling `IndexedDbReplicaLease.TryAcquireAsync` after JavaScript granted the lock leaks it; every tab then stays Follower until reload. A rejected `navigator.locks.request` never settles. | `IndexedDbReplicaLease.cs:34`; `bsync-indexeddb.js:667-685` | Call with `CancellationToken.None` and release a late grant (as `BrowserAccountResourceLock` does); add `.catch` that resolves `false`. | Browser test: account switch during acquisition leaves another tab able to lead. |
| A8 | Encrypted IndexedDB accepts records without the `enc1:` prefix, so forged plaintext bypasses the AAD binding; the browser recipe cannot pass a key; there is no rotation. | `bsync-indexeddb.js:213`; `IndexedDbSyncServiceCollectionExtensions.cs:61` | Record an "encrypted from" marker and refuse plaintext afterwards; add paged re-seal and rotation; expose the key in the recipe. | ADR-016 updated; browser test with a forged plaintext record. |
| ~~A9~~ | Chunked encryption (v2) manifest AAD has no partial/complete flag or final length, so an earlier partial manifest can authenticate a truncated blob. | `ChunkedEncryptedBlobCache.cs:99,128,290` | Bind `complete` and final length in the manifest AAD and re-seal on publication; optionally verify SHA-256 at end of sequential reads. | Substitution test returns an authentication failure. **Done** (0.6.0): `ChunkedManifestSealingTests`; pre-0.6 manifests verified and resealed on first read. Add a sealing-2 case to the independent Node vector before R5. |
| ~~A10~~ | SQLCipher keys stay in a static connection-string dictionary for the process lifetime. | `SqliteStorePool.cs:14-31,72-96` | Remove pool entries on release and delete; avoid keeping key material in connection strings. | Test asserts no key bytes remain after account deletion. **Done** (0.6.0): `KeyRetentionTests` (no key in any process-wide structure, even while open). |
| ~~A11~~ | `SqliteEncryption.EncryptAsync` replaces the database before removing `-wal`/`-shm` (a crash can leave a plaintext WAL) and clears every pool in the process. | `SqliteEncryption.cs:49-52,178` | Checkpoint and delete WAL first, clear only the affected pool, document that flash storage may retain plaintext. | Process-kill test around the swap. **Done** (0.6.0): `EncryptionProcessKillTests` with a SQLCipher crash host. |
| ~~A12~~ | Blob routes accept CORS-simple requests and reveal existence by status; downloads lack `nosniff` and `Content-Disposition`. | `SyncBlobEndpoints.cs` | Require the `Bsync-Protocol` header on all routes; add security headers. | HTTP tests. **Done** (0.6.0): clients send the header; `RequireProtocolHeader` opt-in until pre-0.6 clients are retired (flip the default in 0.7); plain redirectable reads stay exempt. |
| ~~A13~~ | Blob `finish` deletes the partial before recording access; a lost response forces a full re-upload. | `BlobStores.cs:154-163`; `SyncBlobEndpoints.cs:150` | Record access first, or keep a per-scope completion marker. | Retried finish after a fault returns success. **Done** (0.6.0): completion marker in `FileSystemBlobStore`. |
| A14 | IndexedDB edge cases: a blocked open leaks a connection; a closed connection (iOS Safari backgrounding, clearing site data) is never reopened; request-level failures lose their classification (quota). | `bsync-indexeddb.js:88-94,102-127,153` | Close abandoned opens; reopen transparently on `unavailable`; reject only from `onabort`. | Browser tests for each path. |

---

## 4. Workstream P: performance and scale (P1)

Declare budgets before optimizing; record results in [benchmarks](guide.md#benchmarks) on named hardware.

### 4.1 Server

| Id | Problem | Change | Target |
|---|---|---|---|
| P1 | Pushes serialize per scope (usually a tenant) and hold the feed lock across 4–7 round trips per operation; SQL Server runs the app's write handler under the lock; PostgreSQL `pg_notify` inside the transaction serializes commits across all feeds. | Set-based push: load receipts, documents and access rows for all operations in one query each; write with `unnest`/TVP; hold the feed lock only to assign versions; NOTIFY after commit, debounced. Add `lock_timeout`/`statement_timeout`. Evaluate ADR-005 option 1 (snapshot watermark) for PostgreSQL. | Publish a concurrent throughput benchmark (many scopes, many writers per scope) for both authorities. |
| P2 | Pull pages are bounded by count only; 1,000 documents of up to 4 MiB each can be materialized per request. | Byte budget per page and a maximum document size enforced on push; stream serialization. | Peak server memory per pull bounded by configuration. |
| P3 | Hints fan out per scope, so every commit wakes every client of a tenant (pull stampede, timing leak); the multiplexed channel is unbounded; SQL Server polls every feed every 5 s per instance; subscriber add/remove is O(n). | Subscriber registry keyed by scope and principal; bounded channels; maximum stream lifetime with re-authorization; a small recent-commits table (or global counter) instead of scanning feed heads. | 10,000 SSE clients per instance without O(n²) reconnect cost. |
| P4 | Retention keeps per-scope samples in memory, never purges in apps that redeploy more often than the horizon, and runs on every instance. | Persist a `(scope, version, recorded_at)` watermark or commit timestamps; purge by age; run on one leader (advisory lock / `sp_getapplock`). | Purge survives restarts; memory independent of scope count. |
| P5 | Receipts store a full document copy; conflict and rejected receipts and revoked access rows are never purged; PostgreSQL keys repeat `collection`/`scope` text in every index. | Purge every kind by age; drop the document copy (re-read on replay); compact revoked rows; move PostgreSQL to an integer feed id. | Storage per document close to one copy. |
| P6 | Each PostgreSQL operation serializes the same document about six times; each pull opens two connections; `BeginNewEpochAsync` briefly exposes an empty epoch. | Serialize once; read the epoch inside the pull transaction; swap it atomically. | Allocation benchmark per pushed document. |
| P7 | PostgreSQL publications in a caller transaction never hint on the same instance; there is no `NotifyCommitted`; the listener stops silently on unexpected exceptions. | Accept own-instance NOTIFYs flagged as caller transactions; expose `NotifyCommitted`; restart and log on any listener fault. | Single-instance EF publication produces a hint. |
| P8 | `ScopedAuthority` and `InMemorySyncServer` grow without bound and sort all documents under a global lock. | Mark them test-only in docs and API; optional eviction. | — |

### 4.2 Client and browser

| Id | Problem | Change | Target |
|---|---|---|---|
| P9 | Documents cross JS interop as JSON strings embedded in JSON (three encodings each way); queries return one giant string; cursors clone whole stored values. | `[JSImport]`/`[JSExport]` with UTF-8 `byte[]`/`MemoryView`; store documents as UTF-8 bytes; return `current` only via `getAll(range, count)`. | 100,000 × 2 KB documents: full scan and first page within declared budgets in Chromium, Firefox and WebKit. |
| P10 | Clean IndexedDB records store the document twice; a local write costs three interop round trips plus re-deserialization. | Mirror SQLite's `base_same`; combined read-transform-commit operation that returns the dirty count; skip `ToRecord` when the result is unused. | One interop call per local write. |
| P11 | When stored index keys are unusable (`"!"` written by a writer with another index set), every page of an index query loads and sorts the whole collection; nothing rebuilds the keys. | Materialize once per walk; a writer with the right set rebuilds in the background; SQLite checks `user_version`/schema epoch inside each write transaction. | Index query cost independent of collection size after rebuild. |
| P12 | `CountAsync` deserializes everything; the default interface implementation pages with `Skip` (quadratic); the stale sweep restarts from the beginning each batch. | Store-level counts (`IDBIndex.count`, `SELECT COUNT`); `afterId` continuation for sweeps. | — |
| P13 | SQLite store allocates about 47 KB per pushed document (vs 14.5 KB in memory); `RebuildIndexesAsync` loads everything under the write lock. | Fewer JSON round trips on the push path; paged rebuild. | Within 2× of the in-memory path. |
| P14 | `LocalReadModel` reads staged keys one by one; the IndexedDB orchestration store opens the database on every call. | Batched reads; cached connection with `onversionchange`. | — |
| P15 | `ServerSyncCollection` (Blazor Server and prerendering) reads the whole scope per query and per commit for every circuit and component, and throws above 10,000 documents. | Push `SyncQuery` paging and indexes into the authority reader; coalesce notifications. | Bounded work per query. |

### 4.3 Attachments and blobs

| Id | Problem | Change |
|---|---|---|
| B1 | Downloads append whatever one socket read returns (16–64 KB), not `ChunkSize`; each append costs an fsync, a cache scan, and on v1 re-encryption of the whole partial (O(n²)). | `ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false)`; align appends with the v2 chunk size. (`HttpBlobTransfer.cs:258`) |
| B2 | `ManagedBlobCache.Reserve` scans the whole cache on every write; v1 `SizeAsync` decrypts a whole blob under one global lock. | Running byte total and an LRU index; plaintext length in v1 metadata; per-hash locks. (`ManagedBlobCache.cs:104-156`; `EncryptedBlobCache.cs:20,49-56`) |
| B3 | Large direct (presigned) uploads restart from byte 0 every time the 30 s attempt budget cancels them. | Skip direct PUT above a size threshold or exempt it from the attempt deadline; make multipart the default for object stores. (`HttpBlobTransfer.cs:31,118-140`; `BlobTransferManager.cs:354,378`) |
| B4 | Pins are never released automatically and terminal jobs are never pruned, so the cache fills and downloads fail with `capacity-required`. | Release download pins on completion; "keep offline" as an explicit opt-in pin; prune terminal jobs; fix the Tasks sample. (`BlobTransferManager.cs:199,213,341`) |
| B5 | Locked keys make jobs permanently `Failed`; credential waits resume only per hash and jobs cannot be listed; unexpected exceptions leave jobs `Running` and make `DrainAsync` throw. | Treat key-locked as deferred with retry; `ListAsync(state)`/`ResumeAsync(filter)`; auto-resume after credential renewal; catch-all per job. (`BlobTransferManager.cs:384-397`) |
| B6 | Orphaned import staging is never collected and counts as protected capacity. | Sweep staging older than the lock lifetime during reconcile. |
| B7 | Synchronous disposal of a pinned stream leaks the pin and the lifetime lock. | Release in the background on `Dispose`. (`ManagedBlobCache.cs:386-391`) |
| B8 | Every `ReadyToPush` call re-confirms every completed upload with an HTTP request. | Record "confirmed at"; re-check only after the server's collection grace period. |
| B9 | Per-scope blob quotas are missing; `FileSystemBlobStore` lock objects are never removed and are process-local. | Per-scope byte/count quotas; lock eviction; file or distributed lock for shared storage. |
| B10 | Sharing-violation detection uses errno 11; Apple platforms use 35. | Verify on device and add 35. (`BlobCacheLock.cs:43`, `AccountResourceLock.cs:44`) |
| B11 | `CredentialRenewals.Coalesce` shares one renewal under the first caller's cancellation token. | Run the shared renewal under its own token. |
| B12 | The nonce-budget ledger costs a durable transaction per seal and is off by default. | Derive per-generation or per-envelope subkeys with HKDF; retire the ledger types. |

---

## 5. Workstream D: developer experience (P1)

Goal: a typical app reaches offline sync with about three concepts and twenty lines, while every advanced capability
stays reachable.

| Id | Item | Notes |
|---|---|---|
| D1 | **API surface reduction.** Audit the roughly 345 public types (core baseline 2,360 lines). Move advanced orchestration (workflows, packages, read-model generations, maintenance, encryption formats, budgets) into separate packages (`Bsync.Offline`, `Bsync.Attachments`) or `internal`. | Breaking changes are allowed before 1.0 ([compatibility](architecture.md#compatibility-policy)); record each in the CHANGELOG and the [upgrade notes](guide.md#upgrading-between-versions). |
| D2 | **One builder for clients**: `services.AddBsync(o => o.UseHttp(url).UseIndexedDb()/UseSqlite(path).AddCollection<Note>("notes"))` that wires clock, replica identity, cloner, JSON context, session and coordinator. | Keep the current constructors for tests and custom hosts. |
| D3 | **One builder for servers**: `services.AddBsyncServer(o => o.UsePostgreSql(…)/UseSqlServer(…).AddCollection<Note>(…))` plus `app.MapBsync()` with fail-closed authorization (A1). | — |
| D4 | **Source generator**: generates the JSON context, cloner, field accessors for indexes, merge metadata and typed collection accessors from annotated models. Removes `ISyncEntity` boilerplate (`Id`, `UpdatedAt`, `Deleted` can be generated or moved fully into the envelope). | Must stay trim/AOT-safe. |
| D5 | **Live queries**: `ISyncCollection.Subscribe(Action<SyncChange>)` carrying changed ids (already computed by the engine, discarded at `SyncSession.cs:112`), and `LiveQuery<T>` that re-evaluates only when touched ids or index ranges intersect. | Basis for E2 and D6. |
| D6 | **Blazor components**: `<SyncQuery>`/`<LiveQuery>` component, cascading `SyncStatus`, conflict and issue UI building blocks, and a `PersistentComponentState` hand-off so Auto mode does not fetch twice or flash empty. | Register-time error when `AddBrowserSyncCollection` is used in a server project. |
| D7 | **Attachments facade**: `Attachments.ImportAsync/OpenAsync/EnsureLocalAsync/EnsureUploadedAsync/RunMaintenanceAsync(budget)` with automatic pin release, retry, resume and reconcile (replaces about seven hand-wired objects). | Depends on B4, B5. |
| D8 | **PostgreSQL write handler** with the same contract as SQL Server's `ISyncWriteHandler`, so business rules and domain-table writes run in the sync transaction on both authorities. | — |
| D9 | **EF Core integration**: map an EF entity set to a sync collection (projection publisher and write handler generated from the model), for both authorities. | — |
| D10 | **Documentation reset**: a short getting-started per host (WASM, MAUI, WPF, Server), a concepts page, and the README reduced to a landing page. | — |

---

## 6. Workstream C: sync capabilities (P1/P2)

| Id | Priority | Item |
|---|---|---|
| C1 | P1 | **Declarative partial replication (sync rules).** Server-defined, parameterized, claim-aware rules (`Rule("my-tasks", (caller, t) => t.AssigneeId == caller.UserId)`) evaluated at write time into membership buckets; clients subscribe to rules and parameters on demand (`collection.Subscribe("project", projectId)`) with eviction when unsubscribed. Builds on ADR-015 membership and removals. New ADR required. |
| C2 | P1 | **Real-time data push.** WebSocket/SignalR stream that carries pages, not just hints, with resumable checkpoints; SSE hints and polling stay as fallbacks. One connection for all collections. |
| C3 | P1 | **Batched multi-collection push** in one request, matching the existing batched pull. |
| C4 | P1 | **Cross-collection dependency groups and consistent multi-collection snapshots** (a shared sequence for a collection group), so parents and children arrive and commit together. |
| C5 | P1 | **Client data migrations.** Versioned document schemas with migration functions applied lazily or in pages; server-side upcasters for stored JSON; per-client projection by schema version. Close I17 for documents without an extension-data bag. |
| C6 | P2 | **Field-level changes on the wire** (JSON patch or changed-member sets with per-field bases), so concurrent edits of different fields merge without full conflict handling. |
| C7 | P2 | **Named mutators with server execution and client rebase** (Replicache/Zero style) as an alternative to document push for commands that must run server-side; integrate with the existing intent pattern and close I25 (generic command ledger). |
| C8 | P2 | **Integrity checksums** per scope or bucket so replicas detect divergence and self-repair. |
| C9 | P2 | **Change capture from application tables.** PostgreSQL logical decoding; promote SQL Server Change Tracking capture (ADR-041) from experimental. |
| C10 | P2 | **Receipt retention safety**: persisted, time-based receipt horizons tied to the offline horizon (see P4) so a replayed success cannot become a conflict. |
| C11 | P3 | **CRDT types for text and lists** via Yjs (yrs/YDotNet) or Loro interop, stored as a document member. |
| C12 | P3 | Peer-to-peer/LAN sync; end-to-end encrypted collections; MySQL and Cosmos DB/MongoDB authorities; an MCP endpoint for agent access. Only on evidence from real use. |

---

## 7. Workstream E: platforms (P1)

| Id | Item |
|---|---|
| E1 | **Cross-tab coordination**: BroadcastChannel messages `{collection, ids}` after each commit; followers notify only on real changes (today every follower fires `Changed` every 2 s, `SyncSession.cs:891`); followers ask the leader to push immediately (today writes wait up to the 30 s interval, longer when the leader tab is throttled); hand leadership to a visible tab. |
| E2 | **Worker-based browser store**: run the store and the sync loop in a dedicated or shared Web Worker; evaluate SQLite-WASM on OPFS against IndexedDB (ADR-008 asks for evidence; record it). Keep IndexedDB as the fallback. |
| E3 | **Storage pressure**: react to `persist()` returning false, surface `navigator.storage.estimate()` warnings, document Safari's 7-day eviction for non-installed sites. |
| E4 | **`Bsync.Maui` package**: supported WorkManager and BGTaskScheduler adapters (today only samples), background URLSession/foreground-service blob transfer, secure-storage key integration, locked-device deferral. |
| E5 | **Browser background**: document that sync is foreground-only; optional service-worker integration for Background Sync where available. |
| E6 | **Device qualification**: run SQLite/SQLCipher and the MAUI sample on Android, iOS and Mac Catalyst; run browser tests on native Safari and mobile browsers; run SQLite on Linux and macOS. |

---

## 8. Workstream O: operations and tooling (P1/P2)

| Id | Item |
|---|---|
| O1 | Server `ActivitySource`, latency histograms, feed-lock wait, replication lag per scope, poison-operation and repeated-503 metrics, hint subscriber counts (today two counters, `SyncEndpoints.cs:48-52`). |
| O2 | Admin API: feed heads, replica audit, stuck replicas, conflicts and rejections per scope, retention state, epoch operations. |
| O3 | Devtools: an in-app diagnostics component (queue, conflicts, last errors, checkpoint, storage) and an offline diagnostic export viewer. |
| O4 | Rate limiting and quotas guidance with ASP.NET Core rate limiter recipes per scope and per principal. |
| O5 | Lock-order guidance (domain rows before feed or the reverse, consistently) and `AcquirePublicationLockAsync` on PostgreSQL too. |
| O6 | Restore drill scripts (`pg_dump`/`pg_restore`, SQL Server backup/restore) wired into CI. |

---

## 9. Workstream R: qualification and release (P0 for every release)

Carried forward from the previous plans; still open.

| Id | Gate |
|---|---|
| R1 | Make CI reliably green: ten consecutive designated qualification runs on Windows, Linux and macOS, with infrastructure failures separated from product failures. |
| R2 | Releases only from a clean checkout at an immutable tag; archive checksums, assembly/PDB pairing, Source Link and remote source verification recorded per release; never reuse a published version with different bytes. Resolve or formally close the 0.2.0 provenance mismatch. |
| R3 | Old-client/new-server and new-client/old-server process fixtures for every wire-affecting change; fixtures captured from published 0.4.0 and 0.5.0 binaries. |
| R4 | Qualify a maintained SQLite/SQLCipher distribution per supported native target. |
| R5 | Independent cryptography review of the blob envelopes (after A9 and B12) and of IndexedDB record encryption (after A8). |
| R6 | Real object-store evidence: S3-compatible service and MinIO, browser CORS without forwarding credentials. |
| R7 | Physical-device evidence for every advertised background, locked-key and lifecycle behavior (E4, E6); no support claim without it (I28). |
| R8 | Performance and storage budgets declared per target and measured: p50/p95/p99 latency, allocations, peak memory, physical write amplification, cancellation latency. |
| R9 | Public API baselines, source-generated contexts, docs, support matrix and compatibility notes agree for every release. |

---

## 10. Milestones

| Version | Content | Exit criteria |
|---|---|---|
| **0.6.x** | A1–A14, B1, B3, B4, B5 (minimum), R1 | All P0 items closed with regression tests; CI green ten runs in a row. |
| **0.7** | P1–P15, B2, B6–B12, E1, O1 | Published server throughput and browser 100k-document benchmarks within declared budgets. |
| **0.8** | D1–D8, D10, C3, C5 | New getting-started under twenty lines per host; public surface audited and baselines updated. |
| **0.9** | C1, C2, C4, E2, E3, E4, O2, O3 | Sync rules and real-time push pass conformance on both authorities; worker store decision recorded. |
| **1.0** | R2–R9, E6, D9 | Release gates met on the declared support matrix; API frozen under semantic versioning. |
| **1.x** | C6–C12, E5, O4–O6 | Taken on evidence from real use. |

---

## 11. Rules for implementation

1. Every change names its item id; every bug fix starts with a failing test.
2. Protocol changes get a feature flag in `features`, fixtures in `src/Tests/Bsync.Tests/Fixtures/protocol`, and an update to
   [protocol v1](protocol.md); older peers keep working (ADR-011).
3. Store and authority changes must pass the public conformance suites on every provider.
4. New decisions get a new `ADR-NNN` section in [architecture](architecture.md); superseded ADRs are marked, not deleted.
5. Evidence goes into the [support matrix](guide.md#support-matrix) and the CHANGELOG; breaking changes and upgrade steps go into the CHANGELOG and the
   [upgrade notes](guide.md#upgrading-between-versions). Do not add dated execution records
   to `docs/`; put run artifacts in CI artifacts and summarize results in the pull request.
6. Keep this plan current: tick items in the tables (add a "Done in" column entry or strike through) in the same change
   that completes them.

---

## 12. Known limitations today

- Durable authorities: PostgreSQL (verified with PostgreSQL 17.6) and SQL Server (verified on SQL Server 2025 LocalDB);
  the in-memory authority is for tests and samples.
- SQLite is verified on Windows only; IndexedDB in Playwright's Chromium, Firefox and WebKit on Windows. No Android,
  iOS, Mac Catalyst or native Safari runs.
- Full-document replacement drops fields unknown to an older writer unless the document declares
  `[JsonExtensionData]` (I17 partial; C5, C6).
- With the default conflict policy, replicas converge only after kept conflicts are resolved or discarded; an app must
  surface them.
- Receipt retention is manual; purging too early turns a replayed success into a conflict (C10).
- Queries evaluate `Where` and custom `Order` in memory; a custom order reads the whole collection (P11, D5).
- CI is not yet reliably green; browser, WPF and MAUI tests have run on Windows only (R1, E6).
