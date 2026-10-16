# ADR-021: Managed content and immutable package activation

Status: Implementation in progress, 2026-10-16. Not a blanket platform qualification.

## Decision

Reuse `HttpBlobTransfer` and `IBlobCache`. `BlobTransferManager` persists version-1 jobs in the account's
orchestration store, partitioned by the absolute endpoint, hash and direction. A bounded scan cursor prevents
terminal jobs at the start of the key range from starving later work. Attempts have a deadline, bounded concurrency
and persisted backoff. Credentials, presigned URLs and file paths are not job data. Completion means verified local
content for downloads or an authority-confirmed upload; it says nothing about document or business acceptance.

`ManagedBlobCache` records stable pin owners, accounted bytes and last access separately from bytes. It reserves
capacity before writes. A crash between stores may overcount or retain pins, but must not erase protected work to
make accounting look clean. Reconciliation preserves owners and adopts complete legacy files with a
`recovered/unknown` pin. Native/browser inventories count complete, partial and abandoned staging payload bytes.
Unrecognized partial/staging content consumes protected capacity and is not automatically removed.

Physical deletion and pin changes share a **non-expiring** exclusive lock. Native hosts use an open lock file;
browsers use Web Locks. An expiring database fence is insufficient: a delayed old worker could delete a file after
its lease expired and a new owner pinned it. Every participant must use the same lock namespace and managed wrapper.
Direct writes to the underlying cache bypass the contract. Named content locks also serialize transfers of a hash
across endpoints. Waiting for a suspended owner is preferable to unsafe takeover; cancellation ends the wait.

Pins identify pending work, package revisions, transfer jobs and readers. A stable owner is idempotent, not a reference
count. A completed/canceled job retains its pin until `ReleaseTerminalAsync`; document/package owners must already
have independent pins. Use `ImportPinnedAsync` before persisting a document reference, and release its owner only when
that durable work no longer requires the bytes. Use asynchronous disposal for reader streams and snapshots. New readers
use reserved `reader-v2/` and `snapshot-v2/` owners backed by unique non-expiring physical lifetime locks. Reconciliation
removes one only after acquiring its lifetime lock without waiting, proving the prior holder has released it. A slow
live holder remains pinned; OS cleanup may require a later reconciliation pass. Native lifetime files delete on close
and process termination; their random tokens are never reused. Browser lifetime locks end when the context closes.
Custom lock providers without nonblocking probes retain pins. Legacy/custom owners require host reconciliation;
age alone is never sufficient. Applications must not use the reserved reader/snapshot owner namespaces.

## Packages

A manifest contains at most 256 immutable hash/length items and an acyclic dependency graph. Required dependencies
are transitive. Document snapshots are serialized content, not pointers into mutable replicated collections. The
publisher constructs a coherent revision; independent collection checkpoints do not establish global atomicity.
Reusing a package/revision identity with changed content fails. Unknown manifest schemas fail without modification.

Staging commits the manifest and latest-known revision before creating pins/jobs. Repeating staging recovers that
work. Activation pins and hashes every required item, then commits one active pointer and a conditional latest
pointer in a single metadata transaction. An old candidate cannot overwrite a subsequently staged revision.
An incomplete candidate leaves the prior active pointer unchanged. Revisions remain pinned until explicit retirement;
retirement refuses active/latest revisions. Snapshot readers pin their own fixed revision independently.

The host supplies authorization at staging, activation and local opening. This is an explicit offline-access policy,
not a substitute for server authorization. Physical storage loss remains possible; status/opening verify content
again and report incomplete/missing content. Ready means locally complete, with freshness unknown or known outdated.
It is unrelated to `SyncGoal.Complete` or approval of local actions.

## Current bounds and evidence

Native tests cover interrupted transfer recovery, failure classes, cancellation, ownership contention, pin retention,
capacity pressure, incomplete candidates, immutable revisions, reader continuity and external content loss. Browser
package qualification is tracked separately in the improvement plan. Metadata, manifests, transfer jobs and cache
entries have independent version checks. No wire extension is introduced.

Status currently rehashes a bounded manifest; it does not promise constant-time UI refresh or a byte-independent
verification deadline. Hosts may cache a displayed result with its verification time, but must validate opening.
Native lock behavior is tested on Windows only. Blob encryption, real S3 CORS
qualification, and physical-device scheduling remain open gates. Payload accounting excludes physical database/file
overhead; browser origin estimates are separate hints, not a reservation guarantee.
