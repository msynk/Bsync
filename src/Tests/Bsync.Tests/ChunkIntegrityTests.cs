using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed partial class ChunkedEncryptedBlobCacheTests
{
    [Fact]
    public async Task ScrubResumesDurableChunksWithoutReadingPreviousChunksAndCanRestart()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1048576 + 19);
        var item = await Cache().ImportAsync(new MemoryStream(bytes));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing));
        var first = await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048576);
        Assert.Equal(ChunkScrubStatus.Deferred, first.Status); Assert.Equal(1, first.VerifiedChunks);
        Assert.Equal(1048576, first.VerifiedBytes); Assert.Single(observed.Reads);
        var second = await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), long.MaxValue, 1);
        Assert.Equal(2, second.VerifiedChunks); Assert.Equal(2, observed.Reads.Distinct().Count());
        var third = await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 19);
        Assert.Equal(ChunkScrubStatus.Complete, third.Status); Assert.Equal(bytes.Length, third.VerifiedBytes);
        Assert.Equal(3, observed.Reads.Distinct().Count());
        Assert.Equal(third, await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 0));
        Assert.Equal(3, observed.Reads.Count);
        var restarted = await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 0, restart: true);
        Assert.Equal(0, restarted.VerifiedChunks); Assert.Equal(first.Generation, restarted.Generation);
    }

    [Fact]
    public async Task ScrubDoesNotStartChunkThatExceedsAllowanceOrTreatMissingAsComplete()
    {
        var item = await Cache().ImportAsync(new MemoryStream(new byte[1048576]));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing)) { FailRead = true };
        var result = await Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048575);
        Assert.Equal(ChunkScrubStatus.Deferred, result.Status); Assert.Empty(observed.Reads);
        Assert.Empty(await _metadata.ReadPageAsync("encrypted-scrub/v1/", null, 100));
        Assert.Equal(ChunkScrubStatus.Missing, (await Cache().ScrubAsync(Hash([1]), TimeSpan.FromSeconds(10), 100)).Status);
    }

    [Fact]
    public async Task ScrubDoesNotCheckpointFailedReadAndRejectsCheckpointTampering()
    {
        var item = await Cache().ImportAsync(new MemoryStream(new byte[1048577]));
        await Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048576);
        var progress = Assert.Single(await _metadata.ReadPageAsync("encrypted-scrub/v1/", null, 100));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing)) { FailRead = true };
        await Assert.ThrowsAsync<IOException>(() => Cache(chunks: observed).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1));
        Assert.Equal(progress, await _metadata.ReadAsync(progress.Key));
        var cipher = Convert.FromBase64String(progress.Payload); cipher[^1] ^= 1;
        Assert.True(await _metadata.TryWriteAsync([new(progress.Key, progress.Revision, Convert.ToBase64String(cipher))]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1));
        Assert.NotNull(await _metadata.ReadAsync(progress.Key));
    }

    [Fact]
    public async Task ScrubRotationStartsFreshAndCollectionRetiresOnlyOldProgress()
    {
        var item = await Cache().ImportAsync(new MemoryStream(new byte[1048577]));
        var old = await Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048576);
        await Cache(2).RotateAsync(item.Sha256);
        var current = await Cache(2).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 0);
        Assert.NotEqual(old.Generation, current.Generation); Assert.Equal(0, current.VerifiedChunks);
        await Cache(2).CollectAsync();
        Assert.Null(await _metadata.ReadAsync("encrypted-scrub/v1/" + old.Generation));
        Assert.Equal(ChunkScrubStatus.Complete, (await Cache(2).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), long.MaxValue)).Status);
    }

    [Fact]
    public async Task ScrubExpiryAfterNoncooperativeReadRetainsLastCommittedProgress()
    {
        var item = await Cache().ImportAsync(new MemoryStream(new byte[1048577]));
        await Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048576);
        var time = new FakeTimeProvider();
        var chunks = new ScrubReadHook(new BlobCacheChunkStore(Backing), () => time.Advance(TimeSpan.FromSeconds(2)));
        var result = await Cache(chunks: chunks).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(1), 1, timeProvider: time);
        Assert.Equal(ChunkScrubStatus.Deferred, result.Status); Assert.Equal(1, result.VerifiedChunks);
        Assert.Equal(ChunkScrubStatus.Complete, (await Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1)).Status);
    }

    [Fact]
    public async Task ScrubCallerCancellationAndLockedKeyDoNotEraseProgress()
    {
        var item = await Cache().ImportAsync(new MemoryStream(new byte[1048577]));
        await Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1048576);
        var progress = Assert.Single(await _metadata.ReadPageAsync("encrypted-scrub/v1/", null, 100));
        using var stop = new CancellationTokenSource();
        var chunks = new ScrubReadHook(new BlobCacheChunkStore(Backing), stop.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cache(chunks: chunks).ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1, cancellationToken: stop.Token));
        _keys.Locked = true;
        await Assert.ThrowsAsync<ReplicaKeyUnavailableException>(() => Cache().ScrubAsync(item.Sha256, TimeSpan.FromSeconds(10), 1));
        Assert.Equal(progress, await _metadata.ReadAsync(progress.Key));
    }

    [Fact]
    public async Task KeyRetentionIncludesPartialRetiredReaderAndQuarantinedGenerations()
    {
        var bytes = new byte[1048577]; var item = await Cache().ImportAsync(new MemoryStream(bytes));
        var reader = (await Cache().OpenReadAsync(item.Sha256))!;
        await Cache(2).RotateAsync(item.Sha256); await Cache(2).CollectAsync();
        var page = await Cache(2).GetKeyRetentionPageAsync();
        Assert.Equal(new[] { 1, 2 }, page.Generations.Select(g => g.Key!.Version).Order().ToArray());
        await reader.DisposeAsync(); await Cache(2).CollectAsync();
        Assert.Equal(2, Assert.Single((await Cache(2).GetKeyRetentionPageAsync()).Generations).Key!.Version);
        await Cache(3).AppendPartialAsync(Hash([1, 2]), new byte[] { 1 });
        await Cache(3).RecoverAsync(item.Sha256, new MemoryStream(bytes));
        await Cache(3).CollectAsync();
        var retained = (await Cache(3).GetKeyRetentionPageAsync()).Generations;
        Assert.Equal(3, retained.Count); Assert.Contains(retained, g => g.Key!.Version == 2);
        _keys.Locked = true;
        Assert.Equal(3, (await Cache(3).GetKeyRetentionPageAsync()).Generations.Count);
    }

    [Fact]
    public async Task KeyRetentionLegacyUnknownBlocksInferenceAndPagingIsBounded()
    {
        for (var i = 0; i < 3; i++) await Cache().AppendPartialAsync(Hash([(byte)i]), new byte[] { (byte)i });
        var key = (await _metadata.ReadPageAsync("encrypted-generation-key/v1/", null, 100))[0];
        Assert.True(await _metadata.TryWriteAsync([new(key.Key, key.Revision, null)]));
        var all = new List<ChunkKeyRetention>(); string? cursor = null;
        do
        {
            var page = await Cache().GetKeyRetentionPageAsync(cursor, 1);
            Assert.InRange(page.Generations.Count, 0, 1); all.AddRange(page.Generations); cursor = page.Continuation;
        } while (cursor is not null);
        Assert.Equal(3, all.Count); Assert.Single(all, g => g.Key is null);
        await Assert.ThrowsAsync<ArgumentException>(() => Cache().GetKeyRetentionPageAsync("foreign/cursor"));
    }

    private sealed class ScrubReadHook(IBlobChunkStore inner, Action afterRead) : IBlobChunkStore
    {
        public async Task<byte[]> ReadAsync(string id, int length, CancellationToken cancellationToken = default)
        { var bytes = await inner.ReadAsync(id, length, cancellationToken); afterRead(); return bytes; }
        public Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => inner.WriteAsync(id, bytes, cancellationToken);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
        public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => inner.GetStoredBytesAsync(cancellationToken);
    }
}
