# Managed transfers and offline packages

These APIs are new source capabilities under qualification. Use one `AccountScope`, orchestration store, blob store
and lock namespace per application/environment/account/organization. Never derive server authorization from this
local identity. Keep the account catalog used for recoverable deletion outside resources being deleted.

For high-level resource registration and fresh foreground/headless scopes, use `AccountComposition` with the same
durable catalog and physical locks. Register data resources before key resources. Its work scope holds shared account
ownership through joined disposal, and coordinated deletion requires exclusive ownership. See
[ADR-027](../architecture/adr-027-account-ownership-and-protected-metadata.md) for the lock and upgrade contracts.

The experimental `ChunkedEncryptedBlobCache` explicitly selects a separate v2 layout with bounded chunks, authenticated
ranges, resumable rotation and retained-source v1 migration. It does not raise the existing v1 writer's 16 MiB cap.
Read [ADR-028](../architecture/adr-028-experimental-chunked-encryption.md) before enabling it, particularly old-writer
quiescence, key invocation budgets, async disposal, quarantine recovery and pending crypto/performance qualification.

For bounded maintenance, native/browser raw providers expose `IBlobCachePagedInventory`; `ReconcilePageAsync` walks
managed metadata then complete provider objects using a cooperative budget and opaque cursor. Continue until null,
then periodically repeat to revisit live owners. Native enumeration retains only a bounded candidate page but still
scans directory entries. Unknown partials remain physically accounted and protected. The legacy `ReconcileAsync` and
`ListAsync` APIs remain full-list compatibility operations; avoid them for unbounded datasets.

`MaintainOwnersAsync` resolves only explicitly named `app/` pins, outside global metadata locks. A resolver returns
Live, Released, Unknown or Unavailable; only permanent Released permits removal, and owner IDs must never be reused.
Dry-run reports observations without removing pins. Built-in/legacy pins remain conservative. Terminal transfer cleanup
uses `ReconcileReleasedAsync`; read-model and encrypted-generation collection have separate ownership-aware APIs.
Run maintenance under the enclosing account scope, give urgent pending work priority, and persist returned cursors.

Native composition:

