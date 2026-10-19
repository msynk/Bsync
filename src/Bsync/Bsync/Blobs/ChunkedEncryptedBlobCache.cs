using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>
/// Experimental v2 AES-GCM chunk format, pending independent cryptographic review. Explicitly opt in by constructing
/// this type with dedicated chunk storage and metadata. Never opens or rewrites v1 implicitly. Plaintext identity and
/// the server wire protocol are unchanged. Reads authenticate the manifest and requested chunks, not untouched chunks.
/// All users must share the physical operation and generation lock namespaces and hold an enclosing account lifetime.
/// Close older processes/tabs before the first write, which permanently upgrades the provider feature version to 2.
/// </summary>
public sealed partial class ChunkedEncryptedBlobCache : IBlobCache, IBlobCacheInventory
{
    private const string Prefix = "encrypted-blob/v2/";
    private const string Generations = "encrypted-generation/v2/";
    private const string Chunks = "encrypted-chunk/v2/";
    private readonly IBlobChunkStore _chunks;
    private readonly IOrchestrationStore _store;
    private readonly IBlobCacheLock _operations;
    private readonly IAccountResourceLock _lifetimes;
    private readonly ILocalKeyProvider _keys;
    private readonly LocalKeyReference _writeKey;
    private readonly BlobEncryption _encryption;
    private readonly int _chunkSize;
    private readonly long _maximum;
    private readonly long _hardBudget;
    private static readonly string CapacityLock = Hash("Bsync.chunked/v2/capacity"u8);
    // These envelopes are internal storage JSON, never HTML. Keeping Base64 '+' literal avoids
    // large escaping scratch buffers. Plaintext manifests and all readers keep their existing context.
    private static readonly ChunkJson EnvelopeJson = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>
    /// Selects v2 writes with chunks of 64 KiB..1 MiB and at most 1024 chunks per object (maximum 1 GiB).
    /// Existing manifests carry their own chunk size and key reference. Key providers must enforce the documented
    /// per-key invocation budget. Import requires a seekable stable source; streaming transfers use known-hash partials.
    /// </summary>
    public ChunkedEncryptedBlobCache(IBlobChunkStore chunks, IOrchestrationStore metadata, IBlobCacheLock operations,
        IAccountResourceLock generationLifetimes, ILocalKeyProvider keys, LocalKeyReference writeKey, BlobEncryption encryption,
        int chunkSize = 1048576, long maximumBlobBytes = 1073741824, long hardBudgetBytes = long.MaxValue)
    {
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        _store = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _lifetimes = generationLifetimes ?? throw new ArgumentNullException(nameof(generationLifetimes));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _writeKey = writeKey ?? throw new ArgumentNullException(nameof(writeKey));
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
        if (chunkSize is < 65536 or > 1048576) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (maximumBlobBytes < 1 || maximumBlobBytes > Math.Min(1073741824L, 1024L * chunkSize)) throw new ArgumentOutOfRangeException(nameof(maximumBlobBytes));
        _chunkSize = chunkSize; _maximum = maximumBlobBytes;
        ArgumentOutOfRangeException.ThrowIfLessThan(hardBudgetBytes, 1); _hardBudget = hardBudgetBytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private Task RequireFormat(CancellationToken token) => _store is IOrchestrationFeatureStore features
        ? features.RequireFeatureVersionAsync(2, token)
        : throw new NotSupportedException("V2 encryption requires an orchestration provider with a downgrade barrier.");
    private static string Key(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Expected a lowercase SHA-256.", nameof(hash));
        return Prefix + hash;
    }
    private ValueTask<IAsyncDisposable> Own(string hash, CancellationToken token) => _operations.AcquireAsync(Hash(Encoding.UTF8.GetBytes(Key(hash))), token);
    private AccountScope Scope(string generation) => new("Bsync.Encrypted.Generation.v2", _writeKey.Account.StorageKey, generation);
    private LocalKeyReference Reference(ChunkEnvelope envelope) => new(_writeKey.Account, envelope.KeyId, envelope.KeyVersion);
    private static ChunkState Decode(OrchestrationRecord? record)
    {
        if (record is null) return new(2, null, null);
        if (record.Payload.Length > 524288) throw new InvalidDataException("Encrypted state exceeds parser bounds.");
        var state = JsonSerializer.Deserialize(record.Payload, ChunkJson.Default.ChunkState) ?? throw new InvalidDataException("Missing encrypted state.");
        if (state.Version != 2) throw new LocalStoreUnavailableException("outdated", "Unsupported encrypted state; data retained.");
        return state;
    }
    private byte[] Aad(string purpose, string hash, string generation, LocalKeyReference key, int index = -1, int length = -1) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "Bsync.chunked/2/AES-256-GCM", purpose, key.Account.StorageKey, hash, generation,
            key.KeyId, key.Version.ToString(CultureInfo.InvariantCulture), index.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture)));
    private async Task<LocalKeyAccess> Access(LocalKeyReference reference, CancellationToken token)
    {
        var access = await _keys.AcquireAsync(reference, token).ConfigureAwait(false);
        if (access.Reference == reference) return access;
        access.Dispose(); throw new CryptographicException("Key provider returned another identity.");
    }
    private async Task<ChunkManifest> OpenManifest(string hash, ChunkEnvelope envelope, CancellationToken token)
    {
        if (!Guid.TryParseExact(envelope.Generation, "N", out _) || envelope.Ciphertext is null || envelope.Ciphertext.Length > 262144 ||
            string.IsNullOrEmpty(envelope.KeyId) || envelope.KeyVersion < 1) throw new InvalidDataException("Invalid encrypted manifest bounds.");
        var reference = Reference(envelope);
        using var access = await Access(reference, token).ConfigureAwait(false);
        var bytes = await _encryption.OpenAsync(access.Key, Convert.FromBase64String(envelope.Ciphertext), Aad("manifest", hash, envelope.Generation, reference), token).ConfigureAwait(false);
        try
        {
            var manifest = JsonSerializer.Deserialize(bytes, ChunkJson.Default.ChunkManifest) ?? throw new InvalidDataException("Missing manifest.");
            if (manifest.Version != 2) throw new LocalStoreUnavailableException("outdated", "Unsupported chunk manifest; data retained.");
            if (manifest.Hash != hash || manifest.Generation != envelope.Generation || manifest.ChunkSize is < 65536 or > 1048576 ||
                manifest.Length is < 0 or > 1073741824 || manifest.Chunks is null || manifest.Chunks.Length > 1024)
                throw new InvalidDataException("Invalid chunk manifest.");
            long total = 0;
            for (var i = 0; i < manifest.Chunks.Length; i++)
            {
                var chunk = manifest.Chunks[i];
                if (chunk is null || chunk.Id is null) throw new InvalidDataException("Missing chunk descriptor.");
                _ = Key(chunk.Id);
                if (chunk.Length < 1 || chunk.Length > manifest.ChunkSize || (i < manifest.Chunks.Length - 1 && chunk.Length != manifest.ChunkSize))
                    throw new InvalidDataException("Invalid chunk ordering or length.");
                total = checked(total + chunk.Length);
            }
            if (total != manifest.Length) throw new InvalidDataException("Manifest length mismatch.");
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async Task<ChunkEnvelope> SealManifest(ChunkManifest manifest, LocalKeyReference reference, CancellationToken token)
    {
        using var access = await Access(reference, token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ChunkJson.Default.ChunkManifest);
        try
        {
            var encrypted = await _encryption.SealAsync(access.Key, bytes, Aad("manifest", manifest.Hash, manifest.Generation, reference), token).ConfigureAwait(false);
            return new(manifest.Generation, reference.KeyId, reference.Version, Convert.ToBase64String(encrypted));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async Task Save(string hash, OrchestrationRecord? record, ChunkState state, CancellationToken token)
    {
        await RequireFormat(token).ConfigureAwait(false);
        if (!await _store.TryWriteAsync([new(Key(hash), record?.Revision, JsonSerializer.Serialize(state, EnvelopeJson.ChunkState))], cancellationToken: token).ConfigureAwait(false))
            throw new IOException("Encrypted state changed outside physical ownership.");
    }
    private async Task<byte[]> ReadChunk(ChunkManifest manifest, ChunkEnvelope envelope, int index, CancellationToken token)
    {
        var chunk = manifest.Chunks[index];
        var bytes = await _chunks.ReadAsync(chunk.Id, checked(chunk.Length + 28), token).ConfigureAwait(false);
        var reference = Reference(envelope);
        using var access = await Access(reference, token).ConfigureAwait(false);
        return await _encryption.OpenAsync(access.Key, bytes, Aad("chunk", manifest.Hash, manifest.Generation, reference, index, chunk.Length), token).ConfigureAwait(false);
    }
    private async Task<ChunkDescriptor> WriteChunk(ChunkManifest manifest, LocalKeyReference reference, int index, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using var access = await Access(reference, token).ConfigureAwait(false);
        var encrypted = await _encryption.SealAsync(access.Key, bytes, Aad("chunk", manifest.Hash, manifest.Generation, reference, index, bytes.Length), token).ConfigureAwait(false);
        var id = Hash(encrypted);
        // Inventory is durable before physical bytes. Interrupted writes are discoverable without scanning the cache.
        if (!await _store.TryWriteAsync([new(Chunks + manifest.Generation + "/" + id, null, encrypted.Length.ToString(CultureInfo.InvariantCulture))], cancellationToken: token).ConfigureAwait(false))
            throw new IOException("Encrypted chunk identity collision.");
        // Serialize bounded physical writes while checking total ciphertext, including old readers and staging.
        // No network download or full-object hash runs under this capacity lock.
        await using (var capacity = await _operations.AcquireAsync(CapacityLock, token).ConfigureAwait(false))
        {
            // The unlimited sentinel requires no physical inventory. Browser inventory cursors read payload values;
            // scanning them before every append otherwise rereads all preceding ciphertext (quadratic I/O).
            if (_hardBudget != long.MaxValue && await _chunks.GetStoredBytesAsync(token).ConfigureAwait(false) > _hardBudget - encrypted.Length)
                throw new BlobCapacityException();
            await _chunks.WriteAsync(id, encrypted, token).ConfigureAwait(false);
        }
        return new(id, bytes.Length);
    }
    private async Task DeleteChunk(string generation, string id, CancellationToken token)
    {
        var record = await _store.ReadAsync(Chunks + generation + "/" + id, token).ConfigureAwait(false);
        if (record is null) return;
        await _chunks.DeleteAsync(id, token).ConfigureAwait(false);
        if (!await _store.TryWriteAsync([new(record.Key, record.Revision, null)], cancellationToken: token).ConfigureAwait(false)) throw new IOException("Chunk cleanup changed concurrently.");
    }

    /// <inheritdoc />
    public async Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var state = Decode(await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false));
        return state.Complete is { } complete ? (await OpenManifest(sha256, complete, cancellationToken).ConfigureAwait(false)).Length : null;
    }
    /// <inheritdoc />
    public async Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var state = Decode(await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false));
        return state.Partial is { } partial ? (await OpenManifest(sha256, partial, cancellationToken).ConfigureAwait(false)).Length : 0;
    }
    /// <inheritdoc />
    public async Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        await Append(sha256, bytes, cancellationToken).ConfigureAwait(false);
    }
    private async Task Append(string hash, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var record = await _store.ReadAsync(Key(hash), token).ConfigureAwait(false);
        var state = Decode(record);
        ChunkManifest manifest; LocalKeyReference reference;
        if (state.Partial is { } partial)
        { manifest = await OpenManifest(hash, partial, token).ConfigureAwait(false); reference = Reference(partial); }
        else
        {
            manifest = new(2, hash, Guid.NewGuid().ToString("N"), _chunkSize, 0, []); reference = _writeKey;
            // Authenticate existing complete metadata before starting any replacement; wrong keys never trigger overwrite.
            if (!state.RecoveryAuthorized && state.Complete is { } complete) await OpenManifest(hash, complete, token).ConfigureAwait(false);
            using var access = await Access(reference, token).ConfigureAwait(false); _ = access.Key;
            await RequireFormat(token).ConfigureAwait(false);
            if (!await _store.TryWriteAsync([
                new(Generations + manifest.Generation, null, hash),
                new(GenerationKeys + manifest.Generation, null, JsonSerializer.Serialize(new ChunkGenerationKey(1, reference.KeyId, reference.Version), ChunkJson.Default.ChunkGenerationKey))
            ], cancellationToken: token).ConfigureAwait(false)) throw new IOException("Generation collision.");
        }
        if (bytes.Length > _maximum - manifest.Length || manifest.Length + bytes.Length > 1024L * manifest.ChunkSize) throw new BlobCapacityException();
        await RequireFormat(token).ConfigureAwait(false);
        var descriptors = manifest.Chunks.ToList();
        var oldTail = descriptors.Count > 0 && descriptors[^1].Length < manifest.ChunkSize ? descriptors[^1] : null;
        byte[]? buffer = null;
        try
        {
            var filled = 0;
            if (oldTail is not null && bytes.Length > 0)
            {
                buffer = new byte[manifest.ChunkSize];
                var previous = await ReadChunk(manifest, state.Partial!, descriptors.Count - 1, token).ConfigureAwait(false);
                try { previous.CopyTo(buffer, 0); filled = previous.Length; } finally { CryptographicOperations.ZeroMemory(previous); }
                descriptors.RemoveAt(descriptors.Count - 1);
            }
            var remaining = bytes;
            while (remaining.Length > 0)
            {
                // Callers keep input stable until completion. Aligned chunks can be encrypted directly;
                // only an incomplete chunk needs a private buffer for joining with a later append.
                if (filled == 0 && remaining.Length >= manifest.ChunkSize)
                {
                    descriptors.Add(await WriteChunk(manifest, reference, descriptors.Count, remaining[..manifest.ChunkSize], token).ConfigureAwait(false));
                    remaining = remaining[manifest.ChunkSize..];
                    continue;
                }
                buffer ??= new byte[manifest.ChunkSize];
                var take = Math.Min(buffer.Length - filled, remaining.Length);
                remaining.Span[..take].CopyTo(buffer.AsSpan(filled)); filled += take; remaining = remaining[take..];
                if (filled == buffer.Length || remaining.IsEmpty)
                {
                    descriptors.Add(await WriteChunk(manifest, reference, descriptors.Count, buffer.AsMemory(0, filled), token).ConfigureAwait(false));
                    filled = 0;
                }
            }
            var next = manifest with { Length = manifest.Length + bytes.Length, Chunks = descriptors.ToArray() };
            var envelope = await SealManifest(next, reference, token).ConfigureAwait(false);
            await Save(hash, record, state with { Partial = envelope }, token).ConfigureAwait(false);
            if (oldTail is not null && bytes.Length > 0) await DeleteChunk(manifest.Generation, oldTail.Id, token).ConfigureAwait(false);
        }
        finally { if (buffer is not null) CryptographicOperations.ZeroMemory(buffer); }
    }
    /// <inheritdoc />
    public async Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        return await Complete(sha256, cancellationToken).ConfigureAwait(false);
    }
    private async Task<bool> Complete(string hash, CancellationToken token)
    {
        var record = await _store.ReadAsync(Key(hash), token).ConfigureAwait(false);
        var state = Decode(record);
        if (state.Partial is not { } partial) return false;
        var manifest = await OpenManifest(hash, partial, token).ConfigureAwait(false);
        using var digest = new StreamingSha256();
        for (var i = 0; i < manifest.Chunks.Length; i++)
        {
            var bytes = await ReadChunk(manifest, partial, i, token).ConfigureAwait(false);
            try { digest.AppendData(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (Convert.ToHexStringLower(digest.GetHashAndReset()) != hash)
        { await Save(hash, record, state with { Partial = null, PartialPurpose = null }, token).ConfigureAwait(false); return false; }
        if (!state.RecoveryAuthorized && state.Complete is { } old)
        {
            // Ambiguous old authentication failures remain conservative; explicit quarantine recovery is separate.
            var previous = await OpenManifest(hash, old, token).ConfigureAwait(false);
            for (var i = 0; i < previous.Chunks.Length; i++)
            {
                var bytes = await ReadChunk(previous, old, i, token).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        if (state.RecoveryAuthorized && state.Complete is { } quarantined)
        {
            var protection = await _store.ReadAsync("encrypted-protected/v2/" + quarantined.Generation, token).ConfigureAwait(false);
            if (protection?.Payload != hash) throw new InvalidDataException("Recovery quarantine ownership is missing; original data retained.");
        }
        token.ThrowIfCancellationRequested();
        await Save(hash, record, state with { Complete = partial, Partial = null, RecoveryAuthorized = false, PartialPurpose = null }, token).ConfigureAwait(false);
        return true;
    }
    /// <inheritdoc />
    public async Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false);
        var state = Decode(record);
        if (state.Partial is { } partial) await OpenManifest(sha256, partial, cancellationToken).ConfigureAwait(false);
        await Save(sha256, record, state with { Partial = null, PartialPurpose = null }, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var state = Decode(await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false));
        if (state.Complete is not { } complete) return null;
        var manifest = await OpenManifest(sha256, complete, cancellationToken).ConfigureAwait(false);
        var lifetime = await _lifetimes.TryAcquireAsync(Scope(complete.Generation), false, cancellationToken).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
        return new ChunkReadStream(this, manifest, complete, lifetime);
    }
    /// <inheritdoc />
    public async Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default)
    {
        if (!content.CanSeek) throw new ArgumentException("V2 import requires a seekable stable source; use partial transfer APIs for a known hash.", nameof(content));
        var start = content.Position;
        if (content.Length - start > _maximum) throw new BlobCapacityException();
        var hash = Convert.ToHexStringLower(await StreamingSha256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        content.Position = start;
        await using var held = await Own(hash, cancellationToken).ConfigureAwait(false);
        await ReplaceFromStream(hash, content, cancellationToken).ConfigureAwait(false);
        return (hash, content.Position - start);
    }
    private async Task ReplaceFromStream(string hash, Stream source, CancellationToken token, string purpose = "import")
    {
        var record = await _store.ReadAsync(Key(hash), token).ConfigureAwait(false);
        var state = Decode(record);
        if (!state.RecoveryAuthorized && state.Complete is { } complete) await OpenManifest(hash, complete, token).ConfigureAwait(false);
        var progress = state.Partial is { } partial ? await OpenManifest(hash, partial, token).ConfigureAwait(false) : null;
        using var writeAccess = await Access(_writeKey, token).ConfigureAwait(false); _ = writeAccess.Key;
        var resume = progress is not null && state.PartialPurpose == purpose && Reference(state.Partial!) == _writeKey && progress.Length <= source.Length - source.Position;
        if (resume) source.Position += progress!.Length;
        else await Save(hash, record, state with { Partial = null, PartialPurpose = purpose }, token).ConfigureAwait(false);
        var buffer = new byte[_chunkSize];
        try
        {
            var any = false;
            while (true)
            {
                var count = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token).ConfigureAwait(false);
                if (count == 0) break;
                any = true; await Append(hash, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (!any && !resume) await Append(hash, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
            if (!await Complete(hash, token).ConfigureAwait(false)) throw new BlobCorruptedException("Source changed during import.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    /// <summary>Rotates a complete object through verified encrypted staging. Existing readers retain the old generation/key.</summary>
    public async Task RotateAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var state = Decode(await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false));
        if (state.Partial is not null && state.PartialPurpose != "rotation") throw new InvalidOperationException("Finish or explicitly discard the unrelated partial before rotating.");
        if (state.Complete is not { } complete) return;
        var manifest = await OpenManifest(sha256, complete, cancellationToken).ConfigureAwait(false);
        var lifetime = await _lifetimes.TryAcquireAsync(Scope(complete.Generation), false, cancellationToken).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
        await using var source = new ChunkReadStream(this, manifest, complete, lifetime);
        await ReplaceFromStream(sha256, source, cancellationToken, "rotation").ConfigureAwait(false);
    }
    /// <summary>Explicitly copies and verifies a v1 object into v2. The source is retained for host-managed rollback/key retirement.</summary>
    public async Task MigrateAsync(EncryptedBlobCache source, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var stream = await source.OpenReadAsync(sha256, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Missing v1 object.");
        var result = await ImportAsync(stream, cancellationToken).ConfigureAwait(false);
        if (result.Sha256 != sha256) throw new BlobCorruptedException("V1 migration identity mismatch.");
    }
    /// <summary>
    /// Explicit host-authorized recovery from unreadable ciphertext. The stable seekable source is fully hash checked
    /// before any mutation. Original pointers and every referenced generation are durably quarantined, then replacement
    /// is verified again before publication. Quarantine is never automatically reclaimed. Authorization and source
    /// trust belong to the caller; this method does not distinguish a wrong key from damaged ciphertext.
    /// </summary>
    public async Task RecoverAsync(string sha256, Stream verifiedSource, CancellationToken cancellationToken = default)
    {
        _ = Key(sha256);
        if (!verifiedSource.CanSeek) throw new ArgumentException("Recovery requires a seekable stable source.", nameof(verifiedSource));
        if (verifiedSource.Length - verifiedSource.Position > _maximum) throw new BlobCapacityException();
        var start = verifiedSource.Position;
        if (Convert.ToHexStringLower(await StreamingSha256.HashDataAsync(verifiedSource, cancellationToken).ConfigureAwait(false)) != sha256)
            throw new BlobCorruptedException("Recovery source identity mismatch.");
        verifiedSource.Position = start;
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false);
        var state = Decode(record);
        using var access = await Access(_writeKey, cancellationToken).ConfigureAwait(false); _ = access.Key;
        await RequireFormat(cancellationToken).ConfigureAwait(false);
        List<OrchestrationMutation> mutations = [];
        if (record is not null) mutations.Add(new("encrypted-quarantine/v2/" + sha256 + "/" + Guid.NewGuid().ToString("N"), null, record.Payload));
        foreach (var generation in new[] { state.Complete?.Generation, state.Partial?.Generation }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid quarantine generation.");
            var protectionKey = "encrypted-protected/v2/" + generation;
            if (await _store.ReadAsync(protectionKey, cancellationToken).ConfigureAwait(false) is null) mutations.Add(new(protectionKey, null, sha256));
        }
        state = state with { Partial = null, RecoveryAuthorized = true };
        mutations.Add(new(Key(sha256), record?.Revision, JsonSerializer.Serialize(state, EnvelopeJson.ChunkState)));
        if (!await _store.TryWriteAsync(mutations, cancellationToken: cancellationToken).ConfigureAwait(false)) throw new IOException("Recovery ownership changed.");
        await ReplaceFromStream(sha256, verifiedSource, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false);
        var state = Decode(record);
        if (state.Complete is { } complete) await OpenManifest(sha256, complete, cancellationToken).ConfigureAwait(false);
        await Save(sha256, record, state with { Complete = null }, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Bounded ordinal object-state inventory, including partial/empty states. Continuation is the last hash returned; SizeAsync distinguishes complete content.</summary>
    public async Task<IReadOnlyList<string>> ListPageAsync(string? afterHash = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var page = await _store.ReadPageAsync(Prefix, afterHash is null ? null : Key(afterHash), limit, cancellationToken).ConfigureAwait(false);
        // Return stored identities, including partial-only entries; callers use SizeAsync to distinguish complete content.
        return page.Select(r => r.Key[Prefix.Length..]).ToArray();
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>(); string? after = null;
        while (true)
        {
            var page = await ListPageAsync(after, 100, cancellationToken).ConfigureAwait(false);
            foreach (var hash in page) if (await SizeAsync(hash, cancellationToken).ConfigureAwait(false) is not null) result.Add(hash);
            if (page.Count < 100) return result;
            after = page[^1];
        }
    }
    /// <inheritdoc />
    public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => _chunks.GetStoredBytesAsync(cancellationToken);

    /// <summary>
    /// Removes journaled chunks not referenced by the selected current complete/partial manifest, including a tail
    /// written before a crash prevented manifest publication. Reads at most limit records. Continuations bind the
    /// selected generation; restart at null if that generation changes. Complete readers conservatively block cleanup.
    /// </summary>
    public async Task<ChunkCleanupResult> ReconcileChunksAsync(string sha256, bool partial, string? continuation = null,
        int limit = 128, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        await using var held = await Own(sha256, cancellationToken).ConfigureAwait(false);
        var state = Decode(await _store.ReadAsync(Key(sha256), cancellationToken).ConfigureAwait(false));
        var envelope = partial ? state.Partial : state.Complete;
        if (envelope is null) return new(0, 0, null, false);
        var manifest = await OpenManifest(sha256, envelope, cancellationToken).ConfigureAwait(false);
        var prefix = Chunks + manifest.Generation + "/";
        if (continuation is not null && !continuation.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Chunk continuation belongs to another generation.", nameof(continuation));
        await using var exclusive = await _lifetimes.TryAcquireAsync(Scope(manifest.Generation), true, cancellationToken).ConfigureAwait(false);
        if (exclusive is null) return new(0, 0, continuation, true);
        var referenced = manifest.Chunks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var page = await _store.ReadPageAsync(prefix, continuation, limit, cancellationToken).ConfigureAwait(false);
        var removed = 0; long removedBytes = 0;
        foreach (var chunk in page)
        {
            var id = chunk.Key[prefix.Length..]; _ = Key(id);
            if (referenced.Contains(id)) continue;
            if (!int.TryParse(chunk.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size is < 28 or > 1048604)
                throw new InvalidDataException("Invalid chunk inventory; data retained.");
            await DeleteChunk(manifest.Generation, id, cancellationToken).ConfigureAwait(false); removed++; removedBytes += size;
        }
        return new(removed, removedBytes, page.Count == limit ? page[^1].Key : null, false);
    }

    /// <summary>Rotates within one cooperative budget covering ownership, key access, verification and publication. Timeout retains staging.</summary>
    public async Task RotateAsync(string sha256, TimeSpan budget, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        using var deadline = new CooperativeDeadline(budget, timeProvider ?? TimeProvider.System, cancellationToken, "chunk_rotation");
        await RotateAsync(sha256, deadline.Token).ConfigureAwait(false); _ = deadline.Remaining;
    }

    /// <summary>Copies v1 within one cooperative budget, retaining the source and resumable v2 staging on interruption.</summary>
    public async Task MigrateAsync(EncryptedBlobCache source, string sha256, TimeSpan budget, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        using var deadline = new CooperativeDeadline(budget, timeProvider ?? TimeProvider.System, cancellationToken, "chunk_migration");
        await MigrateAsync(source, sha256, deadline.Token).ConfigureAwait(false); _ = deadline.Remaining;
    }

    /// <summary>
    /// Reclaims at most chunkLimit physical chunks while scanning at most 32 generations. Old readers prevent collection.
    /// Current partial/complete generations and unrecognized state are preserved. Returns the last scanned generation
    /// key, or null at end; repeat scans to revisit reader-protected generations. Cancellation leaves journals resumable.
    /// </summary>
    public async Task<string?> CollectAsync(string? continuation = null, int chunkLimit = 128, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkLimit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkLimit, 1000);
        if (continuation is not null && !continuation.StartsWith(Generations, StringComparison.Ordinal)) throw new ArgumentException("Foreign generation continuation.", nameof(continuation));
        var page = await _store.ReadPageAsync(Generations, continuation, 32, cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var generation in page)
        {
            var id = generation.Key[Generations.Length..];
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid generation identity.");
            if (await _store.ReadAsync("encrypted-protected/v2/" + id, cancellationToken).ConfigureAwait(false) is not null) { continuation = generation.Key; continue; }
            await using var held = await Own(generation.Payload, cancellationToken).ConfigureAwait(false);
            var state = Decode(await _store.ReadAsync(Key(generation.Payload), cancellationToken).ConfigureAwait(false));
            if (state.Complete is { } complete) await OpenManifest(generation.Payload, complete, cancellationToken).ConfigureAwait(false);
            if (state.Partial is { } partial) await OpenManifest(generation.Payload, partial, cancellationToken).ConfigureAwait(false);
            if (state.Complete?.Generation == id || state.Partial?.Generation == id) { continuation = generation.Key; continue; }
            await using var exclusive = await _lifetimes.TryAcquireAsync(Scope(id), true, cancellationToken).ConfigureAwait(false);
            if (exclusive is null) { continuation = generation.Key; continue; }
            var chunks = await _store.ReadPageAsync(Chunks + id + "/", null, chunkLimit - removed, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in chunks) { await DeleteChunk(id, chunk.Key[(Chunks.Length + id.Length + 1)..], cancellationToken).ConfigureAwait(false); removed++; }
            if (removed == chunkLimit) return continuation;
            var keyReference = await _store.ReadAsync(GenerationKeys + id, cancellationToken).ConfigureAwait(false);
            var scrub = await _store.ReadAsync(Scrubs + id, cancellationToken).ConfigureAwait(false);
            var changes = new List<OrchestrationMutation> { new(generation.Key, generation.Revision, null) };
            if (keyReference is not null) changes.Add(new(keyReference.Key, keyReference.Revision, null));
            if (scrub is not null) changes.Add(new(scrub.Key, scrub.Revision, null));
            if (!await _store.TryWriteAsync(changes, cancellationToken: cancellationToken).ConfigureAwait(false))
                throw new IOException("Generation cleanup changed outside physical ownership.");
            continuation = generation.Key;
        }
        return page.Count == 32 ? continuation : null;
    }

    private sealed class ChunkReadStream(ChunkedEncryptedBlobCache cache, ChunkManifest manifest, ChunkEnvelope envelope, IAsyncDisposable lifetime) : Stream
    {
        private byte[]? _buffer;
        private int _index = -1;
        private long _position;
        private bool _disposed;
        private readonly SemaphoreSlim _gate = new(1, 1);
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => manifest.Length;
        public override long Position { get => _position; set { ObjectDisposedException.ThrowIf(_disposed, this); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
        public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_position >= Length || destination.IsEmpty) return 0;
                var index = checked((int)(_position / manifest.ChunkSize));
                if (index != _index)
                {
                    if (_buffer is not null) { CryptographicOperations.ZeroMemory(_buffer); _buffer = null; }
                    _index = -1;
                    _buffer = await cache.ReadChunk(manifest, envelope, index, cancellationToken).ConfigureAwait(false); _index = index;
                }
                var offset = (int)(_position % manifest.ChunkSize);
                var count = Math.Min(destination.Length, _buffer!.Length - offset);
                _buffer.AsMemory(offset, count).CopyTo(destination); _position += count; return count;
            }
            finally { _gate.Release(); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads for native/browser chunk storage.");
        public override async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                if (_buffer is not null) CryptographicOperations.ZeroMemory(_buffer);
                await lifetime.DisposeAsync().ConfigureAwait(false); _buffer = null; _disposed = true;
            }
            finally { _gate.Release(); }
            GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing) { if (disposing && !_disposed) throw new NotSupportedException("Dispose chunk readers asynchronously to release physical lifetime ownership."); base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed record ChunkState(int Version, ChunkEnvelope? Complete, ChunkEnvelope? Partial, bool RecoveryAuthorized = false, string? PartialPurpose = null);
internal sealed record ChunkEnvelope(string Generation, string KeyId, int KeyVersion, string Ciphertext);
internal sealed record ChunkManifest(int Version, string Hash, string Generation, int ChunkSize, long Length, ChunkDescriptor[] Chunks);
internal sealed record ChunkDescriptor(string Id, int Length);
/// <summary>One bounded chunk reconciliation pass. A busy reader preserves its entire generation.</summary>
/// <param name="RemovedChunks">Unreferenced journal entries removed.</param>
/// <param name="RemovedBytes">Reserved ciphertext bytes of those entries (an interrupted write may not have reached storage).</param>
/// <param name="Continuation">Last scanned inventory key, or null at end.</param>
/// <param name="Busy">A reader still holds the selected generation.</param>
public sealed record ChunkCleanupResult(int RemovedChunks, long RemovedBytes, string? Continuation, bool Busy);
[JsonSerializable(typeof(ChunkState))]
[JsonSerializable(typeof(ChunkManifest))]
[JsonSerializable(typeof(ChunkGenerationKey))]
[JsonSerializable(typeof(ChunkScrubCheckpoint))]
internal partial class ChunkJson : JsonSerializerContext;
