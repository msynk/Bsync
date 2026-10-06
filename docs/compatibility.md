# Compatibility policy and migration notes

## Policy

- Bsync is pre-1.0. `0.1.0` is published on NuGet; current release status is in the
  [README](../README.md#status) and user-visible changes per version in [CHANGELOG.md](../CHANGELOG.md).
- **0.x releases.** A *minor* release (`0.x` to `0.(x+1)`) may break the public API, behaviour, the store schemas or
  optional wire features. Every break is listed below with a migration path. A *patch* release (`0.x.y` to
  `0.x.(y+1)`) never breaks: it contains fixes, additive API and documentation only, never a store schema change,
  and never a wire change an older peer of the same minor version cannot ignore.
- **Public API baselines** (`src/Tests/api`, checked by `PublicApiTests`) are updated only deliberately and reviewed,
  with an entry below when the change is not purely additive.
- From 1.0: semantic versioning; public API compatibility checked in CI; wire protocol, store schema and
  domain schema versioned independently (ADR-011); a documented client/server compatibility window.
- Behavioural changes count as breaking even when signatures do not change.

## Release process

1. Every user-visible change adds a line to `CHANGELOG.md` under *Unreleased* in the same change; breaking ones also
   get an entry in this file.
2. A release sets `VersionPrefix` in `src/Directory.Build.props`, renames *Unreleased* to the version and date, and
   packs from a clean tree whose CI run is green, including the package-consumer job.
3. Publishing to NuGet is a separate, explicitly authorized step (ADR-012); no workflow in this repository publishes.
4. Previews: `.github/workflows/nightly.yml` packs `X.Y.Z-preview.YYYYMMDD.N` every night as a build artifact only.

## Unreleased

Changes committed after 0.4.0 (76944e9).

| Change | Why | Migration |
|---|---|---|
| Read membership for PostgreSQL: `Readers` and `PrincipalKey` on `PostgreSqlSyncAuthorityOptions<T>`, as on the in-memory and SQL Server authorities. | ADR-015 for every included authority. | Additive. |
| Declared secondary indexes (ADR-018): `SyncIndex<T>`, `SyncIndex<T, TValue>`, `SyncIndexQuery<T>`, `SyncIndexCursor`, `SyncIndexKey`, `LocalStoreIndexing`; `SyncQuery<T>.Index` and `Skip`; `ISyncCollection<T>.CountAsync` (default implementation); `ILocalStore<T>.QueryIndexAsync` and `CountIndexAsync` (default implementations evaluate in memory); `SyncEngine<T>.QueryIndexAsync`, `CountIndexAsync`; `InMemoryLocalStore<T>(cloner, indexes)`; `SqliteLocalStore<T>.OpenAsync(options, typeInfo, indexes)`; `IndexedDbLocalStore<T>.OpenAsync(js, options, typeInfo, indexes)`; `AddBrowserSyncCollection(..., indexes)` (new optional parameter). `Bsync.Testing`: `LocalStoreIndexConformance` (6 cases). | Task E2: sorted, paged views of large collections. | Additive, except `AddBrowserSyncCollection` gains a parameter (recompile). Custom stores pass the index cases through the default implementations. |
| **SQLite schema 5** (`SqliteLocalStore<T>.SchemaVersion` = 5): table `bs_index`. A schema 4 database upgrades in place on open, keeping records and pending work; index keys are built when a store declaring indexes opens. Older versions of the store refuse schema 5. | ADR-018. | Upgrade every process that opens the same database together. |
| **IndexedDB schema 4**: index `ix` on record field `ixKeys`. Opening upgrades schema 3 databases; tabs still running an older application are closed as `outdated`. | ADR-018. | Reload old tabs. |
| **Behaviour:** writes rejected with `clock-skew` are re-stamped from the server's time and sent again in the same push (ADR-017 part 2); `SyncOptions<T>.RestampSkewedWrites` (default `true`) turns it off. `ILocalStore<T>.ResetClockHighWaterAsync` (default throws `NotSupportedException`; implemented by the included stores). `Bsync.Testing`: one store conformance case for it. | Writes made offline with a fast clock no longer stay parked. | Apps that read `UpdatedAt` of a not-yet-uploaded document and stored it elsewhere: turn the option off, or reread after upload. |
| New package `Bsync.Storage.Sqlite.Encrypted` (SQLCipher): the SQLite store's API plus `SqliteLocalStoreOptions.EncryptionKey`, `SqliteEncryption`, and key overloads of `SqliteStoreRecovery.CheckAsync`/`RebuildAsync`. Both SQLite packages: `SqliteStoreUnreadableException` (a file that cannot be read with the given key, or no key). `IndexedDbStoreOptions.EncryptionKey`. | Encryption at rest (ADR-016 part 2). | Additive. Use one SQLite package per application, never both. |
| **Protocol (additive):** optional `arguments` on push outcomes. `PushOutcome<T>.Arguments`, `SyncWriteDecision<T>.Reject(code, arguments, message)` and `.Arguments`, `SyncRejection.Arguments` (with value equality), `SyncItemStatus.Arguments`, `SyncIssue.Arguments`. Stored by the in-memory, SQLite (column `rejection_arguments`, part of schema 5) and IndexedDB stores, and in SQL Server receipts (column `arguments`, schema 3). | Task C5: a rejection a UI can explain ("at most 200 characters"). | Additive. |
| **Protocol/binding (additive):** `POST {prefix}/pull` on a `MapSyncCollections` group pulls several collections in one request; their pull responses advertise feature `pull-batch` (`SyncFeatures.PullBatch`). Client: `HttpPullBatch` and `HttpSyncTransportOptions.PullBatch` (share one per account). | Task C3: one request instead of one per collection when many collections sync at once. | Additive; against servers without the feature, transports pull per collection. |
| **PostgreSQL schema 2** (table `bs_document_access`, for read membership). A schema 1 database (0.2.0) upgrades in place on first open, under the schema lock; 0.2.0 then refuses the upgraded database. | ADR-015. | Upgrade every server process of an application together. |
| New optional package `Bsync.Maui`: `MauiSync.UseMauiLifecycle`, `AttachAsync`, `RunInBackgroundAsync`, `SecureReplicaKeys`. | Task G1. | Additive. |
| `Bsync.Blazor`: `BrowserBlobStore` (namespace `Bsync.Blazor.Blobs`) and the module `_content/Bsync.Blazor/bsync-blobs.js`. | The browser's attachment store (task F1). | Additive. |
| **SQL Server schema 3**: column `arguments` on `receipts` (task C5). Schema 1 and 2 databases (0.2.0, 0.3.0, 0.4.0) upgrade in place on first open, under the schema lock; older versions then refuse the database. | Rejection arguments replay from receipts. | Upgrade every server process of an application together. |

## 0.4.0 (committed as 76944e9; not yet on NuGet)

| Change | Why | Migration |
|---|---|---|
| `SyncOptions<T>.ReadyToPush`: holds a pending document back from a push (it stays pending and counts in `SyncResult.Deferred`; a held member holds its dependency group). | Attachments must reach the server before the document that names them (task F1, `docs/patterns/attachments.md`). | Additive; without it every pending document is ready, as before. |
| `SyncSessionOptions<T>.DeleteReplica`, `SyncSession<T>.DeleteReplicaAsync`, `SyncCoordinator.DeleteReplicasAsync`, `SqliteStorePool.DeleteDatabaseAsync`; `LocalReplica<T>.PersistentStorage` and `SyncStatus.PersistentStorage`. The browser recipe sets `DeleteReplica` and requests persistent storage when it opens a replica (waiting at most two seconds). | Wipe on sign-out and storage persistence (task G3, ADR-016 part 1). | Additive. Browser apps may see Firefox ask the user for persistent storage on first use. |
| **Protocol (additive):** optional `replica` member in pull requests; replicas send their clock node id. `PullRequest.Replica`. Server: `ISyncReplicaAudit`, `ReplicaAcknowledgement`, `InMemorySyncReplicaAudit`, `SqlServerReplicaAudit`; `SyncEndpointOptions.ReplicaAudit` and `TimeProvider`. | Proving which content a device held (task H). | Additive; servers that do not audit ignore the member. |
| Client meter: observable gauge `bsync.issues` (`conflict`, `rejected`, `blocked`). | Task H. | Additive. |

## 0.3.0 (committed as bba45f9; not yet on NuGet)

| Change | Why | Migration |
|---|---|---|
| `SyncCoordinator`, `SyncCoordinatorOptions`, `SyncCoordination`, `SyncCoordinatorStatus`; `SyncSessionOptions<T>.Coordination`. A coordinated session asks the coordinator before each run (concurrency limit, priority, parents before children with `DependsOn`, shared backoff), and the coordinator reports one aggregate status and accepts host hints (`Hint`). | Many collections (task C3, F13). | Additive. |
| **Protocol/binding (additive):** `GET {prefix}/hints?collections=a,b` on a `MapSyncCollections` group, one Server-Sent Events stream whose events name the changed collection; pull responses of the group advertise feature `hints-multiplex`. `HttpSyncHints.Multiplexed` opens it for a coordinator; against a server without it, sessions keep one stream each. | One connection instead of one per collection (browsers limit connections per host). | Additive. |
| `SyncGoal` (`Background`, `Complete`, `Accepted`, `And`), `SyncGoalResult`, `SyncProgress`, `SyncDocumentRevision`; `ISyncCollection<T>.SyncAsync(goal, budget, progress)`, `SyncSession<T>.SyncAsync(account, ...)`, `SyncCoordinator.SyncAsync(...)`; `SyncEngine<T>.SyncForAsync(timeBudget, maxRuns)`. | Awaitable "sync now" without a second loop (task C4). | Additive. Custom `ISyncCollection` implementations get a default that throws `NotSupportedException`. |
| `SyncMode` (`TwoWay`, `PullOnly`), `SyncOptions<T>.Mode`, `SyncReadOnlyException`, `SyncSession<T>.PullOnly`. A pull-only replica keeps only current states and refuses local writes; `Capabilities.DurableOfflineWrites` is false for it. | Read-only projections take less space and state their intent (task C2, F11). | Additive. |
| **Behaviour:** a session reports `AttentionRequired` (never `Synced`) while kept conflicts or parked rejections exist, also on later runs and after a restart. Before, a run with a kept conflict, or a later run with a parked rejection, reported `Synced`. | Honest status (task C5). | UIs that waited for `Synced` while conflicts were kept should look at `Conflicts`/`Rejected` instead. |
| `SyncStatus`: `Conflicts`, `Rejected`, `Blocked`, `LastPulled`, `LastPushed`, `Staleness(now)`, `IsStale(threshold, now)`. `ISyncCollection<T>.GetIssuesAsync(offset, limit)` (`SyncIssue`, `SyncIssueKind`, `SyncIssuePage`); `ILocalStore<T>.CountIssuesAsync` with a default implementation (overridden by the SQLite and in-memory stores); `SyncEngine<T>.CountIssuesAsync`. | Task C5. | Additive. Custom `ISyncCollection` implementations get a default that throws `NotSupportedException`. |
| **Behaviour:** `Changed` (and `ISyncCollection.Subscribe`) no longer fires for status updates that only move timestamps, nor for the `Syncing` step of a background refresh of an idle, synced replica. Followers still notify on every refresh tick. | Subscribers were notified on every idle cycle (F14). | None. |
| **Protocol (additive):** optional `features` in pull requests (what the replica understands), optional `removals` and feature `removals` in pull responses (ADR-015). `PullRequest.Features`, `PullResult.Removals`, `SyncFeatures.Removals`, `SyncResult.Removed`. Replicas always ask for removals and apply them: clean copies are removed, copies with local changes or conflicts are hidden. | Taking a document away from one caller no longer resets the collection (F3). | Older replicas never receive removals; when one would be needed they get `reset-required` (`scope-changed`), as before. |
| Read membership: `Readers` and `PrincipalKey` on `InMemorySyncServerOptions<T>` and `SqlServerSyncAuthorityOptions<T>`. With them a caller pulls only its own access rows (full pages, cost proportional to what it can see), writes to an existing document need read access, and checkpoints are bound to the principal. PostgreSQL: not implemented. | F2 and F3 (task C1). | Additive; without the options nothing changes. |
| **SQL Server schema 2** (table `document_access`). A schema 1 database (0.2.0) upgrades in place on first open, under the schema lock, keeping feeds, documents and receipts; 0.2.0 then refuses the upgraded database. | ADR-015. | Upgrade every server process of an application together. |
| `Bsync.Testing`: `AuthorityCapabilities.Membership` (included in `All`), `AuthorityConformanceOptions.Readers` and `PrincipalKey`, four membership cases. **Drivers that declare `All` must now support membership** or declare `All & ~Membership`. | Conformance for ADR-015. | Pass the two options through, or exclude the capability. |
| **Protocol (additive):** optional `serverTime` and feature `server-time` in pull responses; `PullResult.ServerTime`, `SyncFeatures.ServerTime`, `HybridLogicalClock.PhysicalOffset`. A replica whose clock is ahead corrects it after its first pull (ADR-017). | Task D2: writes from a fast clock were parked with `clock-skew`. | Older replicas ignore the member. Writes stamped before the first pull with a fast clock still get `clock-skew` (ADR-017, part 2 proposed). |
| **Protocol (additive):** optional `retentionHorizon` and feature `retention` in pull responses; `PullResult.RetentionHorizon`, `SyncFeatures.Retention`, `SyncResult.Compacted`, `ILocalStore<T>.PurgeTombstonesAsync` with a default implementation (overridden by the SQLite and in-memory stores). A replica drops its clean tombstones at or below the server's horizon after committing a page. | Task D4 (F10): local tombstones grew forever. | Additive. Tombstones with local changes or a kept conflict are never dropped. Older replicas ignore the member. |
| `ISyncRetentionTarget` (implemented by `InMemorySyncServer`, `SqlServerSyncAuthority` and `PostgreSqlSyncAuthority`; new `GetFeedHeadsAsync` on the two durable ones), `SyncRetention`, `SyncRetentionOptions` (`MaxOfflineHorizon`, default 45 days; `ReceiptHorizon`; `Interval`). `Bsync.Server.AspNetCore`: `SyncRetentionService` and `AddSyncRetention`. Options with a receipt horizon shorter than the offline horizon fail the host at startup. | Task D5 (F18): retention had to be scheduled by each application. | Additive. Hosts that already purge on their own schedule can keep doing so. |
| `KeptConflictView` (`Server`, `Local`) and `SyncOptions<T>.KeptConflictView`. With `Local`, a deferred conflict keeps showing the local edit (still not uploaded); discarding the conflict shows the server state. | Task D7 (F20): without a conflict UI, a user's edit seemed to disappear. | Additive; the default (`Server`) keeps the 0.2.0 behaviour. |
| **SQLite schema 4** (`SqliteLocalStore<T>.SchemaVersion` = 4): column `base_same` on `bs_records`. The base copy is not stored when it equals the current state, which is the common case for clean records: a 10,000-document replica of ~1 KiB documents takes 14.5 MB instead of 41.8 MB. A schema 3 database upgrades in place on open; older versions of the store refuse schema 4. | Task D8 (F11). | Upgrade every process that opens the same database together. |
| **Behaviour:** fewer copies per synced document (D9). The in-memory store reuses the copies it owns, the engine shares one copy between base and current after an acceptance, the in-memory server keeps one copy for an accepted receipt, and the SQLite store rebuilds its returned record from the JSON it just wrote instead of reading it back. Isolation is unchanged: callers still never share an instance with the store. | Allocation per document (F12). | None. |

## 0.2.0 (committed as 3009955; not yet on NuGet)

Builds on 0.1.1 and breaks two signatures (below).

| Change | Why | Migration |
|---|---|---|
| **Breaking:** `InMemorySyncServerOptions<T>.Validator` and `PostgreSqlSyncAuthorityOptions<T>.Validator` are now `Func<SyncCallContext, PushOperation<T>, T?, string?>` (the caller comes first). | Validation often depends on who is writing (improvement plan F5). | Add a first parameter: `(op, current) => ...` becomes `(caller, op, current) => ...`. |
| **Breaking:** `SyncSessionOptions<T>.RenewCredentials` returns `Task<CredentialRenewal>` (`Renewed`, `Offline`, `SignInRequired`) instead of `Task<bool>`. An exception from it counts as `Offline`. `CredentialRenewals.Coalesce` shares one renewal between an account's sessions. | A failed connection to the identity provider must not look like "sign in again" (task D6). | `true` becomes `CredentialRenewal.Renewed`; `false` becomes `SignInRequired` when the user must act, or `Offline` when the provider was unreachable. |
| **Behaviour:** a push the server refuses as too large (`payload-too-large`) is split in halves down to one document or dependency group; one that is too large on its own is parked locally with rejection `payload-too-large` (`PushErrorCodes.PayloadTooLarge`). Before, the whole batch failed and blocked the queue. | Task D1 (I19). | Parked records are listed with the other rejections; make the document smaller and retry it. |
| **Behaviour:** when the server defers operations (`retry-later`), the session retries with exponential backoff (`MinBackoff`..`MaxBackoff`, at most `Interval`). Before, it resynced at once in a loop (about 20,000 requests per second in a test). | Task D3. | None. |
| `InMemorySyncServerOptions<T>.WriteHandler`; `PushErrorCodes.DependencyMissing` (`dependency-missing`, sent with `retry-later`). Retry-later outcomes store no receipt in the in-memory and SQL Server authorities, so the same operation is decided later. | Transient versus terminal outcomes (task D3). | Additive. |
| New package `Bsync.Server.SqlServer` (`SqlServerSyncAuthority<T>`, `SqlServerSyncAuthorityOptions<T>`, `SqlServerSchemaException`; Microsoft.Data.SqlClient 7.1.1). Tables in a `bsync` schema of the application's database, created on first use. | ADR-014. | Additive. |
| Core: `ISyncWriteHandler<T>`, `SyncWriteContext<T>`, `SyncWriteDecision<T>`, `SyncWriteDecisionKind`. | Domain logic in the authority's transaction (ADR-014). Used by the SQL Server authority. | Additive. |
| Core: `ISyncPublisher<T>` and `SyncPublishResult`, implemented by `InMemorySyncServer`, `ScopedAuthority`, `PostgreSqlSyncAuthority` and `SqlServerSyncAuthority`. | Server-originated writes without fake operation ids (task B3). | Additive. Custom authorities may implement it. |
| **Protocol (additive):** optional `limits` member (`maxOperationsPerPush`, `maxPageSize`) and feature `limits` in pull responses; all included authorities send them. `SyncLimits`, `SyncFeatures.Limits`, `PullResult.Limits`. Replicas clamp their batch sizes to advertised limits. | Limits were specified as advertised but were not (F6). | Older replicas ignore the member. Older servers do not send it; replicas then use their configuration as before. |
| `Bsync.Testing`: `AuthorityCapabilities.Publisher` (and `All` now includes it), `AuthorityUnderTest.Publisher`, five publisher cases; **breaking for drivers written against 0.1.1:** `AuthorityConformanceOptions.Validator` takes the caller first. | Publisher conformance; same signature as the authorities. | Drivers pass the new delegate through unchanged. |
| `Bsync.Server.AspNetCore`: `MapSyncCollections` and `SyncCollectionGroupBuilder`. | Several collections under one policy and one set of options. | Additive. |
| Checkpoint scope encoding: unchanged. The improvement plan's finding F4 (PostgreSQL concatenates collection, scope and fingerprint without separators) was checked: the parts are joined with U+001F, which `SyncIds` excludes from collections and scopes, so the encoding is unambiguous and existing checkpoints stay valid. The SQL Server authority length-prefixes each part. | No reset needed. | None. |

## 0.1.1 (committed as 93dee98; not yet on NuGet)

| Change | Why | Migration |
|---|---|---|
| `Bsync.Testing` gains the public authority conformance suite: `AuthorityConformance` (21 cases), `AuthorityConformanceCase`, `AuthorityCapabilities`, `AuthorityConformanceOptions`, `IAuthorityConformanceDriver`, `AuthorityUnderTest`, `InMemoryAuthorityDriver`, `HttpAuthorityDriver`, `HttpConformanceServer`. | Out-of-repository authorities can prove conformance (I04–I07, I10, I12, I14, I18, I19). | Additive. |

## Before 0.1.0

The sections below describe changes made before the first published release; all of them shipped in `0.1.0`.

### Package consolidation

| Change | Why | Migration |
|---|---|---|
| **Breaking:** `Bsync.Client` and `Bsync.Transport.Http` merged into `Bsync`. The client types keep namespace `Bsync.Client`; `HttpSyncTransport<T>` and `HttpSyncTransportOptions` moved from `Bsync.Transport.Http` to `Bsync.Transport`. `Bsync` now depends on `Microsoft.Extensions.DependencyInjection.Abstractions` and `Logging.Abstractions` 10.0.12. | Fewer packages; neither package carried a dependency that had to stay out of any host (ADR-012). | Replace references to `Bsync.Client` and `Bsync.Transport.Http` with `Bsync`. Replace `using Bsync.Transport.Http;` with `using Bsync.Transport;`. |
| **Breaking:** `Bsync.Storage.IndexedDb` merged into `Bsync.Blazor`. Its types moved from namespace `Bsync.Storage.IndexedDb` to `Bsync.Blazor.IndexedDb`, and the JavaScript module is served from `_content/Bsync.Blazor/bsync-indexeddb.js`. | Both are Blazor-only; an Auto-mode app uses both. | Replace references to `Bsync.Storage.IndexedDb` with `Bsync.Blazor`. Replace `using Bsync.Storage.IndexedDb;` with `using Bsync.Blazor.IndexedDb;`. Service workers or CSP rules that list the old module path need the new one. |

### UI-independent client package

| Change | Why | Migration |
|---|---|---|
| **Breaking:** new package `Bsync.Client` (namespace `Bsync.Client`) with `ISyncCollection<T>`, `SyncQuery<T>`, `SyncCapabilities`, `SyncStatus`, `SyncState`, `SyncItemStatus`, `SyncItemState`, `SyncDocumentConflict<T>`, `SyncConfirmation`, `SyncWriteResult`, `SyncSession<T>`, `SyncSessionOptions<T>`, `LocalReplica<T>`, `LocalSyncCollection<T>` and `AddLocalSyncCollection` (now on `ClientServiceCollectionExtensions`). They moved out of `Bsync.Blazor`, which keeps `ServerSyncCollection<T>`, `ServerSyncClock` and `AddServerSyncCollection` and references `Bsync.Client`. `Bsync.Client` depends only on the core and `Microsoft.Extensions.DependencyInjection.Abstractions`/`Logging.Abstractions`. | Native UIs without Blazor (MAUI XAML, WPF, WinForms, Avalonia) and headless hosts can use the session loop without `Microsoft.AspNetCore.Components`. | Add `using Bsync.Client;` (and `@using Bsync.Client` in Razor). Local-replica clients (WebAssembly, Hybrid, native) reference `Bsync.Client` instead of `Bsync.Blazor`; server projects keep `Bsync.Blazor`. `Bsync.Storage.IndexedDb` now references `Bsync.Client` instead of `Bsync.Blazor`. |
| `SyncQuery<T>.Apply` evaluates a query in memory. | Shared by the collections in both packages; usable by custom `ISyncCollection` implementations. | Additive. |

### PostgreSQL, dependency groups, hybrid hosts

| Change | Why | Migration |
|---|---|---|
| New package `Bsync.Server.PostgreSql` (`PostgreSqlSyncAuthority<T>`, `PostgreSqlSyncAuthorityOptions<T>`, `PostgreSqlSchemaException`; Npgsql 10.0.3). One instance serves every scope; the tables are created on first use. | Durable authority (Phase 4). | Additive. The notes sample server uses it when `Bsync:PostgreSql` is configured. |
| **Protocol (additive):** optional `group`/`groupSize` on push operations, optional `features` on pull responses, outcome code `group-aborted` (with `retry-later`); replica-only codes `group-failed`, `groups-unsupported` (`PushErrorCodes`, `SyncFeatures`). | Dependency groups (protocol §4.1). | Older authorities never receive groups: replicas send them only to authorities that advertise `groups`. |
| The in-memory authority's operation fingerprint now includes `group` and `groupSize`. | A replay must match the whole request. | Only in-memory state existed; a receipt stored by an earlier build would answer `operation-id-reused`. |
| `SyncEngine.WriteGroupAsync`, `ISyncCollection.SaveAllAsync`; `SyncRecord.Group`, `SyncGroup`; `PendingOperation.Group`/`GroupSize`. | Dependency groups. | Custom `ISyncCollection` implementations add `SaveAllAsync`; custom stores persist the new fields. |
| SQLite store schema 3 (four group columns), IndexedDB schema 3 (fields only; the version bump closes older tabs). | Persist groups. | Forward only, in place, keeps pending work. |
| `ILocalStore.QueryPageAsync`; `SyncEngine.QueryPageAsync`. `LocalSyncCollection.QueryAsync` with the default order reads bounded pages instead of the whole collection. | Bounded queries. | Custom stores implement `QueryPageAsync` (conformance case added). |
| `ThreeWayMergeOptions` (set and counter members), new `ThreeWayMerge.Merge` overload, `ThreeWayMergeConflictHandler` takes options. | Semantic merges. | Additive. |
| `DocumentUpgrade.TryTake`. | Upgrading old document shapes on read (ADR-013). | Additive. |
| Samples: `Bsync.Samples.Hybrid.Wpf` (in the solution) and `Bsync.Samples.Hybrid.Maui` (Windows target; outside the solution because it needs the `maui-windows` workload). | Native hosts (ADR-007). | Samples only. |

### Phase 10: operations and packaging

| Change | Why | Migration |
|---|---|---|
| Traces and metrics: `Bsync.Diagnostics.SyncDiagnostics` (`SourceName` "Bsync", `NameTag`); `SyncOptions<T>.DiagnosticsName` (must not be empty; default the document type name). | Observability (docs/operations/observability.md). | Additive; nothing is emitted without a listener. |
| `SyncSessionOptions<T>.Logger`; `Bsync.Blazor` references `Microsoft.Extensions.Logging.Abstractions` 10.0.12 explicitly; the DI recipes pass the container's logger factory. | Session logs. | Additive. |
| **Behaviour:** an unexpected exception in the session loop (a store, serializer or application failure) now reports `AttentionRequired`, is logged, and is retried after `MaxBackoff` or on `RequestSync`. Before, it ended the loop silently and the status stayed `Syncing`. | Found while adding logging. | None; apps that watched for a stuck `Syncing` state can rely on `AttentionRequired`. |
| **Behaviour:** an unexpected exception from the authority in `MapSyncCollection` endpoints is answered with `503` and code `unavailable`, and logged. Before, it propagated to the host (usually a bare 500). `SyncEndpoints.MeterName` added. | Clients retry `unavailable`; details stay in server logs. | Authorities that relied on exception middleware to shape responses map their errors to `SyncTransportException` instead. |
| Package metadata for the libraries (`src/Directory.Build.props` and `.targets`; seven at the time, six after the consolidation above): version `0.1.0-preview` at the time, released as `0.1.0`, MIT, repository links, README, symbols (snupkg), deterministic builds. The demo, samples, tests and benchmarks are not packable. | Phase 10 packaging. Nothing is published. | None. |
| Public API baselines in `src/Tests/api/*.txt`, checked by `PublicApiTests`. | API review (ADR-011). | Update with `BSYNC_UPDATE_API=1` after review. |

### Phase 9: recovery

| Change | Why | Migration |
|---|---|---|
| `ILocalStore.GetRejectedAsync(limit)`. | List parked rejections (I19). | Custom stores implement it; run `LocalStoreConformance.Cases`. |
| `SyncEngine.GetRejectedAsync`, `RetryRejectedAsync`, `RevertAsync`, `ExportLocalChangesAsync`, `ImportLocalChangesAsync`. | Stuck-queue tooling and moving local work (ADR-013). | Additive. |
| `ISyncCollection<T>.RetryAsync`, `RevertAsync`. | Same, for components. | Custom `ISyncCollection` implementations add the methods. |
| `SqliteStoreRecovery.CheckAsync`, `RebuildAsync`, `SqliteRebuildReport`. | Damaged SQLite replicas. | Additive. |
| SQLite schema DDL moved to an internal `SqliteSchema` class; `SqliteLocalStore.SchemaVersion` is unchanged (2). | Shared by the store and the rebuild. | None. |

### Phase 8: conflicts, selective sync, retention

| Change | Why | Migration |
|---|---|---|
| **Default conflict policy is now `DeferConflictHandler`** (was `ClientWins`): the replica shows the server state and keeps the local change as an unresolved conflict. | ADR-006: the old default silently overwrote concurrent edits. | Behavioural. To keep the old behaviour pass `new ClientWinsConflictHandler<T>()`. Otherwise surface conflicts (`GetConflictsAsync`) or use `ThreeWayMergeConflictHandler<T>`. With the default, `SyncResult.Conflicts` counts kept conflicts and the run is complete without pushing them. |
| `ConflictOutcome.Defer`, `ConflictResolution.Defer()`, `SyncRecord.Conflict`, `SyncConflict<T>`; `SyncEngine.GetConflictsAsync`, `ResolveConflictAsync`, `DiscardConflictAsync`. | Durable unresolved conflicts (I11). | Switch statements over `ConflictOutcome` need a new case. |
| `ThreeWayMerge`, `ThreeWayMergeResult<T>`, `ThreeWayMergeConflictHandler<T>`. | Field-level merge. | Additive. |
| `ILocalStore`: `GetConflictsAsync(limit)` and `PurgeAsync(ids, generation)` added; `ReplicaCursor` gained `PurgeMissing`; stores persist `SyncRecord.Conflict` and never purge dirty or conflicted records. | Conflicts, scope removal. | Custom stores implement both methods, persist the new fields and run `LocalStoreConformance.Cases`. |
| SQLite store schema 2 (conflict columns and index), IndexedDB schema 2 (conflict index). Both upgrade in place on first open and keep pending work. | Persisted conflicts. | Forward only: after the upgrade an older application refuses the database (`SqliteStoreSchemaException`; IndexedDB reports `outdated`). Ship the upgrade to every tab/process of an app together. |
| `SyncResetRequiredException.Reason` and `ResetReasons` (`epoch`, `scope-changed`, `expired`); the HTTP problem for `reset-required` carries `reason`; `SyncResult.PurgedAfterReset`. | Revocation and retention must remove data from the device; a restore must not. | Clients that do not understand `reason` treat it as `epoch` (records hidden, not removed). |
| `PushErrorCodes.BaseExpired` (`base-expired`); on it the engine clears the record's base, so writing again recreates the document. | A long-offline edit must not resurrect a purged document. | Handle the rejection like other rejections (show it; the user writes again to restore). |
| `InMemorySyncServerOptions.ScopeFingerprint`; `InMemorySyncServer.PurgeTombstones`, `PurgeReceipts`, `PurgedThrough`; backups include the retention horizon. Checkpoints now have the form `{epoch}~{scope}:{position}`. | Selective sync and bounded retention. | Checkpoints in the previous `{epoch}:{position}` form are answered with `reset-required` (`epoch`), so replicas resnapshot once after the server update. |
| `ISyncCollection<T>`: `GetConflictsAsync`, `ResolveConflictAsync`, `DiscardConflictAsync`; `SyncDocumentConflict<T>`; `SyncItemState.Conflicted`. | Conflicts in the component API. | Custom `ISyncCollection` implementations add the methods. |
| `SqliteStorePool.Release(dataSource)`. | Close pooled connections of one database file without `SqliteConnection.ClearAllPools()`, which disrupts other databases in the process. | Additive. |
| `SyncEngine.DeleteAsync` no longer mutates the stored record's document instance inside the store transform. | Transforms must be pure (ADR-004). | None. |

### Session lifecycle and hints

| Change | Why | Migration |
|---|---|---|
| `ISyncCollection.GetItemStatusAsync`, `SyncItemStatus`, `SyncItemState`; `SyncState.Paused`. | Per-item confirmation (I16). | Custom `ISyncCollection` implementations add the method. |
| `SyncSessionOptions`: `LiveHints`, `AttachLifecycle`, `RenewCredentials`; `SyncSession`: `Pause`, `Resume`, `NotifyLocalWriteAsync`, `LiveHints`. | Phase 7. | Additive. |
| `LocalSyncCollection` updates `Status.Pending` immediately after a local write (it previously lagged until the next sync). | Found by a flaky browser test; the UI showed "0 unsynced" for queued work. | None. |
| `MapSyncCollection` maps `GET …/hints` (SSE) when the authority implements `ISyncCommitNotifier`; `HttpSyncTransport.StreamAsync` reads it. | Hints. | Service workers must not proxy `/sync/` requests (see protocol §8). |
| `AddBrowserSyncCollection` enables hints and attaches an `online`/visibility watcher (`BrowserLifecycleWatcher`). | Prompt sync in browsers. | Additive. |

### Blazor integration

| Change | Why | Migration |
|---|---|---|
| New package `Bsync.Blazor`: `ISyncCollection<T>`, `SyncQuery<T>`, `SyncWriteResult`, `SyncCapabilities`, `SyncStatus`, `SyncSession<T>`, `SyncSessionOptions<T>` (record), `LocalSyncCollection<T>`, `ServerSyncCollection<T>`, `AddServerSyncCollection`, `AddLocalSyncCollection`. | Phase 6. | Additive. |
| `Bsync.Storage.IndexedDb` now references `Bsync.Blazor` and adds `AddBrowserSyncCollection`. | Browser recipe. | Additive. |
| Core: `ISyncDocumentReader<T>`, `StoredDocument<T>`, `ISyncCommitNotifier`, `AuthorityCommit`; implemented by `InMemorySyncServer` and `ScopedAuthority`. | Server-connected reads and hints. | Custom authorities implement them to support server-connected hosts. |
| Samples: `Note` moved to `Bsync.Samples.Shared`; the notes PWA uses the shared `NotesPanel` and the browser recipe; new Blazor Web App sample. | One component in every render mode. | Samples only. |

### Browser storage, samples

| Change | Why | Migration |
|---|---|---|
| `ReplicaIdentity` moved from `Bsync.Storage.Sqlite` to `Bsync.Storage` (core). | Shared by SQLite and IndexedDB. | Code inside `Bsync.Storage.Sqlite` resolves it unchanged; others add `using Bsync.Storage;`. |
| `LocalStoreUnavailableException` added (core). | Provider-neutral storage failures. | Additive. |
| Store conformance cases moved to `Bsync.Testing` (`LocalStoreConformance`, `ConformanceDocument`, `Check`). | Run the same cases in browsers. | Custom providers run `LocalStoreConformance.Cases`. |
| New packages `Bsync.Storage.IndexedDb` and `Bsync.Testing`; samples `Bsync.Samples.Notes.Client/Server`. | Phase 5. | Additive. |

### Reset/resnapshot, SQLite store, change observation

| Change | Why | Migration |
|---|---|---|
| `ILocalStore.GetCheckpointAsync` replaced by `GetCursorAsync` returning `ReplicaCursor` (checkpoint, generation, resnapshot); `UpdateAsync` takes `ReplicaCursor?` instead of `Checkpoint?`; `GetStaleAsync` added; `QueryAsync` must hide records marked missing. | Reset flow (I14). | Custom stores persist the three cursor fields atomically with updates and implement `GetStaleAsync`; run `LocalStoreConformanceTests`. |
| `SyncRecord` gained `Generation` and `MissingAfterReset`. | Same. | Stores persist both. |
| On `SyncResetRequiredException` from a non-start checkpoint the engine now resets and resnapshots instead of failing. | Same. | Check `SyncResult.ResetPerformed` / `MissingAfterReset` if the app wants to tell the user. |
| `InMemorySyncServer`: `CreateBackup`, `HighestVersion`; options `RestoreFrom`, `VersionFloor`. | Restore simulation, version rule. | Additive. |
| `SyncEngine.Observe`, `SyncChange`, `SyncChangeKind` added. | Post-commit observation. | Additive. |
| New package project `Bsync.Storage.Sqlite` (depends on Microsoft.Data.Sqlite 10.0.12). | Durable native store. | Additive; not for WebAssembly. |
| `ISyncAuthority<T>`, `SyncCallContext`, `AuthorityLimits`, `ScopedAuthority<T>`; `InMemorySyncServer` implements `ISyncAuthority<T>` and gained `CanRead`/`CanWrite` options. | One authority for HTTP and in-process callers (I18), scopes (I07). | Additive. |
| `InProcessTransport` takes any `ISyncAuthority<T>` and an optional `SyncCallContext`. | Same. | Existing calls with an `InMemorySyncServer` still compile. |
| `InMemorySyncServer.Push` throws `SyncTransportException` (`payload-too-large`) instead of `ArgumentException` for too many operations; `Pull` throws `SyncProtocolException` for a limit below 1. | Maps to HTTP 413/400. | Catch the new types. |
| `SyncTransportException`, `SyncErrorCodes`, `SyncJsonTypes<T>` added. | Classified transport errors, shared JSON metadata. | Additive. |
| New package projects `Bsync.Server.AspNetCore` and `Bsync.Transport.Http`. | HTTP binding. | Additive. |

### Phase 2: wire encoding, conformance, AOT

| Change | Why | Migration |
|---|---|---|
| `HlcTimestamp` serializes to JSON as its canonical string (`"001790000000000:000000:node"`) instead of an object; malformed strings are rejected. | Wire spec §2.3; sortable, validated. | Data serialized by earlier builds with the object form no longer deserializes. No durable store existed, so no stored data is affected. |
| `Checkpoint` serializes as a string or `null`. | Wire spec §2.5. | None. |
| Protocol records carry explicit JSON names; versions are digit strings; `PushOutcomeKind` is a kebab-case string (`retry-later`); integers and unknown kinds are refused. | Wire spec §3. | Use the protocol types with a source-generated `JsonSerializerContext`. |
| `SyncOptions.Cloner` removed. The engine has a reflection constructor (annotated `[RequiresUnreferencedCode]`) and a new constructor taking `Func<T, T> cloner`. | ADR-011: no hidden reflection. | `new SyncEngine<T>(store, transport, clock, cloner, handler, options)`; for JSON use `DocumentCloner.Json(context.T)`. |
| `InMemoryLocalStore()` (reflection) and `InMemoryLocalStore(Func<T,T>)` are separate constructors; the parameterless one is annotated. | Same. | Pass a cloner in trimmed apps. |
| `InMemorySyncServer(string, Func<T,T>?)` replaced by an annotated `InMemorySyncServer(string serverId = "server")`; `InMemorySyncServerOptions.Cloner` and `Fingerprint` are `required`. | Same; the demo's Conflict Lab was silently using reflection fingerprints. | Use the options constructor with `DocumentCloner.Json`/`JsonFingerprint`. |
| `DocumentCloner.Json(JsonTypeInfo<T>)` and `DocumentCloner.JsonFingerprint(JsonTypeInfo<T>)` added; `Bsync` is marked `IsAotCompatible`. | AOT-safe helpers. | Additive. |
| `InMemoryLocalStore.UpdateAsync` returns a fresh copy of the committed record for unchanged entries (previously the transform's working copy). | Found by the store conformance suite. | None. |

### Phase 1 and first part of Phase 2

#### Behaviour

| Change | Why | Migration |
|---|---|---|
| The server no longer re-stamps `UpdatedAt`; it is the authoring (origin) timestamp. | Fixes upload-order-dependent LWW (S10) and clock poisoning (S05). | Do not use `UpdatedAt` as a server change cursor or version. Use `SyncRecord.BaseVersion` for the confirmed server version. |
| The server rejects writes whose `UpdatedAt` is more than `MaxClockSkew` (default 5 min) ahead of server time. | I12. | Rejected records carry `SyncRecord.Rejection` with code `clock-skew`; fix the device clock and write again. |
| `WriteAsync` no longer mutates the caller's document. | I02: no hidden aliasing. | Read the stamped timestamp from the returned `LocalWriteReceipt`. |
| `LastWriteWinsConflictHandler` returns `ConflictOutcome.KeepFork` (the fork keeps its origin timestamp) instead of `Resolve(fork)`. | Upload-order independence. | Only affects code that inspected the handler's result. |
| Replication calls on one engine are single-flight; overlapping calls wait. | T10. | None. |
| `PushAsync` drains the whole queue in batches, bounded by `MaxPushBatches`, instead of one batch. | S01. | None; check `SyncResult.IsComplete`. |
| Push results that name unknown operations, repeat an operation, or carry malformed state throw `SyncProtocolException` before anything is applied. | I09. | Custom transports must return one outcome per operation id. |
| Options are validated; out-of-range values throw `ArgumentOutOfRangeException` from the engine constructor. | S09. | Fix the configuration. |
| Document ids must satisfy `SyncIds.IsValid` (1–256 UTF-16 code units, no control characters or unpaired surrogates). | T59. | Validate ids at creation. |
| HLC node ids must be 1–64 characters from `[A-Za-z0-9._~-]`; `HlcTimestamp` validates its fields; `Parse` is strict. | S06, S07, I12. | Use GUIDs in "N" format or similar for node ids. |

#### API

| Change | Migration |
|---|---|
| `ILocalStore`: `UpsertAsync`, `SetCheckpointAsync`, `GetDirtyAsync` removed; `UpdateAsync`, `GetPendingAsync`, `CountDirtyAsync`, `GetClockHighWaterAsync` added. | Custom stores implement the atomic `UpdateAsync` contract (ADR-004). |
| `SyncRecord` gained `BaseVersion`, `LocalRevision`, `Pending`, `Rejection`, `Observed`, `ObservedVersion`, `IsPushable`, `KnownVersion`. | Positional constructor unchanged. |
| `Checkpoint` is now an opaque `Checkpoint(string? Value)`; `IsBefore` removed. | Treat checkpoints as opaque tokens. |
| `PushRow`/`PushRequest(Rows)`/`PushResult(Accepted, Conflicts)` replaced by `PushOperation`/`PushRequest(Operations)`/`PushResult(Outcomes)` with `PushOutcome`. | Custom transports and servers adopt the new messages (see `InMemorySyncServer`). |
| `PullResult.Documents` replaced by `PullResult.Changes` (`RemoteChange` with `Version`); `StreamEvent.Documents` likewise. | As above. |
| `SyncOptions.MaxPushPasses` removed; added `MaxPushBatches`, `MaxPullPages`, `MaxConflictRetries`, `Validate()`. | Use `MaxPushBatches` for total work and `MaxConflictRetries` for per-document conflict retries. |
| `SyncResult` gained `Rejected`, `Deferred`, `HasRemainingWork`, `IsComplete`. | Additive. |
| `SyncEngine.WriteAsync` returns `Task<LocalWriteReceipt>`; `DeleteAsync` returns `Task<LocalWriteReceipt?>`; added `CountDirtyAsync`. | Source-compatible for `await` callers; binary-incompatible. |
| `ConflictOutcome.KeepFork` and `ConflictResolution.KeepFork()` added. | Switch statements over `ConflictOutcome` need a new case. |
| `HybridLogicalClock` constructor gained optional `highWaterMark` and `maxForwardDrift`; added `Last`. `ClockDriftException` added. | Additive. |
| `InMemorySyncServer`: new options constructor (`InMemorySyncServerOptions`), `Epoch`, `GetVersion`, `ReceiptCount`. First constructor parameter renamed `node` → `serverId`. | Named-argument callers update the name. |
| Added `SyncProtocolException`, `SyncResetRequiredException`, `SyncIds`, `PushErrorCodes`. | Additive. |

#### Test changes

The 18 original tests are kept. One assertion changed: `ConflictHandlerTests.LastWriteWins_PrefersTheNewerTimestamp`
now expects `KeepFork` instead of `UseResolved` for a newer fork.
