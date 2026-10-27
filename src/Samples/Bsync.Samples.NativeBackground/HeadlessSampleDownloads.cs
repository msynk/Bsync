using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage.Sqlite;

namespace Bsync.Samples.NativeBackground;

public sealed partial class HeadlessSampleHost
{
    public string DownloadDirectory => Path.Combine(directory, "downloads");

    /// <summary>Reopens receipts only for an explicitly registered account. OS callbacks cannot provision an account.</summary>
    public async Task<SampleDownloadRegistration?> OpenDownloadsAsync(CancellationToken cancellationToken = default)
    {
        await using var catalog = await Catalog(cancellationToken).ConfigureAwait(false);
        var access = await Locks.TryAcquireAsync(DemoAccount, false, cancellationToken).ConfigureAwait(false);
        if (access is null) return null;
        var transferred = false;
        try
        {
            if (await catalog.ReadAsync("registered/" + DemoAccount.StorageKey, cancellationToken).ConfigureAwait(false) is not { Payload: "1" }
                || await catalog.ReadAsync("deleting/" + DemoAccount.StorageKey, cancellationToken).ConfigureAwait(false) is not null) return null;
            Directory.CreateDirectory(DownloadDirectory);
            var store = await SqliteOrchestrationStore.OpenAsync(Path.Combine(DownloadDirectory, "receipts.db"), DemoAccount, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                var registration = new SampleDownloadRegistration(store,
                    new(DemoAccount, store, new FileBlobCacheLock(Path.Combine(DownloadDirectory, "receipt-locks")), Path.Combine(DownloadDirectory, "spool")), access);
                transferred = true; return registration;
            }
            catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { if (!transferred) await access.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Fresh verified-cache lifetime for receipt import; a deleted registration always returns null.</summary>
    public async Task<IBackgroundDownloadScope?> OpenDownloadScopeAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (storageKey != DemoAccount.StorageKey) return null;
        var catalog = await Catalog(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? access = null; SqliteOrchestrationStore? metadata = null;
        try
        {
            access = await Locks.TryAcquireAsync(DemoAccount, false, cancellationToken).ConfigureAwait(false);
            if (access is null || await catalog.ReadAsync("registered/" + storageKey, cancellationToken).ConfigureAwait(false) is not { Payload: "1" }
                || await catalog.ReadAsync("deleting/" + storageKey, cancellationToken).ConfigureAwait(false) is not null)
            { await catalog.DisposeAsync().ConfigureAwait(false); if (access is not null) await access.DisposeAsync().ConfigureAwait(false); return null; }
            Directory.CreateDirectory(DownloadDirectory);
            metadata = await SqliteOrchestrationStore.OpenAsync(Path.Combine(DownloadDirectory, "cache.db"), DemoAccount, cancellationToken: cancellationToken).ConfigureAwait(false);
            var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(DownloadDirectory, "cache")), metadata,
                new FileBlobCacheLock(Path.Combine(DownloadDirectory, "cache-locks")), 512L * 1024 * 1024, 512L * 1024 * 1024);
            return new DownloadScope(cache, metadata, catalog, access);
        }
        catch
        {
            if (metadata is not null) await metadata.DisposeAsync().ConfigureAwait(false);
            await catalog.DisposeAsync().ConfigureAwait(false); if (access is not null) await access.DisposeAsync().ConfigureAwait(false); throw;
        }
    }

    /// <summary>
    /// Durable deletion fence precedes OS cancellation and joined receipt-store disposal. Retrying after interruption is
    /// safe. New callbacks cannot reopen the account; permanent demo tombstones prevent accidental identity reuse.
    /// </summary>
    public async Task DeleteDemoAsync(Func<Task> cancelAndJoin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancelAndJoin);
        await using var catalog = await Catalog(cancellationToken).ConfigureAwait(false);
        var tombstone = "deleting/" + DemoAccount.StorageKey;
        var existing = await catalog.ReadAsync(tombstone, cancellationToken).ConfigureAwait(false);
        if (existing is null && !await catalog.TryWriteAsync([new(tombstone, null, "1")], cancellationToken: cancellationToken).ConfigureAwait(false))
            throw new IOException("Deletion ownership changed; retry.");
        await cancelAndJoin().ConfigureAwait(false);
        await using var access = await Locks.TryAcquireAsync(DemoAccount, true, cancellationToken).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
        var registration = await catalog.ReadAsync("registered/" + DemoAccount.StorageKey, cancellationToken).ConfigureAwait(false);
        if (registration is not null && !await catalog.TryWriteAsync([new(registration.Key, registration.Revision, null)], cancellationToken: cancellationToken).ConfigureAwait(false))
            throw new IOException("Registration changed; deletion retained for retry.");
        if (Directory.Exists(DownloadDirectory))
        {
            await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(DownloadDirectory, "receipts.db"), cancellationToken).ConfigureAwait(false);
            await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(DownloadDirectory, "cache.db"), cancellationToken).ConfigureAwait(false);
            Directory.Delete(DownloadDirectory, recursive: true);
        }
    }

    private sealed class DownloadScope(ManagedBlobCache cache, SqliteOrchestrationStore metadata,
        SqliteOrchestrationStore catalog, IAsyncDisposable access) : IBackgroundDownloadScope
    {
        public AccountScope Account => DemoAccount;
        public ManagedBlobCache Cache => cache;
        public async ValueTask DisposeAsync()
        {
            try { await metadata.DisposeAsync().ConfigureAwait(false); await catalog.DisposeAsync().ConfigureAwait(false); }
            finally { await access.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

public sealed class SampleDownloadRegistration(SqliteOrchestrationStore store, BackgroundDownloadInbox inbox, IAsyncDisposable accountAccess) : IAsyncDisposable
{
    private bool _disposed;
    public BackgroundDownloadInbox Inbox { get; } = inbox;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await store.DisposeAsync().ConfigureAwait(false); }
        finally { await accountAccess.DisposeAsync().ConfigureAwait(false); }
    }
}
