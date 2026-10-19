using System.Text.Json.Nodes;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

internal static class ChunkCrashHost
{
    internal static async Task RunAsync(string path, bool after)
    {
        var account = new AccountScope("crash", "test", "account");
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account);
        ChunkedEncryptedBlobCache Cache(IOrchestrationStore store, int version) => new(new BlobCacheChunkStore(new FileBlobCache(path + ".chunks")), store,
            new FileBlobCacheLock(path + ".operations"), new FileAccountResourceLock(path + ".lifetimes"), new Keys(), new(account, "chunks", version), new PlatformBlobEncryption());
        var bytes = new byte[1048700]; Array.Fill(bytes, (byte)7);
        var initial = Cache(metadata, 1);
        var blob = await initial.ImportAsync(new MemoryStream(bytes));
        await using var oldReader = await initial.OpenReadAsync(blob.Sha256);
        await Cache(new Boundary(metadata, after), 2).RotateAsync(blob.Sha256);
        throw new InvalidOperationException("Expected the process to be killed at publication.");
    }
    internal static async Task ScrubAsync(string path, bool after)
    {
        var account = new AccountScope("crash", "test", "account");
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account);
        ChunkedEncryptedBlobCache Cache(IOrchestrationStore store) => new(new BlobCacheChunkStore(new FileBlobCache(path + ".chunks")), store,
            new FileBlobCacheLock(path + ".operations"), new FileAccountResourceLock(path + ".lifetimes"), new Keys(), new(account, "chunks", 1), new PlatformBlobEncryption());
        var bytes = new byte[1048700]; Array.Fill(bytes, (byte)7);
        var blob = await Cache(metadata).ImportAsync(new MemoryStream(bytes));
        await Cache(new Boundary(metadata, after, scrub: true)).ScrubAsync(blob.Sha256, TimeSpan.FromMinutes(1), long.MaxValue);
        throw new InvalidOperationException("Expected the process to be killed at checkpoint.");
    }
    private sealed class Keys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)reference.Version, 32).ToArray()));
    }
    private sealed class Boundary(IOrchestrationStore inner, bool after, bool scrub = false) : IOrchestrationFeatureStore
    {
        public Task RequireFeatureVersionAsync(int version, CancellationToken cancellationToken = default) => ((IOrchestrationFeatureStore)inner).RequireFeatureVersionAsync(version, cancellationToken);
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        {
            var boundary = scrub ? mutations.Any(m => m.Key.StartsWith("encrypted-scrub/v1/", StringComparison.Ordinal) && m.Payload is not null)
                : mutations.Any(m => m.Key.StartsWith("encrypted-blob/v2/", StringComparison.Ordinal) && m.Payload is { } payload &&
                JsonNode.Parse(payload)?["Complete"]?["KeyVersion"]?.GetValue<int>() == 2 && JsonNode.Parse(payload)?["Partial"] is null);
            if (boundary && !after) await Pause();
            var result = await inner.TryWriteAsync(mutations, fence, cancellationToken);
            if (boundary && after && result) await Pause();
            return result;
        }
        private static async Task Pause() { Console.WriteLine("chunk-publication"); await Task.Delay(Timeout.Infinite); }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
