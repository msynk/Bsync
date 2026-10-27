using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

if (args.Length != 2) throw new ArgumentException("Usage: create|upgrade|refuse <fixture-directory>");
var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "metadata.db");
var account = new Bsync.Client.AccountScope("migration", "published050", "alice");
if (args[0] == "refuse")
{
    try { await using var unexpected = await SqliteOrchestrationStore.OpenAsync(path, account); }
    catch (LocalStoreUnavailableException error) when (error.Reason == "outdated") { Console.WriteLine("Published reader refused version 3 without changing it."); return; }
    throw new InvalidOperationException("Published reader accepted unsupported metadata.");
}
await using var store = await SqliteOrchestrationStore.OpenAsync(path, account);
var locks = new FileBlobCacheLock(Path.Combine(directory, "locks"));
var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(directory, "bytes")), store, locks, 4096, 4096);
using var http = new HttpClient { BaseAddress = new("https://offline.invalid/") };
var transfers = new BlobTransferManager(store, cache, new(http));
var manager = new OfflinePackageManager(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1 }));
var partialHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 2, 3 }));
var child = new OfflinePackageManifest(1, "manual", "one", null, [new("page", hash, 1, true, [])]);
if (args[0] == "create")
{
    await store.RequireFeatureVersionAsync(2);
    if (!await store.TryWriteAsync([new("fixture/pending", null, "immutable pending intent")])) throw new InvalidOperationException("Fixture already exists.");
    await cache.ImportAsync(new MemoryStream([1])); await cache.PinAsync(hash, "draft/migration");
    await cache.AppendPartialAsync(partialHash, new byte[] { 2 });
    await manager.StageAsync(child); if (!await manager.TryActivateAsync("manual", "one")) throw new InvalidOperationException("Cannot activate old package.");
    Console.WriteLine("Published 0.5.0 fixture created with package, pin, partial and pending intent.");
    return;
}
#if NEW_FEATURES
if (args[0] == "upgrade")
{
    if ((await store.ReadAsync("fixture/pending"))?.Payload != "immutable pending intent") throw new InvalidDataException("Pending work lost.");
    if (!(await cache.GetEntryAsync(hash)).Owners.Contains("draft/migration") || await cache.PartialLengthAsync(partialHash) != 1) throw new InvalidDataException("Pins/partial lost.");
    await manager.StageSetAsync(new(1, "set", "one", null, [new("manual", "one", OfflinePackageManager.GetManifestSha256(child))]), [child]);
    if (!await manager.TryActivateSetAsync("set", "one")) throw new InvalidOperationException("Set activation failed.");
    await using var reader = await manager.OpenActiveSetAsync("set"); await using var stream = await reader.OpenAsync("manual", "page");
    if (stream!.ReadByte() != 1 || await cache.PartialLengthAsync(partialHash) != 1) throw new InvalidDataException("Content changed during upgrade.");
    Console.WriteLine("Candidate upgraded and activated set while retaining published pins, partial and intent.");
    return;
}
#endif
throw new ArgumentException("Unsupported migration mode for this package version.");
