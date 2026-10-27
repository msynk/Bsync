using Bsync.Blazor.Blobs;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    [JSInvokable(nameof(RawStreamingBlob))]
    public static async Task<string> RawStreamingBlob(string name, string expectedHash)
    {
        var store = await BrowserBlobStore.OpenAsync(_js, name);
        try
        {
            const int size = 17 * 1024 * 1024 + 11;
            using var source = new RepeatedRawStream(size);
            (string Sha256, long Size) imported;
            try { imported = await store.ImportAsync(source); }
            catch (Bsync.Storage.LocalStoreUnavailableException error) when (error.Message.Contains("refuses Blob persistence", StringComparison.Ordinal))
            {
                if (await store.PartialLengthAsync(expectedHash) != size || await store.SizeAsync(expectedHash) is not null)
                    throw new Exception("Unsupported persistence lost partial bytes or published an incomplete object.");
                return "blob-persistence-unavailable";
            }
            if (imported != (expectedHash, size)) throw new Exception("Streaming raw import hash/length mismatch.");
            await using var reader = await store.OpenReadAsync(imported.Sha256) ?? throw new Exception("Missing raw content.");
            await store.DeleteAsync(imported.Sha256);
            reader.Seek(-11, SeekOrigin.End);
            var tail = new byte[11]; await reader.ReadExactlyAsync(tail);
            if (tail.Any(b => b != 42)) throw new Exception("Raw snapshot changed after deletion.");
            return "ok";
        }
        finally { await BrowserBlobStore.DeleteAllAsync(_js, name); }
    }

    private sealed class RepeatedRawStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - _position); buffer.Span[..count].Fill(42); _position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
