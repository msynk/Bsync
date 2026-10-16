using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class OfflinePackageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-package-" + Guid.NewGuid().ToString("N"));
    private SqliteOrchestrationStore _store = null!;
    private ManagedBlobCache _cache = null!;
    private OfflinePackageManager _manager = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = await SqliteOrchestrationStore.OpenAsync(Path.Combine(_root, "state.db"), new("app", "test", "alice"));
        Reconstruct();
    }
    private void Reconstruct()
    {
        var locks = new FileBlobCacheLock(Path.Combine(_root, "locks"));
        _cache = new(new FileBlobCache(Path.Combine(_root, "bytes")), _store, locks, 1024, 1024);
        var transfers = new BlobTransferManager(_store, _cache, new(new HttpClient(new NoNetwork()) { BaseAddress = new("https://offline.test/") }));
        _manager = new(_store, _cache, transfers, locks, (_, _) => Task.FromResult(true));
    }
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(_root, "state.db"));
        Directory.Delete(_root, true);
    }
    private static OfflinePackageManifest Manifest(string revision, byte value) => new(1, "manual", revision, null,
        [new("page", Convert.ToHexStringLower(SHA256.HashData(new[] { value })), 1, true, [])]);

    [Fact]
    public async Task LastVerificationSurvivesRestartButDoesNotClaimToDetectLaterStorageLoss()
    {
        var first = Manifest("one", 1);
        await _manager.StageAsync(first);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateAsync("manual", "one"));
        Assert.Null(await _manager.GetLastVerificationAsync("manual"));
        var verified = await _manager.GetStatusAsync("manual");
        Reconstruct();
        Assert.Equal(verified.VerifiedUtc, (await _manager.GetLastVerificationAsync("manual"))!.VerifiedUtc);
        await new FileBlobCache(Path.Combine(_root, "bytes")).DeleteAsync(first.Items[0].Sha256);
        Assert.Equal(OfflinePackageReadiness.ReadyFreshnessUnknown, (await _manager.GetLastVerificationAsync("manual"))!.Readiness);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _manager.OpenActiveAsync("manual"));
        Assert.Equal(OfflinePackageReadiness.Incomplete, (await _manager.GetStatusAsync("manual")).Readiness);
        Assert.Equal(OfflinePackageReadiness.Incomplete, (await _manager.GetLastVerificationAsync("manual"))!.Readiness);
        await _manager.StageAsync(Manifest("two", 2));
        Assert.Null(await _manager.GetLastVerificationAsync("manual"));
    }

    [Fact]
    public async Task IncompleteCandidateNeverReplacesActiveAndReaderKeepsItsRevision()
    {
        var first = Manifest("one", 1); var second = Manifest("two", 2);
        await _manager.StageAsync(first);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateAsync("manual", "one"));
        await using var reader = await _manager.OpenActiveAsync("manual");
        await _manager.StageAsync(second);
        Assert.False(await _manager.TryActivateAsync("manual", "two"));
        Reconstruct();
        var status = await _manager.GetStatusAsync("manual");
        Assert.Equal("one", status.ActiveRevision);
        Assert.Equal(OfflinePackageReadiness.ReadyKnownOutdated, status.Readiness);
        await _cache.ImportAsync(new MemoryStream([2]));
        Assert.True(await _manager.TryActivateAsync("manual", "two"));
        await _manager.RemoveRetiredAsync("manual", "one");
        await using var old = await reader.OpenAsync("page");
        Assert.Equal(1, old!.ReadByte());
        await using var current = await _manager.OpenActiveAsync("manual");
        Assert.Equal("two", current.Revision);
    }

    [Fact]
    public async Task ExternalStorageLossInvalidatesReadinessAndOpening()
    {
        var manifest = Manifest("one", 1);
        await _manager.StageAsync(manifest);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateAsync("manual", "one"));
        await new FileBlobCache(Path.Combine(_root, "bytes")).DeleteAsync(manifest.Items[0].Sha256);
        var status = await _manager.GetStatusAsync("manual");
        Assert.Equal(OfflinePackageReadiness.Incomplete, status.Readiness);
        Assert.Equal("page", Assert.Single(status.MissingRequired));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _manager.OpenActiveAsync("manual"));
    }

    [Fact]
    public async Task OldCandidateCannotOverwriteNewerStagingAndRevisionIdentityIsImmutable()
    {
        await _manager.StageAsync(Manifest("one", 1));
        await _cache.ImportAsync(new MemoryStream([1]));
        await _manager.StageAsync(Manifest("two", 2));
        Assert.False(await _manager.TryActivateAsync("manual", "one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.StageAsync(Manifest("one", 3)));
        Assert.Equal(OfflinePackageReadiness.NoActiveRevision, (await _manager.GetStatusAsync("manual")).Readiness);
    }

    [Fact]
    public async Task RequiredDependenciesMustBePresentAndCyclesAreRejectedBeforeWriting()
    {
        var manifest = Manifest("one", 1);
        var dependency = Manifest("unused", 2).Items[0] with { Id = "dependency", Required = false };
        manifest = manifest with { Items = [manifest.Items[0] with { Dependencies = ["dependency"] }, dependency] };
        await _manager.StageAsync(manifest);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.False(await _manager.TryActivateAsync("manual", "one"));
        await _cache.ImportAsync(new MemoryStream([2]));
        Assert.True(await _manager.TryActivateAsync("manual", "one"));
        var cyclic = manifest with { Revision = "cycle", Items = [manifest.Items[0], dependency with { Dependencies = ["page"] }] };
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.StageAsync(cyclic));
        Assert.Equal("one", (await _manager.GetStatusAsync("manual")).LatestKnownRevision);
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Cold-open path made a network request.");
    }
}
