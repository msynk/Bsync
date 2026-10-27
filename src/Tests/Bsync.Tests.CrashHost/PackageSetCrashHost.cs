using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

internal static class PackageSetCrashHost
{
    internal static async Task RunAsync(string path, string phase, bool after)
    {
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"));
        var locks = new FileBlobCacheLock(path + ".locks");
        var cache = new ManagedBlobCache(new FileBlobCache(path + ".bytes"), metadata, locks, 1024, 1024);
        using var http = new HttpClient { BaseAddress = new("https://offline.invalid/") };
        var transfers = new BlobTransferManager(metadata, cache, new(http));
        OfflinePackageManager Manager(IOrchestrationStore store) => new(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
        static OfflinePackageManifest Child(string revision, byte value) => new(1, "child", revision, null,
            [new("item", Convert.ToHexStringLower(SHA256.HashData(new[] { value })), 1, true, [])]);
        static OfflinePackageSetManifest Root(OfflinePackageManifest child) => new(1, "set", child.Revision, null,
            [new(child.PackageId, child.Revision, OfflinePackageManager.GetManifestSha256(child))]);
        var first = Child("one", 1); var second = Child("two", 2);
        var manager = Manager(metadata);
        await manager.StageSetAsync(Root(first), [first]); await cache.ImportAsync(new MemoryStream([1]));
        if (!await manager.TryActivateSetAsync("set", "one")) throw new InvalidOperationException("Initial root missing.");
        await using var reader = await manager.OpenActiveSetAsync("set");
        await cache.ImportAsync(new MemoryStream([2]));
        var intercepted = Manager(new Boundary(metadata, phase, after));
        if (phase == "stage") await intercepted.StageSetAsync(Root(second), [second]);
        else { await manager.StageSetAsync(Root(second), [second]); await intercepted.TryActivateSetAsync("set", "two"); }
        throw new InvalidOperationException("Crash boundary not reached.");
    }
    private sealed class Boundary(IOrchestrationFeatureStore inner, string phase, bool after) : IOrchestrationFeatureStore
    {
        public Task RequireFeatureVersionAsync(int version, CancellationToken cancellationToken = default) => inner.RequireFeatureVersionAsync(version, cancellationToken);
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        {
            var boundary = mutations.Any(m => m.Key.StartsWith("package-sets/v1/", StringComparison.Ordinal) && m.Key.EndsWith(phase == "stage" ? "/latest" : "/active", StringComparison.Ordinal) && m.Payload == "two");
            if (boundary && !after) await Pause();
            var result = await inner.TryWriteAsync(mutations, fence, cancellationToken);
            if (boundary && after && result) await Pause();
            return result;
        }
        private static async Task Pause() { Console.WriteLine("package-boundary"); await Task.Delay(Timeout.Infinite); }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
