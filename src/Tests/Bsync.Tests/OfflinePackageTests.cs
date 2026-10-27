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
    public async Task OptionalChildrenDoNotBlockActivationAndSnapshotsKeepTheirObservedMembership()
    {
        var required = Manifest("one", 1);
        var optional = Manifest("one", 2) with { PackageId = "optional" };
        var root = new OfflinePackageSetManifest(2, "optional-set", "one", null,
            [new(required.PackageId, required.Revision, OfflinePackageManager.GetManifestSha256(required)),
             new(optional.PackageId, optional.Revision, OfflinePackageManager.GetManifestSha256(optional)) { Optional = true }]);
        Assert.Throws<ArgumentException>(() => OfflinePackageManager.ValidateSet(root with { SchemaVersion = 1 }));
        await _manager.StageSetAsync(root, [required, optional]);
        Assert.False(await _manager.TryActivateSetAsync(root.SetId, root.Revision));
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateSetAsync(root.SetId, root.Revision));
        var status = await _manager.GetSetStatusAsync(root.SetId);
        Assert.Empty(status.MissingRequired); Assert.Equal(0, status.BytesRemaining);
        Assert.Equal("incomplete", Assert.Single(status.OptionalFailures).Reason);
        await using var old = await _manager.OpenActiveSetAsync(root.SetId);
        Assert.Null(await old.OpenAsync(optional.PackageId, "page"));
        await _cache.ImportAsync(new MemoryStream([2]));
        Assert.Null(await old.OpenAsync(optional.PackageId, "page"));
        await using var repaired = await _manager.OpenActiveSetAsync(root.SetId);
        await using var content = await repaired.OpenAsync(optional.PackageId, "page");
        Assert.NotNull(content);
        var locks = new FileBlobCacheLock(Path.Combine(_root, "locks"));
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://offline.test/") };
        var revoked = new OfflinePackageManager(_store, _cache, new(_store, _cache, new(http)), locks,
            (manifest, _) => Task.FromResult(manifest.PackageId != optional.PackageId));
        Assert.True(await revoked.TryActivateSetAsync(root.SetId, root.Revision));
        Assert.Equal("unauthorized", Assert.Single((await revoked.GetSetStatusAsync(root.SetId)).OptionalFailures).Reason);
        await using var limited = await revoked.OpenActiveSetAsync(root.SetId);
        Assert.Null(await limited.OpenAsync(optional.PackageId, "page"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => limited.OpenAsync("unknown", "page"));
    }

    [Fact]
    public async Task PackageSetQuotaRevocationAndCorruptionPreserveActiveRootAndPins()
    {
        var locks = new FileBlobCacheLock(Path.Combine(_root, "locks"));
        var limited = new ManagedBlobCache(new FileBlobCache(Path.Combine(_root, "bytes")), _store, locks, 1, 1);
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://offline.test/") };
        var authorized = true;
        var manager = new OfflinePackageManager(_store, limited, new(_store, limited, new(http)), locks, (_, _) => Task.FromResult(authorized));
        static OfflinePackageSetManifest Root(OfflinePackageManifest child) => new(1, "set", child.Revision, null,
            [new(child.PackageId, child.Revision, OfflinePackageManager.GetManifestSha256(child))]);
        var first = Manifest("one", 1); var next = Manifest("two", 2);
        await manager.StageSetAsync(Root(first), [first]); await limited.ImportAsync(new MemoryStream([1]));
        Assert.True(await manager.TryActivateSetAsync("set", "one"));
        await manager.StageSetAsync(Root(next), [next]);
        await Assert.ThrowsAsync<BlobCapacityException>(() => limited.ImportAsync(new MemoryStream([2])));
        Assert.False(await manager.TryActivateSetAsync("set", "two"));
        Assert.Equal("one", (await manager.GetSetStatusAsync("set")).ActiveRevision);
        Assert.NotEmpty((await limited.GetEntryAsync(first.Items[0].Sha256)).Owners);
        authorized = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.TryActivateSetAsync("set", "two"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.OpenActiveSetAsync("set"));
        authorized = true;
        var hash = first.Items[0].Sha256;
        await File.WriteAllBytesAsync(Path.Combine(_root, "bytes", "objects", hash[..2], hash), [9]);
        Assert.Single((await manager.GetSetStatusAsync("set")).ActiveMissingRequired);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.OpenActiveSetAsync("set"));
        Assert.NotEmpty((await limited.GetEntryAsync(hash)).Owners);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.Empty((await manager.GetSetStatusAsync("set")).ActiveMissingRequired);
        await using var reader = await manager.OpenActiveSetAsync("set");
        await using var read = await reader.OpenAsync("manual", "page"); Assert.Equal(1, read!.ReadByte());
    }

    [Fact]
    public async Task PackageSetActivatesMoreThan256ItemsAndRetainsOldSnapshotThroughRetirement()
    {
        static OfflinePackageManifest Child(string id, string revision, byte value) => new(1, id, revision, null,
            Enumerable.Range(0, 150).Select(i => new OfflinePackageItem(i.ToString(), Convert.ToHexStringLower(SHA256.HashData(new[] { value })), 1, true, [])).ToArray());
        static OfflinePackageSetManifest Root(string revision, OfflinePackageManifest[] children) => new(1, "catalog", revision, null,
            children.Select(c => new OfflinePackageSetChild(c.PackageId, c.Revision, OfflinePackageManager.GetManifestSha256(c))).ToArray());
        var first = new[] { Child("left", "one", 1), Child("right", "one", 1) };
        await _manager.StageSetAsync(Root("one", first), first);
        Assert.Equal(1, (await _manager.GetSetStatusAsync("catalog")).BytesRemaining);
        Assert.False(await _manager.TryActivateSetAsync("catalog", "one"));
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateSetAsync("catalog", "one"));
        await using var old = await _manager.OpenActiveSetAsync("catalog");
        var next = new[] { Child("left", "two", 2), Child("right", "two", 2) };
        await _manager.StageSetAsync(Root("two", next), next);
        Assert.False(await _manager.TryActivateSetAsync("catalog", "two"));
        Assert.Equal("one", (await _manager.GetSetStatusAsync("catalog")).ActiveRevision);
        var status = await _manager.GetSetStatusAsync("catalog");
        Assert.Equal(1, status.BytesRemaining);
        Assert.Equal(300, status.MissingRequired.Length);
        Assert.Empty(status.ActiveMissingRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.RemoveRetiredAsync("left", "one"));
        Reconstruct(); // Root and child references, not process memory, govern recovery.
        await _cache.ImportAsync(new MemoryStream([2]));
        Assert.True(await _manager.TryActivateSetAsync("catalog", "two"));
        await _manager.RemoveRetiredSetAsync("catalog", "one");
        await _manager.RemoveRetiredAsync("left", "one");
        await _manager.RemoveRetiredAsync("right", "one");
        await using var oldStream = await old.OpenAsync("left", "149");
        Assert.Equal(1, oldStream!.ReadByte());
        await using var current = await _manager.OpenActiveSetAsync("catalog");
        await using var newStream = await current.OpenAsync("right", "149");
        Assert.Equal(2, newStream!.ReadByte());
        Assert.Empty((await _manager.GetSetStatusAsync("catalog")).MissingRequired);
    }

    [Fact]
    public async Task PackageSetRefusesSubstitutionAndRootReuseWithoutChangingActiveRoot()
    {
        var child = Manifest("one", 1);
        var root = new OfflinePackageSetManifest(1, "set", "one", null,
            [new(child.PackageId, child.Revision, OfflinePackageManager.GetManifestSha256(child))]);
        await _manager.StageSetAsync(root, [child]);
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateSetAsync("set", "one"));
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.StageSetAsync(root, [Manifest("one", 2)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.StageSetAsync(root with { PublishedUtc = DateTimeOffset.UtcNow }, [child]));
        Assert.Throws<ArgumentException>(() => OfflinePackageManager.ValidateSet(root with { Children = [root.Children[0], root.Children[0]] }));
        Assert.Equal("one", (await _manager.GetSetStatusAsync("set")).ActiveRevision);
    }

    [Fact]
    public async Task CandidateIssuesDoNotHideUsableActiveRevisionAndCountSharedBytesOnce()
    {
        Assert.Null(await _manager.GetPreparationAsync("manual"));
        await _manager.StageAsync(Manifest("one", 1));
        await _cache.ImportAsync(new MemoryStream([1]));
        Assert.True(await _manager.TryActivateAsync("manual", "one"));
        var next = Manifest("two", 2);
        next = next with { Items = [next.Items[0], next.Items[0] with { Id = "same-content" }, new("optional", new string('a', 64), 9, false, [])] };
        await _manager.StageAsync(next);
        var preparation = (await _manager.GetPreparationAsync("manual"))!;
        Assert.Equal("two", preparation.Revision);
        Assert.False(preparation.RequiredContentComplete);
        Assert.Equal(1, preparation.BytesRemaining);
        Assert.Equal(3, preparation.Issues.Length);
        Assert.All(preparation.Issues, issue => Assert.Equal(BlobTransferState.Queued, issue.TransferState));
        Assert.Equal(OfflinePackageReadiness.ReadyKnownOutdated, (await _manager.GetStatusAsync("manual")).Readiness);
        await _cache.ImportAsync(new MemoryStream([2]));
        preparation = (await _manager.GetPreparationAsync("manual"))!;
        Assert.True(preparation.RequiredContentComplete);
        Assert.False(Assert.Single(preparation.Issues).Required);
        Assert.Equal("one", (await _manager.GetStatusAsync("manual")).ActiveRevision);
    }

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
