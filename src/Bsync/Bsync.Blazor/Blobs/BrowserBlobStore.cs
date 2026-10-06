using System.Security.Cryptography;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Microsoft.JSInterop;

namespace Bsync.Blazor.Blobs;

/// <summary>
/// The browser's content-addressed store for attachment bytes (task F1), in IndexedDB
/// (<c>_content/Bsync.Blazor/bsync-blobs.js</c>). Content becomes readable only after its SHA-256 was verified, so a
/// partial or corrupted transfer is never opened as complete; transfers in progress are kept in chunks and continue after
/// a reload. Quota and availability failures surface as <see cref="Storage.LocalStoreUnavailableException"/> (reasons
/// <c>quota</c>, <c>unavailable</c>), never as silent success. Use one store per account and delete it on sign-out.
/// </summary>
public sealed class BrowserBlobStore : IBlobCache
{
    private const string ModulePath = "./_content/Bsync.Blazor/bsync-blobs.js";
    private const int ReadChunk = 1024 * 1024;

    private readonly IJSObjectReference _module;

    private BrowserBlobStore(IJSObjectReference module, string name)
    {
        _module = module;
        Name = name;
    }

    /// <summary>The store's name (for example <c>bsync-{account}</c>).</summary>
    public string Name { get; }

    /// <summary>Opens the store. Call only after the browser runtime is interactive.</summary>
    public static async Task<BrowserBlobStore> OpenAsync(IJSRuntime js, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        return new BrowserBlobStore(module, name);
    }

    /// <summary>Deletes every blob of a store (sign-out wipe).</summary>
    public static async Task DeleteAllAsync(IJSRuntime js, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        var module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        await IndexedDbLocalStore<NoDocument>.Call(() => module.InvokeVoidAsync("clear", cancellationToken, name)).ConfigureAwait(false);
    }

    /// <summary>The size of the verified content, or <see langword="null"/> when it is not on this device.</summary>
    public async Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default) =>
        await IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeAsync<long>("size", cancellationToken, Name, sha256)).ConfigureAwait(false) is var size and >= 0 ? size : null;

    /// <summary>How many bytes of an unfinished transfer of <paramref name="sha256"/> are kept.</summary>
    public Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default) =>
        IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeAsync<long>("partialLength", cancellationToken, Name, sha256));

    /// <summary>Appends a chunk to the unfinished transfer of <paramref name="sha256"/>.</summary>
    public Task AppendPartialAsync(string sha256, byte[] chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeVoidAsync("appendPartial", cancellationToken, Name, sha256, chunk));
    }

    /// <inheritdoc />
    public Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        AppendPartialAsync(sha256, bytes.ToArray(), cancellationToken);

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) =>
        await SizeAsync(sha256, cancellationToken).ConfigureAwait(false) is { } size ? OpenRead(sha256, size) : null;

    /// <summary>
    /// Verifies the unfinished transfer against <paramref name="sha256"/> and makes it the content. Returns
    /// <see langword="false"/> (and discards it) on a mismatch.
    /// </summary>
    public Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default) =>
        IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeAsync<bool>("completePartial", cancellationToken, Name, sha256));

    /// <summary>Discards the unfinished transfer of <paramref name="sha256"/>.</summary>
    public Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default) =>
        IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeVoidAsync("discardPartial", cancellationToken, Name, sha256));

    /// <summary>
    /// Stores <paramref name="content"/> (for example a file the user picked), verified like a download, and returns its
    /// SHA-256 and size. The bytes are stored before this returns, so a document saved afterwards never names missing bytes.
    /// </summary>
    public async Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var staging = $"import-{Guid.NewGuid():N}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ReadChunk];
        long size = 0;
        int read;
        while ((read = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await AppendPartialAsync(staging, buffer[..read], cancellationToken).ConfigureAwait(false);
            size += read;
            if (read < buffer.Length)
            {
                break;
            }
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        await IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeVoidAsync("discardPartial", cancellationToken, Name, sha256)).ConfigureAwait(false);
        await IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeVoidAsync("renamePartial", cancellationToken, Name, staging, sha256)).ConfigureAwait(false);
        if (!await CompletePartialAsync(sha256, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The imported content changed while it was stored.");
        }

        return (sha256, size);
    }

    /// <summary>Reads verified content in ranges (it is not loaded at once). Throws if it is not on this device.</summary>
    public Stream OpenRead(string sha256, long size) => new RangeStream(this, sha256, size);

    /// <summary>Removes verified content (eviction; never content that a pending document still has to upload).</summary>
    public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default) =>
        IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeVoidAsync("remove", cancellationToken, Name, sha256));

    /// <summary>The hashes of the content on this device.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        await IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeAsync<string[]>("list", cancellationToken, Name)).ConfigureAwait(false);

    private Task<byte[]> ReadRangeAsync(string sha256, long offset, int count, CancellationToken cancellationToken) =>
        IndexedDbLocalStore<NoDocument>.Call(() => _module.InvokeAsync<byte[]>("readRange", cancellationToken, Name, sha256, offset, count));

    private sealed class RangeStream(BrowserBlobStore store, string sha256, long size) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => size;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, size);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, ReadChunk), size - _position);
            if (count <= 0)
            {
                return 0;
            }

            var bytes = await store.ReadRangeAsync(sha256, _position, count, cancellationToken).ConfigureAwait(false);
            bytes.CopyTo(buffer);
            _position += bytes.Length;
            return bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Synchronous reads would block the browser's only thread on JavaScript; read asynchronously.
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read the content asynchronously.");

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => size + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A type argument for the shared interop helpers of the IndexedDB store.</summary>
    private sealed class NoDocument : ISyncEntity
    {
        public string Id { get; set; } = string.Empty;

        public Clocks.HlcTimestamp UpdatedAt { get; set; }

        public bool Deleted { get; set; }
    }
}
