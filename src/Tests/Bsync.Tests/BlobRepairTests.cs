using System.Net;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class BlobRepairTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bsync-recovery-").FullName;
    private readonly InMemoryOrchestrationStore _store = new();
    private static readonly byte[] Content = [1, 2, 3, 4];
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Content));
    private string ObjectPath => Path.Combine(_root, "objects", Hash[..2], Hash);
    public void Dispose() => Directory.Delete(_root, true);
    private ManagedBlobCache Cache(long capacity = 1024, IOrchestrationStore? store = null) =>
        new(new FileBlobCache(_root), store ?? _store, new FileBlobCacheLock(_root + "/locks"), capacity, capacity);
    private static OfflinePackageManifest Manifest(string revision, string hash) => new(1, "manual", revision, null, [new("page", hash, 4, true, [])]);
    private (BlobTransferManager Transfers, OfflinePackageManager Packages) Managers(ManagedBlobCache cache, HttpClient http, IOrchestrationStore? store = null)
    {
        var transfers = new BlobTransferManager(store ?? _store, cache, new(http));
        return (transfers, new(store ?? _store, cache, transfers, new FileBlobCacheLock(_root + "/locks"), (_, _) => Task.FromResult(true)));
    }
    private async Task Damage(ManagedBlobCache cache, OfflinePackageManager packages)
    {
        await cache.ImportAsync(new MemoryStream(Content));
        await packages.StageAsync(Manifest("one", Hash));
        await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        await File.WriteAllBytesAsync(ObjectPath, [9, 9, 9, 9]);
    }

    [Fact]
    public async Task AuthorizationRecheckRetainsReplacementStagingAndOldGeneration()
    {
        var cache = Cache(); var allowed = false;
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://repair.test/") };
        var transfers = new BlobTransferManager(_store, cache, new(http), new() { AuthorizePromotionAsync = (_, _, _) => ValueTask.FromResult(allowed) });
        var packages = new OfflinePackageManager(_store, cache, transfers, new FileBlobCacheLock(_root + "/locks"), (_, _) => Task.FromResult(true));
        await Damage(cache, packages);
        await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(ObjectPath));
        Assert.Equal(4, await cache.PartialLengthAsync(Hash));
        Assert.Equal("authorization-required", (await transfers.GetRepairAsync(Hash))!.ErrorCode);
        allowed = true;
        await transfers.SetStateAsync(Hash, BlobTransferDirection.Download, BlobTransferState.Queued);
        await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(Content, await File.ReadAllBytesAsync(ObjectPath));
    }

    [Fact]
    public async Task PhysicalInventoryCountsRetiredReaderBytesUntilExclusiveReclamation()
    {
        var backing = new FileBlobCache(_root);
        await backing.ImportAsync(new MemoryStream(Content));
        var reader = (await backing.OpenReadAsync(Hash))!;
        await backing.ImportAsync(new MemoryStream(Content));
        Assert.Equal(8, await backing.GetStoredBytesAsync());
        Assert.Equal(0, await backing.CollectRetiredAsync());
        await reader.DisposeAsync();
        Assert.Equal(4, await backing.CollectRetiredAsync());
        Assert.Equal(4, await backing.GetStoredBytesAsync());
    }

    [Fact]
    public async Task RetiredCollectionAdvancesPastUnknownAndLiveFilesAcrossRestarts()
    {
        var cache = new FileBlobCache(_root);
        var retired = Path.Combine(_root, "retired");
        var first = new string('0', 64) + "-" + new string('0', 32) + ".old";
        var last = new string('1', 64) + "-" + new string('1', 32) + ".old";
        await File.WriteAllBytesAsync(Path.Combine(retired, "!unknown"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(retired, first), [2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(retired, last), [4, 5, 6]);
        await using (var reader = new FileStream(Path.Combine(retired, first), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var page = await cache.CollectRetiredPageAsync(limit: 2);
            Assert.Equal(2, page.ExaminedFiles); Assert.Equal(0, page.RemovedBytes); Assert.Equal(first, page.Continuation);
            var next = await new FileBlobCache(_root).CollectRetiredPageAsync(page.Continuation, 2);
            Assert.Equal(1, next.ExaminedFiles); Assert.Equal(3, next.RemovedBytes); Assert.Null(next.Continuation);
            await File.WriteAllBytesAsync(Path.Combine(retired, last), [4, 5, 6]);
            Assert.Equal(0, await cache.CollectRetiredAsync(2));
            Assert.Equal(3, await cache.CollectRetiredAsync(2));
            Assert.Equal(3, await cache.GetStoredBytesAsync());
        }
        Assert.Equal(2, await cache.CollectRetiredAsync(2));
        Assert.Equal(1, await cache.GetStoredBytesAsync());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.CollectRetiredPageAsync(cancellationToken: canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.CollectRetiredPageAsync("../outside"));
    }

    [Fact]
    public async Task ActiveRevisionRepairsEvenWhenCandidateIsUnavailable()
    {
        var cache = Cache();
        using var http = new HttpClient(new Handler(r => r.RequestUri!.AbsolutePath.EndsWith(Hash, StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) } : new(HttpStatusCode.NotFound))) { BaseAddress = new("https://repair.test/") };
        var (transfers, packages) = Managers(cache, http);
        await Damage(cache, packages);
        await packages.StageAsync(Manifest("two", new string('a', 64)));
        var status = await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        Assert.Equal("one", status.ActiveRevision);
        Assert.Equal(OfflinePackageReadiness.ReadyKnownOutdated, status.Readiness);
        Assert.Equal(BlobRepairState.Completed, (await transfers.GetRepairAsync(Hash))!.State);
        Assert.False((await packages.GetPreparationAsync("manual"))!.RequiredContentComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityAndBadSourcePreserveOldBytesAndDoNotRetryForever(bool capacity)
    {
        var cache = Cache(capacity ? 7 : 1024);
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(capacity ? Content : [8, 8, 8, 8]) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://repair.test/") };
        var (transfers, packages) = Managers(cache, http);
        await Damage(cache, packages);
        Assert.Equal(OfflinePackageReadiness.Incomplete, (await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10))).Readiness);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(ObjectPath));
        var job = (await transfers.GetAsync(Hash, BlobTransferDirection.Download))!;
        Assert.Equal(capacity ? "capacity-required" : "hash-or-length-mismatch", job.ErrorCode);
        Assert.Equal(capacity ? BlobRepairState.Deferred : BlobRepairState.RecoveryRequired, (await transfers.GetRepairAsync(Hash))!.State);
        await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OldReaderSurvivesReplacementAndConcurrentImportWaitsForRepair()
    {
        var cache = Cache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (_, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) };
        })) { BaseAddress = new("https://repair.test/") };
        var (_, packages) = Managers(cache, http);
        await Damage(cache, packages);
        await using var reader = await cache.OpenReadAsync(Hash);
        var repair = packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var import = cache.ImportAsync(new MemoryStream(Content));
        Assert.False(import.IsCompleted);
        release.SetResult();
        await Task.WhenAll(repair, import).WaitAsync(TimeSpan.FromSeconds(10));
        byte[] old = new byte[4]; await reader!.ReadExactlyAsync(old);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, old);
        Assert.Equal(Content, await File.ReadAllBytesAsync(ObjectPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationDeadlineCoversAuthorizationAndRetainsActiveRevision(bool cooperative)
    {
        var time = new FakeTimeProvider(); var expire = false;
        var cache = Cache();
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network"))) { BaseAddress = new("https://repair.test/") };
        var transfers = new BlobTransferManager(_store, cache, new(http));
        var packages = new OfflinePackageManager(_store, cache, transfers, new FileBlobCacheLock(_root + "/locks"), (_, ct) =>
        {
            if (expire) time.Advance(TimeSpan.FromSeconds(2));
            if (cooperative) ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }, time);
        await cache.ImportAsync(new MemoryStream(Content));
        await packages.StageAsync(Manifest("one", Hash));
        Assert.True(await packages.TryActivateAsync("manual", "one"));
        await packages.StageAsync(Manifest("two", new string('a', 64)));
        expire = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packages.PrepareAsync("manual", TimeSpan.FromSeconds(1)));
        expire = false;
        Assert.Equal("one", (await packages.GetStatusAsync("manual")).ActiveRevision);
    }

    [Fact]
    public async Task CancellationKeepsReplacementStagingAndRestartFinishesWithoutRedownload()
    {
        var cache = Cache();
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://repair.test/") };
        using var stop = new CancellationTokenSource();
        var transport = new HttpBlobTransfer(http);
        var transfers = new BlobTransferManager(_store, cache, transport);
        var packages = new OfflinePackageManager(_store, cache, transfers, new FileBlobCacheLock(_root + "/locks"), (_, _) => Task.FromResult(true));
        await Damage(cache, packages);
        transport.Progress += progress => { if (!progress.Verified && progress.AvailableBytes == 4) stop.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packages.PrepareAsync("manual", TimeSpan.FromSeconds(10), stop.Token));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(ObjectPath));
        Assert.Equal(4, await cache.PartialLengthAsync(Hash));
        Assert.Null(await packages.GetLastVerificationAsync("manual"));
        var restarted = Managers(cache, http);
        Assert.Equal(OfflinePackageReadiness.ReadyFreshnessUnknown, (await restarted.Packages.PrepareAsync("manual", TimeSpan.FromSeconds(10))).Readiness);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(BlobRepairState.Completed, (await restarted.Transfers.GetRepairAsync(Hash))!.State);
    }

    [Fact]
    public async Task TerminalCleanupRecoversAfterDeleteBeforeUnpinAndPreservesOtherOwners()
    {
        var store = new FaultStore(_store);
        var cache = Cache(store: store);
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network"))) { BaseAddress = new("https://repair.test/") };
        var (transfers, _) = Managers(cache, http, store);
        await cache.ImportPinnedAsync(new MemoryStream(Content), "pending/draft");
        await transfers.EnqueueAsync(Hash, 4, BlobTransferDirection.Download);
        await transfers.DrainAsync(TimeSpan.FromSeconds(10));
        store.FailAfterCleanupCommit = true;
        await Assert.ThrowsAsync<IOException>(() => transfers.ReleaseTerminalAsync(Hash, BlobTransferDirection.Download));
        Assert.Null(await transfers.GetAsync(Hash, BlobTransferDirection.Download));
        Assert.Equal(2, (await cache.GetEntryAsync(Hash)).Owners.Length);
        var restarted = Managers(cache, http, store).Transfers;
        Assert.Equal(1, await restarted.ReconcileReleasedAsync(1));
        Assert.Equal(["pending/draft"], (await cache.GetEntryAsync(Hash)).Owners);
        Assert.Equal(0, await restarted.ReconcileReleasedAsync());
        await restarted.EnqueueAsync(Hash, 4, BlobTransferDirection.Download);
        Assert.Equal(2, (await cache.GetEntryAsync(Hash)).Owners.Length);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(action(request)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class FaultStore(IOrchestrationStore inner) : IOrchestrationStore
    {
        public bool FailAfterCleanupCommit;
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.TryWriteAsync(mutations, fence, cancellationToken);
            if (result && FailAfterCleanupCommit && mutations.Any(m => m.Key.StartsWith("transfer-cleanup/", StringComparison.Ordinal) && m.Payload is not null))
            { FailAfterCleanupCommit = false; throw new IOException("Lost cleanup commit acknowledgement"); }
            return result;
        }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
