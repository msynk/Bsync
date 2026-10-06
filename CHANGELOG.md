# Changelog

User-visible changes per release. Pre-1.0: a minor release may break, a patch release never does
([compatibility policy](docs/compatibility.md#policy)); every breaking change also has a migration note in
[docs/compatibility.md](docs/compatibility.md). Release status: [README](README.md#status).

## Unreleased (0.3.0)

### Added

- Read membership in the feed (ADR-015): with `Readers` and `PrincipalKey`, the in-memory and SQL Server authorities
  serve each caller only what it may read, in full pages (500 of 100,000 documents in five pages in 0.26 s on SQL
  Server 2025 LocalDB), and a document leaving a caller's view arrives as a removal instead of a collection reset
  (protocol feature `removals`). Clean copies leave the device; local drafts are hidden and kept.
- SQL Server schema 2, upgraded in place from schema 1.
- Pull-only collections (`SyncOptions.Mode = SyncMode.PullOnly`): one copy per document and no write bookkeeping;
  local writes throw `SyncReadOnlyException`.
- `SyncCoordinator` for many collections: concurrency limit, priorities, parents synced before children
  (`DependsOn`), a shared backoff, one aggregate status that names failing collections, a host hint bridge, and one
  multiplexed hint stream per account instead of one per collection (feature `hints-multiplex`).
- Awaitable sync goals: `SyncAsync(SyncGoal.Complete | Background | Accepted(id, revision), budget)` on a collection or
  the coordinator, with progress; `SyncEngine.SyncForAsync(timeBudget)` for time-limited background tasks.
- Honest status: `SyncStatus.Conflicts`, `Rejected`, `Blocked`, `LastPulled`, `LastPushed`, `Staleness`, `IsStale`;
  `ISyncCollection.GetIssuesAsync` pages issues with a total.
- Local tombstone compaction: pull responses carry the server's retention horizon (feature `retention`), and replicas
  drop their clean tombstones below it. In a 30-day soak with 7-day server retention a replica kept at most 160.
- A retention service: `AddSyncRetention` runs `SyncRetention` in an ASP.NET Core host and purges tombstones and
  receipts older than `MaxOfflineHorizon` (default 45 days) in every in-memory, SQL Server or PostgreSQL authority it
  is given. A receipt horizon shorter than the offline horizon fails the host at startup.
- `SyncOptions.KeptConflictView = KeptConflictView.Local` keeps showing the user's own edit while a conflict waits.
- Operations: the `bsync.issues` gauge (conflicts, rejections, blocked group members per collection); an optional
  replica acknowledgement audit (`SyncEndpointOptions.ReplicaAudit`, `SqlServerReplicaAudit`) recording which
  checkpoint each replica reached and when; a SQL Server restore drill, retention service and capacity guidance in
  the runbook; an authority throughput benchmark with concurrent sessions (`docs/benchmarks.md`).
- Wipe on sign-out: `SyncSession.DeleteReplicaAsync(account)` and `SyncCoordinator.DeleteReplicasAsync(account)` close
  an account's replicas and delete them from the device (`SqliteStorePool.DeleteDatabaseAsync`; the browser recipe
  deletes the IndexedDB database). The browser recipe requests persistent storage and reports it in
  `SyncStatus.PersistentStorage`. Encryption at rest is proposed in ADR-016, not implemented.
- `SyncOptions.ReadyToPush` holds a pending document back until something it names has reached the server.
- The Tasks sample attaches files to tasks: content-addressed blob stores on the device and the server, resumable
  chunked uploads and range downloads verified by SHA-256, uploads before the task, reads limited to callers who can
  read a referencing task, pinning and eviction (`docs/patterns/attachments.md`). Browser clients, presigned URLs and an
  S3 adapter are not done yet.
- The Tasks sample distributes bundles: a server-published manifest that devices switch to in one step once every
  item is downloaded and verified, keeping the previous revision in use meanwhile (`docs/patterns/bundles.md`).
- The Tasks sample executes immutable intents (complete, rename) exactly once in the write handler's transaction,
  with execution state separate from sync state; documented as a pattern in `docs/patterns/intents.md`. No new
  package types.

### Changed

- A session never reports `Synced` while conflicts or rejections wait for a decision; it reports `AttentionRequired`.
- Subscribers are not notified on idle background cycles or for timestamp-only status updates.

- Pull responses carry the server's time (feature `server-time`); a replica whose clock is ahead corrects its clock,
  so its writes are no longer rejected with `clock-skew` after the first sync (ADR-017, part 1).
- SQLite schema 4: a record's base copy is not stored when it equals the current state. A 10,000-document replica of
  ~1 KiB documents shrinks from 41.8 MB to 14.5 MB. Schema 3 databases upgrade on open.
- Fewer document copies per sync: pushing one document allocates 30.7 KB instead of 66.9 KB with the in-memory store
  and 62.6 KB instead of 80.6 KB with SQLite; the in-memory server allocates 8.1 KB instead of 16.4 KB per accepted
  write.

## 0.2.0 (committed as 3009955; not yet on NuGet)

Breaking: two signatures (see Changed). Builds on 0.1.1.

### Added

- `Bsync.Server.SqlServer`: a durable authority on the application's own SQL Server database (ADR-014). Protocol
  tables live in a `bsync` schema next to the application's tables; versions are allocated under a per-feed lock
  (`UPDLOCK, HOLDLOCK`), so readers never skip a later-committing lower version. Writes enlist in a caller's
  `DbTransaction` (for example EF Core's), and an `ISyncWriteHandler<T>` runs domain logic in the same transaction:
  accept with a server-computed canonical document, conflict, reject with a stable code, or retry later. Includes
  retention, epochs with a version floor that also covers feeds created later, dependency groups, and polled
  cross-process commit hints (`CommitPollInterval`) plus `NotifyCommitted` for a host's own bus. Verified on SQL
  Server 2025 LocalDB.
- `ISyncPublisher<T>` for server-originated writes (API endpoints, jobs, imports, projection rebuilds):
  `UpsertAsync`, `DeleteAsync`, `ReplaceScopeAsync` (tombstones documents no longer listed) and `PublishAsync`
  (fan-out of read-only projections). Unchanged content keeps its version, so rebuilding a projection adds no feed
  entries. Implemented by the in-memory, SQL Server and PostgreSQL authorities and `ScopedAuthority`; database
  authorities enlist in a caller's transaction.
- `MapSyncCollections(options, group => group.Add<A>("a").Add<B>("b"))` maps several collections with one set of
  options and one route group for authorization.
- Sample `Bsync.Samples.Tasks.*`: EF Core on SQL Server as the system of record, a write handler that computes a
  field and rejects with a stable code, a publisher used by an API endpoint, bearer tokens, a console client on
  SQLite and a WebAssembly client on IndexedDB, with an automated end-to-end test.
- Protocol: pull responses advertise the server's limits (feature `limits`); replicas clamp their push and pull
  batch sizes to them.
- `CredentialRenewal` (`Renewed`, `Offline`, `SignInRequired`) and `CredentialRenewals.Coalesce`, so an unreachable
  identity provider keeps the queue and retries, and only a real sign-in requirement stops uploads.
- `InMemorySyncServerOptions<T>.WriteHandler` and `PushErrorCodes.DependencyMissing`: a server can answer
  `retry-later` for a write whose dependency has not arrived yet; the replica resends the same operation with backoff.
- `Bsync.Testing`: five publisher cases (capability `AuthorityCapabilities.Publisher`, `AuthorityUnderTest.Publisher`);
  the suite now has 26 cases.

### Changed

- **Breaking:** the `Validator` option of `InMemorySyncServerOptions<T>` and `PostgreSqlSyncAuthorityOptions<T>`
  (and `AuthorityConformanceOptions.Validator` in `Bsync.Testing`) receives the caller (`SyncCallContext`) as its
  first argument. Migration: `(op, current) => ...` becomes `(caller, op, current) => ...`.
- **Breaking:** `SyncSessionOptions<T>.RenewCredentials` returns `Task<CredentialRenewal>` instead of `Task<bool>`.
- A push refused as too large is split in halves; a document too large on its own is parked locally with rejection
  `payload-too-large` and the rest of the queue continues (before, one oversized document blocked the collection).

### Fixed

- The session no longer resyncs in a tight loop while the server answers `retry-later`; it backs off (a test measured
  19,903 push requests in one second before the fix).
- Test reliability: two races in the T24/T30 concurrency drills (the reader could stop on a page fetched before the
  last commits), in the SQL Server and PostgreSQL test projects.

## 0.1.1 (committed as 93dee98; not yet on NuGet)

### Added

- `Bsync.Testing`: the authority conformance suite is public. `AuthorityConformance.Cases` (21 cases: one outcome
  per operation, version and conflict rules, receipt replay, operation id reuse, malformed operations, origin
  timestamps and the clock-skew bound, validation, feed coverage and paging, resume, foreign checkpoints, filtered
  reads, scope fingerprints, scope isolation, dependency groups, tombstone and receipt retention, new epochs), run
  through an `IAuthorityConformanceDriver`. `InMemoryAuthorityDriver` is the reference driver; `HttpAuthorityDriver`
  runs the same cases through the HTTP binding. The in-memory, JSON-wire, HTTP and PostgreSQL suites in this
  repository all run these cases.
- CI builds and tests projects that reference only the packed packages (`src/PackageConsumers`): the public
  conformance suites from the `Bsync.Testing` package, and an ASP.NET Core host with the browser store's static
  assets and a SQLite replica that syncs over HTTP.
- A nightly workflow packs `-preview` packages as build artifacts (never published).
- `CHANGELOG.md`, the 0.x compatibility policy and the release process (`docs/compatibility.md`).

### Fixed

- Documentation now matches the code and NuGet: six packages at `0.1.0` are published; PostgreSQL is a durable
  authority; protocol headers are implemented; document upgrade on read (`DocumentUpgrade`) is documented in the
  runbook; the live stream is consumed by `SyncSession` as hints; CI status and test counts come from real runs.
  The README has a single [Status](README.md#status) section the other documents link to.
- Test reliability: `HttpBindingTests.TimeoutAfterCommitIsRetrySafe` retried with the same 300 ms timeout it
  used to provoke the first timeout, so on a loaded machine the retry could time out too (1 failure in 15 local
  runs; a likely cause of the intermittent Windows CI failures). The retry now uses a normal timeout.

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

