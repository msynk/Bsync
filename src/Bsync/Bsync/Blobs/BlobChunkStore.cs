namespace Bsync.Blobs;

/// <summary>Immutable bounded ciphertext storage for chunked encryption; IDs are ciphertext SHA-256 values.</summary>
public interface IBlobChunkStore
{
    /// <summary>Durably writes one immutable record, at most 1 MiB plus a 28-byte AES-GCM envelope.</summary>
    Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    /// <summary>Reads one bounded record; missing, oversized or truncated data must fail.</summary>
    Task<byte[]> ReadAsync(string id, int expectedLength, CancellationToken cancellationToken = default);
    /// <summary>Idempotently deletes one unreferenced record.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Physical payload bytes, including abandoned writes.</summary>
    Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Native/browser chunk provider over a dedicated FileBlobCache or BrowserBlobStore. Each backing object is one
/// bounded ciphertext chunk, never the full plaintext object. Account metadata journals inventory before writes.
/// </summary>
public sealed class BlobCacheChunkStore : IBlobChunkStore
{
    private readonly IBlobCache _cache;
    private readonly IBlobCacheInventory _inventory;
    /// <summary>Uses a dedicated raw backing cache with physical inventory. Do not share it with plaintext or v1 objects.</summary>
    public BlobCacheChunkStore(IBlobCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _inventory = cache as IBlobCacheInventory ?? throw new ArgumentException("Chunk storage requires physical inventory.", nameof(cache));
    }
    /// <inheritdoc />
    public async Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length is < 28 or > 1048604) throw new ArgumentOutOfRangeException(nameof(bytes));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.Span));
        if (hash != id) throw new InvalidDataException("Chunk identity mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        // Use the journaled chunk ID for staging too. Join started provider commits before releasing ownership;
        // canceled JS awaits cannot cancel their underlying IndexedDB transactions.
        await _cache.DiscardPartialAsync(id, CancellationToken.None).ConfigureAwait(false);
        await _cache.AppendPartialAsync(id, bytes, CancellationToken.None).ConfigureAwait(false);
        if (!await _cache.CompletePartialAsync(id, CancellationToken.None).ConfigureAwait(false)) throw new InvalidDataException("Chunk provider identity mismatch.");
    }
    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(string id, int expectedLength, CancellationToken cancellationToken = default)
    {
        if (expectedLength is < 28 or > 1048604) throw new InvalidDataException("Invalid chunk length.");
        await using var stream = await _cache.OpenReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing encrypted chunk.");
        if (stream.Length != expectedLength) throw new InvalidDataException("Encrypted chunk length mismatch.");
        var bytes = new byte[expectedLength];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != id) throw new InvalidDataException("Encrypted chunk identity mismatch.");
        return bytes;
    }
    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _cache.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        await _cache.DiscardPartialAsync(id, CancellationToken.None).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => _inventory.GetStoredBytesAsync(cancellationToken);
}
