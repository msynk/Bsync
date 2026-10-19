using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

internal static class RepairCrashHost
{
    public static async Task RunAsync(string path, bool after)
    {
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"));
        var store = new PauseStore(metadata, after);
        var locks = new FileBlobCacheLock(path + ".locks");
        var cache = new ManagedBlobCache(new FileBlobCache(path + ".blobs"), store, locks, 100, 100);
        using var http = new HttpClient(new Handler()) { BaseAddress = new("https://repair.test/") };
        var transfers = new BlobTransferManager(store, cache, new(http), new() { AttemptBudget = TimeSpan.FromMinutes(5) });
        var packages = new OfflinePackageManager(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
        var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3, 4]));
        await packages.StageAsync(new(1, "manual", "one", null, [new("page", blob.Sha256, 4, true, [])]));
        await packages.PrepareAsync("manual", TimeSpan.FromMinutes(5));
        await File.WriteAllBytesAsync(Path.Combine(path + ".blobs", "objects", blob.Sha256[..2], blob.Sha256), [9, 9, 9, 9]);
        store.Armed = true;
        await packages.PrepareAsync("manual", TimeSpan.FromMinutes(5));
        throw new InvalidOperationException("Repair did not reach the crash boundary.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
    }
    private sealed class PauseStore(IOrchestrationStore inner, bool after) : IOrchestrationStore
    {
        public bool Armed;
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        {
            var state = -1;
            foreach (var mutation in mutations.Where(m => m.Key.StartsWith("repair/v1/", StringComparison.Ordinal) && m.Payload is not null))
            {
                using var document = JsonDocument.Parse(mutation.Payload!);
                state = document.RootElement.GetProperty("State").GetInt32();
            }
            if (Armed && after && state == (int)BlobRepairState.Completed) await Pause();
            var result = await inner.TryWriteAsync(mutations, fence, cancellationToken);
            if (result && Armed && !after && state == (int)BlobRepairState.Promoting) await Pause();
            return result;
        }
        private static async Task Pause() { Console.WriteLine("repair-boundary"); await Task.Delay(Timeout.Infinite); }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
