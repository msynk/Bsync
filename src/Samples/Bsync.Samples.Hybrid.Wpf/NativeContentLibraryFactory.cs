using System.IO;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Samples.Shared;
using Bsync.Storage.Sqlite;

namespace Bsync.Samples.Hybrid.Wpf;

public sealed class NativeContentLibraryFactory(string directory) : IContentLibraryFactory
{
    public async Task<ContentLibrarySession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var account = new AccountScope("notes-library", "sample", "default");
        var root = Path.Combine(directory, "library", account.StorageKey);
        Directory.CreateDirectory(root);
        var metadata = await SqliteOrchestrationStore.OpenAsync(Path.Combine(root, "metadata.db"), account, cancellationToken: cancellationToken);
        var locks = new FileBlobCacheLock(Path.Combine(root, "locks"));
        var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(root, "bytes")), metadata, locks, 1024 * 1024, 2 * 1024 * 1024);
        await cache.ReconcileAsync(cancellationToken);
        return new(metadata, cache, locks, metadata.DisposeAsync);
    }
}
