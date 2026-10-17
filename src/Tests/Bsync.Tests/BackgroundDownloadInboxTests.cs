using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Samples.NativeBackground;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class BackgroundDownloadInboxTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-download-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly AccountScope _account = new("sample", "test", "alice");
    private readonly InMemoryOrchestrationStore _cacheStore = new();
    private static readonly byte[] Bytes = "Downloaded without a live UI instance"u8.ToArray();
    private static string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
    private BackgroundDownloadInbox Inbox(IOrchestrationStore store, long budget = 1024) => new(_account, store,
        new FileBlobCacheLock(Path.Combine(_root, "inbox-locks")), Path.Combine(_root, "spool"), budget);
    private ManagedBlobCache Cache(long budget = 1024) => new(new FileBlobCache(Path.Combine(_root, "cache")), _cacheStore,
        new FileBlobCacheLock(Path.Combine(_root, "cache-locks")), budget, budget);
    private Task<IBackgroundDownloadScope?> Open(string key, CancellationToken ct)
    {
        Assert.Equal(_account.StorageKey, key);
        return Task.FromResult<IBackgroundDownloadScope?>(new Scope(_account, Cache()));
    }
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(_root, "receipts.db"));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private async Task<string> Temporary(byte[]? bytes = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllBytesAsync(path, bytes ?? Bytes);
        return path;
    }

    [Fact]
    public async Task RelaunchUsesDurableReceiptAndImportsOnceWithoutOriginalScope()
    {
        string id;
        await using (var store = await SqliteOrchestrationStore.OpenAsync(Path.Combine(_root, "receipts.db"), _account))
        {
            var inbox = Inbox(store);
            id = await inbox.PrepareAsync(Hash, Bytes.Length, "package/one");
            var temporary = await Temporary();
            await inbox.AcceptAsync(id, temporary, 200);
            Assert.False(File.Exists(temporary));
        }
        await using var reopened = await SqliteOrchestrationStore.OpenAsync(Path.Combine(_root, "receipts.db"), _account);
        var restarted = Inbox(reopened);
        Assert.Equal(id, Assert.Single(await restarted.ListAsync()).Id);
        Assert.Equal(1, await restarted.DrainAsync(Open));
        Assert.Equal(0, await restarted.DrainAsync(Open));
        Assert.True((await restarted.GetAsync(id))!.Imported);
        Assert.False(restarted.HasStagedFile(id));
        Assert.Equal(["package/one"], (await Cache().GetEntryAsync(Hash)).Owners);
        // A duplicate callback delivered after completion cannot replace the verified bytes.
        var late = await Temporary(new byte[Bytes.Length]);
        await restarted.AcceptAsync(id, late, 200);
        Assert.True(File.Exists(late));
        Assert.False(restarted.HasStagedFile(id));
    }

    [Fact]
    public async Task LockedKeysWrongAccountAndCapacityFailureRetainDownload()
    {
        await using var store = new InMemoryOrchestrationStore();
        var inbox = Inbox(store);
        var id = await inbox.PrepareAsync(Hash, Bytes.Length, "attachment/one");
        await inbox.AcceptAsync(id, await Temporary(), 200);
        await Assert.ThrowsAsync<ReplicaKeyUnavailableException>(() => inbox.DrainAsync((_, _) => throw new ReplicaKeyUnavailableException(true)));
        Assert.Equal(0, await inbox.DrainAsync((_, _) => Task.FromResult<IBackgroundDownloadScope?>(null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.DrainAsync((_, _) => Task.FromResult<IBackgroundDownloadScope?>(new Scope(new("sample", "test", "bob"), Cache()))));
        await Assert.ThrowsAsync<BlobCapacityException>(() => inbox.DrainAsync((_, _) => Task.FromResult<IBackgroundDownloadScope?>(new Scope(_account, Cache(1)))));
        Assert.True(inbox.HasStagedFile(id));
        Assert.False((await inbox.GetAsync(id))!.Imported);
        Assert.Null(await Cache().SizeAsync(Hash));
        Assert.Equal(1, await inbox.DrainAsync(Open));
    }

    [Fact]
    public async Task BadStatusLengthAndHashNeverBecomeCompleteAndRequireExplicitRepair()
    {
        await using var store = new InMemoryOrchestrationStore();
        var inbox = Inbox(store);
        var id = await inbox.PrepareAsync(Hash, Bytes.Length, "package/one");
        var denied = await Temporary(); var shortFile = await Temporary([1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => inbox.AcceptAsync(id, denied, 403));
        await Assert.ThrowsAsync<InvalidDataException>(() => inbox.AcceptAsync(id, shortFile, 200));
        await inbox.AcceptAsync(id, await Temporary(new byte[Bytes.Length]), 200);
        await Assert.ThrowsAsync<InvalidDataException>(() => inbox.DrainAsync(Open));
        Assert.Null(await Cache().SizeAsync(Hash));
        Assert.True(inbox.HasStagedFile(id));
        await inbox.ForgetAsync(id);
        Assert.Null(await inbox.GetAsync(id));
        Assert.False(inbox.HasStagedFile(id));
        var late = await Temporary();
        await Assert.ThrowsAsync<InvalidDataException>(() => inbox.AcceptAsync(id, late, 200));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.GetAsync("../outside"));
    }

    [Fact]
    public async Task ReservationsAndUnknownFilesConsumeBoundedCapacity()
    {
        await using var store = new InMemoryOrchestrationStore();
        var inbox = Inbox(store, Bytes.Length);
        var id = await inbox.PrepareAsync(Hash, Bytes.Length, "package/one");
        await Assert.ThrowsAsync<BlobCapacityException>(() => inbox.PrepareAsync(Hash, 1, "package/two"));
        await inbox.ForgetAsync(id);
        await File.WriteAllBytesAsync(Path.Combine(_root, "spool", "unknown"), Bytes);
        await Assert.ThrowsAsync<BlobCapacityException>(() => inbox.PrepareAsync(Hash, 1, "package/two"));
        Assert.True(File.Exists(Path.Combine(_root, "spool", "unknown")));
    }

    [Fact]
    public async Task CrashAfterImportBeforeReceiptCommitReplaysWithTheSamePin()
    {
        await using var store = new InMemoryOrchestrationStore();
        var crash = new FailCommit(store);
        var inbox = Inbox(crash);
        var id = await inbox.PrepareAsync(Hash, Bytes.Length, "package/one");
        await inbox.AcceptAsync(id, await Temporary(), 200);
        crash.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => inbox.DrainAsync(Open));
        Assert.Equal(Bytes.Length, await Cache().SizeAsync(Hash));
        Assert.True(inbox.HasStagedFile(id));
        Assert.False((await inbox.GetAsync(id))!.Imported);
        Assert.Equal(1, await Inbox(store).DrainAsync(Open));
        Assert.Equal(["package/one"], (await Cache().GetEntryAsync(Hash)).Owners);
    }

    private sealed class Scope(AccountScope account, ManagedBlobCache cache) : IBackgroundDownloadScope
    {
        public AccountScope Account => account;
        public ManagedBlobCache Cache => cache;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FailCommit(IOrchestrationStore inner) : IOrchestrationStore
    {
        public bool Fail { get; set; }
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken ct = default) => inner.ReadAsync(key, ct);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken ct = default) => inner.ReadPageAsync(prefix, afterKey, limit, ct);
        public Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("Injected receipt commit failure after cache import.") : inner.TryWriteAsync(mutations, fence, cancellationToken);
        public Task DeleteAllAsync(CancellationToken ct = default) => inner.DeleteAllAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
