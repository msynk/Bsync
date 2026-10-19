using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed partial class ChunkedEncryptedBlobCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-chunked-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryOrchestrationStore _metadata = new();
    private readonly Keys _keys = new();
    private static readonly AccountScope Account = new("tests", "local", "chunks");
    private FileBlobCache Backing => new(Path.Combine(_root, "chunks"));
    private ChunkedEncryptedBlobCache Cache(int version = 1, IBlobChunkStore? chunks = null, long budget = long.MaxValue) =>
        new(chunks ?? new BlobCacheChunkStore(Backing), _metadata, new FileBlobCacheLock(Path.Combine(_root, "operations")),
            new FileAccountResourceLock(Path.Combine(_root, "lifetimes")), _keys, new(Account, "files", version), new PlatformBlobEncryption(), hardBudgetBytes: budget);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Keys : ILocalKeyProvider
    {
        public bool Locked { get; set; }
        public bool Wrong { get; set; }
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Locked ? LocalKeyAccess.Locked(reference) : LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)(reference.Version + (Wrong ? 1 : 0)), 32).ToArray()));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task AssertBytes(IBlobCache cache, string hash, byte[] bytes)
    {
        await using var read = (await cache.OpenReadAsync(hash))!;
        var actual = new byte[bytes.Length]; await read.ReadExactlyAsync(actual); Assert.Equal(bytes, actual);
    }

    [Fact]
    public async Task IndependentNodeFormatVectorOpensAndRejectsReorderedManifest()
    {
        await Fixtures.ChunkedFormatVector.RunAsync(Backing, _metadata, new FileBlobCacheLock(Path.Combine(_root, "operations")),
            new FileAccountResourceLock(Path.Combine(_root, "lifetimes")), new PlatformBlobEncryption());
    }

    [Fact]
    public async Task PartialTailSurvivesRestartAndRangesReadOnlyIntersectingChunks()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1048576 + 123);
        var hash = Hash(bytes); var cache = Cache();
        await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 123));
        Assert.Null(await cache.OpenReadAsync(hash));
        cache = Cache(); Assert.Equal(123, await cache.PartialLengthAsync(hash));
        await cache.AppendPartialAsync(hash, bytes.AsMemory(123, 1048576));
        await Cache().AppendPartialAsync(hash, bytes.AsMemory(1048576 + 123));
        Assert.True(await Cache().CompletePartialAsync(hash));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing));
        await using var read = (await Cache(chunks: observed).OpenReadAsync(hash))!;
        Assert.Empty(observed.Reads);
        read.Seek(1048570, SeekOrigin.Begin);
        var range = new byte[20]; await read.ReadExactlyAsync(range);
        Assert.Equal(bytes.AsSpan(1048570, 20).ToArray(), range);
        Assert.Equal(2, observed.Reads.Count);
        await AssertBytes(Cache(), hash, bytes);
    }

    [Fact]
    public async Task RotationRetainsOldReaderUntilReleasedAndThenReclaimsOldCiphertext()
    {
        var bytes = RandomNumberGenerator.GetBytes(1048700);
        var cache = Cache(); var imported = await cache.ImportAsync(new MemoryStream(bytes));
        var originalBytes = await cache.GetStoredBytesAsync();
        var reader = (await cache.OpenReadAsync(imported.Sha256))!;
        await Cache(2).RotateAsync(imported.Sha256);
        Assert.True(await cache.GetStoredBytesAsync() > originalBytes);
        await Cache(2).CollectAsync();
        Assert.True(await cache.GetStoredBytesAsync() > originalBytes);
        var old = new byte[bytes.Length]; await reader.ReadExactlyAsync(old); Assert.Equal(bytes, old);
        await reader.DisposeAsync();
        await Cache(2).CollectAsync();
        Assert.Equal(originalBytes, await cache.GetStoredBytesAsync());
        await AssertBytes(Cache(2), imported.Sha256, bytes);
    }

    [Theory]
    [InlineData(0, 2097152)]
    [InlineData(123, 2097152)]
    [InlineData(1048576, 1048593)]
    [InlineData(1048576, 0)]
    public async Task AppendsPreserveCallerMemoryAcrossAlignedChunksAndPartialTails(int prefix, int appended)
    {
        var expected = RandomNumberGenerator.GetBytes(prefix + appended);
        var caller = expected.ToArray();
        var hash = Hash(expected);
        var cache = Cache();
        await cache.AppendPartialAsync(hash, caller.AsMemory(0, prefix));
        await cache.AppendPartialAsync(hash, caller.AsMemory(prefix));
        Assert.Equal(expected, caller);
        Assert.Equal(expected.Length, await cache.PartialLengthAsync(hash));
        Assert.True(await cache.CompletePartialAsync(hash));
        // The completed cache owns encrypted bytes independently of the caller's subsequent reuse.
        CryptographicOperations.ZeroMemory(caller);
        await AssertBytes(Cache(), hash, expected);
    }

    [Fact]
    public async Task EscapedEnvelopeCiphertextResumesRotatesAndRecoversWithLiteralBase64()
    {
        var bytes = Enumerable.Range(0, 1048593).Select(i => (byte)(i % 251)).ToArray();
        var hash = Hash(bytes);
        var key = "encrypted-blob/v2/" + hash;
        async Task EscapeCiphertext(string slot)
        {
            var record = (await _metadata.ReadAsync(key))!;
            var ciphertext = JsonNode.Parse(record.Payload)![slot]!["Ciphertext"]!.GetValue<string>();
            Assert.Contains("\"Ciphertext\":\"" + ciphertext + "\"", record.Payload);
            // Exercise old JSON escapes deterministically, including Base64 '+' when present.
            var escaped = string.Concat(ciphertext.Select(c => "\\u" + ((int)c).ToString("x4")));
            var payload = record.Payload.Replace("\"" + ciphertext + "\"", "\"" + escaped + "\"", StringComparison.Ordinal);
            Assert.NotEqual(record.Payload, payload);
            Assert.True(await _metadata.TryWriteAsync([new(key, record.Revision, payload)]));
        }
        await Cache().AppendPartialAsync(hash, bytes.AsMemory(0, 1048576));
        await EscapeCiphertext("Partial");
        await Cache().AppendPartialAsync(hash, bytes.AsMemory(1048576));
        Assert.True(await Cache().CompletePartialAsync(hash));
        await EscapeCiphertext("Complete");
        await AssertBytes(Cache(), hash, bytes);
        await Cache(2).RotateAsync(hash);
        await EscapeCiphertext("Complete");
        await Cache(2).RecoverAsync(hash, new MemoryStream(bytes));
        await AssertBytes(Cache(2), hash, bytes);
        // Recovery's atomic publication uses the same bounded envelope representation as normal saves.
        await EscapeCiphertext("Complete");
        await AssertBytes(Cache(2), hash, bytes);
    }

    [Fact]
    public async Task TamperAndWrongKeyPreserveOriginalUntilExplicitRecoveryQuarantinesIt()
    {
        var bytes = RandomNumberGenerator.GetBytes(1000);
        var cache = Cache(); var imported = await cache.ImportAsync(new MemoryStream(bytes));
        var before = (await _metadata.ReadAsync("encrypted-blob/v2/" + imported.Sha256))!;
        _keys.Locked = true;
        await Assert.ThrowsAsync<ReplicaKeyUnavailableException>(() => cache.OpenReadAsync(imported.Sha256));
        _keys.Locked = false; _keys.Wrong = true;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.ImportAsync(new MemoryStream(bytes)));
        Assert.Equal(before, await _metadata.ReadAsync(before.Key));
        _keys.Wrong = false;
        var physical = Assert.Single(await Backing.ListAsync());
        var path = Path.Combine(Backing.Directory, "objects", physical[..2], physical);
        var damaged = await File.ReadAllBytesAsync(path); damaged[20] ^= 1; await File.WriteAllBytesAsync(path, damaged);
        await using (var read = (await cache.OpenReadAsync(imported.Sha256))!)
            await Assert.ThrowsAsync<InvalidDataException>(() => read.ReadAsync(new byte[20]).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ImportAsync(new MemoryStream(bytes)));
        var protectedBytes = await cache.GetStoredBytesAsync();
        await cache.RecoverAsync(imported.Sha256, new MemoryStream(bytes));
        Assert.Single(await _metadata.ReadPageAsync("encrypted-quarantine/v2/", null, 10));
        await cache.CollectAsync();
        Assert.True(await cache.GetStoredBytesAsync() >= protectedBytes);
        await AssertBytes(cache, imported.Sha256, bytes);
    }

    [Fact]
    public async Task QuarantineObservationIsPagedAndWorksWithLockedKeysWithoutReadingCiphertext()
    {
        var bytes = RandomNumberGenerator.GetBytes(1048600);
        var cache = Cache(); var imported = await cache.ImportAsync(new MemoryStream(bytes));
        await cache.RecoverAsync(imported.Sha256, new MemoryStream(bytes));
        _keys.Locked = true;
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing)) { FailRead = true };
        cache = Cache(chunks: observed);
        long reserved = 0, quarantined = 0; int count = 0; string? cursor = null;
        do
        {
            var page = await cache.ObserveStoragePageAsync(cursor, 1);
            Assert.InRange(page.ExaminedChunks, 0, 1);
            count += page.ExaminedChunks; reserved += page.ReservedBytes; quarantined += page.QuarantinedBytes;
            cursor = page.Continuation;
        } while (cursor is not null);
        Assert.Equal(4, count);
        Assert.Equal(2 * (bytes.Length + 56), reserved);
        Assert.Equal(bytes.Length + 56, quarantined);
        Assert.Empty(observed.Reads);
        Assert.Equal(reserved, await cache.GetStoredBytesAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveStoragePageAsync("another/namespace"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ObserveStoragePageAsync(cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task MalformedQuarantineInventoryIsPreservedAndNeverReportedAsZero()
    {
        var key = "encrypted-chunk/v2/" + new string('0', 32) + "/" + new string('a', 64);
        await _metadata.TryWriteAsync([new(key, null, "1048605")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache().ObserveStoragePageAsync());
        Assert.Equal("1048605", (await _metadata.ReadAsync(key))!.Payload);
    }

    [Fact]
    public async Task ManifestSubstitutionAndCapacityRefusalDoNotReplaceReadableGeneration()
    {
        var bytes = RandomNumberGenerator.GetBytes(1000); var cache = Cache();
        var imported = await cache.ImportAsync(new MemoryStream(bytes));
        var record = (await _metadata.ReadAsync("encrypted-blob/v2/" + imported.Sha256))!;
        var other = Hash([1, 2, 3]);
        await _metadata.TryWriteAsync([new("encrypted-blob/v2/" + other, null, record.Payload)]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.OpenReadAsync(other));
        await Assert.ThrowsAsync<BlobCapacityException>(() => Cache(2, budget: 1500).RotateAsync(imported.Sha256));
        await AssertBytes(cache, imported.Sha256, bytes);
    }

    [Fact]
    public async Task InterruptedRotationResumesCommittedChunksAndFailedReadCanSeekBack()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1048576 + 17);
        var cache = Cache(); var imported = await cache.ImportAsync(new MemoryStream(bytes));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing)) { FailOnWrite = 2 };
        await Assert.ThrowsAsync<IOException>(() => Cache(2, observed).RotateAsync(imported.Sha256));
        Assert.Equal(1048576, await Cache(2).PartialLengthAsync(imported.Sha256));
        var resumed = new ObservedChunks(new BlobCacheChunkStore(Backing));
        await Cache(2, resumed).RotateAsync(imported.Sha256);
        Assert.Equal(2, resumed.Writes);
        await AssertBytes(Cache(2), imported.Sha256, bytes);
        await using var reader = (await Cache(2, resumed).OpenReadAsync(imported.Sha256))!;
        var first = new byte[32]; await reader.ReadExactlyAsync(first);
        resumed.FailRead = true; reader.Position = 1048576;
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[32]).AsTask());
        resumed.FailRead = false; reader.Position = 0;
        var retry = new byte[32]; await reader.ReadExactlyAsync(retry); Assert.Equal(first, retry);
    }

    [Fact]
    public async Task InterruptedChunkWriteIsDiscoverableAndDoesNotAdvancePartialProgress()
    {
        var bytes = RandomNumberGenerator.GetBytes(2000); var hash = Hash(bytes);
        var cache = Cache(); await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 1000));
        var observed = new ObservedChunks(new BlobCacheChunkStore(Backing)) { FailAfterWrite = true };
        await Assert.ThrowsAsync<IOException>(() => Cache(chunks: observed).AppendPartialAsync(hash, bytes.AsMemory(1000)));
        Assert.Equal(1000, await Cache().PartialLengthAsync(hash));
        await Cache().AppendPartialAsync(hash, bytes.AsMemory(1000));
        Assert.True(await Cache().CompletePartialAsync(hash));
        await AssertBytes(cache, hash, bytes);
        // Abandoned records remain inventoried, never invisible physical writes.
        Assert.True((await _metadata.ReadPageAsync("encrypted-chunk/v2/", null, 100)).Count >= 2);
        Assert.Equal(1, (await cache.ReconcileChunksAsync(hash, partial: false)).RemovedChunks);
        Assert.Equal(bytes.Length + 28, await cache.GetStoredBytesAsync());
    }

    [Theory]
    [InlineData(17)]
    [InlineData(256)]
    [InlineData(1024)]
    [Trait("Category", "LargeEncrypted")]
    public async Task LargeObjectsUseBoundedCiphertextChunksAndCorrectFullHash(int mebibytes)
    {
        var chunks = new ObservedChunks(new BlobCacheChunkStore(Backing));
        var cache = Cache(chunks: chunks);
        using var source = new PatternStream((long)mebibytes * 1048576);
        var result = await cache.ImportAsync(source);
        Assert.Equal(source.Length, result.Size);
        Assert.InRange(chunks.MaximumWrite, 28, 1048604);
        await using var read = (await Cache().OpenReadAsync(result.Sha256))!;
        Assert.Equal(result.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(read)));
        Assert.Equal(result.Size + 28L * mebibytes, await cache.GetStoredBytesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlimitedWritesDoNotRescanPayloadsAndFiniteBudgetsStillRejectOverflow(bool finite)
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1048576); var hash = Hash(bytes);
        var chunks = new ObservedChunks(new BlobCacheChunkStore(Backing));
        var cache = Cache(chunks: chunks, budget: finite ? 1048576 + 28 : long.MaxValue);
        await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 1048576));
        if (finite)
        {
            await Assert.ThrowsAsync<BlobCapacityException>(() => cache.AppendPartialAsync(hash, bytes.AsMemory(1048576)));
            Assert.Equal(1, chunks.Writes);
            Assert.Equal(1048576, await cache.PartialLengthAsync(hash));
            Assert.Equal(2, chunks.InventoryReads);
        }
        else
        {
            await cache.AppendPartialAsync(hash, bytes.AsMemory(1048576));
            Assert.True(await cache.CompletePartialAsync(hash));
            await AssertBytes(cache, hash, bytes);
            Assert.Equal(0, chunks.InventoryReads);
        }
    }

    private sealed class ObservedChunks(IBlobChunkStore inner) : IBlobChunkStore
    {
        public List<string> Reads { get; } = [];
        public int MaximumWrite { get; private set; }
        public int Writes { get; private set; }
        public int InventoryReads { get; private set; }
        public int FailOnWrite { get; init; }
        public bool FailRead { get; set; }
        public bool FailAfterWrite { get; init; }
        public async Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { MaximumWrite = Math.Max(MaximumWrite, bytes.Length); Writes++; await inner.WriteAsync(id, bytes, cancellationToken); if (FailAfterWrite || Writes == FailOnWrite) throw new IOException("Crash after durable chunk before manifest publication."); }
        public Task<byte[]> ReadAsync(string id, int length, CancellationToken cancellationToken = default) { Reads.Add(id); if (FailRead) throw new IOException("Injected read failure."); return inner.ReadAsync(id, length, cancellationToken); }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
        public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default)
        { InventoryReads++; return inner.GetStoredBytesAsync(cancellationToken); }
    }
    private sealed class PatternStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - Position);
            for (var i = 0; i < count; i++) buffer[i] = (byte)((Position + i) % 251);
            Position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override long Seek(long offset, SeekOrigin origin) => Position = (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
