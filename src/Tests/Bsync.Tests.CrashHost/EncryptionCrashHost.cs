using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

internal static class EncryptionCrashHost
{
    public static async Task RunAsync(string database, bool afterCommit)
    {
        var account = new AccountScope("tests", "local", "account");
        var root = Path.GetDirectoryName(database)!;
        await using var sqlite = await SqliteOrchestrationStore.OpenAsync(database, account);
        var metadata = new BlockingMetadata(sqlite, afterCommit);
        var backing = new FileBlobCache(Path.Combine(root, "ciphertext"));
        var locks = new FileBlobCacheLock(Path.Combine(root, "locks"));
        EncryptedBlobCache Cache(int version) => new(backing, metadata, locks, new Keys(), new(account, "blobs", version), new PlatformBlobEncryption());
        var cache = Cache(1);
        var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3, 4]));
        await cache.AppendPartialAsync(blob.Sha256, new byte[] { 1, 2 });
        metadata.Armed = true;
        await Cache(2).RotateAsync(blob.Sha256);
        throw new InvalidOperationException("Expected rotation boundary was not reached.");
    }

    private sealed class Keys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)reference.Version, 32).ToArray()));
    }

    private sealed class BlockingMetadata(IOrchestrationStore inner, bool afterCommit) : IOrchestrationStore
    {
        public bool Armed;
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        {
            var stop = Armed && mutations.Any(m => m.Key.StartsWith("encrypted-blob/v1/", StringComparison.Ordinal));
            if (stop && !afterCommit) { Console.WriteLine("rotation-boundary"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            var result = await inner.TryWriteAsync(mutations, fence, cancellationToken);
            if (stop && afterCommit) { Console.WriteLine("rotation-boundary"); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return result;
        }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
