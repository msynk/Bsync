# Pattern: attachments (blobs beside documents)

Documents replicate as JSON; files do not belong in it. Attachments are content-addressed blobs that travel beside the
documents that name them. The building blocks are in the packages (task F1); the Tasks sample shows them in a
native client, a browser client and a server:

| Piece | Where |
|---|---|
| `BlobReference`, `IBlobSource`, `IBlobCache`, `FileBlobCache`, resumable `HttpBlobTransfer` (client) | `Bsync` (namespace `Bsync.Blobs`) |
| `BrowserBlobStore` (the browser's `IBlobCache`, in IndexedDB) | `Bsync.Blazor` (namespace `Bsync.Blazor.Blobs`) |
| Blob routes `MapSyncBlobs`, `IBlobStore`, `FileSystemBlobStore`, `IBlobAccess`, `SyncBlobCollector` (server) | `Bsync.Server.AspNetCore` (namespace `Bsync.Server.AspNetCore.Blobs`) |
| `S3BlobStore` (S3-compatible bucket, presigned downloads and uploads) | `Bsync.Server.Blobs.S3` (optional; depends on the AWS SDK) |
| The application's rules: who holds and may read which content, and a garbage-collection job | [`TasksBlobs.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/TasksBlobs.cs) |
| Device store with uploaded markers, pins and bundles; attaching, upload-before-push, download, eviction | [`LocalBlobStore.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/LocalBlobStore.cs), [`TasksClient.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/TasksClient.cs) |
| Browser client with an attachment UI | [`TaskAttachments.cs`](../../src/Samples/Bsync.Samples.Tasks.Client/TaskAttachments.cs), [`App.razor`](../../src/Samples/Bsync.Samples.Tasks.Client/App.razor) |
| Reference checks in the write handler | [`TaskWriteHandler.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/TaskWriteHandler.cs) |
| Tests | `BlobRouteTests` (`src/Tests/Bsync.Tests`), `TasksSampleTests` (`src/Tests/Bsync.Tests.SqlServer` and `src/Tests/Bsync.Tests.Browser`) |

## The reference

A document carries a `BlobReference` per attachment: `{ id, sha256, size, contentType, fileName }`. The bytes live in
content-addressed stores on the device and on the server, keyed by SHA-256, so identical content is stored once and a
hash check tells whether bytes are complete and intact.

## Ordering rules

1. **Bytes before the save.** Import the content into the device store first (`IBlobCache.ImportAsync` hashes it,
   writes it durably and makes it readable under its hash), then save the document with the reference. A saved
   reference always has its bytes on the device.
2. **Upload before the document.** `SyncOptions<T>.ReadyToPush` holds a pending document back while any content it
   names that is on this device is not yet on the server. Held documents count as `Deferred`, so the session backs off
   rather than spinning; a held member holds its dependency group. The browser sample uploads inside `ReadyToPush`
   (`TaskAttachments.EnsureUploadedAsync`) and answers `false` while offline. The server checks again in its write
   handler and answers `retry-later` (`dependency-missing`) for a reference to content the tenant has not uploaded, so
   a client that skips the gate cannot create dangling references either.
3. **Finishing is idempotent.** An upload ends with a "finish" request that verifies the hash and records that the
   caller's scope holds the content (`IBlobAccess.RecordAsync`). If its response is lost, repeating it changes nothing,
   and the object is never stored twice.

## Server setup

```csharp
IBlobStore store = new FileSystemBlobStore(Path.Combine(contentRoot, "blobs"));
app.MapSyncBlobs(store, new MyBlobAccess(connectionString), syncOptions).RequireAuthorization();
```

`syncOptions` is the `SyncEndpointOptions` of the sync routes (the caller's scope comes from its `ResolveScope`).
`IBlobAccess` is the application's: `HoldsAsync` and `RecordAsync` keep track of which scope uploaded which content,
`CanReadAsync` decides reads, for example "a live document the caller can read references it". The routes are in the
[protocol](../protocol/v1.md#8-http-binding) (section 8, "Blobs"); `SyncBlobEndpointOptions.MaxSize` limits a blob
(200 MiB by default).

## Transfers

- `HttpBlobTransfer.UploadAsync` sends chunks (4 MiB by default) to `PUT sync/blobs/{sha256}/uploads/{offset}`. The
  server appends only at the offset it holds and keeps every byte that arrived, also from a request cut off midway;
  `POST sync/blobs/{sha256}/uploads?size=` tells a restarted client where to continue (or that the content is complete).
- `HttpBlobTransfer.DownloadAsync` uses HTTP range requests into the cache's partial transfer; a restarted client
  continues from its length. The finished transfer is hashed before it becomes readable, so a partial or corrupted
  transfer is never opened as complete: a mismatch discards it (`BlobCorruptedException`) and the next attempt starts
  over.
- Measured in the sample test on loopback: a 50 MiB upload cut at 60% and resumed after a client restart sent the
  remaining 40% (at most one chunk twice); a download cut at 60%, damaged on disk and resumed received the remaining
  40%, was rejected by its hash, and succeeded on the next attempt.

## Access and deduplication

- A caller may read a blob only if `CanReadAsync` allows it. Everyone else gets 404, not 403, so a hash does not reveal
  whether content exists.
- Storage is deduplicated across scopes, but upload is not: a scope must upload content itself before the server
  records that it holds it, even when another scope stored the same bytes. Otherwise anyone could test whether some
  file exists on the server by announcing its hash.

## On the device

- A document whose content is not on the device stays readable; the sample's `OpenAttachment` returns `null` and the UI
  shows the attachment as unavailable until a download succeeds.
- The console sample's `Pin(taskId)` prefetches a task's attachments on every sync and protects them from eviction;
  `FileBlobCache.Evict(maxBytes, keep)` removes least-recently-used content, never content in `keep` (pinned tasks and
  tasks with local changes, which still have to upload it).

## Object storage and presigned URLs

`S3BlobStore` (package `Bsync.Server.Blobs.S3`) keeps verified content in an S3-compatible bucket under
`objects/ab/<sha256>`. Downloads are answered with a redirect to a presigned URL (five minutes by default), so the bytes
do not pass through the application server; range requests, and therefore resumption, work against the bucket too. A
caller without read access gets 404 and no URL.

Uploads have two modes:

- **Chunked through the server (default).** Chunks stay on the server's disk, because S3 multipart parts must be at
  least 5 MB; a verified upload is written to the bucket once and the partial file removed.
- **Presigned (`PresignUploads = true`).** The start response carries an `uploadUrl`, and `HttpBlobTransfer` sends the
  whole content there in one `PUT` (`HttpBlobTransferOptions.DirectUploads`, on by default). Finishing reads the
  object back once to verify its hash, copies it to its place and deletes the upload. An interrupted presigned upload
  starts again from the beginning; content larger than `MaxSize` is refused at finish and removed by the collector.
  Use a `DirectClient` without the sync credentials (the sample's console client does; the browser default has none).

Browsers need a CORS rule on the bucket for the app's origin: `GET` for downloads, and `PUT` for presigned uploads.
Tested against a local S3-compatible server (moto): `TasksSampleTests.AttachmentsInObjectStorage`, run with
`BSYNC_S3=http://127.0.0.1:59000` (an upload cut halfway is sent again whole to the bucket, never through the
application server; downloads redirected and resumed).

## Garbage collection

`SyncBlobCollector.CollectAsync(store, referenced, minimumAge)` deletes objects older than `minimumAge` that are not in
the referenced set (read again just before deleting), and partial uploads abandoned for longer than that. The grace
period protects content uploaded for a document that has not been written yet. The sample's `BlobJanitor` passes every
hash named by a task or bundle and also removes the tenants' upload records of collected content; run such a job on a
schedule.

## In the browser

`BrowserBlobStore` keeps verified content and resumable chunks in IndexedDB (`bsync-blobs-<name>`); content is readable
only after its SHA-256 was checked, and quota errors surface as `LocalStoreUnavailableException`. Open one store per
account and delete it on sign-out (`BrowserBlobStore.DeleteAllAsync`). It is an `IBlobCache`, so `HttpBlobTransfer`
uploads from it and downloads into it. A service worker must not proxy the blob routes (the same rule as the hint
stream).

Tested: `IndexedDbBrowserTests.BrowserBlobStore` (import and ranged read-back, a corrupted transfer refused, a transfer
continued after a page reload, wipe) and the browser Tasks client's `TasksSampleTests.AttachmentsReachOtherDevices`
(an attachment added while the server is unreachable waits with its task, is uploaded on reconnect, and opens verified
on a second device, also offline after a reload) in Chromium, Firefox and WebKit.

## Metrics

`HttpBlobTransfer` records `bsync.blob.bytes` and `bsync.blob.transfers` on the `Bsync` meter; the routes record
`bsync.server.blob.bytes` and `bsync.server.blob.requests` on the `Bsync.Server` meter
([observability](../operations/observability.md)).
