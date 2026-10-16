using Bsync.Blobs;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class ManagedBlobCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-managed-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryOrchestrationStore _store = new();
    private ManagedBlobCache Cache(long limit = 8) => new(new FileBlobCache(Path.Combine(_root, "bytes")), _store, new FileBlobCacheLock(Path.Combine(_root, "locks")), limit, limit);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static MemoryStream Bytes(byte value, int count = 4) => new(Enumerable.Repeat(value, count).ToArray());

    [Fact]
    public async Task ReimportRepairsSameLengthCorruptionAndPreservesOwnersAndOpenHandles()
    {
        var cache = Cache(8);
        var blob = await cache.ImportPinnedAsync(Bytes(1), "pending/work");
        var file = Path.Combine(_root, "bytes", "objects", blob.Sha256[..2], blob.Sha256);
        await File.WriteAllBytesAsync(file, [9, 9, 9, 9]);
        await using var old = await cache.OpenReadAsync(blob.Sha256);
        await cache.ImportAsync(Bytes(1));
        Assert.Equal(9, old!.ReadByte()); // A pre-existing handle is not rewritten in place.
        await using var repaired = await cache.OpenReadAsync(blob.Sha256);
        Assert.Equal(1, repaired!.ReadByte());
        Assert.Contains("pending/work", (await cache.GetEntryAsync(blob.Sha256)).Owners);
        Assert.Equal(4, (await cache.GetEntryAsync(blob.Sha256)).Bytes);
    }

    [Fact]
    public async Task RepairCapacityRefusalRetainsCorruptBytesAndPins()
    {
        var cache = Cache(4);
        var blob = await cache.ImportPinnedAsync(Bytes(1), "pending/work");
        var file = Path.Combine(_root, "bytes", "objects", blob.Sha256[..2], blob.Sha256);
        await File.WriteAllBytesAsync(file, [9, 9, 9, 9]);
        await Assert.ThrowsAsync<BlobCapacityException>(() => cache.ImportAsync(Bytes(1)));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(file));
        Assert.Contains("pending/work", (await cache.GetEntryAsync(blob.Sha256)).Owners);
    }

    [Fact]
    public async Task SourceChangingDuringRepairCannotReplaceExistingBytes()
    {
        var cache = Cache(8);
        var blob = await cache.ImportPinnedAsync(Bytes(1), "pending/work");
        var file = Path.Combine(_root, "bytes", "objects", blob.Sha256[..2], blob.Sha256);
        await File.WriteAllBytesAsync(file, [9, 9, 9, 9]);
        await Assert.ThrowsAsync<BlobCorruptedException>(() => cache.ImportAsync(new ChangingSource()));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(file));
        await cache.ReconcileAsync();
        await cache.ImportAsync(Bytes(1));
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, await File.ReadAllBytesAsync(file));
    }

    private sealed class ChangingSource() : MemoryStream(new byte[] { 1, 1, 1, 1 })
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                if (base.Position == Length && value == 0)
                {
                    base.Position = 0;
                    Write(new byte[] { 2, 2, 2, 2 });
                }
                base.Position = value;
            }
        }
    }

    [Fact]
    public async Task ReconciliationRemovesOnlyReleasedReaderLifetimePins()
    {
        var cache = Cache();
        var blob = await cache.ImportAsync(Bytes(1));
        await using var reader = await cache.OpenReadAsync(blob.Sha256);
        var abandoned = "reader-v2/" + new string('a', 64);
        await cache.PinAsync(blob.Sha256, abandoned);
        await cache.PinAsync(blob.Sha256, "reader/legacy-retained");
        await cache.PinAsync(blob.Sha256, "pending/work");
        await Cache().ReconcileAsync();
        var owners = (await cache.GetEntryAsync(blob.Sha256)).Owners;
        Assert.DoesNotContain(abandoned, owners);
        Assert.Contains("reader/legacy-retained", owners);
        Assert.Contains("pending/work", owners);
        Assert.Single(owners, owner => owner.StartsWith("reader-v2/", StringComparison.Ordinal));
        Assert.Equal(1, reader!.ReadByte());
    }

    [Fact]
    public async Task PendingPinSurvivesReconstructionAndEvictionRemovesOnlyUnprotectedBytes()
    {
        var cache = Cache();
        var pinned = await cache.ImportPinnedAsync(Bytes(1), "pending/one");
        var expendable = await cache.ImportAsync(Bytes(2));
        cache = Cache();
        await cache.ImportAsync(Bytes(3));
        Assert.NotNull(await cache.SizeAsync(pinned.Sha256));
        Assert.Null(await cache.SizeAsync(expendable.Sha256));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DeleteAsync(pinned.Sha256));
        await cache.UnpinAsync(pinned.Sha256, "pending/one");
        await cache.DeleteAsync(pinned.Sha256);
        Assert.Null(await cache.SizeAsync(pinned.Sha256));
    }

    [Fact]
    public async Task ReaderPinAndPartialReservationPreventCapacityOvercommit()
    {
        var cache = Cache();
        var first = await cache.ImportAsync(Bytes(1));
        await using (var reader = await cache.OpenReadAsync(first.Sha256))
        {
            await cache.ImportPinnedAsync(Bytes(2), "pending/two");
            await Assert.ThrowsAsync<BlobCapacityException>(() => cache.ImportAsync(Bytes(3)));
            Assert.Equal(1, reader!.ReadByte());
        }
        await cache.ImportAsync(Bytes(3));
        Assert.Null(await cache.SizeAsync(first.Sha256));
        var partial = new string('a', 64);
        await cache.PinAsync(partial, "transfer");
        await cache.AppendPartialAsync(partial, new byte[4]);
        await Assert.ThrowsAsync<BlobCapacityException>(() => cache.AppendPartialAsync(partial, new byte[1]));
        Assert.Equal(4, await cache.PartialLengthAsync(partial));
    }

    [Fact]
    public async Task SharedOwnerDoesNotUseReferenceCountingAndReconciliationPreservesPins()
    {
        var cache = Cache();
        var blob = await cache.ImportPinnedAsync(Bytes(1), "work");
        await cache.PinAsync(blob.Sha256, "work");
        Assert.Single((await cache.GetEntryAsync(blob.Sha256)).Owners);
        await new FileBlobCache(Path.Combine(_root, "bytes")).DeleteAsync(blob.Sha256);
        await cache.ReconcileAsync();
        var entry = await cache.GetEntryAsync(blob.Sha256);
        Assert.Equal(0, entry.Bytes);
        Assert.Single(entry.Owners);
        Assert.Null(await cache.OpenReadAsync(blob.Sha256));
    }

    [Fact]
    public async Task IndependentLockInstancesDoNotOverlapAndCanceledWaitDoesNotStealOwnership()
    {
        var directory = Path.Combine(_root, "locks");
        var first = new FileBlobCacheLock(directory);
        var second = new FileBlobCacheLock(directory);
        await using (await first.AcquireAsync("metadata"))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second.AcquireAsync("metadata", cancel.Token));
        }
        await using var acquired = await second.AcquireAsync("metadata");
    }

    [Fact]
    public async Task ConcurrentPinAndEvictionAlwaysLeavePinnedBytesOrAnExplicitMissingResult()
    {
        var cache = Cache(4);
        var blob = await cache.ImportAsync(Bytes(1));
        await Task.WhenAll(cache.PinAsync(blob.Sha256, "work"), Task.Run(async () =>
        {
            try { await Cache(4).ImportAsync(Bytes(2)); } catch (BlobCapacityException) { }
        }));
        Assert.Single((await cache.GetEntryAsync(blob.Sha256)).Owners);
        if (await cache.SizeAsync(blob.Sha256) is not null)
            await Assert.ThrowsAsync<BlobCapacityException>(() => cache.ImportAsync(Bytes(3)));
    }

    [Fact]
    public async Task UnknownPartialAndLegacyObjectsConsumeCapacityWithoutBeingDeleted()
    {
        var raw = new FileBlobCache(Path.Combine(_root, "bytes"));
        var orphan = new string('b', 64);
        await raw.AppendPartialAsync(orphan, new byte[5]);
        var cache = Cache(8);
        await Assert.ThrowsAsync<BlobCapacityException>(() => cache.ImportAsync(Bytes(1)));
        Assert.Equal(5, await raw.PartialLengthAsync(orphan));
        await raw.DiscardPartialAsync(orphan);
        var legacy = await raw.ImportAsync(Bytes(2));
        await cache.ReconcileAsync();
        Assert.Contains("recovered/unknown", (await cache.GetEntryAsync(legacy.Sha256)).Owners);
        await Assert.ThrowsAsync<BlobCapacityException>(() => cache.ImportAsync(Bytes(3, 8)));
        Assert.NotNull(await raw.SizeAsync(legacy.Sha256));
    }
}
