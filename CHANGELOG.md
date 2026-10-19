# Changelog

User-visible changes per release. Pre-1.0: a minor release may break, a patch release never does
([compatibility policy](docs/compatibility.md#policy)); every breaking change also has a migration note in
[docs/compatibility.md](docs/compatibility.md). Release status: [README](README.md#status).

## 0.5.0 - 2026-10-19

- Bound full-content SHA-256 on WebAssembly instead of using the runtime's whole-input buffering fallback. Keep
  native platform hashing, plaintext identities and formats unchanged; add independent digest and allocation regressions.
  Add exact-package browser memory/I/O workloads and source-generated reports for trimmed/native AOT qualification.
  Record executable identity for embedded-assembly publications instead of attempting to hash an unavailable DLL path.
  Avoid rereading the entire ciphertext inventory on every append when no hard capacity budget is configured;
  finite budgets retain their serialized physical-capacity checks.

- Send explicit checksums and known-length S3 sample uploads so compatible services do not persist transfer encoding
  as object content encoding. Add isolated real SeaweedFS qualification with expiry, refresh and three-engine CORS checks.

- Close the S3 sample's verification/publication race by uploading an exclusively held, bounded snapshot of the exact
  verified bytes. Add a configurable real-service/CORS qualification runner; emulator evidence remains separate.

- Avoid HTML escaping of Base64 in internal encrypted-v2 storage envelopes, reducing JSON scratch-buffer pressure.
  Existing escaped records remain readable; encrypted plaintext manifests, wire formats and public APIs are unchanged.

- Avoid a redundant plaintext chunk buffer for aligned encrypted-v2 appends. Preserve caller memory and partial-tail
  semantics; add isolated exact-package memory workloads with enforced GC caps and retained failure evidence.

- Add bounded, resumable authentication scrubs for sealed chunk generations, with encrypted durable checkpoints and
  explicit freshness limits. Journal generation key obligations through partials, rotation, live readers and quarantine.
- Preserve nested execution causes through session/coordinator lifecycle and transport aliases. Add native storage
  measurement tooling; physical-device, independent crypto and release qualification remain separate gates.

- Preserve observed budget and lease causes through nested cooperative deadlines and registered library-owned token
  aliases. Keep existing cancellation outcomes and diagnostic precedence; remove token associations when scopes close.

- Observe admitted session lease loss between runs and while paused; wake ordinary idle waits without bypassing pause
  or server `Retry-After`. Join and detach the observer before normal lease disposal, and report one ownership stop signal
  independently of acquisition/run observations.
- Join superseded account hint listeners before starting replacements; ignore late hints and stale unsupported-stream
  responses. Reject late/lost lease acquisition before status reads or replication, and classify acquisition and hint
  lifetime stop signals without treating normal lease disposal or unsupported hints as cancellation.
- Include account resolution, replica opening, local observations and synchronous progress callbacks in session goal-wait
  budgets. Isolate wait cancellation/progress failures from shared replication, join observations before closing the replica,
  and expose `SyncGoalResult.HasObservation` so an unobserved zero pending count cannot imply success.
- Stop paused sessions polling storage in a tight loop. Add `TimeProvider` budgets to one-shot sync, preserve unrelated
  cancellation errors, reject late completion, and distinguish session-run cancellation from execution lease loss in diagnostics.
- Add opt-in dependent audience publication to the Tasks sample's API, sync and intent writers, with explicit transaction
  ownership and lock ordering; cover rollback, replay, deletes and competing authenticated API/sync requests on SQL Server.
- Include workflow lease acquisition in its execution budget; prevent late transfer callbacks acknowledging completion.
  Add bounded-invocation stop diagnostics and byte/time-limited blob verification with bounded reads and explicit deferral.
- Add opt-in account maintenance with exclusive ownership, durable page checkpoints, urgent-work deferral, dry runs and
  item/byte/time allowances. Restore durable package/transfer pins and conservatively reconcile abandoned built-in owners.
- Join physical Web Lock release before browser account/cache disposal completes, including concurrent disposal calls.
- Add bounded quarantine observations and cached repair/quarantine gauges; count committed repair transitions without
  identity tags. Advance native retired-file cleanup past live readers with resumable cursors, and keep cache metadata
  available during v1 rotation.
- Add opt-in experimental chunked AES-GCM storage up to 1 GiB, authenticated ranges, resumable rotation, explicit
  v1 migration and quarantine-preserving recovery. Independent crypto review and device/performance gates remain open.
- Add account resource composition, protected orchestration payloads with atomic CAS/fences, explicit payload policies,
  and a permanent provider downgrade barrier before new-format writes.
- Add owned read-model generations, retention limits, paged cache reconciliation and application-owner maintenance;
  account for retired native replacement files until physical readers release them.
- Add headless bootstrap and complete Android/Apple reference-host source projects, receiver reconciliation contracts,
  and a versioned external-file blob adapter. Mobile builds/devices and external deployment qualification remain open;
  the named real loopback object-store implementation is covered by the qualification recorded above.
- Automatically repair missing, truncated and same-length damaged package content on opted-in managed
  plaintext caches, including an older active revision while a newer candidate is incomplete. Durable repair
  journals preserve staging and recover interrupted promotion; unreadable encrypted content remains protected.
- Preserve browser async readers across replacement and apply one cooperative budget to package preparation
  and upload prerequisites, including queueing and transfer ownership waits.
- Journal terminal transfer pin release and expose bounded recovery with `ReconcileReleasedAsync`.
- Add structured verification and optional replacement capability without changing `IBlobCache` or the wire protocol.
  See [ADR-026](docs/architecture/adr-026-verified-content-repair.md) for limits and compatibility.

## 0.4.0 (published; observed 2026-10-17)

All six downloaded archives declare source commit `e4286fe2141d0d1e836cfc88dbfa5dfd82154d37`.
This corrects the former unreleased heading; it does not assert a historical tag or clean-build provenance.
Archive hashes and new regression evidence are in the [execution record](docs/review/gap-closure-2026-10-17.md).

- Fixed direct document reads/listing to enforce reader membership in all three authorities before page limits.
- Added optional AES-GCM encrypted blob caches for native and browser storage, with bounded authenticated ranges,
  partial recovery, explicit migration and atomic key rotation; plaintext content identities remain unchanged.
- Added credential-isolated download tickets with one expiry refresh, strict range validation and an explicit proxy path.
- Added candidate package preparation status and a shared browser/WPF content-library sample for interrupted updates.
- Tasks now persists immutable completion requests, displays transport/execution outcomes separately, scopes stores by
  server/tenant/user, and uses pinned managed attachments plus durable ReadyToPush upload prerequisites.
- Added EF Core transaction retry and related-ACL projection recipes, optional WorkManager/BGProcessingTask sources,
  published 0.2.0 IndexedDB fixtures, package-only regressions and bounded desktop performance probes.
  Native OS delivery and release qualification remain separate gates; see the support matrix.
- Added an optional iOS background-download callback recipe with durable account receipts, verified pinned import
  after relaunch, and explicit plaintext OS spool boundaries; actual device delivery remains unqualified.
- Added actual published-0.2.0/current HTTP client/server process fixtures to CI and release candidate checks.

- Added immutable intent request/payload registration and separate persistence, transport and business-outcome
  contracts, with generated JSON registrations in the Tasks sample and restart coverage for two request kinds.
- Added an optional application-transaction external-effect outbox recipe with stable receiver keys, fenced result
  acknowledgement, bounded retries and poison isolation; receiver/worker integration remains application-owned.
- Managed content reimport detects same-size corruption and promotes verified replacements while retaining pins;
  repair accounts for both old and staged bytes and preserves existing native reader handles.

- Added account lifecycle operations, renewable execution leases, and versioned orchestration metadata in SQLite
  and IndexedDB; explicit offline opens defer transport construction and preserve pending work.
- Added durable transfer jobs, managed cache pins/budgets, immutable package staging/activation and revision-bound
  readers. Browser physical eviction uses Web Locks; native eviction uses exclusive file locks.
- Added recoverable workflow and portable background-dispatch contracts. Native OS scheduler adapters remain unqualified.
- Added full-reconciliation local read-model generations, atomic progress and immutable readers; hosts must capture a
  stable source snapshot. Retired/staging generations are currently retained until account deletion.
- Added explicit credential-required/migration-pending account outcomes and a bounded, aggregate-only diagnostic export.
- Added optional shared account-resource lifetimes and exclusive deletion across processes/tabs, with pending work
  rechecked under exclusive ownership; local key access contracts and a browser offline recovery-key example.
- Added cached package-verification observations for UI polling and opt-in bounded backlog/storage/background metrics.
- Added query materialization/return counters and a published-0.2.0 SQLite migration fixture.
- Added SQL Server publication lock ordering and tested Tasks sample command-ledger/projection-outbox recipes.
- Blob uploads refresh a refused presigned URL once; empty downloads are verified locally.
- Blob progress distinguishes partial offsets from verified completion; mismatched announced lengths retain existing
  content and fail explicitly. New reader/snapshot pins recover after process/tab termination using physical lifetime locks.

These orchestration APIs remain under qualification; see [implementation progress](docs/Improvement-Plan.md).
Blob encryption, automatic read-model reclamation, online/membership-aware server rebuilds, native scheduling adapters and full release gates are
not claimed complete.

- SQL Server packages now require `System.Security.Cryptography.Pkcs` >= 9.0.21, remediating
  GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7 in the SQL Client dependency chain.
- Sample WebAssembly servers use 10.0.13 for GHSA-g27p-qf6j-c96c and GHSA-qjpx-jqm5-h2c4;
  the browser test project's separate SQL Client reference also receives the patched Pkcs minimum.
- Release candidates require a clean version tag, matching changelog and package metadata, and emit
  checksums/toolchain/Source Link build maps. CI and nightly artifacts also carry provenance.
- Dependency auditing covers transitive packages and fails on findings or audit failures; the package-only
  web consumer exercises central package management. See [release validation](docs/operations/releases.md).

## 0.2.0 source baseline (published artifact provenance unresolved)

Six 0.2.0 packages are published on NuGet. They identify an earlier source commit, so this source
change list is not proof of exact binary contents ([inventory](docs/review/release-baseline-2026-10-16.md)).
Breaking: the package set, two signatures and one status behaviour (see Changed).

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
