# Bsync

Local-first document replication for .NET and Blazor: local writes that never wait for the network,
change tracking, retry-safe push, checkpointed pull and pluggable conflict resolution.

> **Status:** `0.1.0` is published on NuGet; this branch builds `0.5.0` (unreleased). Pre-1.0: minor versions may
> break. What is released, tested and where: [Status](#status). Targets `net10.0`.

## Status

This section is the single source of truth for release and verification status; other documents link here.

- **Packages.** Six packages are at `0.1.0` on NuGet.org (published 2026-09-28): `Bsync`, `Bsync.Blazor`,
  `Bsync.Storage.Sqlite`, `Bsync.Server.AspNetCore`, `Bsync.Server.PostgreSql` and `Bsync.Testing`. The repository
  is at `0.5.0` (unreleased; `0.1.1` to `0.5.0` are committed but not published) and adds
  `Bsync.Server.SqlServer`, `Bsync.Storage.Sqlite.Encrypted` (SQLCipher), `Bsync.Server.Blobs.S3` (attachments in
  object storage) and `Bsync.Maui` (needs the MAUI workload; not packed by CI).
  Changes are listed in [CHANGELOG.md](CHANGELOG.md).
- **Versioning.** Pre-1.0: a minor release may break the public API or behaviour, a patch release never does
  ([compatibility policy](docs/compatibility.md#policy)).
- **Authorities.** Durable: PostgreSQL (`Bsync.Server.PostgreSql`, released) and SQL Server
  (`Bsync.Server.SqlServer`, unreleased; verified on SQL Server 2025 LocalDB). The in-memory authority is for tests
  and samples.
- **Stores.** SQLite on native hosts, IndexedDB in browsers; in-memory for tests.
- **Evidence.** Hosts, versions and test runs are recorded in [docs/support-matrix.md](docs/support-matrix.md),
  including what has *not* been run (Android, iOS, Mac Catalyst, native Safari). Most runs so far used one
  Windows machine.
- **CI.** [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push: unit tests on Windows, Linux
  and macOS (with the encrypted SQLite store); the PostgreSQL authority on Linux; the SQL Server authority and the
  tasks sample on Linux, with an S3-compatible server; browser and WPF tests on Windows; pack, plus a build and test of
  consumers that reference only the packed packages. Its current state is in the support matrix.
- **Roadmap.** [docs/roadmap.md](docs/roadmap.md).

## What it guarantees today

Precisely, for the reference authority and the stores that pass the conformance suite (in-memory, SQLite,
IndexedDB; details and evidence in [docs/architecture/invariants.md](docs/architecture/invariants.md)):

- **Atomic local writes.** A write and its pending upload commit together and never wait for the network.
- **No lost local edits.** An acknowledgement, pull or conflict resolution never overwrites or marks clean
  a local edit made while the network call was in flight.
- **Retry-safe pushes.** Each write becomes an operation with a persisted id and immutable payload. If a
  response is lost, the same operation is resent and the server replays its original outcome.
- **Per-document optimistic concurrency.** The server accepts a write only if it was based on the current
  version; otherwise it returns a conflict for the client's handler.
- **Nothing overwritten by default.** A conflicting local change is kept as an unresolved conflict next to
  the server state until the app or user decides (or a policy you choose decides).
- **Atomic, monotone pull.** A page and its checkpoint commit together; older versions never replace newer
  ones.
- **Bounded, honest runs.** Batch and retry budgets are enforced, and `SyncResult.IsComplete` says whether
  work remains.

- **All-or-nothing groups.** Changes written together with `WriteGroupAsync`/`SaveAllAsync` are applied by the
  server all together or not at all.

It does **not** provide cross-collection transactions, causal consistency, or offline execution for purely
server-rendered UI.

## Project layout

```
src/Bsync.slnx                          Solution
src/Bsync/                              Library projects (the packages)
src/Bsync/Bsync/                        Core: engine, clock, conflicts, storage/transport contracts, in-memory
                                        reference store and authority, HTTP client transport (Bsync.Transport),
                                        and the UI-independent client (Bsync.Client: ISyncCollection, local session
                                        loop, AddLocalSyncCollection) for Blazor WebAssembly/Hybrid, MAUI, WPF,
                                        WinForms, Avalonia and console hosts
src/Bsync/Bsync.Storage.Sqlite/         Durable SQLite store for native hosts (MAUI, WPF, WinForms, console)
src/Bsync/Bsync.Server.AspNetCore/      ASP.NET Core endpoints for the protocol over any ISyncAuthority
src/Bsync/Bsync.Server.PostgreSql/      Durable PostgreSQL authority (Npgsql)
src/Bsync/Bsync.Server.SqlServer/       Durable SQL Server authority on the application's database (Microsoft.Data.SqlClient)
src/Bsync/Bsync.Testing/                Public conformance suites for stores and authorities, with in-memory and HTTP
                                        authority drivers (framework-free; store cases also run in browsers)
src/Bsync/Bsync.Blazor/                 Blazor integration: durable browser store (Bsync.Blazor.IndexedDb, with a
                                        multi-tab replication lease, AddBrowserSyncCollection) and the
                                        server-connected collection (AddServerSyncCollection)
src/Samples/                            Samples and the demo
src/Samples/Bsync.Samples.Shared/       Note model + NotesPanel component shared by the samples
src/Samples/Bsync.Samples.Notes.*       Offline-capable notes PWA: ASP.NET Core server + WebAssembly client
src/Samples/Bsync.Samples.WebApp*       Blazor Web App: one component in static SSR, Server, WebAssembly and Auto
src/Samples/Bsync.Samples.Tasks.*       Relational system of record: EF Core on SQL Server, write handler, publisher,
                                        bearer tokens; console (SQLite) and WebAssembly (IndexedDB) clients
src/Samples/Bsync.Samples.Hybrid.Wpf/   WPF Blazor Hybrid app: SQLite replica, same NotesPanel
src/Samples/Bsync.Samples.Hybrid.Maui/  .NET MAUI Blazor Hybrid app (Windows target; needs the maui-windows workload)
src/Samples/Bsync.Demo/                 Blazor WebAssembly playground simulating several devices in one tab
src/Tests/                              Tests, test hosts and benchmarks
src/Tests/Bsync.Tests/                  xUnit tests: unit, regression, provider conformance, wire fixtures,
                                        fault injection, process-kill, seeded randomized convergence
src/Tests/Bsync.Tests.PostgreSql/       Authority conformance and PostgreSQL-specific tests (needs BSYNC_POSTGRES)
src/Tests/Bsync.Tests.SqlServer/        Authority conformance and SQL Server-specific tests (needs BSYNC_SQLSERVER)
src/Tests/Bsync.Tests.Browser/          Playwright tests (Chromium, Firefox, WebKit) and their WASM harness
src/Tests/Bsync.Tests.CrashHost/        Helper process the tests kill mid-write
src/Tests/Bsync.Benchmarks/             BenchmarkDotNet workloads (docs/benchmarks.md)
src/Tests/api/                          Public API baselines checked by PublicApiTests
src/PackageConsumers/                   Projects that use only the packed packages (CI pack job)
docs/                                   Baseline review, architecture decisions, invariants, roadmap, compatibility
```

## How it works

A `SyncEngine<TDocument>` replicates one collection between a local store and a server transport.
Entities implement `ISyncEntity`:

```csharp
public interface ISyncEntity
{
    string Id { get; set; }              // stable, globally unique key (e.g. GUIDv7 assigned on create)
    HlcTimestamp UpdatedAt { get; set; } // origin timestamp: when/where the current state was authored
    bool Deleted { get; set; }           // soft-delete flag so deletions replicate
}
```

Replication metadata lives in the store's `SyncRecord<T>` envelope, not on the entity:

| Metadata | Meaning |
|---|---|
| `LocalRevision` | Incremented by every local write; decides whether an acknowledgement still applies. |
| `Pending` | The persisted operation (id, revision, base version, immutable payload) being sent. |
| `Base`, `BaseVersion` | Last confirmed server state and its server version (the concurrency token). |
| `Rejection` | Set when the server permanently rejected a revision; the record is parked until edited again. |
| `Conflict` | A local change kept after it conflicted with a newer server change: server state, local change, common ancestor. |
| Checkpoint | Opaque server-issued feed position (per store). |

### Local writes

`WriteAsync` stores a copy of the document stamped with a fresh HLC timestamp and returns a
`LocalWriteReceipt`. `DeleteAsync` stores a tombstone. Both are atomic compare-and-transform operations
on the store and never wait for replication.

### Pull

`PullAsync` asks for changes after the stored checkpoint and applies each page together with its new
checkpoint in one atomic store update. Records with unconfirmed local changes keep their local state; the
newer server state is remembered and the divergence is resolved on push.

### Push

`PushAsync` drains the pending queue in batches. For each record it first persists an operation with a
new id, then sends it with the base version it was made against. Each operation gets its own outcome:

- **Accepted**: the record adopts the server version, unless it was edited again meanwhile, in which case
  the later edit stays pending on the new base.
- **Conflict**: the configured `IConflictHandler<T>` decides; by default the conflict is kept for a decision.
- **Rejected**: the record is parked with `SyncRecord.Rejection` and does not block other records.
- **Retry later** or no outcome: the operation stays pending and is resent with the same id.

`SyncAsync` runs pull then push. Replication on one engine is single-flight.

### Conflict handlers

| Handler | Behaviour |
| --- | --- |
| `DeferConflictHandler<T>` | **Default.** Shows the server state and keeps the local change as an unresolved conflict. Nothing is lost. |
| `ThreeWayMergeConflictHandler<T>` | Merges changes to different fields (JSON-level, AOT-safe) and pushes the result; same-field conflicts go to a fallback (`Defer` by default). |
| `ClientWinsConflictHandler<T>` | Local change is re-pushed over the concurrent server change (the remote edit is lost). |
| `ServerWinsConflictHandler<T>` | Server state wins; the conflicting local change is discarded. |
| `LastWriteWinsConflictHandler<T>` | The later *authoring* timestamp wins, independent of upload order. Depends on roughly synchronized clocks. |
| `DelegateConflictHandler<T>` | Wraps a function for custom merges. |

A handler receives copies of `RealMaster` (server current), `AssumedMaster` (the base of the local edit)
and `Fork` (latest local state) and returns `AcceptMaster()`, `KeepFork()`, `Resolve(merged)` or `Defer()`.
Handlers must be deterministic and side-effect free; they never run for a replayed outcome.

Kept conflicts survive restarts and are decided explicitly:

```csharp
foreach (var record in await engine.GetConflictsAsync())
{
    var (server, mine, ancestor) = (record.Conflict!.Server, record.Conflict.Local, record.Conflict.Base);
    var merge = ancestor is null ? null : ThreeWayMerge.Merge(ancestor, mine, server, AppJsonContext.Default.Note);
    if (merge is { IsClean: true }) await engine.ResolveConflictAsync(record.Current.Id, merge.Merged);
    else await engine.DiscardConflictAsync(record.Current.Id);   // or show both versions to the user
}
```

Merge rules: members changed on one side win; different changes of the same member conflict (the server
value is kept and the JSON Pointer reported); objects merge per member, arrays and scalars are atomic, an
absent member differs from `null`, and delete versus edit is a conflict. Counters and sets are not merged
semantically.

## Getting started

```csharp
using Bsync;
using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Server;
using Bsync.Storage;

// 1. A clock with a stable, persisted, per-replica node id ([A-Za-z0-9._~-], up to 64 chars).
var clock = new HybridLogicalClock(node: "device-a");

// 2. Local store + a transport to the server (in-memory reference implementations).
var store = new InMemoryLocalStore<Note>();
var server = new InMemorySyncServer<Note>();
var transport = new InProcessTransport<Note>(server);

// 3. The engine.
var engine = new SyncEngine<Note>(store, transport, clock,
    conflictHandler: new LastWriteWinsConflictHandler<Note>());

// 4. Local-first writes (committed locally, queued for upload).
LocalWriteReceipt receipt = await engine.WriteAsync(new Note { Title = "Hello", Body = "world" });

// 5. Sync when connectivity allows, and check whether everything was done.
SyncResult result = await engine.SyncAsync();
if (!result.IsComplete) { /* work remains: deferred, rejected or over budget */ }

// 6. Read what the app sees.
IReadOnlyList<Note> notes = await engine.QueryAsync();
```

```csharp
public sealed class Note : ISyncEntity
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public HlcTimestamp UpdatedAt { get; set; }
    public bool Deleted { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
```

## Trimming and AOT (Blazor WebAssembly)

The library is marked `IsAotCompatible`. Constructors that fall back to reflection-based JSON
(`SyncEngine` without a cloner, `new InMemoryLocalStore<T>()`, `new InMemorySyncServer<T>()`) are annotated
with `[RequiresUnreferencedCode]`, so the trimming analyzer flags them. In trimmed or AOT builds use the
overloads that take delegates, for example with source-generated JSON:

```csharp
[JsonSerializable(typeof(Note))]
partial class AppJsonContext : JsonSerializerContext;

var clone = DocumentCloner.Json(AppJsonContext.Default.Note);   // or a hand-written n => n.Clone()
var store = new InMemoryLocalStore<Note>(clone);
var engine = new SyncEngine<Note>(store, transport, clock, clone);

var server = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
{
    Cloner = clone,
    Fingerprint = DocumentCloner.JsonFingerprint(AppJsonContext.Default.Note),
});
```

## Durable storage on native hosts (SQLite)

```csharp
var store = await SqliteLocalStore<Note>.OpenAsync(
    new SqliteLocalStoreOptions { DataSource = Path.Combine(appData, "replica.db"), Collection = "notes" },
    AppJsonContext.Default.Note);

var identity = await store.GetReplicaIdentityAsync();          // stable replica id + incarnation
var clock = new HybridLogicalClock(identity.Incarnation);      // a copied database gets a new incarnation
var engine = new SyncEngine<Note>(store, transport, clock, DocumentCloner.Json(AppJsonContext.Default.Note));
```

Every store update is one SQLite transaction (WAL, `synchronous=FULL` by default). Acknowledged local
writes survive process termination (tested by killing a writer process); power loss is not tested. Call
`BeginNewIncarnationAsync` when a database file may have been copied or restored from a device backup.
Not for Blazor WebAssembly; use the IndexedDB store there. Store schemas are versioned and upgrade in place on
first open (pending work is kept); a database written by a newer version of the app is refused, not
modified.

## Blazor: one component, every render mode

Components inject `ISyncCollection<T>` and never depend on the render mode:

```razor
@inject ISyncCollection<Note> Notes
...
var result = await Notes.SaveAsync(note);   // SavedLocally (WebAssembly/native) or AcceptedByServer (server)
using var subscription = Notes.Subscribe(() => InvokeAsync(ReloadAsync));
```

Each runtime registers the implementation that fits it:

```csharp
// Server project (Interactive Server, prerendering, static SSR): calls the authority in-process as the user.
builder.Services.AddServerSyncCollection<Note>(_ => authority, DocumentCloner.Json(AppJson.Default.Note),
    user => user.FindFirst("tenant")?.Value);

// WebAssembly client project: an IndexedDB replica per account, synced over HTTP by one tab.
builder.Services.AddBrowserSyncCollection<Note>("notes", AppJson.Default.Note,
    (sp, account) => new HttpSyncTransport<Note>(http, transportOptions, SyncJsonTypes<Note>.From(AppJson.Default)),
    resolveAccount: (sp, ct) => /* signed-in user id */);
```

`Capabilities` says what the host can do (offline writes, server-confirmed writes, live updates),
`Status` reports `Synced`, `Syncing`, `Offline`, `Follower`, `Paused`, `AttentionRequired` and so on, and
`GetItemStatusAsync(id)` tells whether one document is still pending, rejected or conflicted.
`GetConflictsAsync`, `ResolveConflictAsync` and `DiscardConflictAsync` expose kept conflicts to components
(the samples' `NotesPanel` offers "Keep mine" / "Keep theirs"). See `src/Samples/Bsync.Samples.WebApp` for all
four render modes side by side.

Apps with many collections give each session a `Coordination` on one `SyncCoordinator`: it limits how many sync at
once, syncs parents before children (`DependsOn`), shares one hint stream (`HttpSyncHints.Multiplexed`, served by
`MapSyncCollections`), and reports one aggregate `Status`. To wait for sync, call `SyncAsync` with a goal:

```csharp
var result = await notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10));    // everything uploaded and pulled
var receipt = await engine.WriteAsync(order);
await coordinator.SyncAsync(SyncGoal.Accepted(order.Id, receipt.LocalRevision), TimeSpan.FromSeconds(5), ["orders"]);
hub.On<string>("changed", coordinator.Hint);                                        // your own real-time hub as hints
```

Browser sessions sync after each local write, when the browser comes back online or the tab becomes visible,
when the server announces a change over its Server-Sent Events hint stream, and on an interval as a safety
net. Hints only make things faster: losing them never loses data. If your app has a service worker, do not
let it proxy `/sync/` requests; a proxied hint stream blocks service-worker updates.

## Durable storage in the browser (IndexedDB)

```csharp
var store = await IndexedDbLocalStore<Note>.OpenAsync(jsRuntime,
    new IndexedDbStoreOptions { DatabaseName = $"bsync-{userId}", Collection = "notes" },
    AppJsonContext.Default.Note);

// Only one tab should run the sync loop; others read and write locally.
await using var lease = await IndexedDbReplicaLease.TryAcquireAsync(jsRuntime, $"bsync-{userId}");
```

Open it only once the WebAssembly runtime is interactive (never during prerendering). Writes from several
tabs are safe: every update commits in one IndexedDB transaction and only if no other tab changed the same
records first. Failures (IndexedDB missing, quota, an upgrade from another tab) surface as
`LocalStoreUnavailableException`. See `src/Samples/Bsync.Samples.Notes.Client` for a complete offline PWA.

The browser recipe asks for persistent storage when it opens a replica and reports the answer in
`SyncStatus.PersistentStorage`; without it, a browser may delete the replica under storage pressure.

### Signing out on a shared device

```csharp
if (session.Status.Pending > 0) { /* warn: unsynced changes will be lost */ }
await coordinator.DeleteReplicasAsync(account);   // or session.DeleteReplicaAsync(account) for one collection
```

Replication of the account stops, its replicas are closed, and `SyncSessionOptions.DeleteReplica` removes the files
(the browser recipe deletes the account's IndexedDB database; native apps call `SqliteStorePool.DeleteDatabaseAsync`
and delete their own blob files). Nothing is deleted on the server.

### Encryption at rest

Native apps reference `Bsync.Storage.Sqlite.Encrypted` instead of `Bsync.Storage.Sqlite` (never both) and pass a
32-byte key kept in the platform's protected store; browser apps pass a key to `IndexedDbStoreOptions.EncryptionKey`:

```csharp
var store = await SqliteLocalStore<Note>.OpenAsync(new SqliteLocalStoreOptions
{
    DataSource = path, Collection = "notes", EncryptionKey = keyFromSecureStorage,   // SqliteEncryption.NewKey() once
}, AppJson.Default.Note);
```

A wrong or missing key fails explicitly; it never opens an empty replica. See
[ADR-016](docs/architecture/adr-016-encryption-at-rest.md) for the threat model and what stays readable.

## Server restores, access changes and retention

If the server is restored from a backup, it must start a new epoch and never reuse a version number.
Replicas then reset automatically: they keep pending edits and kept conflicts, pull a fresh snapshot, and
mark local records the restored server no longer has as `MissingAfterReset` (hidden, not deleted).

With read membership (in-memory, SQL Server and PostgreSQL authorities, ADR-015), each caller pulls only the documents it may
read, and a document that leaves its view arrives as a removal: clean copies leave the device, local drafts are kept
hidden. Nothing else resets:

```csharp
Readers = note => note.SharedWith,                              // computed at every write; a change is a new version
PrincipalKey = caller => caller.Principal.FindFirst("sub")?.Value,
```

Without membership, when what a user may see changes (revoked or granted access, a different filter), the authority's
`ScopeFingerprint` changes and the old checkpoint is refused with reason `scope-changed`; the replica
resnapshots and removes documents it may no longer see from the device (never from the server). Documents
with local changes or kept conflicts are never removed.

Tombstones and operation receipts can be purged on the server (`PurgeTombstones`, `PurgeReceipts`). A
replica offline for longer than the retention horizon resets (`expired`), and an edit based on a purged
document is rejected with `base-expired` rather than resurrecting it; writing it again recreates it.
`SyncResult.ResetPerformed`, `MissingAfterReset` and `PurgedAfterReset` report what happened. See
`docs/protocol/v1.md` §4 and §6.1.

To purge on a schedule, register the retention service; it purges what is older than the offline horizon in
every authority it is given, and refuses (at startup) a receipt horizon shorter than that horizon:

```csharp
builder.Services.AddSyncRetention(options =>
{
    options.MaxOfflineHorizon = TimeSpan.FromDays(45); // the default
    options.Targets.Add(authority);                    // or register authorities as ISyncRetentionTarget
});
```

Replicas drop their own clean tombstones below the server's retention horizon (feature `retention`), so local
tombstones do not accumulate either.

## Groups: changes that belong together

```csharp
await engine.WriteGroupAsync([order, line1, line2]);          // or ISyncCollection.SaveAllAsync
```

The group is committed locally at once, uploaded in one request, and applied all together or not at all. If one
change conflicts, the policy decides. A resolved change resends the group; a conflict kept for the user parks the
others until it is resolved. Only authorities that advertise `groups` receive grouped changes (both included
authorities do). See protocol §4.1.

## A durable server: PostgreSQL

```csharp
var dataSource = NpgsqlDataSource.Create(connectionString);
var authority = await PostgreSqlSyncAuthority<Note>.CreateAsync(new()
{
    DataSource = dataSource, DocumentType = AppJson.Default.Note, Collection = "notes",
});
app.MapSyncCollection("notes", authority, json, endpointOptions).RequireAuthorization();
```

- One instance serves every tenant: the caller's scope selects its feed.
- Any number of server processes may share the database; commit hints reach clients of every process through
  `LISTEN`/`NOTIFY`.
- After restoring a backup, call `BeginNewEpochAsync(versionFloor)`. For retention, use `PurgeTombstonesAsync`
  and `PurgeReceiptsAsync`.
- Tests: `BSYNC_POSTGRES="Host=...;Username=...;Password=..." dotnet test src/Tests/Bsync.Tests.PostgreSql`.

## A durable server on your SQL Server database

The authority keeps its tables in a `bsync` schema of the application's own database, so the application's tables
and the replication feed commit together (ADR-014):

```csharp
var authority = await SqlServerSyncAuthority<Note>.CreateAsync(new()
{
    ConnectionString = connectionString, DocumentType = AppJson.Default.Note, Collection = "notes",
    WriteHandler = new NoteRules(),            // optional: runs in the authority's transaction
});
app.MapSyncCollection("notes", authority, json, endpointOptions).RequireAuthorization();
```

A write handler sees each replicated write that would be accepted, with the open connection and transaction. It
can write the application's tables, return a canonical document (for example with a server-computed field), or
answer conflict, reject (a stable error code the replica keeps) or retry later:

```csharp
sealed class NoteRules : ISyncWriteHandler<Note>
{
    public async ValueTask<SyncWriteDecision<Note>> HandleAsync(SyncWriteContext<Note> write, CancellationToken ct)
    {
        if (write.Submitted.Title.Length > 200) return SyncWriteDecision<Note>.Reject("title-too-long");
        await SaveToDomainTableAsync((SqlConnection)write.Connection!, (SqlTransaction)write.Transaction!, write.Submitted, ct);
        write.Submitted.WordCount = Count(write.Submitted.Body);   // reaches every replica
        return SyncWriteDecision<Note>.Accept(write.Submitted);
    }
}
```

Server-originated changes go through the publisher, optionally in the application's transaction (EF Core shown):

```csharp
await using var transaction = await db.Database.BeginTransactionAsync();
db.Prices.Add(price);
await db.SaveChangesAsync();
await authority.UpsertAsync(tenant, new PriceDocument { Id = price.Id, Amount = price.Amount }, transaction.GetDbTransaction());
await transaction.CommitAsync();
authority.NotifyCommitted(new AuthorityCommit(tenant, [price.Id]));   // optional: prompt hint
```

`ReplaceScopeAsync(scope, documents)` rebuilds a projection: unchanged documents keep their version (a rebuild of
10,000 unchanged documents adds no feed entry) and documents no longer listed become tombstones. Commits by other
server processes are announced by polling the feed heads (`CommitPollInterval`, default 5 s) or by calling
`NotifyCommitted` from your own message bus. After a restore call `BeginNewEpochAsync(versionFloor)`.
A complete example with EF Core, bearer tokens and two kinds of clients: `src/Samples/Bsync.Samples.Tasks.Server`.
Actions that must run exactly once (complete, approve, send) are modeled as immutable intents executed by a write
handler; see [the intents pattern](docs/patterns/intents.md), which the Tasks sample implements. Files attached to
documents are uploaded before the document and transferred resumably; see [the attachments pattern](docs/patterns/attachments.md).
Sets of files that must switch version together are [bundles](docs/patterns/bundles.md).
Tests: `BSYNC_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Integrated Security=true" dotnet test src/Tests/Bsync.Tests.SqlServer`.

## Native apps (WPF, .NET MAUI)

`src/Samples/Bsync.Samples.Hybrid.Wpf` and `src/Samples/Bsync.Samples.Hybrid.Maui` host the same `NotesPanel` in a
`BlazorWebView`, with a SQLite replica registered through `AddLocalSyncCollection`. Sync pauses while the window
is minimized or the app is in the background. Both have a `--smoke` mode that the tests use to drive the real UI.

MAUI apps can use the optional `Bsync.Maui` package (build it with the MAUI workloads; Android, iOS and Mac Catalyst
need `-p:BsyncMauiMobileTargets=true`):

```csharp
builder.Services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note> { /* ... */ }.UseMauiLifecycle());
var key = await SecureReplicaKeys.GetOrCreateAsync(account);          // for Bsync.Storage.Sqlite.Encrypted
var result = await MauiSync.RunInBackgroundAsync(session, account, TimeSpan.FromSeconds(25));   // from a background task
```

`UseMauiLifecycle` syncs when internet access returns and pauses and resumes with the app's window.
`RunInBackgroundAsync` is the body of an Android `WorkManager` worker or an iOS `BGTaskScheduler` task. Registering
those tasks is platform code in the app, and it has not been verified here.

## When something goes wrong

- A rejected change stays parked: list it with `GetRejectedAsync`, fix the cause, then `RetryRejectedAsync`
  (a new operation), or `RevertAsync` to go back to the server state. Components use
  `ISyncCollection.RetryAsync`/`RevertAsync`.
- `ExportLocalChangesAsync`/`ImportLocalChangesAsync` move unsynchronized work, with its operation ids, to
  another store.
- A damaged SQLite file: `SqliteStoreRecovery.CheckAsync(path)`, then `RebuildAsync(path)`. The rebuild moves
  the file aside (never deletes it), keeps every readable record with local work, and resyncs the rest.

See [docs/operations/disaster-recovery.md](docs/operations/disaster-recovery.md) for server restores,
retention, schema roll-outs and the full runbook.

## Observability

Traces and metrics use the .NET built-ins: `ActivitySource`/`Meter` named `Bsync` (client engine) and the
`Bsync.Server` meter (endpoints). The session and endpoints log through `ILogger`. Nothing records document
contents or ids.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SyncDiagnostics.SourceName))
    .WithMetrics(m => m.AddMeter(SyncDiagnostics.SourceName, SyncEndpoints.MeterName));
```

Instruments, tags and alert suggestions: [docs/operations/observability.md](docs/operations/observability.md).
Benchmark results: [docs/benchmarks.md](docs/benchmarks.md).

## Observing changes

```csharp
using var subscription = engine.Observe(change =>
    Console.WriteLine($"{change.Kind}: {string.Join(", ", change.Ids)}"));
```

One callback per committed transaction, after the commit. Dispatch to your UI thread yourself.

## Serving and calling over HTTP

Server (ASP.NET Core):

```csharp
var json = SyncJsonTypes<Note>.From(AppJsonContext.Default);   // context declares the 4 protocol types
var authority = new ScopedAuthority<Note>(
    scope => new InMemorySyncServer<Note>(serverOptions),         // one isolated authority per tenant
    new AuthorityLimits(MaxOperationsPerPush: 1000, MaxPageSize: 1000));

app.MapSyncCollection("notes", authority, json, new SyncEndpointOptions
{
    SupportedSchemas = new HashSet<string> { "notes-v1" },
    ResolveScope = http => http.User.FindFirst("tenant")?.Value, // from authenticated claims only
}).RequireAuthorization();
```

Client (WebAssembly or native):

```csharp
var transport = new HttpSyncTransport<Note>(httpClient,
    new HttpSyncTransportOptions { Collection = "notes", SchemaId = "notes-v1" }, json);
```

Errors surface as `SyncResetRequiredException` (handled by the engine), `SyncProtocolException`, or
`SyncTransportException` with `ErrorCode`, `IsTransient` and `RetryAfter`. Server-rendered code calls the
same authority in-process with `new InProcessTransport<Note>(authority, new SyncCallContext(user, scope))`,
so authorization is identical. `CanRead`/`CanWrite` hooks on the reference authority filter and authorize
per document. For a durable authority see [A durable server: PostgreSQL](#a-durable-server-postgresql).

## Wire protocol

The messages, JSON encoding (64-bit versions as digit strings, canonical HLC strings, opaque checkpoints)
and the rules for authorities and replicas are specified in [docs/protocol/v1.md](docs/protocol/v1.md),
with fixtures in `docs/protocol/fixtures`. Add the protocol types for your document to a
`JsonSerializerContext` (`PullRequest`, `PullResult<T>`, `PushRequest<T>`, `PushResult<T>`). Give
documents a `[JsonExtensionData]` member so fields unknown to older clients are not erased.

## Testing your own store or authority

The package `Bsync.Testing` carries the conformance suites that every provider in this repository passes. They have
no test-framework dependency; wrap each case in your framework's test:

```csharp
// An authority: implement IAuthorityConformanceDriver (create an empty authority from AuthorityConformanceOptions,
// and optionally purge/new-epoch operations), then run every case its capabilities allow.
foreach (var conformanceCase in AuthorityConformance.CasesFor(driver.Capabilities))
    await conformanceCase.RunAsync(driver);

// The same cases over HTTP: serve each authority with MapSyncCollection and let the driver connect to it.
var overHttp = new HttpAuthorityDriver(driver, (authority, ct) => StartMyServerAsync(authority, ct));

// A local store.
foreach (var conformanceCase in LocalStoreConformance.Cases)
    await conformanceCase.RunAsync(() => OpenEmptyStoreAsync());
```

`InMemoryAuthorityDriver` is the reference driver. Provider-specific drills (delayed commits, several processes,
crashes, restores from a real backup) are not part of the public suites; see `src/Tests/Bsync.Tests.PostgreSql` for
the PostgreSQL ones.

## Hybrid Logical Clock

`HlcTimestamp(WallTime, Counter, Node)` is validated on construction (wall time ≤ 15 digits, counter ≤
999,999, ASCII node alphabet), so numeric order and the ordinal order of `Encode()` always agree.
`HybridLogicalClock` is thread-safe and strictly monotonic; counter overflow carries into wall time.
The engine seeds its clock from the store's high-water mark before its first write, so timestamps are not
reused after a restart. The server validates that timestamps are not too far in the future and never
re-stamps them.

## The demo

`src/Samples/Bsync.Demo` is a Blazor WebAssembly playground that simulates several devices in one browser
tab, each with its own engine and clock, talking to one in-process server. Nothing is persisted.

- **Playground** (`/playground`): create, edit and delete notes per device; toggle devices offline.
- **Conflict Lab** (`/conflicts`): force concurrent edits and compare conflict strategies.
- **Clock Explorer** (`/clock`): visualize HLC timestamp generation.

```bash
dotnet run --project src/Samples/Bsync.Demo
```

## Building and testing

```bash
dotnet build src/Bsync.slnx -c Release
dotnet test src/Tests/Bsync.Tests -c Release                      # unit, conformance, HTTP, Blazor, recovery
BSYNC_POSTGRES="Host=localhost;Username=postgres;Password=..." dotnet test src/Tests/Bsync.Tests.PostgreSql -c Release
BSYNC_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Integrated Security=true" dotnet test src/Tests/Bsync.Tests.SqlServer -c Release
dotnet publish src/Samples/Bsync.Demo -c Release                              # optional
dotnet publish src/Samples/Bsync.Demo -c Release -p:RunAOTCompilation=true    # needs the wasm-tools workload
```

Browser and desktop tests (download Playwright's Chromium, Firefox and WebKit on first run, about 500 MB). They
also run the WPF sample on Windows, the MAUI sample with `-p:BuildMauiSample=true`, and the multi-process
PostgreSQL test when `BSYNC_POSTGRES` is set:

```bash
dotnet test src/Tests/Bsync.Tests.Browser -c Release
dotnet test src/Tests/Bsync.Tests.Browser -c Release -p:BrowserHostAot=true   # same tests, WebAssembly AOT build
```

A change to a package's public API fails `PublicApiTests` until the baseline in `src/Tests/api` is regenerated on purpose
(`BSYNC_UPDATE_API=1 dotnet test src/Tests/Bsync.Tests --filter PublicApiTests`) and reviewed. `dotnet pack` builds
the seven library packages; nothing is published from this repository's tooling.
CI: `.github/workflows/ci.yml` (Windows, Linux and macOS unit tests, PostgreSQL, SQL Server, browser tests, pack
and package consumers) and `.github/workflows/nightly.yml` (nightly `-preview` pack, not published).

Consume the packages exactly as a NuGet user would (no project references; `src/PackageConsumers/nuget.config` takes
`Bsync*` only from `artifacts/packages`):

```bash
for p in Bsync Bsync.Blazor Bsync.Server.AspNetCore Bsync.Server.PostgreSql Bsync.Server.SqlServer Bsync.Storage.Sqlite Bsync.Testing; do
  dotnet pack src/Bsync/$p -c Release -o artifacts/packages -p:VersionSuffix=local; done
dotnet test src/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.3.0-local
dotnet publish src/PackageConsumers/Bsync.PackageConsumer.Web -c Release -p:BsyncPackageVersion=0.3.0-local -o artifacts/consumer-web
dotnet artifacts/consumer-web/Bsync.PackageConsumer.Web.dll --smoke
```

Run the notes sample: `dotnet run --project src/Samples/Bsync.Samples.Notes.Server` (the offline service worker
is active only in a published build).

Test display names carry invariant (`I04`) and catalogue (`T11`) ids, for example:

```bash
dotnet test src/Bsync.slnx --filter "DisplayName~I04"
```

## Documentation

- [Baseline review](docs/review/baseline.md): reproduced defects and what changed.
- [Architecture decisions and invariants](docs/architecture/README.md).
- [Protocol specification v1](docs/protocol/v1.md).
- [Compatibility policy and migration notes](docs/compatibility.md).
- [Disaster recovery and stuck replicas](docs/operations/disaster-recovery.md) and [observability](docs/operations/observability.md).
- Patterns: [immutable intents](docs/patterns/intents.md), [attachments](docs/patterns/attachments.md) and [bundles](docs/patterns/bundles.md).
- [Benchmarks](docs/benchmarks.md).
- [Support matrix](docs/support-matrix.md) and [roadmap](docs/roadmap.md).

## License

MIT. See [LICENSE](LICENSE).
