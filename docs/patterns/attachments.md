# Pattern: attachments (blobs beside documents)

Documents replicate as JSON; files do not belong in it. This page shows how to attach files to replicated documents
with the engine as it is, plus one small hook (`SyncOptions.ReadyToPush`). The tested example is in the Tasks sample
(task F1):

| Piece | Where |
|---|---|
| `BlobReference`, `IBlobSource`, resumable `BlobTransfer` client | [`Blobs.cs`](../../src/Samples/Bsync.Samples.Tasks.Shared/Blobs.cs) |
| Device blob store (content-addressed, next to the SQLite file) | [`LocalBlobStore.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/LocalBlobStore.cs) |
| Attaching, upload-before-push, download, pinning, eviction | [`TasksClient.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/TasksClient.cs) |
| Server blob store (`IBlobStore`, file system) and routes | [`BlobStore.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/BlobStore.cs), [`BlobEndpoints.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/BlobEndpoints.cs) |
| Reference checks in the write handler | [`TaskWriteHandler.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/TaskWriteHandler.cs) |
| End-to-end test | `TasksSampleTests.AttachmentsSurviveInterruptions` (`src/Tests/Bsync.Tests.SqlServer`) |

## The reference

A document carries `{ id, sha256, size, contentType, fileName }` per attachment. The bytes live in content-addressed
stores on the device and on the server, keyed by SHA-256, so identical content is stored once and a hash check tells
whether bytes are complete and intact.

## Ordering rules

1. **Bytes before the save.** `AttachAsync` copies the content into the device store, flushes it to disk and moves it
   into place under its hash, and only then saves the document with the reference. A saved reference always has its
   bytes on the device.
2. **Upload before the document.** `SyncOptions<T>.ReadyToPush` holds a pending document back while any content it
   names that is on this device is not yet on the server. Held documents count as `Deferred`, so the session backs off
   rather than spinning; a held member holds its dependency group. The server checks again in its write handler and
   answers `retry-later` (`dependency-missing`) for a reference to content the tenant has not uploaded, so a client
   that skips the gate cannot create dangling references either.
3. **Finishing is idempotent.** An upload ends with a "finish" request that verifies the hash and records that the
   tenant holds the content. If its response is lost, repeating it changes nothing, and the object is never stored
   twice.

## Transfers

- Uploads go in chunks (4 MiB in the sample) to `PUT api/blobs/{sha256}/uploads/{offset}`. The server appends only at
  the offset it holds and keeps every byte that arrived, also from a request cut off midway; `POST
  api/blobs/{sha256}/uploads` tells a restarted client where to continue (or that the content is complete).
- Downloads use HTTP range requests (`HttpBlobSource`) into a partial file next to the store; a restarted client
  continues from its length. The finished file is hashed before it is renamed into the store, so a partial or
  corrupted file is never opened as complete: a mismatch discards it (`BlobCorruptedException`) and the next attempt
  starts over.
- Measured in the test on loopback: a 50 MiB upload cut at 60% and resumed after a client restart sent the remaining
  40% (at most one chunk twice); a download cut at 60%, damaged on disk and resumed received the remaining 40%, was
  rejected by its hash, and succeeded on the next attempt.

## Access and deduplication

- A caller may read a blob only if a task it can read references it. Everyone else gets 404, not 403, so a hash does
  not reveal whether content exists.
- Storage is deduplicated across tenants, but upload is not: a tenant must upload content itself before it may
  reference it, even when another tenant stored the same bytes. Otherwise anyone could test whether some file exists on
  the server by announcing its hash.

## On the device

- A document whose content is not on the device stays readable; `OpenAttachment` returns `null` and the UI shows the
  attachment as unavailable until `DownloadAsync` succeeds.
- `Pin(taskId)` prefetches a task's attachments on every sync and protects them from eviction.
- `EvictAsync(maxBytes)` removes least-recently-used content, never content of pinned tasks or of tasks with local
  changes (which still have to upload it).

## Object storage and presigned downloads

With `Tasks:S3:ServiceUrl`, `Bucket`, `AccessKey` and `SecretKey` set, the sample server keeps verified content in an
S3-compatible bucket ([`S3BlobStore.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/S3BlobStore.cs), AWS SDK).
Uploads in progress stay on the server's disk, because S3 multipart parts must be at least 5 MB and clients send
smaller chunks; a verified upload is written to the bucket once and the partial file removed. Downloads are answered
with a redirect to a presigned URL valid for five minutes, so the bytes do not pass through the application server;
range requests, and therefore resumption, work against the bucket too. A caller without read access gets 404 and no
URL. Tested against a local S3-compatible server (moto): `TasksSampleTests.AttachmentsInObjectStorage`, run with
`BSYNC_S3=http://127.0.0.1:59000`. Browsers following such a redirect need CORS on the bucket for the app's origin.

## In the browser

`Bsync.Blazor` has `BrowserBlobStore` (`Bsync.Blazor.Blobs`), the browser's content-addressed store: verified content and
resumable chunks in IndexedDB (`bsync-blobs-<name>`), content readable only after its SHA-256 was checked, quota errors
surfaced as `LocalStoreUnavailableException`. It was moved into the package because browser storage is platform code
every browser app needs. `IndexedDbBrowserTests.BrowserBlobStore` checks it in Chromium, Firefox and WebKit: import and
ranged read-back, a corrupted transfer refused, a transfer continued after a page reload, and wipe. A service worker
must not proxy `api/blobs` (the same rule as the hint stream).

## Not done yet

- **The WebAssembly Tasks client** does not use `BrowserBlobStore` yet: it has no attachment UI, and the shared
  `BlobTransfer` writes downloads to a file; a browser download writes chunks with `AppendPartialAsync` instead.
- **Presigned uploads.** Uploads still go through the application server.
- **Garbage collection** of server objects that no task or bundle references any more.
- The server's `IBlobStore` and the transfer client stay in the sample: only one server and one native client use them.
