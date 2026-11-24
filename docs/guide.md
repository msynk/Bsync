# Bsync guide

This guide is for developers who build on Bsync and for the people who operate it. Part 1 covers the building blocks
beyond ordinary synced collections, Part 2 is the operations runbook, and Part 3 covers upgrades, supported targets
and measured performance.

Start with the [README](../README.md) for installation, collections, stores, authorities and conflicts. Design
decisions and invariants are in [architecture](architecture.md), the wire format in [protocol](protocol.md), and open
work in the [implementation plan](implementation-plan.md) (ids such as `B4` refer to its items).

**Contents**

- [Part 1 — Building apps](#part-1--building-apps):
  [ground rules](#ground-rules-for-offline-orchestration) ·
  [local reads and read models](#local-reads-and-read-models) ·
  [attachments and managed content](#attachments-and-managed-content) ·
  [intents and recoverable work](#intents-and-recoverable-work) ·
  [accounts, local unlock and maintenance](#accounts-local-unlock-and-maintenance) ·
  [MAUI and native background](#maui-and-native-background)
- [Part 2 — Operating Bsync](#part-2--operating-bsync):
  [observability](#observability) ·
  [disaster recovery and stuck replicas](#disaster-recovery-and-stuck-replicas) ·
  [release process](#release-process)
- [Part 3 — Versions, support and performance](#part-3--versions-support-and-performance):
  [upgrading between versions](#upgrading-between-versions) ·
  [support matrix](#support-matrix) ·
  [benchmarks](#benchmarks)

---

## Part 1 — Building apps

### Ground rules for offline orchestration

These rules apply to every API in Part 1 that takes an account, a budget or a cursor.

- **One account scope per account.** Use one `AccountScope`, orchestration store (`SqliteOrchestrationStore` or
  `IndexedDbOrchestrationStore`), blob store and physical lock namespace per application, environment, account and
  organization. Never derive server authorization from this local identity.
- **Keep the account catalog outside deletable storage**, so deletion and background relaunch can always read it.
- **Budgets are cooperative.** A budget expiry returns a resumable result (`false`, `RemainingWork`, a cursor); caller
  cancellation throws. Persist cursors, schedule another bounded pass later, never retry in a tight loop.
- **Dispose with `await using`.** Synchronous disposal keeps a conservative pin or lock until the process or tab ends.
- **Durable state is the truth.** Progress events are UI hints; persisted job states, offsets and checkpoints win.
- **No distributed rollback.** Bsync records progress so work can resume; it never undoes remote effects.

### Local reads and read models

Use collection queries for editable data, and a `LocalReadModel` for derived data (summaries, search projections,
dashboards) that can always be regenerated from the authoritative local documents.

#### Collection queries

- The ID path and declared-index paths read keyset pages of 200 and stop after the requested number of matches; a
  residual `Where` or a large `Skip` can still visit the whole range. A custom `Order` reads and sorts everything.
- Pages are not a snapshot across calls. For a stable revision use a read model or an offline package snapshot.
- `bsync.query.materialized` / `bsync.query.returned` (tag `bsync.query.mode`) expose scan-heavy UI queries.

#### Rebuildable read models

`LocalReadModel` provides durable staging, all-or-nothing activation of a complete generation, and immutable readers.
First capture a **stable input**: either copy values and the source epoch/version under a gate that every source
writer and replication applier also takes, or keep a real source snapshot transaction open while enumerating. Live
`Skip`/`Take` pages are not a substitute; without a boundary, present the projection as provisional instead.

```csharp
// metadata belongs to exactly one AccountScope; captured was copied under the host's source write gate.
var model = new LocalReadModel(metadata, "work-summary");
async IAsyncEnumerable<ReadModelRow> Project()
{
    foreach (var item in captured.Items)
        yield return new(item.Id, JsonSerializer.Serialize(item, AppJson.Default.Summary));
    await Task.CompletedTask;
}
var activated = await model.RebuildAsync("summary-v2", captured.EpochAndVersion,
    Project(), TimeSpan.FromSeconds(20), cancellationToken);
await using var snapshot = await model.OpenActiveAsync(cancellationToken);
if (snapshot is not null)
{
    // Interpret payloads with snapshot.Generation.SchemaVersion; compare SourceVersion with the source,
    // or show freshness as unknown. Continue with page.Continuation on this same snapshot.
    var page = await snapshot.ReadPageAsync(100, cancellationToken: cancellationToken);
}
```

- `false` means another builder owns the work, the budget expired or ownership was lost; the previous generation stays
  readable and `GetStatusAsync` reports unfinished work. Restart with a freshly captured input.
- Rebuild on startup and resume even if no observer fired; full replacement handles removals, epoch resets and schema
  changes. Decide explicitly how conflicted documents contribute; a read model never resolves a conflict.
- **Retention:** the legacy constructor keeps retired generations until account deletion. For bounded retention use
  `new LocalReadModel(metadata, name, account, generationLocks, retention: options)` with a lock provider shared by
  every process and tab, and call `CollectRetiredAsync` with its cursor. Default ceilings (64 generations, 1 million
  rows, 128 MiB of payload) refuse a rebuild rather than evict readers; `GetStorageStatusAsync` reports usage.
- **Erasure and protection:** old snapshots may hold data the user lost access to; stop readers and delete derived
  storage when policy requires. In the browser, protecting orchestration payloads needs `ProtectedOrchestrationStore`;
  encrypted blobs do not encrypt read models.

Design: ADR-023 and ADR-027 in [architecture](architecture.md).

### Attachments and managed content

Use attachments when documents need files. Use offline packages when a set of files must switch to a new revision all
at once and stay usable offline.

#### How attachments work

Documents replicate as JSON; files travel beside them as **content-addressed blobs**. A document carries one
`BlobReference` per file (`{ id, sha256, size, contentType, fileName }`); stores are keyed by SHA-256, so identical
content is stored once and a hash check proves bytes are complete.

| Piece | Package |
|---|---|
| `BlobReference`, `IBlobCache`, `FileBlobCache`, resumable `HttpBlobTransfer`, `ManagedBlobCache`, `BlobTransferManager`, `OfflinePackageManager`, `EncryptedBlobCache` | `Bsync` |
| `BrowserBlobStore` (IndexedDB `IBlobCache`) | `Bsync.Blazor` |
| `MapSyncBlobs`, `IBlobStore`, `FileSystemBlobStore`, `IBlobAccess`, `SyncBlobCollector` | `Bsync.Server.AspNetCore` |

The Tasks sample is the worked example: access rules and the GC job in
[`TasksBlobs.cs`](../src/Samples/Bsync.Samples.Tasks.Server/TasksBlobs.cs), reference checks in
[`TaskWriteHandler.cs`](../src/Samples/Bsync.Samples.Tasks.Server/TaskWriteHandler.cs), the device side in
[`LocalBlobStore.cs`](../src/Samples/Bsync.Samples.Tasks.Console/LocalBlobStore.cs) and
[`TasksClient.cs`](../src/Samples/Bsync.Samples.Tasks.Console/TasksClient.cs), the browser UI in
[`TaskAttachments.cs`](../src/Samples/Bsync.Samples.Tasks.Client/TaskAttachments.cs).

**Ordering rules**

1. **Bytes before the save.** Import into the device store first (`IBlobCache.ImportAsync` hashes and writes durably),
   then save the document with the reference.
2. **Upload before the document.** `SyncOptions<T>.ReadyToPush` holds back a pending document (and its dependency
   group) while content it names is not on the server; held documents count as `Deferred`. The server write handler
   checks again and answers `retry-later` (`dependency-missing`) for content the tenant has not uploaded.
3. **Finishing is idempotent.** The "finish" request verifies the hash and records that the caller's scope holds the
   content (`IBlobAccess.RecordAsync`); repeating it changes nothing.

A document whose content is not yet on the device stays readable; show the attachment as unavailable.

#### Server setup

```csharp
IBlobStore store = new FileSystemBlobStore(Path.Combine(contentRoot, "blobs"));
app.MapSyncBlobs(store, new MyBlobAccess(connectionString), syncOptions).RequireAuthorization();
```

- `syncOptions` is your sync routes' `SyncEndpointOptions`; the scope comes from its required `ResolveScope`. Anonymous
  callers get 401 unless `AllowAnonymous` is set; add your own policy with `.RequireAuthorization(...)`.
- Your `IBlobAccess` tracks which scope uploaded what (`HoldsAsync`, `RecordAsync`) and decides reads (`CanReadAsync`).
  Unauthorized reads get 404, so a hash does not reveal existence. Storage is deduplicated, uploads are not: each scope
  uploads content itself before it counts as holding it.
- `SyncBlobEndpointOptions.MaxSize` defaults to 200 MiB. Routes are in [protocol](protocol.md), section 8.
- With cookie authentication, set `SyncBlobEndpointOptions.RequireProtocolHeader = true` once every client is 0.6.0 or
  later, and use `HttpBlobTransferOptions.DownloadTickets`: every blob request then carries `Bsync-Protocol`, so browsers
  preflight it and other sites cannot use the user's cookies on these routes.
- A custom `IBlobStore` should implement `GetCompletedSizeAsync` and `ForgetCompletedAsync` (as `FileSystemBlobStore`
  does) so a finish repeated after a fault needs no new upload; without them such a finish restarts the upload.

#### Transfers and object storage

- Uploads go in 4 MiB chunks; the server keeps every byte that arrived and tells a restarted client where to continue.
  Downloads resume with range requests and are hashed before they become readable (a mismatch throws
  `BlobCorruptedException` and the next attempt starts over).
- `HttpBlobTransfer.Progress` reports offset, length and a separate `Verified` flag; the offset can go backwards after
  a repair. Never put hashes from these events into metric tags.
- The [`S3BlobStore`](../src/Samples/Bsync.Samples.Tasks.Server/S3BlobStore.cs) recipe (copy it; reference `AWSSDK.S3`)
  stores verified content under `objects/ab/<sha256>` and redirects downloads to presigned URLs. Uploads go through
  the server by default; with `PresignUploads = true` the client PUTs directly to the bucket and the server verifies
  a private disk copy on finish (`MaximumStagedBytes` 512 MiB; provision protected temp disk). Interrupted presigned
  uploads restart from zero (B3).
- Browsers need bucket CORS for the app's origin (`GET`, plus `PUT` for presigned uploads). Use a `DirectClient`
  without sync credentials for bucket requests.
- **Download tickets** (`HttpBlobTransferOptions.DownloadTickets = true`, off by default): the client asks
  `GET {prefix}/{sha256}/download` for a short-lived presigned URL, never caches it, and refreshes once on 401/403.
  An opted-in client fails against a server without tickets instead of falling back to credentialed redirects. After
  sign-in, call `ResumeCredentialWaitAsync(hash, direction)` once for jobs waiting on credentials.
- Qualify a real bucket with the [object-store qualification runner](../src/Tools/Bsync.ObjectStoreQualification/README.md);
  real-service evidence is still open (R6).

#### Garbage collection and the browser store

- `SyncBlobCollector.CollectAsync(store, referenced, minimumAge)` deletes unreferenced objects and abandoned partial
  uploads older than `minimumAge` (the grace period covers content uploaded for a document not yet written). Run it on
  a schedule with every hash any document or package names, and remove the scopes' upload records for collected
  content, as the sample's `BlobJanitor` does.
- `BrowserBlobStore` keeps verified content in IndexedDB (`bsync-blobs-<name>`); quota errors surface as
  `LocalStoreUnavailableException`. Open one per account and call `DeleteAllAsync` on sign-out. A service worker must
  not proxy blob routes. Private WebKit sessions limit objects to 16 MiB (ADR-036).

#### Managed cache and transfer jobs

For many files, budgets and eviction, wrap the raw store in `ManagedBlobCache` and drive transfers through
`BlobTransferManager`:

```csharp
var account = new AccountScope("catalog", "production", authenticatedAccountId);
await using var metadata = await SqliteOrchestrationStore.OpenAsync(metadataPath, account);
var locks = new FileBlobCacheLock(accountLockDirectory);
var cache = new ManagedBlobCache(new FileBlobCache(accountBlobDirectory), metadata, locks,
    softBudgetBytes: 256L * 1024 * 1024, hardBudgetBytes: 512L * 1024 * 1024);
await cache.ReconcileAsync();
var jobs = new BlobTransferManager(metadata, cache, new HttpBlobTransfer(accountHttpClient));
var released = await jobs.ReconcileReleasedAsync(limit: 100);   // run again if released == 100
var packages = new OfflinePackageManager(metadata, cache, jobs, locks, AuthorizeLocalPackageAsync);
```

In the browser use `IndexedDbOrchestrationStore.OpenAsync(js, account)`, `BrowserBlobStore.OpenAsync(js,
account.StorageKey)` and `BrowserBlobCacheLock.OpenAsync(js, account)`. Browsers can still evict storage, so report
missing prepared content.

- **Pending attachment:** `ImportPinnedAsync(seekableStream, stableWorkOwner)` before saving the reference, then
  `EnqueueAsync(hash, size, Upload)`, and gate the document with `ReadyToPush`.
- **Jobs:** `DrainAsync(budget, ct)` runs bounded attempts. Paused, credential-blocked and failed jobs need a host
  decision; requeue with `SetStateAsync(..., BlobTransferState.Queued)` after fixing the cause.
- **Pins:** `MaintainOwnersAsync` resolves only explicitly named `app/` pins; your resolver returns Live, Released,
  Unknown or Unavailable, and only permanent Released allows removal. Never reuse owner ids or expire pins by age.
- **Bounded maintenance:** prefer `ReconcilePageAsync` and `CollectRetiredPageAsync` with persisted cursors over the
  full-list `ReconcileAsync`/`ListAsync`. Unknown files and abandoned partials are kept and count against capacity.

Known gaps: download pins are not released automatically and terminal jobs are not pruned, so caches can fill (B4);
locked keys fail jobs instead of deferring them (B5); orphaned import staging is not collected (B6). A simpler
attachments facade is planned (D7).

#### Offline packages and package sets

The publisher supplies an authorized `OfflinePackageManifest` of immutable documents and files. The device keeps
"latest known" separate from "in use": readers see the old or the new revision, never a mix, and the old one stays
usable while the new one downloads.

```csharp
await packages.StageAsync(manifest, ct);                         // local and durable, no network
var status = await packages.PrepareAsync(manifest.PackageId, TimeSpan.FromSeconds(20), ct);
// Later, including after a cold offline restart:
await using var snapshot = await packages.OpenActiveAsync(manifest.PackageId, ct);
await using var content = await snapshot.OpenAsync("document", ct);
```

- `PrepareAsync` resumes staging, drains downloads, verifies and activates within one budget, keeping the prior
  revision if required content is missing. `TryActivateAsync` activates content imported by another trusted path.
- `GetPreparationAsync` reports what the latest candidate still misses; `GetStatusAsync` verifies the active revision.
  For frequent UI refreshes use `GetLastVerificationAsync` and show the age of `VerifiedUtc`. Opening snapshots and
  items always verifies content.
- **Package sets** (root schema 2): mark optional children `Optional = true`; their absence does not block required
  children. Read `OptionalFailures` and the snapshot's `UnavailableOptionalChildren` (ADR-039).
- Retire old revisions explicitly after replacing their active and latest pointers.

The Tasks sample's server-published bundles ([`Bundles.cs`](../src/Samples/Bsync.Samples.Tasks.Shared/Bundles.cs),
[`BundleEndpoints.cs`](../src/Samples/Bsync.Samples.Tasks.Server/BundleEndpoints.cs): a pull-only collection plus
`UpdateBundlesAsync`, `GetActiveBundle`, `GetBundleStateAsync`) show the same idea with a server; old bundle revisions
are not garbage-collected server-side. The Notes apps' `ContentLibraryPanel` demonstrates prepare, interrupt, restart
and switch.

#### Integrity, repair and encryption

- **Repair:** re-import known-good bytes with `ManagedBlobCache.ImportAsync`; the replacement is verified and promoted
  atomically, keeping pins (capacity must hold both copies). Package preparation also schedules repairs for providers
  implementing `IBlobCacheReplacement`; read the outcome with `jobs.GetRepairAsync(hash)`. Reopen after a repair.
- **Verification:** `BlobVerification.VerifyAsync(cache, hash, size, budget:, maximumContentBytes:, ...)` needs both a
  time and a byte allowance; only `Verified` proves the hash and length.
- **Encrypted cache (v1):** `EncryptedBlobCache` wraps a dedicated `FileBlobCache` or `BrowserBlobStore` with an
  `ILocalKeyProvider`, a versioned `LocalKeyReference` and `PlatformBlobEncryption` or
  `BrowserBlobEncryption.OpenAsync(js)`; wrap it in `ManagedBlobCache`. Objects are limited to 16 MiB. Rotate with
  `RotateEncryptionAsync(hash)` and keep old key versions until every object has rotated (ADR-025).
- **Chunked encryption (v2) is experimental.** `ChunkedEncryptedBlobCache` adds bounded chunks, authenticated ranges,
  resumable rotation, bounded scrubbing (`ScrubAsync`) and quarantine (`RecoverAsync`). Its manifest does not yet bind
  completeness and final length (A9) and it has had no independent cryptography review (R5). Read ADR-028 first.
- **Retiring a key:** under exclusive account maintenance ownership, scan `GetKeyRetentionPageAsync` to the last page
  and keep every referenced key; also account for v1 objects, protected orchestration and backups (ADR-035).

#### External file services

[`VersionedFileBlobStore`](../src/Samples/Bsync.Samples.Tasks.Server/VersionedFileBlobStore.cs) is a reference
`IBlobStore` that bridges the blob routes to an external file service. Implement `IVersionedFileService` only if the
service offers immutable versions, a trustworthy SHA-256 and length, idempotent completion under a supplied operation
id, and completion lookup. The adapter persists the operation id before `PutAsync` and calls `FindCompletionAsync` on
retry, so a lost reply reuses the same version. Derive authority from domain ownership (a file id or hash is not a
token), acquire service credentials per call, and keep operation ids for your replay horizon.
`VersionedFileBridgeTests` is the contract to adapt.

### Intents and recoverable work

Use an intent when an offline edit is an **action** ("complete", "approve", "send invoice") that must run exactly once
on the server. Use `RecoverableWorkflow` when local work spans several independently idempotent steps.

#### Modelling actions as intents

An intent is a document in its own collection; replaying state is harmless, replaying an action is not.

| Member | Set by | Meaning |
|---|---|---|
| `Id` | client | New per intent (GUID v7), never reused, so intents never coalesce. |
| `Kind`, target id, payload | client | What to do, to what. |
| `TaskRevision` | client | Target revision the user saw; `null` means "whatever it is now". |
| `State`, `Code`, `ExecutedBy` | **server** | `pending`, `executed` or `rejected`, a stable code, who executed it. |

1. **Create, never edit.** Later writes to an intent are rejected (`intent-immutable`).
2. **Execute in the accepting transaction.** The intents write handler changes the application tables and publishes
   the changed target (`ISyncPublisher.UpsertAsync(scope, document, transaction)`) in one transaction (ADR-014).
3. **Record command identity with the domain change**, not only in transport receipts, so a new operation id cannot
   repeat a command; never purge those identities with receipts.
4. **Acceptance is not execution.** An intent that cannot run is still accepted, with `State = rejected` and a code.
5. **Take identity from `SyncWriteContext.Caller`**, never from the payload.
6. **Wait for missing targets** with `SyncWriteDecision.RetryLater(PushErrorCodes.DependencyMissing)`.
7. **Sync targets first**: with a `SyncCoordinator`, declare the intents collection `DependsOn` the targets.

Show both the sync status (`GetItemStatusAsync`) and the execution state on the document. Reference:
[`TaskIntent`](../src/Samples/Bsync.Samples.Tasks.Shared/TaskIntent.cs) and
[`IntentWriteHandler`](../src/Samples/Bsync.Samples.Tasks.Server/IntentWriteHandler.cs).

`IntentRequest` and `IntentDefinition<TPayload>` give you a reusable, versioned request shape (JSON object payloads up
to 64 KiB, at most 64 prerequisite references):

```csharp
public sealed record AcknowledgePayload(string Revision);
[JsonSerializable(typeof(AcknowledgePayload))]
[JsonSerializable(typeof(IntentRequest))]
public partial class ActionJson : JsonSerializerContext;

var acknowledge = new IntentDefinition<AcknowledgePayload>(
    "acknowledge-revision", 1, ActionJson.Default.AcknowledgePayload);
var request = acknowledge.Create(Guid.CreateVersion7().ToString(), "document-7", "r5", new("r5"));
// Embed request in your replicated action document (Id = request.CommandId) and save it once.
// Server: match a registered definition, derive actor and scope from the authenticated context,
// and recheck permissions and the target revision inside the execution transaction.
```

Delivery uses the ordinary pending-operation queue. `IntentExecutionResult` is server-owned (overwrite submitted
results); `IntentStatus` keeps transport status and execution result separate, and a null result means unknown. To
cancel, send another command referencing the original. The contracts do not deduplicate execution; that is your
server's command ledger (a generic one is planned, C7).

#### External effects: the outbox recipe

When an accepted command must call an external service, use the optional SQL Server
[`ExternalEffectOutbox`](../src/Bsync/Bsync.Server.SqlServer/Workflows/ExternalEffectOutbox.cs):

- `EnqueueAsync` in the same transaction as the domain change, command identity and queued result. Include your
  environment namespace in the scope so environments never share receiver idempotency keys.
- `DrainAsync` claims a bounded batch and calls the receiver with a stable idempotency key outside any transaction.
  Without receiver deduplication, delivery is at least once. The sender must recheck current permissions and take
  the actor from the command ledger.
- Claims last 30 seconds; five failures quarantine a row until you requeue it explicitly. Never delete completed
  identities. Install the table with `InitializeAsync` and register your own host worker.

The `DrainAsync(IExternalEffectReceiver, ...)` overload (`ExternalEffectDelivery`) takes declared receiver
capabilities and decides on replay:

| Receiver observation | Next action |
|---|---|
| Confirmed success or rejection | Publish that result and acknowledge |
| Pending | Keep; look up again later |
| Definitively not executed | Send again after current authorization |
| Unknown, receiver deduplicates | Retry under the same key |
| Unknown, no deduplication | `ReconciliationRequired`; never resent automatically |
| Credentials unavailable | Keep queued without using the poison-attempt budget |

#### Recoverable multi-step work

Prefer one aggregate document when an edit needs one atomic local commit. For work spanning collections and blobs,
`RecoverableWorkflow` records a versioned step definition and progress before each step:

```csharp
var workflow = new RecoverableWorkflow(accountMetadata, account);
await workflow.RunAsync(workId, "attachment-publication/1", steps,
    TimeSpan.FromSeconds(20), cancellationToken);
```

- Each step gets a stable operation id and must deduplicate it: a crash after the effect, or a step returning after
  its deadline, repeats the step. Completed steps are skipped; permanent failures wait for `RetryFailedAsync`. There is
  no automatic compensation.
- Keep old definitions registered until their work completes; changing an existing workflow's version or step order is
  refused. Store step inputs in durable documents keyed by workflow id.
- Do not use it as a document outbox or a server command ledger.

### Accounts, local unlock and maintenance

Use this when your app supports several accounts, encrypts local data with keys that may be unavailable, or needs
periodic cleanup of caches and pins.

#### Account composition and local keys

`AccountComposition` registers an account's resources and opens fresh foreground or headless scopes over the durable
catalog and physical locks; register data resources before keys. Deletion takes exclusive ownership: register cache,
orchestration, scheduler and key callbacks with the coordinator, remove keys last, and make every callback safe to
replay. Lock and upgrade contracts: ADR-027.

`ILocalKeyProvider.AcquireAsync` retrieves an existing `LocalKeyReference` (account, key id, version) and returns
`Available`, `Locked` or `Unavailable`; it never provisions or replaces a key.

- In an `OpenReplica` factory, acquire the exact reference stored with the resource, use `access.Key` only to open,
  then dispose. A locked key throws `ReplicaKeyUnavailableException`, which the coordinator reports without deleting
  data.
- Network credentials are separate (`CredentialRenewal`). The server enforces revocation on reconnect; it cannot erase
  content already available offline.
- **Provision** in the foreground under exclusive setup ownership: generate 32 random bytes, durably record the key
  reference, then store the key. Never overwrite an entry; a missing key is not permission to create a new database.

**MAUI:** the sample's [`ProtectedLocalKeys`](../src/Samples/Bsync.Samples.Hybrid.Maui/ProtectedLocalKeys.cs) reads an
exact key from `ISecureStorage` (stored under `ProtectedLocalKeys.StorageName(reference)`). Do not use the older
`SecureReplicaKeys.GetOrCreateAsync` to reopen existing encrypted storage. Locked-device behavior is unverified.

**Browser:** the Notes PWA's *Offline unlock example* opens an encrypted notebook with a user-held 32-byte recovery
key; a wrong key keeps the existing database. Encryption covers persisted document payloads only (ids, timestamps,
indexes, orchestration metadata and blobs stay readable), and code running in the origin can read whatever is
unlocked. The IndexedDB store still accepts unprefixed plaintext records and has no key rotation (A8). Closed-tab sync
is unsupported (E5).

#### Account maintenance

`AccountMaintenance` runs short, exclusive windows after foreground and background scopes are closed, yielding to
urgent work and checkpointing replay-safe pages:

```csharp
var runner = new AccountMaintenance(accounts, externalCatalog, timeProvider);
AccountMaintenanceStep[] steps =
[
    new("package-pins", "1", (scope, page, ct) => packages.ReconcilePinsAsync(scope, page, ct)),
    new("transfer-pins", "1", (scope, page, ct) => transfers.ReconcilePinsAsync(scope, page, ct)),
    new("cache-owners", "1", async (scope, page, ct) =>
        (await cache.ReconcileBuiltInOwnersAsync(scope, page, ct)).Progress),
];
var result = await runner.MaintainAsync(account, "cache-maintenance-v1", steps,
    TimeSpan.FromSeconds(2), hasUrgentWork,
    maximumItems: 64, maximumBytes: 64L * 1024 * 1024,
    dryRun: false, cancellationToken: stopping);
```

| Outcome | Meaning |
|---|---|
| `Completed` / `RemainingWork` | Finished / call again later (progress is checkpointed). |
| `Busy` | A live account user owns resources. |
| `UrgentWork` | `hasUrgentWork` asked to defer (base it on durable work). |
| `RequiresLargerBudget` | The next unit needs larger item/byte allowances; more time will not help. |
| `BudgetExpired` / `Failed` | Work preserved / a step failed. Caller cancellation throws. |

`dryRun: true` inspects a page without changing anything. Custom steps must be idempotent and honor the limits;
never register an unbounded legacy API or a network operation as a step. Changing step versions or order needs a new
plan name. Monitor `bsync.maintenance.runs{bsync.outcome}`. Design: ADR-029.

### MAUI and native background

Use the MAUI recipe for lifecycle-aware sync in a MAUI app, and the native background samples when sync or transfers
must continue in the background. **Native background execution has not been verified on physical Android or Apple
devices; treat it as sample code** (E4, E6, R7).

#### .NET MAUI recipe

Bsync ships no MAUI package. Copy [`MauiSync.cs`](../src/Samples/Bsync.Samples.Hybrid.Maui/MauiSync.cs) into your app.

| Piece | What it does |
|---|---|
| `UseMauiLifecycle()` / `MauiSync.AttachAsync(session)` | Sync when internet returns, pause when windows stop, resume and sync when they return. |
| `MauiSync.RunInBackgroundAsync(session, account, budget)` | One budgeted sync for a WorkManager or BGTaskScheduler task; report `SyncResult.HasRemainingWork` to the scheduler. |
| `ProtectedLocalKeys.AcquireAsync(reference)` | Reads an existing versioned key (see above). |

```csharp
builder.Services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note> { /* ... */ }.UseMauiLifecycle());
using var key = await new ProtectedLocalKeys(SecureStorage.Default).AcquireAsync(keyReference);
var result = await MauiSync.RunInBackgroundAsync(session, account, TimeSpan.FromSeconds(25));
```

Connectivity is a hint; the session still reconciles on its interval. Only the Windows MAUI build is tested.

#### Background dispatch

`BackgroundWorkDispatcher.RequestAsync` persists a request before calling an `IBackgroundWorkScheduler`; `Scheduled`
means a host accepted it, not that it ran. OS workers and the foreground fallback both call
`RunAsync(account, budget, execute)`, whose callback opens a fresh account scope, gets keys and credentials and does
bounded work. An execution lease and generation fence stop competing or stale runners.

The unpackaged samples [`Bsync.Samples.NativeBackground`](../src/Samples/Bsync.Samples.NativeBackground),
[`Bsync.Samples.Background.Android`](../src/Samples/Bsync.Samples.Background.Android) and
[`Bsync.Samples.Background.Apple`](../src/Samples/Bsync.Samples.Background.Apple) build with
`-p:NativeBackgroundTarget=net10.0-android` or `net10.0-ios` on an equipped host, or via the manually dispatched
`.github/workflows/native-background.yml`. At bootstrap, including headless relaunch, configure
`NativeBackgroundRuntime.Current` with:

- an account resolver that reads the durable catalog, refuses deleted or signed-out accounts, takes the account
  lifetime lock and opens a **fresh** scope with existing keys (never create a key headlessly);
- a bounded enumeration (at most 100) of pending accounts from durable state.

Persist work before requesting the dispatcher, and call `runtime.ReconcileAsync(TimeSpan.FromSeconds(25), stopToken)`
on every foreground/resume: OS delivery is never guaranteed.

**Android:** `AndroidBackgroundScheduler(applicationContext)` schedules unique work `bsync/{Account.StorageKey}` with
constraints and backoff. Configure the runtime in `Application.OnCreate`, keep the worker's Java name across upgrades,
allow the WorkManager initializer (never initialize twice), declare `INTERNET` and `ACCESS_NETWORK_STATE`, and call
`scheduler.CancelAsync(account)` during account deletion.

**iOS/iPadOS:** list your identifier in `BGTaskSchedulerPermittedIdentifiers`, add `processing` to
`UIBackgroundModes`, and register one `IosBackgroundScheduler(identifier)` before launch completes (including headless
launches). `EarliestBeginDate` is a hint. Locked-key availability depends on your keychain policy; return
KeyUnavailable and keep work, never prompt headlessly.

#### iOS background downloads (opt-in)

`IosBackgroundDownloads` with a per-account `BackgroundDownloadInbox` downloads large files through a background
URLSession. Background uploads are not supported.

1. On **every** launch, construct one inbox and one `IosBackgroundDownloads` per active account from the durable catalog.
2. `inbox.PrepareAsync(hash, size, pinOwner)`, get a presigned GET ticket from the server, then
   `downloads.ResumeAsync(id, ticket)`. Never give the OS session bearer tokens or cookies.
3. In `HandleEventsForBackgroundUrl`, route the exact identifier and call `downloads.HandleEvents(completionHandler)`.
4. On launch or resume, `inbox.DrainAsync(openFreshAccount, token)` verifies and imports; failures keep the spool.
5. Retry pending receipts from `inbox.ListAsync()` with fresh tickets; `ForgetAsync` completed ones.
6. Before deleting an account, await `downloads.InvalidateAsync()`, then delete its catalog entry, spool, cache and keys.

**The spool holds plaintext.** Set file protection and backup exclusion for spool and catalog, and do not enable this
where plaintext OS temporary files are forbidden.

#### Qualifying on devices

Before claiming device support, run OS-delivered jobs, kill and relaunch, expiry mid-upload, foreground overlap,
locked keys, revoked credentials, connectivity changes and long postponement, asserting that work stays pending after
every deferral; record simulator and hardware results separately. `python scripts/check_native_bindings.py android|ios`
only compiles the platform sources against reference assemblies.

---

## Part 2 — Operating Bsync

### Observability

Use this to wire Bsync's traces, metrics and logs into your monitoring, choose alerts, and collect a redacted
diagnostic report from a device.

Bsync uses `ActivitySource` and `Meter`; the Blazor session and ASP.NET Core endpoints log through `ILogger`. No tag
carries document contents, document or account ids, hashes or endpoints.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SyncDiagnostics.SourceName))                         // "Bsync"
    .WithMetrics(m => m.AddMeter(SyncDiagnostics.SourceName, SyncEndpoints.MeterName)); // + "Bsync.Server"
```

Name each engine with `SyncOptions<T>.DiagnosticsName` (low cardinality). In-process OpenTelemetry export is tested;
export to a networked OTLP collector and browser-side export are not verified.

#### Client (`Bsync` meter)

| Instrument | Tags | Meaning |
|---|---|---|
| `bsync.push.operations` | `bsync.name`, `bsync.outcome` (`accepted`, `conflict`, `rejected`, `retry-later`, `missing`), `bsync.duplicate`, `error.type` | Operations sent and the answer. |
| `bsync.pull.changes` | `bsync.name` | Server changes applied. |
| `bsync.conflicts` | `bsync.name`, `bsync.decision` (`use-master`, `use-resolved`, `keep-fork`, `defer`) | Conflict decisions. |
| `bsync.resets` | `bsync.name`, `bsync.reason` (`epoch`, `scope-changed`, `expired`) | Replica resets. |
| `bsync.run.duration` (s) | `bsync.name`, `bsync.operation`, `bsync.result`, `error.type` | Replication run duration. |
| `bsync.queue.depth`, `bsync.queue.oldest_age` (s) | `bsync.name` | Unconfirmed local changes and the oldest one's age. |
| `bsync.issues` | `bsync.name`, `bsync.issue` (`conflict`, `rejected`, `blocked`) | Documents waiting for a person. |
| `bsync.blob.bytes`, `bsync.blob.transfers` | `bsync.direction`, `bsync.result` (`complete`, `failed`, `corrupt`) | Attachment traffic; `corrupt` = hash mismatch. |
| `bsync.query.materialized`, `bsync.query.returned` | `bsync.query.mode` | Local query cost. |

Spans `bsync.sync`, `bsync.pull` and `bsync.push` carry counts and `result`; failures set error status and
`error.type`, and resets add a `bsync.reset` event. Session logs (category `Bsync.SyncSession`): `SyncStateChanged`
(1), `SyncProtocolError` (2) and `SyncUnexpectedError` (3; the session reports `AttentionRequired`, keeps work and
retries). Override the logger with `SyncSessionOptions.Logger`.

#### Server (`Bsync.Server` meter and log category)

| Instrument | Tags | Meaning |
|---|---|---|
| `bsync.server.requests` | `bsync.collection`, `bsync.endpoint` (`pull`, `push`, `hints`), `bsync.result` | Every protocol request. |
| `bsync.server.push.operations` | `bsync.collection`, `bsync.outcome`, `bsync.duplicate` | Operations decided. |
| `bsync.server.blob.bytes`, `bsync.server.blob.requests` | `bsync.direction`, `bsync.endpoint`, `bsync.result` | Blob traffic through the server (presigned traffic excluded). |

Logs: `SyncRequestRefused` (1; Information for 4xx, Warning for 5xx) and `SyncAuthorityFailed` (2, Error; the client
gets a retryable `503 unavailable`). Server tracing, latency and lock-wait metrics are planned (O1).

#### Offline metrics

Create one `OfflineMetrics` per account metadata store per process, call `RefreshAsync` periodically with a record
limit, and dispose on account close. Callbacks read cached aggregates only.

| Metric | Meaning |
|---|---|
| `bsync.transfer.backlog`, `bsync.transfer.pending_expected_bytes` | Non-terminal jobs (including ones needing intervention) and their full sizes. |
| `bsync.transfer.oldest_known_age`, `bsync.transfer.unknown_age` | Oldest pending job; count without a timestamp. |
| `bsync.cache.reserved_bytes`, `bsync.cache.pinned_reserved_bytes`, `bsync.cache.capacity_refusals` | Reservations and hard-budget refusals. |
| `bsync.blob.verified_bytes`, `bsync.blob.repair_queued`, `bsync.blob.repairs` | Verification and repair activity. |
| `bsync.package.last_verified{bsync.readiness}` | Last persisted package verifications. |
| `bsync.background.pending`, `bsync.background.last_outcome` | Durable background requests. |
| `bsync.offline.snapshot.oldest_age`, `bsync.offline.snapshot.partial` | Staleness and truncation of the snapshot itself. |
| `bsync.maintenance.runs{bsync.outcome}` | Account maintenance outcomes. |

Collect snapshot age/partial with the backlog metrics; counts are approximate. Never sum page histograms
(`bsync.cache.protected_page_bytes`) as cache size. `bsync.execution.stops{bsync.reason,bsync.operation}` counts
cancellation (`caller_canceled`), deadline (`budget_expired`) and `lease_lost` signals per budgeted operation; they nest,
so filter by operation and never sum them (ADR-030 to ADR-034).

#### Replica audit and diagnostic export

- With `SyncEndpointOptions.ReplicaAudit` (`InMemorySyncReplicaAudit` or `SqlServerReplicaAudit`, table
  `bsync.replica_acknowledgements`), the server records which checkpoint each replica reached; `GetAsync(replica)`
  proves what a device held and when. Purge by age.
- `await OfflineDiagnosticExport.ExportJsonAsync(accountMetadata, recordLimit: 1000)` returns allowlisted aggregate
  counts only (transfers, workflows, background, cache, read models), never identities, endpoints, payloads or error
  text. The scan is bounded (1–10,000 records, `Truncated` when partial) and live, not a snapshot. Aggregate volumes
  are still application data.

#### What to alert on

- `bsync.queue.oldest_age` growing: uploads are not getting through.
- `bsync.push.operations{outcome="rejected"}` by `error.type` (validation, permission, `clock-skew`).
- `bsync.conflicts{decision="defer"}` and `bsync.issues`: decisions waiting for users.
- `bsync.resets` by reason: expected after a restore or permission change, suspicious otherwise.
- `bsync.server.requests{result="unavailable"}` with `SyncAuthorityFailed` logs.
- `bsync.blob.transfers{result="corrupt"}` or `bsync.server.blob.requests{result="blob-hash-mismatch"}`.
- `bsync.transfer.backlog` and `bsync.cache.capacity_refusals` rising (caches filling, B4).

### Disaster recovery and stuck replicas

Use this runbook when a server database is restored, retention needs planning, a device store is damaged, or changes
do not reach the server. Design: ADR-013 in [architecture](architecture.md).

#### 1. Restoring the server from a backup

The authority must start a new **epoch** (older checkpoints get `reset-required`, reason `epoch`), never reuse a
version (continue above a **version floor**), and keep the receipts in the backup. Record each feed head
(`GetHighestVersionAsync(scope)`, or `bsync.feeds.sequence` on SQL Server) at least as often as you take backups.

1. Stop the server processes (or enter maintenance mode).
2. Restore the application database (`RESTORE DATABASE`, `pg_restore` or point-in-time); the `bsync` schema and your
   tables return to the same point.
3. Before serving requests, call once `await authority.BeginNewEpochAsync(floor);` with `floor` above every recorded
   version plus a margin. (The in-memory authority takes `RestoreFrom` and `VersionFloor` options.)
4. Start the processes. Replicas reset on their next sync and upload their pending work again.
5. Watch `bsync.resets{reason="epoch"}` and `bsync.server.push.operations{outcome="Conflict"}` for a few hours.

Replicas keep unsynchronized edits and kept conflicts; a replayed upload is answered from its receipt. Clean copies of
documents created after the backup are hidden as `MissingAfterReset`, not deleted. The floor also applies to feeds
first created after the restore (on PostgreSQL from 0.6.0, stored as `version_floor` in `bs_meta`; earlier PostgreSQL
versions restarted such feeds at version 1). Scripted restore drills are planned (O6).

#### 2. Retention of tombstones and receipts

- `PurgeTombstones(throughVersion)` raises the retention horizon; older replicas reset (`expired`), and edits based on
  purged documents are rejected with `base-expired`.
- `PurgeReceipts(throughVersion)`: keep receipts longer than the longest offline period with unsent work, otherwise a
  resend that had succeeded comes back as a conflict.
- `AddSyncRetention` purges on a schedule (`MaxOfflineHorizon` default 45 days; `ReceiptHorizon` never shorter). Its
  samples live in memory, so after a restart nothing is purged for one horizon; run it in one process (P4, C10).
  Without it, schedule `PurgeTombstonesAsync`/`PurgeReceiptsAsync` per scope yourself.

#### 3. Access changes

When a user's visible set changes, `ScopeFingerprint` changes and the next pull resets with `scope-changed`; the
replica removes documents it may no longer see from the device only. Records with local changes or kept conflicts are
never removed. With read membership (ADR-015), removals arrive as ordinary changes.

#### 4. A damaged SQLite database

Symptoms: `SqliteException` 11 (`SQLITE_CORRUPT`) or 26 (`SQLITE_NOTADB`).

1. Stop the engine and dispose the store.
2. `var problems = await SqliteStoreRecovery.CheckAsync(path);` (empty means sound).
3. `var report = await SqliteStoreRecovery.RebuildAsync(path);` (`DamagedCopy`, `SalvagedRecords`,
   `DiscardedRecords`, `ReadError`, `Complete`).
4. Reopen (a new replica id, so derive the clock node from the new incarnation) and sync.

The rebuild keeps readable records with local work and their operation ids, kept conflicts, rejections and the clock
high-water mark; clean data is pulled again. Rows on unreadable pages are lost and cannot be counted. The damaged copy
holds user data: protect it and delete it when support is done.

#### 5. Lost browser storage

If the browser evicts IndexedDB or the user clears site data, the replica repopulates from the server, but
**unsynchronized work is lost**. Call `IndexedDbLocalStore.RequestPersistenceAsync()` to make eviction less likely.

#### 6. A stuck queue

| Situation | How to see it | What to do |
|---|---|---|
| Rejected change | `GetRejectedAsync`, `SyncItemState.Rejected` | Fix the cause, then `RetryRejectedAsync` / `RetryAsync`, or `RevertAsync`. |
| Kept conflict | `GetConflictsAsync`, `SyncItemState.Conflicted` | `ResolveConflictAsync` (e.g. `ThreeWayMerge`) or `DiscardConflictAsync`. |
| Transient failures | `SyncStatus.State` `Offline` | Nothing; the session backs off and honors `Retry-After`. |
| `upgrade-required` | `SyncStatus.State` `AttentionRequired` | Ship the app update; local work is kept. |
| Held by `ReadyToPush` | Item stays `Pending` | Check transfer jobs and credentials. |
| Moving work to a new store | `ExportLocalChangesAsync` | `ImportLocalChangesAsync`, then sync. |

A deterministic server failure (such as a throwing validator) currently repeats as a retryable 503; look for
repeated `SyncAuthorityFailed` logs (A3).

#### 7. Schema roll-outs

- **Store schema** upgrades in place on first open and keeps pending work; a database from a newer app is refused
  untouched (`SqliteStoreSchemaException`, IndexedDB `outdated`). Never share one database between app versions.
- **Domain schema:** deploy a server with `SupportedSchemas = { "notes-v1", "notes-v2" }`, ship the app with
  `SchemaId = "notes-v2"`, and drop `notes-v1` when old clients are gone (they stop with `upgrade-required`).
- Keep documents JSON-compatible (`[JsonPropertyName]`, `[JsonExtensionData]`, `DocumentUpgrade.TryTake` in
  `IJsonOnDeserialized`) and drain upload queues before changing a shape.

#### 8. Capacity (SQL Server)

Writes to one feed (usually a tenant) are serialized by the feed lock (ADR-005), so plan for the busiest feed; pulls
take no feed lock. Storage is one row per document, a receipt per accepted operation and tombstones until purged, plus
one `bsync.document_access` row per principal per document with read membership. Watch page latch waits on
`bsync.feeds` when one feed takes most writes. Measured figures are in the benchmark section of this guide.

#### 9. Production checklist

- Backups and a **tested** restore that starts a new epoch with a version floor.
- Receipt retention longer than the longest offline period; a tombstone retention users understand.
- Alerts on rejections, conflicts and `bsync.server.requests{result="unavailable"}`.
- Scopes resolved from authenticated claims, `AllowAnonymous` off and no `SharedScope` warning in the startup log (A1).
- A scheduled blob garbage-collection job if you use attachments.

### Release process

Use this when preparing or validating a release. Releases come only from a clean checkout at an immutable tag, and
publishing is always a separate, explicitly authorized step.

1. Every user-visible change adds a `CHANGELOG.md` line under *Unreleased*; breaking changes also get a compatibility
   note.
2. Pick a never-published version. Set `VersionPrefix` in `src/Directory.Build.props` and the changelog heading
   `## <version> - YYYY-MM-DD`.
3. Commit, complete the qualification runs (including package consumers), and create a protected `v<version>` tag.
   Never reuse or move a tag.
4. Run `.github/workflows/release.yml` on the tag. It checks clean source and version metadata, tests core and
   encrypted SQLite, packs the six packages (`Bsync`, `Bsync.Blazor`, `Bsync.Server.AspNetCore`,
   `Bsync.Server.PostgreSql`, `Bsync.Server.SqlServer`, `Bsync.Storage.Sqlite`), verifies provenance and runs
   package-only conformance and web smoke tests.
5. Review the `release-candidate` (packages, symbols, `provenance.json`) and `release-test-evidence` artifacts.
6. Publish to NuGet manually (ADR-012); no workflow publishes. Nightly previews (`nightly.yml`,
   `X.Y.Z-preview.YYYYMMDD.N`) are build artifacts only.

The ten-consecutive-green-runs gate on Windows, Linux and macOS is not yet met (R1).

#### Provenance rules

- **Never repack changed bytes under a published version**; use a new version or preview suffix.
- `scripts/release.py manifest` verifies all six `.nupkg`/`.snupkg` pairs, rejects mixed feeds and records whether
  the tree was dirty.
- `Bsync.PackageVerifier` checks each DLL/PDB pair, Source Link repository/commit and embedded-source checksums, and
  writes `symbols-verification.json`. Release candidates also pass `--verify-sources`, which compares checksums with
  sources downloaded from the exact commit and fails for a dirty tree. Never normalize line endings to hide a mismatch.
- The 0.2.0 packages point at an earlier commit than their tag; this is unresolved (R2).

#### Validating a candidate locally

Use an empty `artifacts/packages` feed and a new suffix for every pack, including reruns (cached versions are not
re-read).

```powershell
python -m unittest discover -s scripts -p 'test_*.py' -v
# Repeat for each of the six packages listed in scripts/release.py:
dotnet pack src/Bsync/Bsync -c Release -o artifacts/packages -p:VersionSuffix=local.1 -p:ContinuousIntegrationBuild=true
python scripts/release.py manifest --version 0.6.0-local.1
$packageCommit = git rev-parse HEAD
dotnet run --project src/Tools/Bsync.PackageVerifier -c Release -- artifacts/packages $packageCommit
python scripts/verify_symbols_test.py artifacts/packages -v
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.6.0-local.1
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release -p:BsyncPackageVersion=0.6.0-local.1 -o artifacts/consumer-web
dotnet artifacts/consumer-web/Bsync.PackageConsumer.Web.dll --smoke
python scripts/check_http_compatibility.py --version 0.6.0-local.1 --packages artifacts/packages
```

`check_http_compatibility.py` runs the candidate against published 0.2.0 in both directions, including ticket
downloads (fixtures for 0.4.0 and 0.5.0 are planned, R3). For a package-only browser run, publish
`src/Tests/PackageConsumers/Bsync.PackageConsumer.Browser` with the same version, set `BSYNC_BROWSER_HOST` to its
`wwwroot`, and run `Bsync.Tests.Browser` with `--no-build --filter FullyQualifiedName~IndexedDbBrowserTests`.

#### Dependency policy

- NuGet auditing stays on; NU1900–NU1905 fail the build, including audit-service failures (retry, never suppress).
- Minimums: Microsoft DI/logging, Blazor/JS interop and SQLite Core 10.0.12; SQL Client 7.1.1;
  `System.Security.Cryptography.Pkcs` 9.0.21 for `Bsync.Server.SqlServer` (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).
  With central package management, do not pin lower transitive versions.
- Plain SQLite and SQLCipher bundles are mutually exclusive in a process; qualifying a maintained distribution per
  native target is open (R4).

---

## Part 3 — Versions, support and performance

### Upgrading between versions

Published versions are 0.1.0, 0.2.0, 0.4.0 and 0.5.0 (six packages each on NuGet). No 0.3.0 was released, only
previews, so go from 0.2.0 straight to 0.4.0. The source targets unreleased 0.6.0. Before 1.0, a minor release can break
the API, behaviour, store schemas or optional wire features; a patch release never does. Full change lists:
[CHANGELOG.md](../CHANGELOG.md). Item ids such as R3 or P1 refer to the [implementation plan](implementation-plan.md).

**General rules for every upgrade**

- Store schema upgrades only go forward. They run in place on first open and keep pending work; afterwards older
  binaries refuse the store (SQLite throws a schema exception, IndexedDB reports `outdated`, authorities refuse the newer
  schema). Upgrade every process, tab and server instance sharing a database together. Never downgrade versions by hand.
- Protocol additions are optional members advertised through `features`. Older peers ignore them and newer replicas fall
  back when a server lacks a feature, so an older client keeps working against a newer server. Exceptions are noted below.
- CI runs fixtures captured from the published 0.1.0 and 0.2.0 releases: SQLite and IndexedDB databases, plus
  0.2.0 client/server HTTP processes. 0.5.0 added upgrade/downgrade fixtures from published 0.4.0. Old-binary fixtures
  for 0.5.0 → 0.6.0 are still missing (R3).

#### Store and wire versions at a glance

| Store | 0.1.0 | 0.2.0 | 0.4.0 | 0.5.0 | 0.6.0 (unreleased) |
|---|---|---|---|---|---|
| SQLite document schema | 3 | 5 (from 3 in place) | 5 | 5 | 5 |
| IndexedDB document schema | 3 | 4 (from 3 in place) | 4 | 4 | 4 |
| Orchestration metadata (SQLite/IndexedDB) | – | – | schema 1 (separate from the document schema) | feature header 2, set explicitly when protected payloads or v2 blobs are enabled | feature version 3, set on first use of package sets |
| Browser blob database | – | 1 or 2 | 1 or 2 | 1 or 2 | 3 (from 1 or 2 in place) |
| PostgreSQL authority | 1 | 2 (from 1 in place) | 2 | 2 | 2 |
| SQL Server authority | – | 3 (new; dev schemas 1/2 upgrade) | 3 | 3 | 3; Change Tracking capture migrates to 2 |
| Sync wire protocol | v1 | v1 + optional features | v1 + opt-in download tickets | v1 | v1 + optional multipart blob routes |

#### 0.1.0 → 0.2.0

The published 0.2.0 archives name an earlier commit than the source baseline, so their exact contents are unproven (R2).

- **`Bsync.Testing` merged into `Bsync`.** No 0.2.0 `Bsync.Testing` package exists. Replace the package reference with
  `Bsync`. The `Bsync.Testing` namespace and its types are unchanged.
- **`Bsync.Storage.Sqlite` no longer ships a native SQLite library** (it depends on `Microsoft.Data.Sqlite.Core`). Add
  exactly one of `SQLitePCLRaw.bundle_e_sqlite3` (plain) or `SQLitePCLRaw.bundle_e_sqlcipher` (encrypted); without one,
  opening a store throws `InvalidOperationException`. EF Core SQLite or `Microsoft.Data.Sqlite` already bring the plain one.
- **Validators take the caller first.** `InMemorySyncServerOptions<T>.Validator` and
  `PostgreSqlSyncAuthorityOptions<T>.Validator` change from `(op, current) => ...` to `(caller, op, current) => ...`.
- **`RenewCredentials` returns `Task<CredentialRenewal>`.** Map `true` to `Renewed`. Map `false` to `SignInRequired`
  when the user must act, or to `Offline` when the identity provider was unreachable. An exception counts as `Offline`.
- **Status:** a session reports `AttentionRequired`, never `Synced`, while kept conflicts or parked rejections exist,
  including after a restart. UIs that waited for `Synced` should read `Conflicts`/`Rejected` instead.
- **Behaviour:** oversized pushes are split; a document or group too large on its own is parked with
  `payload-too-large`. `retry-later` backs off exponentially. `Changed`/`Subscribe` no longer fire for timestamp-only
  status updates.
- **Custom stores/transports:** `UpdateAsync` may receive an accepted document the engine did not copy (copy what you
  keep); a transport must not keep or change documents after returning them in a push result. `AddBrowserSyncCollection`
  gained an optional `indexes` parameter (recompile).
- **Schemas:** SQLite 3 → 5, IndexedDB 3 → 4 and PostgreSQL 1 → 2 upgrade in place; 0.1.0 then refuses them. Reload
  old browser tabs (closed as `outdated`). New `Bsync.Server.SqlServer` creates a `bsync` schema at version 3.
- **Wire.** All new protocol members are optional: `limits`, `server-time`, `retention`, `removals`, `hints-multiplex`,
  `pull-batch`, rejection `arguments`, replica id. Older replicas get `reset-required` (`scope-changed`) where a removal
  would be needed, as before. Checkpoint encoding is unchanged.
- **Clock re-stamping:** writes rejected with `clock-skew` are re-stamped from server time. If you stored the `UpdatedAt`
  of a not-yet-uploaded document, set `RestampSkewedWrites = false` or reread it after upload.
- Firefox may ask for persistent storage when the browser recipe opens a replica. `S3BlobStore` and `MauiSync` are
  sample recipes to copy, not packages (S3 needs `AWSSDK.S3`).

#### 0.2.0 → 0.4.0

Mostly additive (accounts, orchestration stores, transfer/cache/package managers, workflows, read models, intents).

- **SQL Server packages** need `System.Security.Cryptography.Pkcs` 9.0.21 or later (security advisories).
- **Direct reads now enforce reader membership.** In all three authorities, `GetAsync`/`ListAsync` apply persisted
  membership whenever `PrincipalKey` is set. Upgrade the server if you use reader membership. Leaving out `CanRead`
  does not bypass membership.
- **Orchestration tables** have their own schema 1; the document `user_version` is unchanged. Handle the new account
  outcomes `CredentialsRequired` and `Migrating`. `StartReplication=false` delays creating the transport.
- **Account resource locks (opt-in).** Every coordinator and worker must share the same lock namespace. Resources
  without `InspectPendingAsync` now count as unknown pending work, and default deletion refuses them. Configure
  inspection, or choose explicit export or discard.
- **Blob providers:** reimport repairs by verified atomic replacement (needs capacity for both copies); custom
  providers must support verified promotion or refuse repair. Custom `IBlobCacheLock` implementations must accept
  `lifetime-` lock names and implement the probe for dead-owner reclamation. Do not let an older app evict managed content.
- **Encrypted blob caches** (optional) migrate explicitly into a separate cache: copy and verify every object, keep the
  source until the host switches, keep old keys until all objects rotate. **Download tickets** are opt-in; an opted-in
  client refuses an older server instead of falling back.
- **Tasks sample and intent recipes.** The sample now scopes stores by server, tenant and user. Old user-only
  databases are not adopted automatically: export pending work under the old account and import it into the verified
  target. Adopting the command-ledger, outbox or external-effect recipes needs your own migrations, a worker and a
  replay/backfill policy.
- **Downgrade:** keep new metadata and cache bytes; there is no automatic downgrade.

#### 0.4.0 → 0.5.0

No document wire or schema change; new APIs are additive. Behaviour changes to check:

- **Stop causes.** Nested operations now report `budget_expired` or `lease_lost` where they used to report
  `caller_canceled`. Diagnostics consumers should accept the new values.
- **Goal waits.** Budgets now include account resolution and opening. A zero budget does no I/O. Check the new
  `SyncGoalResult.HasObservation` before trusting `Pending = 0`; when it is false, the count is unknown.
- **`SyncForAsync`** takes its budget clock from `SyncOptions.TimeProvider`. Cancellation that is not a budget expiry
  now propagates as an exception.
- **`PrepareAsync`/`EnsureUploadsAsync`** apply the budget to the whole operation and can throw
  `OperationCanceledException` before activation. Durable jobs stay resumable.
- Workflow budget expiry returns the last observed (possibly stale) progress; call `GetAsync` for current state.
  Browser `OpenReadAsync` holds a stable snapshot until disposed. Unsupported encrypted schemas throw
  `LocalStoreUnavailableException("outdated")` instead of `InvalidDataException`.
- **Paged cleanup.** If you reopen between passes, persist the continuation from `CollectRetiredPageAsync`. Diagnostics
  consumers must accept new aggregate names.
- **Scrub and key journals.** If you use an explicit payload policy, register the `encrypted-scrub/v1/` and
  `encrypted-generation-key/v1/` namespaces. A legacy generation has no key journal, so keep every key it may have used.
- **Account maintenance:** all participants must share the physical lock namespace; use a new plan identity when cursor
  meaning changes.
- **Protected payloads and chunked v2 blobs (both experimental).** Before enabling either, stop **all** older processes
  and tabs, then call `AccountComposition.UpgradeFeatureStoreAsync` under exclusive ownership. If accounts share one
  database, stop all of them first. Published 0.4.0 refuses feature header 2 on reopen. Third-party stores must
  implement `IOrchestrationFeatureStore` to host these formats.
- **Owned read models.** With the new constructor, dispose snapshots asynchronously before cleanup can reclaim them.
  The legacy namespace keeps its old no-cleanup behaviour. Chunk readers need asynchronous reads and disposal.
- **Downgrade to 0.4.0.** 0.4.0 ignores the new repair and cleanup journals and cannot finish their work. Keep those
  records and bytes for a later upgrade, and never run old and new managers on one cache at the same time.

#### 0.5.0 → 0.6.0 (unreleased)

Apart from the fail-closed endpoints below, the public APIs add to 0.5.0. Everything new is experimental.

- **Sync and blob routes fail closed (A1).** `SyncEndpointOptions.ResolveScope` is now required, so code that relied on
  the old `"default"` scope stops compiling. Resolve the scope from a tenant or user claim, or, only when every caller may
  share one data set, use `ResolveScope = SyncEndpointOptions.SharedScope()` (mapping then logs a warning). Anonymous
  callers now get `401 unauthorized` on every route even without `.RequireAuthorization()`; set `AllowAnonymous = true`
  for public data, demos and tests.

- **SQL Server workflow classes** moved from the Tasks sample into `Bsync.Server.SqlServer.Workflows`. Update the
  namespace and give each receiver a stable `Identity`.
- **Browser blob database 1/2 → 3** (builds a capacity ledger first). Older modules refuse it, so close older tabs
  first. Existing content stays readable. Where a session refuses Blob persistence, objects above 16 MiB cannot complete
  (partials are kept).
- **Package sets:** first use raises the orchestration feature version to 3, which 0.5.0 refuses. Stop older processes
  and tabs first; never drop child-reference records. Schema-2 set roots (optional children) are refused by older readers.
- **Multipart upload routes** are additive. A 501 at initiation falls back to normal uploads; authentication errors,
  404s and conflicting sessions do not. Keep resume metadata across retries. S3 stays a sample recipe, not a package.
- **SQL workflow migrations** run explicitly. Change Tracking capture migrates to version 2. Stop all old capture
  workers first, because old runtimes refuse version 2. Change Tracking needs same-database publication and an external
  restore epoch.

### Support matrix

**Tiers.** **Verified**: tests or a recorded run executed on that target. **Build-only**: compiles or publishes but
was never run. **Unverified**: source may exist, but it was neither built nor run. Unless noted, runs used one
Windows x64 machine with .NET 10 and Playwright 1.63, which bundles Chromium, Firefox and WebKit builds.

| Target / host | Tier | Note |
|---|---|---|
| Windows x64 .NET 10 (engine, HTTP) | Verified | Full core suite, repeated green runs; native AOT and trimmed checks. |
| Linux / macOS .NET 10 | Verified in CI only | Unit-test jobs passed in several runs; CI not yet reliably green (R1). |
| SQLite on Windows | Verified | Conformance, process-kill, recovery, migration from published 0.1.0. No power-loss test. |
| SQLCipher on Windows | Verified | `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 (community build). |
| SQLite / SQLCipher on Linux, macOS, Android, iOS | Unverified | E6, R4. |
| Blazor WASM + IndexedDB: Chromium, Firefox | Verified | Playwright: conformance, multi-tab, encryption, migrations, AOT harness. |
| Blazor WASM + IndexedDB: WebKit (Playwright) | Verified with gaps | PWA offline reload unverified (Playwright limitation); encrypted blobs to 256 MiB (others 1 GiB). |
| Native Safari, mobile browsers | Unverified | Never run. |
| Standalone WASM demo | Build-only | Publishes (also AOT); not run in a browser. |
| Blazor Web App (Server, WASM, Auto, static SSR) | Verified | Published sample in all three engines. |
| WPF Blazor Hybrid | Verified | UI write, offline restart, upload (WebView2). |
| MAUI Blazor Hybrid, Windows | Verified | UI write reaches the server; lifecycle/background helpers untested. |
| MAUI Android | Unverified | Bindings compile against reference assemblies; full build blocked (SDK download 404). |
| MAUI iOS | Unverified | Bindings compile against reference assemblies; no app build or device run. |
| MAUI Mac Catalyst | Unverified | Not built. |
| Native background (WorkManager, BGTaskScheduler, URLSession) | Unverified | Source recipes only; no OS-delivered run (E4, R7). |
| PostgreSQL authority | Verified | 17.6 on Windows, 17 on Linux CI. Cloud PostgreSQL not run. |
| SQL Server authority | Verified | 2025 LocalDB on Windows, 2022 Linux container in CI. Azure SQL not run. |
| SQL Server workflows / Change Tracking | Verified (experimental) | LocalDB, same database, restore and process-kill drills. |
| S3-compatible storage (sample recipe) | Verified locally | SeaweedFS 4.48 on loopback: expiry, refresh, multipart, CORS in three engines. |
| Amazon S3, MinIO, external TLS/IAM | Unverified | R6. |
| ASP.NET Core endpoints / HTTP transport | Verified | `TestServer` and Kestrel on loopback; no real network. |
| OpenTelemetry export | Verified in-process | No OTLP collector over a network. |
| Published packages | Verified (consumers) | Package-only consumers pass; clean tagged provenance open (R2). |

**Never run:**

- Any Android, iOS or Mac Catalyst build or device, OS-delivered background work and locked-device key access.
- Native Safari, mobile browsers and real browser quota pressure; power-loss durability.
- Amazon S3, MinIO, Azure SQL, cloud PostgreSQL, a real network, an OTLP collector.
- Ten consecutive green CI runs (R1) and an independent cryptography review (R5).

### Benchmarks

The benchmarks live in [`src/Tests/Bsync.Benchmarks`](../src/Tests/Bsync.Benchmarks) and use BenchmarkDotNet plus a
few custom modes. The results come from one development machine in short runs. Use them as orders of magnitude and as
a regression baseline, not as device claims.

**Workloads**

- **Local:** durable write, read by id, full query; 50-item ordered, filtered and indexed pages (10k/50k documents).
- **Replication:** reconnect with 10,000 queued writes; a new replica pulling 10,000 and 100,000 documents; three-way merge.
- **Allocations:** bytes per pushed document (client plus in-process server).
- **Authority throughput:** concurrent sessions pushing then pulling on SQL Server or PostgreSQL, shared or separate feeds.
- **Offline probe:** p50/p95/p99, allocations and peak memory for staging, indexed pages, packages, encrypted range
  reads and reconnect (seed 1729).

**How to run** (from the repository root; build in Release first for the `.dll` modes)

```bash
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*LocalWrite*" "*LocalRead*" "*Merge*" --job short
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*Reconnect*"
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*InitialPull*"
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*ScalePull*"
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*LocalQuery*" --job short
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- push-alloc memory        # or sqlite-full
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- offline-probe <absolute-report.json>
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll peak <file.db> 50000
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll throughput sqlserver "<connection to master>" 16 500
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll throughput postgres "<connection to postgres>" 16 500
```

The throughput mode creates a temporary database and drops it afterwards. For release evidence, run on an idle machine
and keep the JSON report, the exact command, the commit, the device and drive, and the runtime version.

**Latest recorded numbers.** Measured on Windows 10 22H2 with an Intel Core Ultra 7 255H, .NET 10.0.12 (x64) and
BenchmarkDotNet 0.15.8. The storage device was not recorded. Documents were about 1 KiB. The authority was in-process,
so no network was involved. SQLite used `synchronous=FULL` unless noted. Numbers from September and October 2026.

| Workload | In-memory | SQLite |
|---|---:|---:|
| `WriteAsync` (commit + queue), mean | 9.3 µs | 850 µs (258 µs with `NORMAL`) |
| `GetAsync` by id, 10,000 docs | 0.88 µs | 29.6 µs |
| `QueryAsync` of all 10,000 docs | 38.8 ms | 57.2 ms |
| Ordered 50-item page, no index, 10k / 50k docs (p95) | 32 ms / 318 ms | 61 ms / 365 ms |
| Same page with a declared index, 10k / 50k docs (p95) | 0.22 ms / 0.35 ms | 0.37 ms / 0.47 ms |
| Reconnect, 10,000 queued writes | 824 ms (643 MB alloc.) | 2.36 s (900 MB alloc.) |
| New replica pulls 10,000 docs | 243 ms | 1.00 s |
| New replica pulls 100,000 docs | 6.8 s (2.43 GB alloc.) | 14.3 s (3.57 GB alloc.) |
| Allocated per pushed document | 14.5 KB | 47.2 KB |
| SQLite file size, 10,000 clean docs | – | 14.5 MB (1,445 B/doc) |
| Peak working set, open SQLite replica and read one page, 10k / 50k docs | – | 63.6 MiB / 154.9 MiB |
| `ThreeWayMerge.Merge`, ~1 KiB doc | 12.6 µs | – |

| Authority throughput, 16 sessions × 500 docs | Push, shared feed | Push, feed per session | Pull, feed per session |
|---|---:|---:|---:|
| SQL Server 2025 LocalDB | ~1,300 ops/s | ~5,300 ops/s | ~33,000 changes/s |
| PostgreSQL 17.6 (default config, same disk) | 813 ops/s | 10,101 ops/s | 41,528 changes/s |

A feed's writes are serialized by its lock, so a shared feed slows as sessions compete; separate feeds (tenants)
commit in parallel and pulls take no lock.

Desktop p95 targets: local write or 50-item page 50 ms; 1 MiB package staging or a 16 MiB encrypted range read
250 ms; reconnecting 1,000 writes 10 s; probe peak memory 1 GiB. Writes and indexed pages meet them easily; an
unindexed ordered page misses at 50,000 documents (and at 10,000 with SQLite). No offline-probe results are recorded yet.

**Known performance gaps**

| Gap | Plan item |
|---|---|
| Shared-feed push throughput falls under contention; the feed lock is held across several round trips per operation | P1 |
| Pull pages are limited by count, not bytes; server memory per pull is not bounded | P2 |
| Hint fan-out wakes every client of a tenant on every commit; the 10,000 SSE clients target is not yet measured | P3 |
| PostgreSQL serializes each document about six times per operation | P6 |
| IndexedDB and browsers have no measurements; interop encodes documents three times | P9, P10 |
| Unusable index keys fall back to loading the whole collection; `CountAsync` deserializes everything | P11, P12 |
| SQLite allocates 47 KB per pushed document, against 14.5 KB in memory | P13 |
| Blazor Server reads the whole scope for each query and commit | P15 |
| Downloads append on every socket read, and v1 re-encryption cost grows quadratically; the cache scans on every write | B1, B2 |
| No phone, tablet or network-latency measurements; per-target budgets, peak memory, write amplification and cancellation latency are not measured | R8, E6 |