```csharp
var account = new AccountScope("catalog", "production", authenticatedAccountId);
await using var metadata = await SqliteOrchestrationStore.OpenAsync(metadataPath, account);
var locks = new FileBlobCacheLock(accountLockDirectory);
var cache = new ManagedBlobCache(new FileBlobCache(accountBlobDirectory), metadata, locks,
    softBudgetBytes: 256L * 1024 * 1024, hardBudgetBytes: 512L * 1024 * 1024);
await cache.ReconcileAsync();
var jobs = new BlobTransferManager(metadata, cache, new HttpBlobTransfer(accountHttpClient));
var released = await jobs.ReconcileReleasedAsync(limit: 100); // Schedule another bounded pass if released == 100.
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
For exclusive account maintenance windows, use the [account maintenance runner](account-maintenance.md) to restore
package/transfer pins and reconcile built-in owners from durable state with page checkpoints and dry-run support.
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
open/verify again after repair.

After 0.4.0, package preparation automatically verifies completed jobs and schedules repair for both the active
revision and the latest candidate. File caches and browser caches opt into `IBlobCacheReplacement`; third-party
providers must implement that capability before automatic replacement is enabled. Use `jobs.GetRepairAsync(hash)`
for the durable repair outcome and `BlobVerification.VerifyAsync(cache, hash, length)` for a fresh structured check.
The journal preserves progress across process death before/after promotion. HTTP download authorization remains
server-owned. Bad source data fails the job without an automatic retry loop; after correcting the cause, explicitly
resume through `SetStateAsync(..., BlobTransferState.Queued)`. Capacity/key deferrals also require host action.
Unreadable encrypted data is preserved. Experimental `ChunkedEncryptedBlobCache.RecoverAsync` accepts explicit
host-authorized recovery, retaining original generation pointers and ciphertext in permanent quarantine; v1 encrypted
objects have no automatic replacement. See [ADR-028](../architecture/adr-028-experimental-chunked-encryption.md).

`PrepareAsync` uses one cooperative budget for authorization, queueing, ownership, transfers, verification and
activation. Catch cancellation and retry when more time is available. It invalidates the cached verification
observation before work; a canceled run can leave that observation absent. Started provider commits are joined.
Browser v1 hashing/transaction work cannot be forcibly stopped and can overrun a deadline. Browser async readers
keep a stable snapshot across repair; their JS memory use is proportional to the v1 object, not a bounded chunk.

Terminal job release now journals the gap between removing a job and releasing its cache pin. Run bounded
`ReconcileReleasedAsync` calls under the account lifetime on startup or during maintenance; a return equal to
the limit means another pass may be needed. Repeating release or enqueueing the same job also finishes its old
cleanup. Other owners' pins are preserved. This does not reclaim arbitrary application pins or read-model data.

Native retired-file cleanup advances past live readers and unknown files. `CollectRetiredAsync` retains its scan
position for the cache instance; `CollectRetiredPageAsync` returns a filename continuation that hosts can persist
across restarts. Restart from null after reaching the end to revisit released readers and newly added files. Each call
probes at most its item limit and retains at most that many filenames, but enumerating the native directory is linear
and checks cancellation per entry. Unknown filenames are preserved and still consume physical capacity.

V1 managed rotation reserves both generations and holds a durable lifetime pin while encryption runs outside the
cache metadata lock. The v1 encryption provider still serializes its own operations. Metadata inspection and unrelated
pin updates remain available during crypto; failed rotation keeps a conservative reservation until reconciliation.

## Encrypted cache and candidate status additions (0.4.0)

`EncryptedBlobCache` can wrap dedicated native `FileBlobCache` or browser `BrowserBlobStore` storage. Pass the existing
account's `ILocalKeyProvider`, a versioned `LocalKeyReference` and `PlatformBlobEncryption` (native) or
`BrowserBlobEncryption.OpenAsync(js)` (browser). Wrap that encrypted adapter in `ManagedBlobCache` for pins/budgets.
Use `ManagedBlobCache.RotateEncryptionAsync(hash)` when changing the configured write key; retain old versions until
every required complete/partial object has rotated successfully. Wrong keys never trigger reimport repair. This format
is limited to 16 MiB and rewrites/authenticates whole objects; see [ADR-025](../architecture/adr-025-encrypted-blob-envelopes.md).

`OfflinePackageManager.GetPreparationAsync` verifies the latest staged candidate and reports missing required/optional
items with their durable transfer states/error codes. `GetStatusAsync` independently describes the active revision.
Both return observation times. Missing-byte totals count distinct required hashes and do not discount unverified partials.

The Notes browser app and WPF app share `ContentLibraryPanel` and `ContentLibrarySession`: prepare revision 1, interrupt
revision 2, restart, read revision 1, finish revision 2, then read the new complete snapshot. Content is bundled with the
sample as publisher-authored immutable bytes; these buttons demonstrate local package boundaries, not a remote catalog.

For explicit full-object integrity work, declare both time and content-byte allowances:

```csharp
var verification = await BlobVerification.VerifyAsync(cache, hash, declaredSize,
    budget: TimeSpan.FromSeconds(5), maximumContentBytes: 16L * 1024 * 1024,
    timeProvider: timeProvider, cancellationToken: stopping);
```

`ContentLimitExceeded` defers without opening storage; it does not authorize repair. Only `Verified` establishes the
full hash/length match. Reads are bounded to the declared length plus a one-byte EOF probe, but provider-internal
allocations/I/O are separate. Deadline expiry and caller cancellation throw after joining started work/disposal.
Interrupted full hashes restart, so schedule sufficient time for one complete object. See
[ADR-030](../architecture/adr-030-execution-budgets-and-verification.md) for limits and stop diagnostics.

For a sealed experimental v2 generation, bounded authentication can instead resume across runs:

```csharp
var progress = await chunkedCache.ScrubAsync(hash, TimeSpan.FromSeconds(5),
    maximumBytes: 8L * 1024 * 1024, maximumChunks: 8,
    timeProvider: timeProvider, cancellationToken: stopping);
// Deferred retains encrypted progress. A later call resumes; restart: true begins a fresh scan.
```

`Complete` means every chunk authenticated against its sealed manifest over this scan. It is not a newly recomputed
full SHA-256 or an instantaneous health snapshot. A changed generation starts at zero; late/canceled reads never
advance the checkpoint. Allow at least one full chunk, plus time for manifest, key and checkpoint work. Each checkpoint
seal consumes the key's encryption-invocation allowance. Failures preserve data and do not authorize repair.

Before retiring an old key, acquire exclusive account maintenance ownership and scan `GetKeyRetentionPageAsync` from
null through its final page. Retain every referenced key; a null key reference is an unknown legacy obligation and
blocks retirement of potentially used keys. Current, partial, retired, reader-held and quarantined generations remain
listed until collection safely removes them. Also account for v1 sources, protected orchestration, backups and other
stores: this cache page alone never authorizes key deletion. See
[ADR-035](../architecture/adr-035-generation-integrity-maintenance.md).
