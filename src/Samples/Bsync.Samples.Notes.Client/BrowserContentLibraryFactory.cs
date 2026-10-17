using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Samples.Shared;
using Microsoft.JSInterop;

namespace Bsync.Samples.Notes.Client;

public sealed class BrowserContentLibraryFactory(IJSRuntime js) : IContentLibraryFactory
{
    public async Task<ContentLibrarySession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var account = new AccountScope("notes-library", "sample", "default");
        var metadata = await IndexedDbOrchestrationStore.OpenAsync(js, account, cancellationToken);
        var locks = await BrowserBlobCacheLock.OpenAsync(js, account, cancellationToken);
        var bytes = await BrowserBlobStore.OpenAsync(js, account.StorageKey, cancellationToken);
        var cache = new ManagedBlobCache(bytes, metadata, locks, 1024 * 1024, 2 * 1024 * 1024);
        await cache.ReconcileAsync(cancellationToken);
        return new(metadata, cache, locks, async () => { await metadata.DisposeAsync(); await locks.DisposeAsync(); });
    }
}
