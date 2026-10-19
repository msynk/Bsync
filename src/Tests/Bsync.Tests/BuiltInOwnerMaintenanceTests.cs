using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class BuiltInOwnerMaintenanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bsync-owner-reconcile-").FullName;
    private readonly InMemoryOrchestrationStore _store = new();
    private readonly InMemoryOrchestrationStore _catalog = new();
    private readonly AccountScope _account = new("test", "local", "owners");
    private FileBlobCacheLock Locks => new(_root + "/locks");
    private ManagedBlobCache Cache => new(new FileBlobCache(_root + "/cache"), _store, Locks, 10000, 10000);
    private AccountComposition Accounts => new(_catalog, new FileAccountResourceLock(_root + "/account"));
    private static readonly MaintenancePageRequest Page = new(null, 16, 10000, false);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task DurablePackageAndTransferRecordsRestoreMissingPinsBeforeOrphanReclamation()
    {
        var cache = Cache; var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://maintenance.test/") };
        var transfers = new BlobTransferManager(_store, cache, new(http));
        var packages = new OfflinePackageManager(_store, cache, transfers, Locks, (_, _) => Task.FromResult(true));
        await packages.StageAsync(new(1, "book", "one", null, [new("page", blob.Sha256, 3, true, [])]));
        foreach (var owner in (await cache.GetEntryAsync(blob.Sha256)).Owners) await cache.UnpinAsync(blob.Sha256, owner);
        await using var scope = await Accounts.OpenMaintenanceAsync(_account);
        await packages.ReconcilePinsAsync(scope, Page with { DryRun = true });
        await transfers.ReconcilePinsAsync(scope, Page with { DryRun = true });
        Assert.Empty((await cache.GetEntryAsync(blob.Sha256)).Owners);
        Assert.True((await packages.ReconcilePinsAsync(scope, Page with { MaximumBytes = 2 })).RequiresLargerBudget);
        Assert.Empty((await cache.GetEntryAsync(blob.Sha256)).Owners);
        await packages.ReconcilePinsAsync(scope, Page); await transfers.ReconcilePinsAsync(scope, Page);
        Assert.Equal(2, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
        Assert.Equal(0, (await cache.ReconcileBuiltInOwnersAsync(scope, Page)).ReleasedOwners);
        Assert.Equal(BlobTransferState.Queued, (await transfers.GetAsync(blob.Sha256, BlobTransferDirection.Download))!.State);
    }

    [Fact]
    public async Task MissingDurableOwnersAndDeadLifetimesReleaseButLiveAndUnknownOwnersRemain()
    {
        var cache = Cache; var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        var missingPackage = "package/" + Hash("missing") + "/" + Hash("one");
        var missingTransfer = "transfers/" + Hash("endpoint") + "/down/" + blob.Sha256;
        var reader = Hash("reader"); var dead = "reader-v2/" + Hash("dead-rotation");
        foreach (var owner in new[] { missingPackage, missingTransfer, dead, "snapshot-v2/" + reader, "app/draft", "reader/legacy" })
            await cache.PinAsync(blob.Sha256, owner);
        await using var scope = await Accounts.OpenMaintenanceAsync(_account);
        // A nonparticipating reader still has an independent physical lifetime proof; never use account age to override it.
        await using (var live = await Locks.AcquireAsync("lifetime-" + reader))
        {
            var dry = await cache.ReconcileBuiltInOwnersAsync(scope, Page with { DryRun = true });
            Assert.Equal(3, dry.ReleasedOwners); Assert.Equal(2, dry.UnknownOwners);
            Assert.Equal(6, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
            var actual = await cache.ReconcileBuiltInOwnersAsync(scope, Page);
            Assert.Equal(3, actual.ReleasedOwners); Assert.Equal(3, actual.RetainedOwners);
        }
        Assert.Equal(1, (await cache.ReconcileBuiltInOwnersAsync(scope, Page)).ReleasedOwners);
        Assert.Equal(new[] { "app/draft", "reader/legacy" }, (await cache.GetEntryAsync(blob.Sha256)).Owners);
    }

    [Fact]
    public async Task UnresolvedRepairAndMalformedPackageRemainProtectedAndByteRefusalChangesNothing()
    {
        var cache = Cache; var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        var owner = "transfers/" + Hash("endpoint") + "/down/" + blob.Sha256;
        var package = Hash("book") + "/" + Hash("one");
        await cache.PinAsync(blob.Sha256, owner); await cache.PinAsync(blob.Sha256, "package/" + package);
        await _store.TryWriteAsync([new("repair/v1/" + owner["transfers/".Length..], null,
            JsonSerializer.Serialize(new BlobRepairJob(1, blob.Sha256, 3, "https://maintenance.test/", "generation", BlobRepairState.RecoveryRequired, 1, null, "invalid-content"))),
            new("packages/" + package, null, "future-format")]);
        await using var scope = await Accounts.OpenMaintenanceAsync(_account);
        var refusal = await cache.ReconcileBuiltInOwnersAsync(scope, Page with { MaximumBytes = 2 });
        Assert.True(refusal.Progress.RequiresLargerBudget); Assert.Equal(0, refusal.Progress.Items);
        var result = await cache.ReconcileBuiltInOwnersAsync(scope, Page);
        Assert.Equal(0, result.ReleasedOwners); Assert.Equal(2, result.UnknownOwners);
        Assert.Equal(2, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
    }

    [Fact]
    public async Task MissingManifestWithCurrentPointerAndFutureCleanupJournalNeverReleasePins()
    {
        var cache = Cache; var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        var package = Hash("book"); var revision = Hash("one");
        var owner = "transfers/" + Hash("endpoint") + "/down/" + blob.Sha256;
        await cache.PinAsync(blob.Sha256, "package/" + package + "/" + revision);
        await cache.PinAsync(blob.Sha256, owner);
        await _store.TryWriteAsync([new("packages/" + package + "/active", null, "one"),
            new("transfer-cleanup/v1/" + owner, null, "future-format")]);
        await using var scope = await Accounts.OpenMaintenanceAsync(_account);
        var result = await cache.ReconcileBuiltInOwnersAsync(scope, Page);
        Assert.Equal(0, result.ReleasedOwners); Assert.Equal(2, result.UnknownOwners);
        Assert.Equal(2, (await cache.GetEntryAsync(blob.Sha256)).Owners.Length);
    }

    [Fact]
    public async Task UnexpectedConcurrentPinChangeFailsCasAndPreservesEveryOwner()
    {
        var cache = Cache; var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        var owner = "transfers/" + Hash("endpoint") + "/down/" + blob.Sha256;
        await cache.PinAsync(blob.Sha256, owner);
        var observed = new ReadHook(_store, async key =>
        {
            if (key == owner) await cache.PinAsync(blob.Sha256, "app/concurrent");
        });
        var reconciler = new ManagedBlobCache(new FileBlobCache(_root + "/cache"), observed, Locks, 10000, 10000);
        await using var scope = await Accounts.OpenMaintenanceAsync(_account);
        var result = await reconciler.ReconcileBuiltInOwnersAsync(scope, Page);
        Assert.Equal(1, result.Conflicts); Assert.Equal(0, result.ReleasedOwners);
        Assert.Equal(new[] { owner, "app/concurrent" }, (await cache.GetEntryAsync(blob.Sha256)).Owners);
    }

    [Fact]
    public async Task SharedOrDisposedScopesCannotAuthorizePinReclamation()
    {
        var scope = await Accounts.OpenWorkAsync(_account);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cache.ReconcileBuiltInOwnersAsync(scope, Page));
        await scope.DisposeAsync();
        scope = await Accounts.OpenMaintenanceAsync(_account); await scope.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Cache.ReconcileBuiltInOwnersAsync(scope, Page));
    }

    private sealed class NoNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Maintenance must not contact the server."); }

    private sealed class ReadHook(IOrchestrationStore inner, Func<string, Task> hook) : IOrchestrationStore
    {
        public async Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { await hook(key); return await inner.ReadAsync(key, cancellationToken); }
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default) => inner.TryWriteAsync(mutations, fence, cancellationToken);
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
