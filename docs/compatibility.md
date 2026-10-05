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

## 0.1.1 (unreleased)

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
