using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class BlobOwnerMaintenanceTests
{
    [Fact]
    public async Task BoundedInventoryAdoptsLegacyObjectsAndImportDoesNotHoldMetadataDuringSourceReads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bsync-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var raw = new FileBlobCache(Path.Combine(directory, "bytes"));
            var cache = new ManagedBlobCache(raw, metadata, new FileBlobCacheLock(Path.Combine(directory, "locks")), 1000, 1000);
            var hashes = new List<string>();
            for (var i = 0; i < 7; i++) hashes.Add((await raw.ImportAsync(new MemoryStream([(byte)i]))).Sha256);
            string? cursor = null; var seen = 0;
            do { var page = await cache.ReconcilePageAsync(TimeSpan.FromSeconds(5), cursor, 2); Assert.InRange(page.Examined, 0, 2); seen += page.Examined; cursor = page.Continuation; } while (cursor is not null);
            Assert.Equal(7, seen);
            foreach (var hash in hashes) Assert.Contains("recovered/unknown", (await cache.GetEntryAsync(hash)).Owners);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var source = new CallbackStream([11, 12, 13], async ct => { await cache.GetEntryAsync(hashes[0], ct); });
            Assert.Equal(3, (await cache.ImportAsync(source, stop.Token)).Size);
            Assert.True(source.Callbacks >= 2); // Prehash and import passes both permit metadata re-entry.
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CallbackStream(byte[] bytes, Func<CancellationToken, Task> callback) : MemoryStream(bytes)
    {
        public int Callbacks { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Callbacks++; await callback(cancellationToken); return await base.ReadAsync(buffer, cancellationToken); }
    }

    [Fact]
    public async Task MaintenancePreservesUnknownUnavailableAndBuiltInOwnersAndRunsCallbacksOutsideLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bsync-owner-maintenance-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(directory, "bytes")), metadata,
                new FileBlobCacheLock(Path.Combine(directory, "locks")), 100, 100);
            var blob = await cache.ImportPinnedAsync(new MemoryStream([1, 2, 3]), "app/retired-unique-id");
            foreach (var owner in new[] { "app/live", "app/unknown", "app/unavailable", "package/retained", "legacy" }) await cache.PinAsync(blob.Sha256, owner);
            var resolver = new Resolver(async (_, owner, ct) =>
            {
                // Re-entrance proves the host lookup is not under the metadata lock.
                await cache.GetEntryAsync(blob.Sha256, ct);
                return owner switch { "app/retired-unique-id" => BlobOwnerResolution.Released, "app/live" => BlobOwnerResolution.Live,
                    "app/unknown" => BlobOwnerResolution.Unknown, _ => BlobOwnerResolution.Unavailable };
            });
            var dry = await cache.MaintainOwnersAsync(resolver, TimeSpan.FromSeconds(3), dryRun: true);
            Assert.Equal((4, 1, 1, 1, 3L), (dry.ExaminedOwners, dry.ReleasedOwners, dry.UnknownOwners, dry.UnavailableOwners, dry.ProtectedBytes));
            Assert.Equal(6, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
            Assert.Equal(1, (await cache.MaintainOwnersAsync(resolver, TimeSpan.FromSeconds(3))).ReleasedOwners);
            Assert.Equal(5, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
            Assert.Equal(0, (await cache.MaintainOwnersAsync(resolver, TimeSpan.FromSeconds(3))).ReleasedOwners);
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Resolver(Func<string, string, CancellationToken, Task<BlobOwnerResolution>> resolve) : IBlobOwnerResolver
    {
        public async ValueTask<BlobOwnerResolution> ResolveAsync(string hash, string owner, CancellationToken cancellationToken = default) => await resolve(hash, owner, cancellationToken);
    }
}
