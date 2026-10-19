using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>
/// Opt-in bounded AES-GCM blob cache. Both partial and complete payloads are encrypted before reaching the backing cache.
/// Uses a dedicated backing cache, account metadata and a shared physical lock namespace. Never access backing bytes directly.
/// Plaintext exists in memory only; a read authenticates the entire object before exposing a seekable memory stream.
/// </summary>
public sealed class EncryptedBlobCache : IBlobCache, IBlobCacheInventory
{
    private const string Prefix = "encrypted-blob/v1/";
    private const string GarbagePrefix = "encrypted-blob-gc/v1/";
    private static readonly string LockName = Convert.ToHexStringLower(SHA256.HashData("Bsync encrypted blob cache v1"u8));
    private readonly IBlobCache _backing;
    private readonly IOrchestrationStore _metadata;
    private readonly IBlobCacheLock _locks;
    private readonly ILocalKeyProvider _keys;
    private readonly LocalKeyReference _writeKey;
    private readonly BlobEncryption _encryption;
    private readonly int _maximum;

    /// <summary>
    /// Creates a cache with a per-object limit from 1 byte to 16 MiB (default 16 MiB). Appends rewrite the entire authenticated partial.
    /// Metadata exposes plaintext hashes, lengths and key identities. Retain old keys until all records have been rotated.
    /// Backing storage and metadata must be dedicated to this account/cache; locks must cover every participating process/tab.
    /// </summary>
    public EncryptedBlobCache(IBlobCache backing, IOrchestrationStore metadata, IBlobCacheLock locks,
        ILocalKeyProvider keys, LocalKeyReference writeKey, BlobEncryption encryption, int maximumBlobBytes = 16 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(backing); ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(writeKey); ArgumentNullException.ThrowIfNull(encryption);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBlobBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBlobBytes, 16 * 1024 * 1024);
        if (backing is not IBlobCacheInventory) throw new ArgumentException("Backing cache must expose physical inventory.", nameof(backing));
        _backing = backing; _metadata = metadata; _locks = locks; _keys = keys; _writeKey = writeKey; _encryption = encryption; _maximum = maximumBlobBytes;
    }

    /// <inheritdoc />
    public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => ((IBlobCacheInventory)_backing).GetStoredBytesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (_, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (state.Complete is null) return null;
        var bytes = await DecryptAsync(sha256, state.Complete, true, cancellationToken).ConfigureAwait(false);
        try { return bytes.LongLength; } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public async Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (_, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (state.Partial is null) return 0;
        var bytes = await DecryptAsync(sha256, state.Partial, false, cancellationToken).ConfigureAwait(false);
        try { return bytes.LongLength; } finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public async Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (record, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        var previous = state.Partial is null ? [] : await DecryptAsync(sha256, state.Partial, false, cancellationToken).ConfigureAwait(false);
        byte[]? next = null;
        try
        {
            if ((long)previous.Length + bytes.Length > _maximum) throw new IOException("Encrypted blob exceeds its configured memory limit.");
            next = new byte[previous.Length + bytes.Length]; previous.CopyTo(next, 0); bytes.CopyTo(next.AsMemory(previous.Length));
            var part = await EncryptAsync(sha256, next, false, cancellationToken).ConfigureAwait(false);
            await CommitAsync(sha256, record, state with { Partial = part }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(previous); if (next is not null) CryptographicOperations.ZeroMemory(next); }
    }

    /// <inheritdoc />
    public async Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (record, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (state.Partial is null) return false;
        var bytes = await DecryptAsync(sha256, state.Partial, false, cancellationToken).ConfigureAwait(false);
        try
        {
            // Only authenticated plaintext with a content-hash mismatch is discardable. Wrong keys/tampering never reach here.
            var matches = Hash(bytes) == sha256;
            if (matches && state.Complete is not null)
            {
                var existing = await DecryptAsync(sha256, state.Complete, true, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(existing);
            }
            var complete = matches ? await EncryptAsync(sha256, bytes, true, cancellationToken).ConfigureAwait(false) : state.Complete;
            await CommitAsync(sha256, record, state with { Partial = null, Complete = complete }, cancellationToken).ConfigureAwait(false);
            return matches;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public async Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (record, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (state.Partial is not null) await CommitAsync(sha256, record, state with { Partial = null }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (_, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        return state.Complete is null ? null : new PlaintextStream(await DecryptAsync(sha256, state.Complete, true, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default) => ImportVerifiedAsync(content, null, cancellationToken);

    internal async Task<(string Sha256, long Size)> ImportVerifiedAsync(Stream content, string? expectedHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = await ReadBoundedAsync(content, _maximum, cancellationToken).ConfigureAwait(false);
        try
        {
            var hash = Hash(bytes);
            if (expectedHash is not null && hash != expectedHash) throw new BlobCorruptedException("Import source changed during reading.");
            await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
            var (record, state) = await ReadAsync(hash, cancellationToken).ConfigureAwait(false);
            // Explicit reimport must not replace unreadable ciphertext under a missing/wrong old key.
            if (state.Complete is not null)
            {
                var existing = await DecryptAsync(hash, state.Complete, true, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(existing);
            }
            var complete = await EncryptAsync(hash, bytes, true, cancellationToken).ConfigureAwait(false);
            await CommitAsync(hash, record, state with { Complete = complete }, cancellationToken).ConfigureAwait(false);
            return (hash, bytes.LongLength);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (record, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        if (state.Complete is not null) await CommitAsync(sha256, record, state with { Complete = null }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        return (await RecordsAsync(Prefix, cancellationToken).ConfigureAwait(false))
            .Where(record => Parse(record).Complete is not null).Select(record => record.Key[Prefix.Length..]).ToArray();
    }

    /// <summary>
    /// Re-encrypts this object's complete and partial content with the configured write key, then atomically switches both
    /// references. A crash keeps either the old or new references. Repeat after interruption; do not retire keys prematurely.
    /// </summary>
    public async Task RotateAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var (record, state) = await ReadAsync(sha256, cancellationToken).ConfigureAwait(false);
        byte[]? complete = null, partial = null;
        try
        {
            // Authenticate every existing part before writing any replacement.
            if (state.Complete is not null) complete = await DecryptAsync(sha256, state.Complete, true, cancellationToken).ConfigureAwait(false);
            if (state.Partial is not null) partial = await DecryptAsync(sha256, state.Partial, false, cancellationToken).ConfigureAwait(false);
            var next = state with
            {
                Complete = complete is null ? null : await EncryptAsync(sha256, complete, true, cancellationToken).ConfigureAwait(false),
                Partial = partial is null ? null : await EncryptAsync(sha256, partial, false, cancellationToken).ConfigureAwait(false),
            };
            if (record is not null) await CommitAsync(sha256, record, next, cancellationToken).ConfigureAwait(false);
        }
        finally { if (complete is not null) CryptographicOperations.ZeroMemory(complete); if (partial is not null) CryptographicOperations.ZeroMemory(partial); }
    }

    /// <summary>
    /// Copies one verified legacy object without deleting the source or changing its plaintext identity. Safe to repeat after
    /// interruption. Quiesce legacy writers first; switch pending-upload/cache references only after every required copy succeeds.
    /// Unfinished legacy downloads must be restarted; this method never silently deletes them.
    /// </summary>
    public async Task MigrateAsync(IBlobCache source, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ValidateHash(sha256);
        await using var stream = await source.OpenReadAsync(sha256, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Migration source blob is missing.");
        // Check the announced identity before importing so a corrupt source cannot create an unrelated successful migration.
        var bytes = await ReadBoundedAsync(stream, _maximum, cancellationToken).ConfigureAwait(false);
        try
        {
            if (Hash(bytes) != sha256) throw new BlobCorruptedException("Migration source hash mismatch.");
            using var input = new MemoryStream(bytes, writable: false);
            await ImportAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Reclaims only journaled encrypted envelopes no current record references. Unknown backing objects/partials are preserved.
    /// All metadata is validated before any deletion. Bounded to 10,000 records per namespace; larger caches fail explicitly.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(LockName, cancellationToken).ConfigureAwait(false);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in await RecordsAsync(Prefix, cancellationToken).ConfigureAwait(false))
        {
            var state = Parse(record);
            foreach (var (part, complete) in new[] { (state.Complete, true), (state.Partial, false) })
            {
                if (part is null) continue;
                var plaintext = await DecryptAsync(record.Key[Prefix.Length..], part, complete, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(plaintext);
                active.Add(part.CipherHash);
            }
        }
        var garbage = await RecordsAsync(GarbagePrefix, cancellationToken).ConfigureAwait(false);
        foreach (var record in garbage) { ValidateHash(record.Key[GarbagePrefix.Length..]); if (record.Payload != "1") throw new InvalidDataException("Unsupported encryption journal schema."); }
        var removed = 0;
        foreach (var record in garbage.Where(record => !active.Contains(record.Key[GarbagePrefix.Length..])))
        {
            var hash = record.Key[GarbagePrefix.Length..];
            await _backing.DeleteAsync(hash, cancellationToken).ConfigureAwait(false);
            await _backing.DiscardPartialAsync(hash, cancellationToken).ConfigureAwait(false);
            if (!await _metadata.TryWriteAsync([new(record.Key, record.Revision, null)], cancellationToken: cancellationToken).ConfigureAwait(false))
                throw new IOException("Encrypted blob journal changed outside its physical lock.");
            removed++;
        }
        return removed;
    }

    private async Task<EncryptedBlobPart> EncryptAsync(string hash, byte[] bytes, bool complete, CancellationToken ct)
    {
        using var access = await _keys.AcquireAsync(_writeKey, ct).ConfigureAwait(false);
        if (access.Reference != _writeKey) throw new CryptographicException("Key provider returned a different key identity.");
        var part = new EncryptedBlobPart("", bytes.Length, _writeKey.KeyId, _writeKey.Version);
        var envelope = await _encryption.SealAsync(access.Key, bytes, Aad(hash, part, complete), ct).ConfigureAwait(false);
        if (envelope.Length != bytes.Length + 28) throw new CryptographicException("Invalid encrypted envelope length.");
        var cipherHash = Hash(envelope);
        var journalKey = GarbagePrefix + cipherHash;
        var journal = await _metadata.ReadAsync(journalKey, ct).ConfigureAwait(false);
        if (journal is not null) throw new CryptographicException("Unexpected duplicate encrypted envelope identity.");
        if (!await _metadata.TryWriteAsync([new(journalKey, null, "1")], cancellationToken: ct).ConfigureAwait(false)) throw new IOException("Encryption journal conflict.");
        using var input = new MemoryStream(envelope, writable: false);
        var stored = await _backing.ImportAsync(input, ct).ConfigureAwait(false);
        if (stored.Sha256 != cipherHash || stored.Size != envelope.Length) throw new IOException("Backing cache changed encrypted bytes.");
        return part with { CipherHash = cipherHash };
    }

    private async Task<byte[]> DecryptAsync(string hash, EncryptedBlobPart part, bool complete, CancellationToken ct)
    {
        var reference = new LocalKeyReference(_writeKey.Account, part.KeyId, part.KeyVersion);
        using var access = await _keys.AcquireAsync(reference, ct).ConfigureAwait(false);
        if (access.Reference != reference) throw new CryptographicException("Key provider returned a different key identity.");
        // Acquire key before opening storage, so a locked account cannot be mistaken for an empty cache.
        var key = access.Key;
        await using var stream = await _backing.OpenReadAsync(part.CipherHash, ct).ConfigureAwait(false)
            ?? throw new BlobCorruptedException("Encrypted content is missing; preserve metadata for explicit recovery.");
        var envelope = await ReadBoundedAsync(stream, checked(_maximum + 28), ct).ConfigureAwait(false);
        if (envelope.Length != part.Length + 28 || Hash(envelope) != part.CipherHash) throw new CryptographicException("Encrypted blob length or digest mismatch.");
        var bytes = await _encryption.OpenAsync(key, envelope, Aad(hash, part, complete), ct).ConfigureAwait(false);
        if (bytes.Length != part.Length || (complete && Hash(bytes) != hash))
        { CryptographicOperations.ZeroMemory(bytes); throw new CryptographicException("Authenticated blob identity mismatch."); }
        return bytes;
    }

    private byte[] Aad(string hash, EncryptedBlobPart part, bool complete) => Encoding.UTF8.GetBytes(
        string.Join('\n', "Bsync.EncryptedBlob/1", _writeKey.Account.StorageKey, hash, complete ? "complete" : "partial",
            part.KeyId, part.KeyVersion.ToString(CultureInfo.InvariantCulture), part.Length.ToString(CultureInfo.InvariantCulture)));

    private async Task<(OrchestrationRecord? Record, EncryptedBlobState State)> ReadAsync(string hash, CancellationToken ct)
    {
        ValidateHash(hash);
        var record = await _metadata.ReadAsync(Prefix + hash, ct).ConfigureAwait(false);
        return (record, record is null ? new(1, null, null) : Parse(record));
    }

    private EncryptedBlobState Parse(OrchestrationRecord record)
    {
        ValidateHash(record.Key[Prefix.Length..]);
        var state = JsonSerializer.Deserialize(record.Payload, EncryptedBlobJson.Default.EncryptedBlobState)
            ?? throw new InvalidDataException("Missing encrypted blob metadata.");
        if (state.Schema != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported encrypted blob schema; ciphertext retained.");
        foreach (var part in new[] { state.Complete, state.Partial })
        {
            if (part is null) continue;
            ValidateHash(part.CipherHash);
            if (part.Length < 0 || part.Length > _maximum) throw new InvalidDataException("Encrypted blob length exceeds configured bounds.");
            _ = new LocalKeyReference(_writeKey.Account, part.KeyId, part.KeyVersion);
        }
        return state;
    }

    private async Task CommitAsync(string hash, OrchestrationRecord? previous, EncryptedBlobState state, CancellationToken ct)
    {
        var payload = state.Complete is null && state.Partial is null ? null : JsonSerializer.Serialize(state, EncryptedBlobJson.Default.EncryptedBlobState);
        if (!await _metadata.TryWriteAsync([new(Prefix + hash, previous?.Revision, payload)], cancellationToken: ct).ConfigureAwait(false))
            throw new IOException("Encrypted blob metadata changed outside its physical lock; ciphertext retained.");
        // Readers own authenticated memory, never handles into backing storage. Replaced envelopes can now be reclaimed.
        // Failure here cannot turn an acknowledged append into an ambiguous retry; the durable journal supports later GC.
        if (previous is null) return;
        var old = Parse(previous);
        foreach (var part in new[] { old.Complete, old.Partial })
        {
            if (part is null || part.CipherHash == state.Complete?.CipherHash || part.CipherHash == state.Partial?.CipherHash) continue;
            try
            {
                await _backing.DeleteAsync(part.CipherHash, CancellationToken.None).ConfigureAwait(false);
                var garbage = await _metadata.ReadAsync(GarbagePrefix + part.CipherHash, CancellationToken.None).ConfigureAwait(false);
                if (garbage is not null) await _metadata.TryWriteAsync([new(garbage.Key, garbage.Revision, null)], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException) { /* Retained journal is retried by ReconcileAsync. */ }
        }
    }

    private async Task<List<OrchestrationRecord>> RecordsAsync(string prefix, CancellationToken ct)
    {
        List<OrchestrationRecord> records = []; string? after = null;
        while (true)
        {
            var page = await _metadata.ReadPageAsync(prefix, after, 1000, ct).ConfigureAwait(false);
            records.AddRange(page);
            if (records.Count > 10000) throw new IOException("Encrypted cache maintenance exceeds 10,000-record bound.");
            if (page.Count < 1000) return records;
            after = page[^1].Key;
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Expected lowercase SHA-256.", nameof(hash));
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(81920, maximum + 1)];
        try
        {
            int count;
            while ((count = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum - checked((int)buffer.Length) + 1)), ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > maximum) throw new IOException("Encrypted blob exceeds its configured memory limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            return buffer.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(chunk); if (buffer.TryGetBuffer(out var memory)) CryptographicOperations.ZeroMemory(memory.AsSpan()); }
    }

    private sealed class PlaintextStream : MemoryStream
    {
        private readonly byte[] _bytes;
        public PlaintextStream(byte[] bytes) : base(bytes, writable: false) => _bytes = bytes;
        protected override void Dispose(bool disposing) { CryptographicOperations.ZeroMemory(_bytes); base.Dispose(disposing); }
    }
}

internal sealed record EncryptedBlobPart(string CipherHash, int Length, string KeyId, int KeyVersion);
internal sealed record EncryptedBlobState(int Schema, EncryptedBlobPart? Complete, EncryptedBlobPart? Partial);
[JsonSerializable(typeof(EncryptedBlobState))]
internal partial class EncryptedBlobJson : JsonSerializerContext;
