# ADR-026: verified plaintext repair and cooperative preparation deadlines

Date: 2026-10-17
Status: implemented repair slice; remaining gap-closure requirements are tracked separately

## Problem

Published 0.4.0 can report a package incomplete forever after a completed object is damaged.
Queueing checked only length, and the HTTP downloader deliberately refused existing damaged bytes.
Repeated preparation must repair both the active revision and a newer candidate without deleting pinned content.

## Decisions

`BlobVerification.VerifyAsync` reads the full plaintext with bounded hash buffers and returns a structured
outcome. Missing and invalid readable content are distinct from key availability, cryptographic failure,
unsupported format and I/O failure. Cancellation propagates. Cryptographic failure does not establish
whether a key is wrong or ciphertext is damaged. Encrypted missing backing content is an I/O/recovery
problem, not evidence that its logical encryption record can be discarded.

`IBlobCache` is unchanged. The optional `IBlobCacheReplacement` capability promises that partial append
leaves old content intact and completion verifies the full hash before an atomic replacement, preserving
open readers. Native files and browser blobs opt in. Managed caches forward the capability. Encrypted v1
and third-party providers without this capability do not automatically replace invalid existing objects.
An absent logical object still uses the ordinary download path.

`BlobTransferManager` owns replacement. It serializes the entire verification/download/promotion/acknowledgement
attempt using `ManagedBlobCache.AcquireTransferAsync`. The standalone HTTP download API retains its conservative
existing-content behavior. Network reads occur outside cache metadata locks; partial writes reserve old plus
staged bytes using existing managed accounting. Bad replacement hashes leave the old object intact and fail
the job. Repeated package preparation does not reopen failed, paused or canceled jobs. A host can explicitly
resume after correcting capacity, key access, source data or credentials.

Native promotion uses the existing flushed-file/atomic-replace implementation. Browser promotion uses one
IndexedDB write transaction. Browser `OpenReadAsync` holds an immutable JavaScript snapshot until asynchronous
disposal; subsequent replacement cannot change that reader. The legacy synchronous `OpenRead(hash, size)`
remains a live view. Use `await using` for managed readers. Snapshot copies are volatile and disappear on
realm termination; they are not a new persistent format.

## Journal and ownership

Repair records use `repair/v1/<authority-hash>/down/<content-hash>`. JSON schema 1 stores plaintext hash,
length, credential-free authority route, repair lineage ID, state, attempts, next-attempt time and stable
error code. Source-generated serialization is used. No bearer tokens, local paths or signed URLs enter it.

| State | Recovery |
|---|---|
| Needed | Queued together with reopening a historical Complete transfer |
| Downloading | Resume the isolated ordinary partial under physical content ownership |
| Promoting | Full bytes received; provider verification and atomic switch may or may not have happened |
| Completed | Verified content is reachable; transfer completion can be replayed |
| Deferred | Preserve bytes; automatic connectivity backoff or explicit capacity/key/credential recovery |
| RecoveryRequired | No automatic destructive fallback; explicit host action required |

The plan proposed separate Verifying and ReadyToPromote states. Existing providers combine verification and
promotion in `CompletePartialAsync`, so this slice persists one **Promoting intent before that operation**.
It never claims verification has finished before the provider reports success. A restarted worker first
verifies complete content: if valid, it acknowledges without downloading or promoting again. Otherwise it
resumes the staged replacement. Real process-kill tests cover both sides of promotion.

The repair lineage ID is not a provider generation counter. Instead, the physical content lock is held
continuously from fresh verification through promotion and acknowledgement. Imports, rotation and transfers
using the managed cache share this lock. A resumed attempt never trusts an old observation. A future provider
that releases ownership during download needs generation CAS before it can use this implementation. Raw
backing-store mutation bypasses these guarantees and is used only for fault injection.

Provider completion now holds a durable temporary lifetime pin while hashing outside the global metadata
lock. Imports likewise reserve under metadata ownership, then read/hash/stage outside it. Reconciliation
preserves live-operation reservations. Native replacement moves the old file into an accounted retired
directory; reclamation requires exclusive physical open after all old readers release their handles.

Transfer ownership is also fenced in orchestration metadata. Journal writes and transfer acknowledgements
use that fence. A stale worker cannot acknowledge completion. A new transfer worker cannot reach the same
physical content until the old worker has joined and released its lock.

Acquisition order for this path is host account lifetime, package pointer lock (during queueing/activation),
content lock, cache metadata lock, then provider locks/transactions. Transfer execution never takes a package
pointer lock while holding content ownership. Reader lifetime locks precede cache metadata operations;
reconciliation uses only nonblocking lifetime probes while holding metadata. Host authorization runs outside
provider transactions. HTTP authorization is evaluated by the authority when requesting bytes. Hosts can
set `AuthorizePromotionAsync` to recheck current policy after downloading and immediately before provider
verification/promotion. Refusal retains staging and records `authorization-required` for explicit resume.

## Deadlines and cleanup

Preparation and upload prerequisites create one `TimeProvider` deadline before metadata/ownership work.
Nested drains receive remaining time and the linked token. Preparation and prerequisites throw cancellation
on expiry; drains return remaining-work on their own budget expiry. Caller cancellation still propagates
from drains. Prepare invalidates its cached verification observation before attempting work; timeout does
not leave a newly trusted readiness timestamp. An old active revision remains until a verified candidate
can be committed. A provider commit already started may finish after expiry.

Native hashing honors cancellation before atomic rename. Browser interop completion is joined with a
non-cancelable await once started: canceling an interop await would not stop its JavaScript transaction.
Browser v1 whole-object hashing may overrun the budget. Noncooperative host code is also joined, never
abandoned while it owns resources. These are cooperative deadlines, not hard real-time guarantees.

Terminal transfer removal atomically creates `transfer-cleanup/v1/<transfer-key>` with payload `1`.
Unpinning and journal removal follow under content ownership. `ReconcileReleasedAsync(limit)` processes
bounded pages; repeating terminal release and creating a new job also finish old cleanup first. The cleanup
rechecks durable job existence before releasing its owner and never removes another owner's pin.

## Compatibility and remaining boundaries

The HTTP protocol, content identities, package manifests, transfer job schema, cache metadata and encrypted
v1 format are unchanged. Older clients ignore new journal prefixes; they cannot perform automatic repair
or finish new cleanup journals. Downgrade can strand repair work or conservative pins, but does not require
rewriting encrypted objects. Do not run mixed versions concurrently against the same managed cache.

The continuation adds bounded provider inventory, owned read-model reclamation, account composition,
protected payloads and experimental encrypted v2 with explicit quarantine recovery. See ADR-027/028 and the
[continuation record](../review/gap-closure-continuation-2026-10-17.md). Provider generations for unlocked
staging are unnecessary while continuous physical ownership is required. Full-hash checkpointing is not
implemented: interrupted verification restarts. Browser v1 snapshots still retain whole objects in JS memory.
Content-free metrics report state/error transitions, acknowledged bytes and replacement-attempt duration;
quarantine data is durably inventoried but is not exposed as a separate metrics gauge. External qualification
and release gates remain open.
