# Changelog

User-visible changes per release. Pre-1.0: a minor release may break, a patch release never does
([compatibility policy](docs/compatibility.md#policy)); every breaking change also has a migration note in
[docs/compatibility.md](docs/compatibility.md). Release status: [README](README.md#status).

## Unreleased (0.2.0)

Everything after 0.1.0, not yet on NuGet. Breaking: the package set, two signatures and one status behaviour (see Changed).

### Added

- `Bsync.Server.SqlServer`: a durable authority on the application's own SQL Server database (ADR-014). Protocol
  tables live in a `bsync` schema next to the application's tables; versions are allocated under a per-feed lock
  (`UPDLOCK, HOLDLOCK`), so readers never skip a later-committing lower version. Writes enlist in a caller's
  `DbTransaction` (for example EF Core's), and an `ISyncWriteHandler<T>` runs domain logic in the same transaction:
  accept with a server-computed canonical document, conflict, reject with a stable code and arguments, or retry
  later. Includes retention, epochs with a version floor that also covers feeds created later, dependency groups,
  read membership, and polled cross-process commit hints (`CommitPollInterval`) plus `NotifyCommitted` for a host's
  own bus. Verified on SQL Server 2025 LocalDB.
- `ISyncPublisher<T>` for server-originated writes (API endpoints, jobs, imports, projection rebuilds):
  `UpsertAsync`, `DeleteAsync`, `ReplaceScopeAsync` (tombstones documents no longer listed) and `PublishAsync`
  (fan-out of read-only projections). Unchanged content keeps its version, so rebuilding a projection adds no feed
  entries. Implemented by the in-memory, SQL Server and PostgreSQL authorities and `ScopedAuthority`; database
  authorities enlist in a caller's transaction.
- Read membership in the feed (ADR-015): with `Readers` and `PrincipalKey`, the in-memory, SQL Server and PostgreSQL
  authorities serve each caller only what it may read, in full pages (500 of 100,000 documents in five pages in
  0.26 s on SQL Server 2025 LocalDB), and a document leaving a caller's view arrives as a removal instead of a
  collection reset (protocol feature `removals`). Clean copies leave the device; local drafts are hidden and kept.
- `MapSyncCollections(options, group => group.Add<A>("a").Add<B>("b"))` maps several collections with one set of
  options and one route group for authorization. Collections mapped this way can be pulled in one request
  (`HttpPullBatch`, feature `pull-batch`; a reset or error in one collection reaches only that collection) and share
  one multiplexed hint stream per account (feature `hints-multiplex`).
- `SyncCoordinator` for many collections: concurrency limit, priorities, parents synced before children
  (`DependsOn`), a shared backoff, one aggregate status that names failing collections, and a host hint bridge.
- Awaitable sync goals: `SyncAsync(SyncGoal.Complete | Background | Accepted(id, revision), budget)` on a collection or
  the coordinator, with progress; `SyncEngine.SyncForAsync(timeBudget)` for time-limited background tasks.
- Pull-only collections (`SyncOptions.Mode = SyncMode.PullOnly`): one copy per document and no write bookkeeping;
  local writes throw `SyncReadOnlyException`.
- Honest status: `SyncStatus.Conflicts`, `Rejected`, `Blocked`, `LastPulled`, `LastPushed`, `Staleness`, `IsStale`;
  `ISyncCollection.GetIssuesAsync` pages issues with a total.
- Rejections carry arguments: `SyncWriteDecision.Reject("title-too-long", new Dictionary<string, string> { ["max"] = "200" })`
  reaches the replica's record, `GetItemStatusAsync` and `GetIssuesAsync`, and replays from receipts keep it.
- `SyncOptions.KeptConflictView = KeptConflictView.Local` keeps showing the user's own edit while a conflict waits.
- `SyncOptions.ReadyToPush` holds a pending document back until something it names has reached the server.
- Declared secondary indexes (ADR-018): `SyncIndex<T>.Create("due", d => d.Due)`, queries with
  `SyncQuery.Index = due.From(today).Descending()`, `Skip`, and `CountAsync`. The in-memory, SQLite and IndexedDB
  stores maintain them; a 50-item ordered page of 50,000 documents takes under 0.5 ms instead of over 300 ms on a
  desktop.
- Local tombstone compaction: pull responses carry the server's retention horizon (feature `retention`), and replicas
  drop their clean tombstones below it. In a 30-day soak with 7-day server retention a replica kept at most 160.
- A retention service: `AddSyncRetention` runs `SyncRetention` in an ASP.NET Core host and purges tombstones and
  receipts older than `MaxOfflineHorizon` (default 45 days) in every in-memory, SQL Server or PostgreSQL authority it
  is given. A receipt horizon shorter than the offline horizon fails the host at startup.
- Attachments in the packages (task F1). `Bsync`: `BlobReference`, `IBlobCache`, `FileBlobCache` and the resumable
  `HttpBlobTransfer` (namespace `Bsync.Blobs`). `Bsync.Blazor`: `BrowserBlobStore` (verified, resumable content in
  IndexedDB). `Bsync.Server.AspNetCore`: `MapSyncBlobs` (resumable chunked uploads, verified finish, range and
  redirect downloads, 404 for callers who may not read), `IBlobStore`, `FileSystemBlobStore`, `IBlobAccess` and
  `SyncBlobCollector` for garbage collection. An `S3BlobStore` recipe in the Tasks sample: presigned downloads and,
  optionally, presigned uploads that bypass the application server.
- Encryption at rest (ADR-016): `Bsync.Storage.Sqlite` on the SQLCipher native bundle (encrypt in place, rekey) and
  `IndexedDbStoreOptions.EncryptionKey` (AES-GCM with WebCrypto). A wrong or missing key fails explicitly.
- Wipe on sign-out: `SyncSession.DeleteReplicaAsync(account)` and `SyncCoordinator.DeleteReplicasAsync(account)` close
  an account's replicas and delete them from the device (`SqliteStorePool.DeleteDatabaseAsync`; the browser recipe
  deletes the IndexedDB database). The browser recipe requests persistent storage and reports it in
  `SyncStatus.PersistentStorage`.
- A MAUI recipe (`MauiSync`, in the MAUI sample; `docs/patterns/maui.md`): sync on reconnect, pause and resume with
  the app, budgeted background runs, keys in `SecureStorage`. Used by the MAUI sample on Windows; mobile targets not
  yet built or run.
- Protocol: pull responses advertise the server's limits (feature `limits`); replicas clamp their push and pull
  batch sizes to them.
- `CredentialRenewal` (`Renewed`, `Offline`, `SignInRequired`) and `CredentialRenewals.Coalesce`, so an unreachable
  identity provider keeps the queue and retries, and only a real sign-in requirement stops uploads.
- `InMemorySyncServerOptions<T>.WriteHandler` and `PushErrorCodes.DependencyMissing`: a server can answer
  `retry-later` for a write whose dependency has not arrived yet; the replica resends the same operation with backoff.
- Operations: the `bsync.issues` gauge (conflicts, rejections, blocked group members per collection); blob metrics
  `bsync.blob.bytes`, `bsync.blob.transfers` (client), `bsync.server.blob.bytes`, `bsync.server.blob.requests`
  (server); an optional replica acknowledgement audit (`SyncEndpointOptions.ReplicaAudit`, `SqlServerReplicaAudit`)
  recording which checkpoint each replica reached and when; a SQL Server restore drill, retention service and
  capacity guidance in the runbook; an authority throughput benchmark with concurrent sessions
  (`docs/benchmarks.md`); `Bsync.Benchmarks push-alloc` measures allocations per pushed document.
- The authority conformance suite is public (namespace `Bsync.Testing`). `AuthorityConformance.Cases` (one outcome per
  operation, version and conflict rules, receipt replay, operation id reuse, malformed operations, origin timestamps
  and the clock-skew bound, validation, feed coverage and paging, resume, foreign checkpoints, filtered reads, scope
  fingerprints, scope isolation, dependency groups, tombstone and receipt retention, new epochs, plus five publisher
  and four membership cases), run through an `IAuthorityConformanceDriver`. `InMemoryAuthorityDriver` is the
  reference driver; `HttpAuthorityDriver` runs the same cases through the HTTP binding. The in-memory, JSON-wire,
  HTTP, SQL Server and PostgreSQL suites in this repository all run these cases. `LocalStoreIndexConformance` covers
  declared indexes.
- Sample `Bsync.Samples.Tasks.*`: EF Core on SQL Server as the system of record, a write handler that computes a
  field and rejects with a stable code, a publisher used by an API endpoint, bearer tokens, a console client on
  SQLite and a WebAssembly client on IndexedDB, with automated end-to-end tests. It executes immutable intents
  (complete, rename) exactly once in the write handler's transaction (`docs/patterns/intents.md`), distributes
  bundles that devices switch to in one step once every item is verified (`docs/patterns/bundles.md`), and attaches
  files through the package blob routes, optionally kept in an S3-compatible bucket, with uploads recorded
  (`dbo.TenantBlobs.UploadedAt`) so a job can collect content nothing references (`docs/patterns/attachments.md`).
  The browser client attaches files too: uploaded before their task, downloaded on demand, readable offline; tested
  in Chromium, Firefox and WebKit.
- CI builds and tests projects that reference only the packed packages (`src/Tests/PackageConsumers`): the public
  conformance suites from the `Bsync` package, and an ASP.NET Core host with the browser store's static
  assets and a SQLite replica that syncs over HTTP.
- A nightly workflow packs `-preview` packages as build artifacts (never published).
- `CHANGELOG.md`, the 0.x compatibility policy and the release process (`docs/compatibility.md`).
- A migration test from a replica database written by the published 0.1.0 SQLite package (pending, conflict,
  rejection, group and unknown members kept through the upgrade to schema 5).

### Changed

- **Breaking (packages):** six packages. `Bsync.Testing` is merged into `Bsync` (same namespace and types); reference
  `Bsync` instead.
- **Breaking (packages):** `Bsync.Storage.Sqlite` depends on `Microsoft.Data.Sqlite.Core` and no longer brings a native
  SQLite library, so one package serves plain and encrypted replicas. Add `SQLitePCLRaw.bundle_e_sqlite3`, or
  `SQLitePCLRaw.bundle_e_sqlcipher` for encryption, to the application.
- **Breaking:** the `Validator` option of `InMemorySyncServerOptions<T>` and `PostgreSqlSyncAuthorityOptions<T>`
  receives the caller (`SyncCallContext`) as its first argument. Migration: `(op, current) => ...` becomes
  `(caller, op, current) => ...`.
- **Breaking:** `SyncSessionOptions<T>.RenewCredentials` returns `Task<CredentialRenewal>` instead of `Task<bool>`.
- **Breaking (behaviour):** a session never reports `Synced` while conflicts or rejections wait for a decision; it
  reports `AttentionRequired`.
- A push refused as too large is split in halves; a document too large on its own is parked locally with rejection
  `payload-too-large` and the rest of the queue continues (before, one oversized document blocked the collection).
- Pull responses carry the server's time (feature `server-time`); a replica whose clock is ahead corrects its clock,
  so its writes are no longer rejected with `clock-skew` after the first sync. Writes stamped with a fast clock
  before the first pull (offline, right after install) are re-stamped from the server's time and uploaded in the
  same sync instead of staying parked (ADR-017; `SyncOptions.RestampSkewedWrites`).
- Subscribers are not notified on idle background cycles or for timestamp-only status updates.
- SQLite schema 5 (column `base_same`, table `bs_index`, column `rejection_arguments`): a record's base copy is not
  stored when it equals the current state, so a 10,000-document replica of ~1 KiB documents shrinks from 41.8 MB to
  14.5 MB. Schema 3 databases (0.1.0) upgrade in place on open.
- IndexedDB schema 4 (index `ix`); schema 3 databases (0.1.0) upgrade in place on open.
- PostgreSQL schema 2 (table `bs_document_access`, for read membership); schema 1 databases (0.1.0) upgrade in place
  on first open.
- Fewer allocations per synced document: pushing one document allocates 14.5 KB instead of 66.9 KB with the in-memory
  store and server, and 47.2 KB instead of 80.6 KB with SQLite; the in-memory server allocates 8.1 KB instead of
  16.4 KB per accepted write (docs/benchmarks.md).

### Fixed

- The session no longer resyncs in a tight loop while the server answers `retry-later`; it backs off (a test measured
  19,903 push requests in one second before the fix).
- Documentation now matches the code and NuGet: six packages at `0.1.0` are published; PostgreSQL is a durable
  authority; protocol headers are implemented; document upgrade on read (`DocumentUpgrade`) is documented in the
  runbook; the live stream is consumed by `SyncSession` as hints; CI status and test counts come from real runs.
  The README has a single [Status](README.md#status) section the other documents link to.
- Test reliability: `HttpBindingTests.TimeoutAfterCommitIsRetrySafe` retried with the same 300 ms timeout it
  used to provoke the first timeout, so on a loaded machine the retry could time out too (1 failure in 15 local
  runs; a likely cause of the intermittent Windows CI failures). The retry now uses a normal timeout. Two races in
  the T24/T30 concurrency drills (the reader could stop on a page fetched before the last commits) are fixed in the
  SQL Server and PostgreSQL test projects.

## 0.1.0 - 2026-09-28

First published release: `Bsync`, `Bsync.Blazor`, `Bsync.Storage.Sqlite`, `Bsync.Server.AspNetCore`,
`Bsync.Server.PostgreSql` and `Bsync.Testing` on NuGet.org.

- Local-first engine: atomic local write plus pending operation, immutable operation ids with server receipts and
  replay, per-document optimistic concurrency, checkpointed atomic pull, epochs and resets (`epoch`,
  `scope-changed`, `expired`), dependency groups.
- Conflict handlers: Defer (default, durable kept conflicts), ThreeWayMerge (with set and counter merges),
  ClientWins, ServerWins, LastWriteWins, Delegate.
- Stores: SQLite (native hosts), IndexedDB (browsers, multi-tab lease), in-memory; schema migrations that keep
  pending work; SQLite check and rebuild; export and import of local work.
- Authorities: in-memory reference and PostgreSQL; ASP.NET Core endpoints with a Server-Sent Events hint stream;
  `HttpSyncTransport`; scopes, read and write authorization, retention.
- `ISyncCollection<T>` for every Blazor render mode and native hosts, with the `SyncSession` loop (triggers,
  backoff, `Retry-After`, pause and resume, credential renewal).
- OpenTelemetry traces and metrics; session and server logs.

Details of every change before this release: [docs/compatibility.md](docs/compatibility.md#before-010).
