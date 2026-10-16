# Managed transfers and offline packages

These APIs are new source capabilities under qualification. Use one `AccountScope`, orchestration store, blob store
and lock namespace per application/environment/account/organization. Never derive server authorization from this
local identity. Keep the account catalog used for recoverable deletion outside resources being deleted.

Native composition:

```csharp
var account = new AccountScope("catalog", "production", authenticatedAccountId);
await using var metadata = await SqliteOrchestrationStore.OpenAsync(metadataPath, account);
var locks = new FileBlobCacheLock(accountLockDirectory);
var cache = new ManagedBlobCache(new FileBlobCache(accountBlobDirectory), metadata, locks,
    softBudgetBytes: 256L * 1024 * 1024, hardBudgetBytes: 512L * 1024 * 1024);
await cache.ReconcileAsync();
var jobs = new BlobTransferManager(metadata, cache, new HttpBlobTransfer(accountHttpClient));
var packages = new OfflinePackageManager(metadata, cache, jobs, locks, AuthorizeLocalPackageAsync);
```

For browsers, substitute `IndexedDbOrchestrationStore.OpenAsync(js, account)`,
`BrowserBlobStore.OpenAsync(js, account.StorageKey)` and `BrowserBlobCacheLock.OpenAsync(js, account)`.
Web Locks are required for managed eviction. Browser storage can still be evicted by the browser/user; persistent
storage requests are hints and an application must report missing prepared content.

For a pending attachment, call `ImportPinnedAsync(seekableStream, stableWorkOwner)` before saving its reference through
the existing collection. Queue the upload with `EnqueueAsync(hash, size, Upload)`. Use `ReadyToPush` to defer a document
until its required uploads are complete, and independently check blob access/availability in the server write handler.
`DrainAsync(budget, ct)` executes bounded attempts; paused, credential-blocked and failed jobs require host action.
Requeue after repairing the indicated condition. Releasing a completed job's pin does not release a pending document's
separate pin. Cancellation cannot recall an upload already committed remotely.

For packages, the publisher supplies an authorized `OfflinePackageManifest` containing immutable document snapshots
and files. `StageAsync` performs local durable preparation without network requests. `PrepareAsync` resumes staging,
drains downloads and attempts activation. It preserves the prior active revision if required content is missing.
`TryActivateAsync` also supports content imported by another trusted local preparation path.

`HttpBlobTransfer.Progress` reports the current local/server offset, total length and a separate `Verified` flag.
The offset includes prior attempts and can regress after repair; bytes at the expected length are not necessarily
verified yet. Observer exceptions are isolated. Notifications are transient UI hints: durable partial offsets and
transfer job states remain authoritative after restart. Do not put content hashes from these events into metric tags.

```csharp
await packages.StageAsync(manifest, ct);
var status = await packages.PrepareAsync(manifest.PackageId, TimeSpan.FromSeconds(20), ct);
// Later, including after a cold offline restart:
await using var snapshot = await packages.OpenActiveAsync(manifest.PackageId, ct);
await using var content = await snapshot.OpenAsync("document", ct);
```

Use `await using` for reader streams, snapshots and locks. Retire old revisions explicitly after replacing their active
and latest pointers. Do not remove account files while any account task or UI writer is running. Register cache,
orchestration, scheduler and key deletion callbacks with the account coordinator; each callback must tolerate replay
after a crash. Preserve/export/discard applies to replicated pending work, not permission to delete another account.

`ReconcileAsync` reclaims new reader/snapshot pins only after their physical lifetime locks become available; live
readers remain protected. Repeat after OS cleanup if needed. Use `await using`: synchronous stream disposal keeps a
conservative pin/lock until asynchronous disposal or process/tab termination. Custom/legacy owners require explicit
durable-work reconciliation; never expire pins by age. Reserve `reader-v2/` and `snapshot-v2/` for library use.
Native/browser inventory includes abandoned partial/staging payloads; unrecognized content consumes protected capacity.
Browser quota estimates/persistence requests are hints, separate from payload accounting. See
[ADR-021](../architecture/adr-021-managed-content.md) for the exact boundaries.

`GetStatusAsync` verifies actual content and persists a schema-1 observation. For frequent UI refreshes,
`GetLastVerificationAsync` reads bounded metadata, rechecks authorization and returns that observation only while
active/latest pointers still match. Display its `VerifiedUtc` age: this is historical verification, not a new guarantee
against external eviction or corruption. Opening snapshots/items still verifies content. Reused direct downloads now
also hash-check existing complete bytes before reporting verified progress; corrupt bytes remain for explicit repair.

Reimport known correct bytes through `ManagedBlobCache.ImportAsync` to repair an existing corrupt object. Existing
bytes are hash-checked; a same-size file is no longer assumed valid. Repair stages and verifies replacement bytes,
then promotes them atomically (native file replacement or the browser transaction), retaining every logical pin.
Capacity must accommodate both old and staged bytes. A refusal, cancellation or failed verification leaves the old
object available for diagnosis and keeps pending references; retry with enough capacity and a trustworthy source.
Already-open native handles keep the prior bytes. A snapshot cannot turn previously read corrupt bytes into good data;
open/verify again after repair. Automatic network repair and a corrupted-source recovery policy remain host concerns.
