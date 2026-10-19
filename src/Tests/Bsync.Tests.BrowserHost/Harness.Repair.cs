using System.Net;
using System.Security.Cryptography;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    [JSInvokable(nameof(AutomaticRepairChecks))]
    public static async Task<string> AutomaticRepairChecks(string accountName, bool repair)
    {
        var account = new AccountScope("tests", "browser", accountName);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var locks = await BrowserBlobCacheLock.OpenAsync(_js, account);
        var bytes = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new ManagedBlobCache(bytes, metadata, locks, 8, 8);
        var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
        var handler = new RepairHandler();
        using var http = new HttpClient(handler) { BaseAddress = _baseAddress };
        var transfers = new BlobTransferManager(metadata, cache, new(http));
        var packages = new OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(true));
        if (!repair)
        {
            await cache.ImportPinnedAsync(new MemoryStream([1, 2, 3, 4]), "pending/draft");
            await packages.StageAsync(new(1, "manual", "one", null, [new("page", hash, 4, true, [])]));
            await packages.PrepareAsync("manual", TimeSpan.FromSeconds(30));
            if (handler.Requests != 0) throw new Exception("Existing content unexpectedly downloaded.");
            return account.StorageKey + "/" + hash;
        }
        var owners = (await cache.GetEntryAsync(hash)).Owners.Order().ToArray();
        await using (var reader = await cache.OpenReadAsync(hash))
        {
            var status = await packages.PrepareAsync("manual", TimeSpan.FromSeconds(30));
            if (status.Readiness != OfflinePackageReadiness.ReadyFreshnessUnknown || handler.Requests != 1)
                throw new Exception("Automatic repair failed.");
            if (reader is not null)
            {
                var old = new byte[reader.Length]; await reader.ReadExactlyAsync(old);
                if (old.Any(b => b != 9)) throw new Exception("Repair changed an existing reader's generation.");
            }
        }
        if (!owners.SequenceEqual((await cache.GetEntryAsync(hash)).Owners.Order())) throw new Exception("Repair changed durable owners.");
        if ((await transfers.GetRepairAsync(hash))?.State != BlobRepairState.Completed) throw new Exception("Repair journal not completed.");
        await metadata.DeleteAllAsync();
        await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
    }
    private sealed class RepairHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        }
    }
}
