using System.Net;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class BlobRepairRegressionTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("same-length")]
    public async Task PreparationRepairsCompletedContentAndPreservesPins(string damage)
    {
        var root = Path.Combine(Path.GetTempPath(), "bsync-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes = [1, 2, 3, 4];
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var store = new InMemoryOrchestrationStore();
            var locks = new FileBlobCacheLock(Path.Combine(root, "locks"));
            var cache = new ManagedBlobCache(new FileBlobCache(root), store, locks, 1024, 1024);
            var handler = new ContentHandler(bytes);
            using var http = new HttpClient(handler) { BaseAddress = new("https://repair.test/") };
            var transfers = new BlobTransferManager(store, cache, new(http));
            var packages = new OfflinePackageManager(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
            await cache.ImportAsync(new MemoryStream(bytes));
            await packages.StageAsync(new(1, "manual", "one", null, [new("page", hash, bytes.Length, true, [])]));
            Assert.Equal(OfflinePackageReadiness.ReadyFreshnessUnknown, (await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10))).Readiness);
            Assert.Equal(BlobTransferState.Complete, (await transfers.GetAsync(hash, BlobTransferDirection.Download))!.State);
            var owners = (await cache.GetEntryAsync(hash)).Owners;
            var path = Path.Combine(root, "objects", hash[..2], hash);
            if (damage == "missing") File.Delete(path);
            else await File.WriteAllBytesAsync(path, damage == "truncated" ? [9] : [9, 9, 9, 9]);
            // Reconstruct managers: repair must follow durable work, not an in-memory notification.
            transfers = new(store, cache, new(http));
            packages = new(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
            var status = await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
            Assert.Equal(OfflinePackageReadiness.ReadyFreshnessUnknown, status.Readiness);
            Assert.Equal(1, handler.Requests);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(owners.Order(), (await cache.GetEntryAsync(hash)).Owners.Order());
            Assert.Equal(BlobTransferState.Complete, (await transfers.GetAsync(hash, BlobTransferDirection.Download))!.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ContentHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
