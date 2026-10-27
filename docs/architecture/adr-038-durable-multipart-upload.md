# ADR-038: Add a resumable multipart transport to the existing transfer queue

Status: implemented experimental profile; provider qualification open (B05, 2026-10-24).

`HttpBlobTransferOptions.MultipartUploads` attaches `ResumableMultipartUpload` to the existing transfer manager.
It does not introduce another queue. The account's orchestration store and physical locks own recovery. A client
operation GUID is committed before initiation, and session/receipt checkpoints precede progress notifications.
Every restart calls the authority to reconcile service receipts; a local completion flag is not authoritative.
Parts are 5–16 MiB, at most 1,000, with one part buffered. Signed URLs are never recovery data. HTTP control replies
are bounded to 1 MiB. Unsupported means an explicit 501 at initiation; authentication failures and 404s do not fall back.

`HttpMultipartUploadProtocol` and `MapSyncMultipartUploads` define schema-1 start/reconcile, part authorization,
complete and abort routes. The endpoint requires authentication and a host resolver on every request. The resolver
must derive account/generation from authenticated context, refuse revoked access, and return null only when multipart
is unsupported. A session ID, operation ID or hash grants no access. Replies use `Cache-Control: no-store`.

Part PUTs use a separate credential-free client. Native hosts must disable cookies, application credential handlers
and automatic redirects; browser fetch explicitly omits credentials and rejects redirects. Do not attach request-URL
logging to this client. A 401/403 refreshes the part ticket once; valid earlier parts remain intact. ETags are opaque
part receipts, never full-content SHA-256. Completion is accepted only after authoritative verification and publication.
Abort joins remote cleanup before local deletion, including initiation whose reply was lost; failed cleanup retains state.

The optional SDK recipe is `S3MultipartProtocol` in the Tasks server sample. Its reviewed profile is bounded to 1 GiB,
8 MiB parts, shared durable metadata and non-expiring shared server locks. It discovers interrupted initiation by exact
scoped staging key, refuses ambiguous inventories, lists actual remote parts, and verifies the completed object through
a private 64 KiB streaming snapshot before publishing its hash-addressed bytes. Snapshot disk use is bounded by object
size; the host must bound concurrent completions and protect/clean its temporary directory after process death.
Verification is persisted before the retry-safe scoped access grant. A lost grant reply is retried even when bytes are
already verified. Abort writes a durable tombstone before remote cleanup. Account deletion must coordinate the same
ownership and invalidate the server-owned account generation before any new resolver admits work.

Configure bucket lifecycle abortion of incomplete multipart uploads and retention of staging objects. This covers
orphans created before an initiation reply/checkpoint; ambiguous sessions require operator reconciliation, not choosing
an arbitrary upload. Retain local/server identities for the replay horizon. Explicit per-session abort is idempotent.
`SweepAsync` processes bounded metadata pages under the same session locks, with an inactivity horizon beyond the
five-minute ticket lifetime. Lost-initiation orphans are reconciled by exact scoped key; failed cleanup remains
retryable. Unknown/corrupt records and legacy records without timestamps are retained. Aborted tombstones are never
silently expired. Verified completion records remain replayable after staging cleanup; sweeping does not revoke a
successful upload. Hosts schedule this operation and choose retention policy. Zero-byte S3 objects use the legacy verified path.

Core restart/URL-expiry/abort tests, authenticated HTTP protocol tests and an SDK fault model are executable.
The SDK fault model is not MinIO evidence. Real local SeaweedFS 4.49 passes native multipart recovery and all three browser CORS/ETag checks. A pinned-source
MinIO workflow is supplied, but local binary/toolchain downloads failed and no MinIO pass is claimed. MinIO, deployment
TLS/IAM, physical service restart and account-deletion/revocation qualification remain open. The S3 recipe is not a newly
supported provider package. See the [execution record](../review/capabilities-implementation-2026-10-24.md).

See the [hardening continuation](../review/capabilities-closure-2026-10-26.md) for exact evidence and limitations.
