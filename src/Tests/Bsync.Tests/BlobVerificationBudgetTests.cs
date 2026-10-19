using System.Security.Cryptography;
using Bsync.Blobs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class BlobVerificationBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(131079)]
    public async Task ExactHashSupportsShortReadsAndBoundsEveryRead(int size)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var stream = new ContentStream(bytes, size);
        var cache = new Cache(() => stream);
        Assert.Equal(BlobVerificationResult.Verified, await BlobVerification.VerifyAsync(cache, Hash(bytes), size, TimeSpan.FromSeconds(10), size));
        Assert.True(stream.Disposed);
        Assert.Equal(size, stream.BytesRead);
        Assert.InRange(stream.LargestRead, 1, 65536);
        Assert.Equal(1, stream.EofReads);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(4, 10)]
    [InlineData(4, 2)]
    public async Task InconsistentStreamLengthCannotHideExtraBytesOrUnboundedReads(int declared, int actual)
    {
        var stream = new ContentStream(new byte[actual], declared);
        var cache = new Cache(() => stream);
        Assert.Equal(BlobVerificationResult.InvalidContent, await BlobVerification.VerifyAsync(cache, Hash(new byte[declared]), declared));
        Assert.True(stream.Disposed);
        Assert.InRange(stream.BytesRead, 0, declared + 1);
    }

    [Fact]
    public async Task OverLimitContentIsDeferredBeforeOpeningWithoutBeingCalledCorrupt()
    {
        var cache = new Cache(() => throw new InvalidOperationException("Must not open storage."));
        Assert.Equal(BlobVerificationResult.ContentLimitExceeded, await BlobVerification.VerifyAsync(cache, Hash([]), 100, Budget, 99));
        Assert.Equal(0, cache.Opens);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BlobVerification.VerifyAsync(cache, Hash([]), 0, Budget, -1));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlobVerification.VerifyAsync(cache, Hash([]), 100, Budget, 99, cancellationToken: stop.Token));
        Assert.Equal(0, cache.Opens);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("read")]
    [InlineData("dispose")]
    public async Task DeadlineCoversNoncooperativeOpenReadAndJoinedDisposal(string stage)
    {
        var time = new FakeTimeProvider();
        void Expire() => time.Advance(TimeSpan.FromSeconds(2));
        var stream = new ContentStream([1, 2, 3], 3)
        {
            OnRead = stage == "read" ? Expire : null,
            OnDispose = stage == "dispose" ? Expire : null,
        };
        var cache = new Cache(() => { if (stage == "open") Expire(); return stream; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlobVerification.VerifyAsync(cache, Hash([1, 2, 3]), 3, Budget, 3, time));
        Assert.True(stream.Disposed);
        if (stage == "open") Assert.Equal(0, stream.BytesRead);
    }

    private sealed class ContentStream(byte[] content, long declared) : Stream
    {
        private int _position;
        public Action? OnRead, OnDispose;
        public bool Disposed;
        public int BytesRead, LargestRead, EofReads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => declared;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            OnRead?.Invoke(); OnRead = null;
            LargestRead = Math.Max(LargestRead, buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, 997), content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count; BytesRead += count;
            if (count == 0) EofReads++;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override ValueTask DisposeAsync() { OnDispose?.Invoke(); Disposed = true; return ValueTask.CompletedTask; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Cache(Func<Stream> open) : IBlobCache
    {
        public int Opens;
        public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) { Opens++; return Task.FromResult<Stream?>(open()); }
        public Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
