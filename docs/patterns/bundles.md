# Pattern: bundles (content sets that switch revision at once)

Some content is only useful complete: a handbook and its images, a price list with its attachments, a form definition
with its assets. A device must never show half of a new version mixed with half of the old one, and it must keep the
old version usable while the new one downloads, possibly over several days of poor connectivity. The tested example
is in the Tasks sample (task F2) and builds on [attachments](attachments.md):

| Piece | Where |
|---|---|
| `BundleManifest`, `PublishBundle`, `BundleState` | [`Bundles.cs`](../../src/Samples/Bsync.Samples.Tasks.Shared/Bundles.cs) |
| Publishing endpoint, read-only collection | [`BundleEndpoints.cs`](../../src/Samples/Bsync.Samples.Tasks.Server/BundleEndpoints.cs) |
| Update, switch, state (`UpdateBundlesAsync`, `GetActiveBundle`, `GetBundleStateAsync`) | [`TasksClient.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/TasksClient.cs), [`LocalBlobStore.cs`](../../src/Samples/Bsync.Samples.Tasks.Console/LocalBlobStore.cs) |
| End-to-end test | `TasksSampleTests.BundleSwitchesAtomically` (`src/Tests/Bsync.Tests.SqlServer`) |

## How it works

1. **The server publishes a manifest.** The back office uploads the files (the same resumable, verified uploads as
   attachments), then calls `POST /api/bundles` with the item references. The endpoint checks that the tenant holds
   every item, increments the bundle's revision, records the items (blob reads are allowed through them) and publishes
   the manifest with `ISyncPublisher.UpsertAsync` in the same transaction. Replicas cannot write bundles: the
   collection is pull-only on the client (`SyncMode.PullOnly`) and its write handler rejects writes.
2. **The device separates "latest known" from "in use".** The synced manifest is the latest revision the device knows
   of. The revision in use is a separate manifest file next to the blob store. `UpdateBundlesAsync` downloads every item
   of a newer revision (resuming interrupted downloads, verifying each by hash) and only then replaces the in-use
   manifest, with a write to a temporary file, a flush and one rename. A reader therefore sees the old or the new
   revision, never a mix.
3. **Readers take one snapshot.** `GetActiveBundle(id)` returns the in-use manifest; reading every item through that
   one manifest keeps them on the same revision, even if a switch happens meanwhile (the old revision's content stays
   until eviction, which never removes items of the revision in use or of the one being downloaded).
4. **The state is visible.** `GetBundleStateAsync(id)` reports the revision in use, the latest known revision, the
   missing items and the bytes still to download, so a UI can say "using version 3; version 4 is downloading, 12 MB
   left".

In the test, revision 2 of a bundle (a changed text file and a new 20 MiB file) is interrupted halfway through the large
file. The device keeps using and reporting revision 1 across a restart, even though the new text file is already on the
device. The next update downloads only the other half and switches both files to revision 2 at once.

## Not done yet

The same gaps as attachments: no browser (IndexedDB) client, no presigned URLs, no S3 adapter, no package types. A
bundle's old revisions are not garbage-collected on the server.
