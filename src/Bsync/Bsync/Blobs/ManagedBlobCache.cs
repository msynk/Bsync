using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>The configured capacity is exhausted by content that cannot be evicted.</summary>
public sealed class BlobCapacityException() : IOException("Insufficient unprotected blob capacity. Release unneeded pins or increase the budget.");

/// <summary>Durable pin ownership and accounted bytes, including partially written/reserved content.</summary>
/// <param name="SchemaVersion">Metadata schema.</param>
/// <param name="Bytes">Conservative accounted byte count.</param>
/// <param name="LastAccessUtc">Deterministic LRU ordering; hash breaks ties.</param>
/// <param name="Owners">Stable owners, not a mutable reference count. Owners survive restart until explicitly released.</param>
public sealed record BlobCacheEntry(int SchemaVersion, long Bytes, DateTimeOffset LastAccessUtc, string[] Owners);

/// <summary>
/// Account-scoped cache decorator with durable owner pins and deterministic eviction. All cache users must use this
/// decorator and the same non-expiring lock provider; direct mutation of the underlying cache bypasses these guarantees.
/// Import requires a seekable stream and reserves capacity before writing. Unknown legacy files are retained and counted
/// by reconciliation. Reader/snapshot lifetime pins are reclaimed only after their physical locks can be acquired;
/// custom/legacy pins remain conservative and require host reconciliation.
/// </summary>
public sealed partial class ManagedBlobCache : IBlobCacheReplacement
{
    /// <inheritdoc />
    public bool SupportsVerifiedReplacement => _inner is IBlobCacheReplacement { SupportsVerifiedReplacement: true };
    private readonly IBlobCache _inner;
    private readonly IBlobCacheInventory _inventory;
    private readonly IOrchestrationStore _store;
    private readonly IBlobCacheLock _locks;
    private readonly long _soft;
    private readonly long _hard;
    private readonly TimeProvider _time;
    private const string Prefix = "cache/";
    private static readonly System.Diagnostics.Metrics.Counter<long> CapacityRefusals = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.cache.capacity_refusals", "{operation}", "Writes refused because protected content/reservations exhaust the configured hard budget.");
    private static BlobCapacityException CapacityExceeded() { CapacityRefusals.Add(1); return new(); }

    /// <summary>Creates an account-scoped cache. Budgets include partial bytes and conservative write reservations.</summary>
    public ManagedBlobCache(IBlobCache inner, IOrchestrationStore store, IBlobCacheLock locks, long softBudgetBytes, long hardBudgetBytes, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(locks);
        _inventory = inner as IBlobCacheInventory ?? throw new NotSupportedException("Managed capacity requires an inventory that includes partial/staging bytes.");
        ArgumentOutOfRangeException.ThrowIfNegative(softBudgetBytes);
        if (hardBudgetBytes < softBudgetBytes || hardBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(hardBudgetBytes));
        _inner = inner; _store = store; _locks = locks; _soft = softBudgetBytes; _hard = hardBudgetBytes; _time = timeProvider ?? TimeProvider.System;
    }
    private static string Key(string hash) { FileBlobCacheLock.ValidateName(hash); if (hash.Length != 64) throw new ArgumentException("Expected a hash.", nameof(hash)); return Prefix + hash; }
    private static BlobCacheEntry Decode(OrchestrationRecord record)
    {
        var entry = JsonSerializer.Deserialize(record.Payload, CacheJson.Default.BlobCacheEntry)!;
        if (entry.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported blob cache metadata; content retained.");
        return entry;
    }
    private async Task<BlobCacheEntry> Read(string hash, CancellationToken ct) =>
        await _store.ReadAsync(Key(hash), ct).ConfigureAwait(false) is { } record ? Decode(record) : new(1, 0, _time.GetUtcNow(), []);
    private async Task Save(string hash, BlobCacheEntry entry, CancellationToken ct)
    {
        var record = await _store.ReadAsync(Key(hash), ct).ConfigureAwait(false);
        if (!await _store.TryWriteAsync([new(Key(hash), record?.Revision, JsonSerializer.Serialize(entry, CacheJson.Default.BlobCacheEntry))], cancellationToken: ct).ConfigureAwait(false))
            throw new InvalidOperationException("Cache metadata changed outside its exclusive lock.");
    }
    private async Task<List<(string Hash, BlobCacheEntry Entry)>> Entries(CancellationToken ct)
    {
        var result = new List<(string, BlobCacheEntry)>(); string? after = null;
        while (true)
        {
            var page = await _store.ReadPageAsync(Prefix, after, 1000, ct).ConfigureAwait(false);
            result.AddRange(page.Select(r => (r.Key[Prefix.Length..], Decode(r))));
            if (page.Count < 1000) return result;
            after = page[^1].Key;
        }
    }
    /// <summary>Serializes transfers of the same content across endpoints and workers, independently of metadata operations.</summary>
    public ValueTask<IAsyncDisposable> AcquireTransferAsync(string hash, CancellationToken cancellationToken = default)
    { _ = Key(hash); return _locks.AcquireAsync(hash, cancellationToken); }

    /// <summary>Pins even missing content before a side effect. Repeating the same stable owner is idempotent.</summary>
    public async Task PinAsync(string hash, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 256) throw new ArgumentException("Pin owners are bounded to 256 characters.", nameof(owner));
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        var entry = await Read(hash, cancellationToken).ConfigureAwait(false);
        if (entry.Owners.Contains(owner, StringComparer.Ordinal)) return;
        if (entry.Owners.Length >= 1000) throw new InvalidOperationException("Content has reached its pin owner limit.");
        await Save(hash, entry with { Owners = [.. entry.Owners, owner], LastAccessUtc = _time.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Releases only this owner. Hosts must reconcile abandoned owners from durable work, never by age alone.</summary>
    public async Task UnpinAsync(string hash, string owner, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        var entry = await Read(hash, cancellationToken).ConfigureAwait(false);
        await Save(hash, entry with { Owners = entry.Owners.Where(o => o != owner).ToArray() }, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Returns durable pin/accounting information.</summary>
    public async Task<BlobCacheEntry> GetEntryAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        return await Read(hash, cancellationToken).ConfigureAwait(false);
    }
    private async Task Reserve(string hash, long bytes, CancellationToken ct)
    {
        if (_inner is FileBlobCache native) await native.CollectRetiredAsync(cancellationToken: ct).ConfigureAwait(false);
        while (true)
        {
        var current = await Read(hash, ct).ConfigureAwait(false);
        long total = 0;
        long knownStored = 0;
        var candidates = new List<(string Hash, BlobCacheEntry Entry)>();
        string? after = null;
        while (true)
        {
            var page = await _store.ReadPageAsync(Prefix, after, 128, ct).ConfigureAwait(false);
            foreach (var record in page)
            {
                var id = record.Key[Prefix.Length..]; var entry = Decode(record);
                var actual = checked((await _inner.SizeAsync(id, ct).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(id, ct).ConfigureAwait(false));
                knownStored = checked(knownStored + actual);
                total = checked(total + Math.Max(entry.Bytes, actual));
                if (id != hash && entry.Owners.Length == 0 && Math.Max(entry.Bytes, actual) > 0) candidates.Add((id, entry));
            }
            candidates = candidates.OrderBy(e => e.Entry.LastAccessUtc).ThenBy(e => e.Hash, StringComparer.Ordinal).Take(32).ToList();
            if (page.Count < 128) break;
            after = page[^1].Key;
        }
        var stored = await _inventory.GetStoredBytesAsync(ct).ConfigureAwait(false);
        total = checked(total + Math.Max(0, stored - knownStored)); // Unknown legacy/staging bytes are protected capacity.
        var currentStored = checked((await _inner.SizeAsync(hash, ct).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(hash, ct).ConfigureAwait(false));
        var accountedCurrent = Math.Max(currentStored, current.Bytes);
        var additional = Math.Max(0, bytes - accountedCurrent);
        if (additional > _hard) throw CapacityExceeded();
        foreach (var candidate in candidates)
        {
            if (total <= _soft - additional) break;
            ct.ThrowIfCancellationRequested();
            var removed = Math.Max(candidate.Entry.Bytes, checked((await _inner.SizeAsync(candidate.Hash, ct).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(candidate.Hash, ct).ConfigureAwait(false)));
            await _inner.DeleteAsync(candidate.Hash, CancellationToken.None).ConfigureAwait(false);
            await _inner.DiscardPartialAsync(candidate.Hash, CancellationToken.None).ConfigureAwait(false);
            await Save(candidate.Hash, candidate.Entry with { Bytes = 0 }, ct).ConfigureAwait(false);
            total -= removed;
        }
        if (total > _hard - additional)
        {
            // A bounded candidate page may be insufficient. Rescan after its durable evictions instead of
            // materializing an unbounded global LRU list. Each successful pass removes at least one nonempty entry.
            if (candidates.Count > 0) continue;
            throw CapacityExceeded();
        }
        // Persist reservation before bytes. Crash between stores can overcount, never undercount a reported write.
        await Save(hash, current with { Bytes = Math.Max(bytes, accountedCurrent), LastAccessUtc = _time.GetUtcNow() }, ct).ConfigureAwait(false);
        return;
        }
    }
    /// <summary>
    /// Reconciles known bytes after crashes and adopts complete legacy objects without deleting them. Call before enabling
    /// writes on an existing cache. Reclaims v2 reader/snapshot pins only after a successful nonblocking lifetime-lock probe.
    /// Unknown/custom/legacy owners and unknown partial files are retained. Repeat later if OS lock cleanup is still pending.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        if (_inner is FileBlobCache native) await native.CollectRetiredAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var hashes = (await Entries(cancellationToken).ConfigureAwait(false)).Select(e => e.Hash)
            .Concat(await _inner.ListAsync(cancellationToken).ConfigureAwait(false)).Distinct(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            var recognized = await _store.ReadAsync(Key(hash), cancellationToken).ConfigureAwait(false) is not null;
            var entry = await Read(hash, cancellationToken).ConfigureAwait(false);
            var owners = new List<string>();
            foreach (var owner in entry.Owners)
            {
                if (owner.StartsWith("reader-v2/", StringComparison.Ordinal) || owner.StartsWith("snapshot-v2/", StringComparison.Ordinal))
                {
                    var name = owner[(owner.IndexOf('/') + 1)..];
                    if (name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    {
                        await using var abandoned = await _locks.TryAcquireAsync("lifetime-" + name, cancellationToken).ConfigureAwait(false);
                        if (abandoned is not null) continue; // Only a dead/released lifetime can relinquish this unique lock.
                    }
                }
                owners.Add(owner); // Unknown/custom/legacy owners remain conservative.
            }
            var bytes = checked((await _inner.SizeAsync(hash, cancellationToken).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(hash, cancellationToken).ConfigureAwait(false));
            // A live operation can have reserved bytes it has not written yet. Never lower that reservation.
            if (owners.Any(o => o.StartsWith("reader-v2/", StringComparison.Ordinal))) bytes = Math.Max(bytes, entry.Bytes);
            await Save(hash, entry with { Bytes = bytes, Owners = recognized ? owners.ToArray() : ["recovered/unknown"] }, cancellationToken).ConfigureAwait(false);
        }
    }
    /// <inheritdoc />
    public Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default) => _inner.SizeAsync(sha256, cancellationToken);
    /// <inheritdoc />
    public Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default) => _inner.PartialLengthAsync(sha256, cancellationToken);
    /// <inheritdoc />
    public async Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        var next = checked(await _inner.PartialLengthAsync(sha256, cancellationToken).ConfigureAwait(false) + bytes.Length + (await _inner.SizeAsync(sha256, cancellationToken).ConfigureAwait(false) ?? 0));
        if (_inner is EncryptedBlobCache)
            next = checked(next + await _inner.PartialLengthAsync(sha256, cancellationToken).ConfigureAwait(false) + 28);
        await Reserve(sha256, next, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _inner.AppendPartialAsync(sha256, bytes, CancellationToken.None).ConfigureAwait(false);
        if (_inner is EncryptedBlobCache) await UpdateBytes(sha256, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var lifetimeName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var owner = "reader-v2/" + lifetimeName;
        await using var lifetime = await _locks.AcquireAsync("lifetime-" + lifetimeName, cancellationToken).ConfigureAwait(false);
        await PinAsync(sha256, owner, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inner is EncryptedBlobCache)
            {
                await using var reservation = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
                var partial = await _inner.PartialLengthAsync(sha256, cancellationToken).ConfigureAwait(false);
                await Reserve(sha256, checked((await _inner.SizeAsync(sha256, cancellationToken).ConfigureAwait(false) ?? 0) + 2 * partial + 28), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // The durable lifetime pin protects bytes while full verification runs outside global metadata ownership.
            // Transfer/import callers still serialize the same physical content identity for the complete operation.
            var complete = await _inner.CompletePartialAsync(sha256, cancellationToken).ConfigureAwait(false);
            await using var update = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
            await UpdateBytes(sha256, cancellationToken).ConfigureAwait(false);
            return complete;
        }
        finally { await UnpinAsync(sha256, owner, CancellationToken.None).ConfigureAwait(false); }
    }
    private async Task UpdateBytes(string hash, CancellationToken ct)
    {
        var entry = await Read(hash, ct).ConfigureAwait(false);
        await Save(hash, entry with { Bytes = checked((await _inner.SizeAsync(hash, ct).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(hash, ct).ConfigureAwait(false)) }, ct).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _inner.DiscardPartialAsync(sha256, CancellationToken.None).ConfigureAwait(false);
        await UpdateBytes(sha256, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var name = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var owner = "reader-v2/" + name;
        var lifetime = await _locks.AcquireAsync("lifetime-" + name, cancellationToken).ConfigureAwait(false);
        try
        {
            await PinAsync(sha256, owner, cancellationToken).ConfigureAwait(false);
            var stream = await _inner.OpenReadAsync(sha256, cancellationToken).ConfigureAwait(false);
            if (stream is not null) return new PinnedStream(stream, async () =>
            {
                try { await UnpinAsync(sha256, owner).ConfigureAwait(false); }
                finally { await lifetime.DisposeAsync().ConfigureAwait(false); }
            });
            await UnpinAsync(sha256, owner, CancellationToken.None).ConfigureAwait(false);
            await lifetime.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            try { await UnpinAsync(sha256, owner, CancellationToken.None).ConfigureAwait(false); }
            finally { await lifetime.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }
    /// <summary>Imports and pins before returning, so saving a reference cannot race eviction. Release owner after durable work retires.</summary>
    public async Task<(string Sha256, long Size)> ImportPinnedAsync(Stream content, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 256) throw new ArgumentException("Pin owner too long.", nameof(owner));
        return await Import(content, owner, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Imports verified bytes. Reimporting correct content repairs a corrupt existing object while retaining its pins;
    /// replacement is promoted only after verification and requires capacity for both old and staged bytes.
    /// </summary>
    public Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default) => Import(content, null, cancellationToken);
    private async Task<(string Sha256, long Size)> Import(Stream content, string? owner, CancellationToken ct)
    {
        if (!content.CanSeek) throw new NotSupportedException("Managed import requires a seekable stream to reserve capacity before writing.");
        var start = content.Position; var size = checked(content.Length - start);
        if (size > _hard) throw CapacityExceeded();
        var hash = Convert.ToHexStringLower(await StreamingSha256.HashDataAsync(content, ct).ConfigureAwait(false));
        content.Position = start;
        await using var transfer = await AcquireTransferAsync(hash, ct).ConfigureAwait(false);
        var lifetimeName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var operationOwner = "reader-v2/" + lifetimeName;
        await using var lifetime = await _locks.AcquireAsync("lifetime-" + lifetimeName, ct).ConfigureAwait(false);
        await PinAsync(hash, operationOwner, ct).ConfigureAwait(false);
        try
        {
        if (owner is not null) await PinAsync(hash, owner, ct).ConfigureAwait(false);
        var existingSize = await _inner.SizeAsync(hash, ct).ConfigureAwait(false) ?? 0;
        await using (var existing = await _inner.OpenReadAsync(hash, ct).ConfigureAwait(false))
            if (existing is not null && existing.Length == size &&
                Convert.ToHexStringLower(await StreamingSha256.HashDataAsync(existing, ct).ConfigureAwait(false)) == hash) return (hash, size);
        if (_inner is EncryptedBlobCache encrypted)
        {
            await using (var reservation = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false))
                await Reserve(hash, checked(existingSize + await _inner.PartialLengthAsync(hash, ct).ConfigureAwait(false) + size + 28), ct).ConfigureAwait(false);
            var imported = await encrypted.ImportVerifiedAsync(content, hash, ct).ConfigureAwait(false);
            await using (var update = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false))
                await UpdateBytes(hash, ct).ConfigureAwait(false);
            return imported;
        }
        // Never delete corrupt bytes before a verified replacement exists. Old readers keep their handles, while
        // durable pins protect the logical identity. Include old bytes and any interrupted staging in the reservation.
        await using (var reservation = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false))
            await Reserve(hash, checked(existingSize + size + await _inner.PartialLengthAsync(hash, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await _inner.DiscardPartialAsync(hash, CancellationToken.None).ConfigureAwait(false);
        var buffer = new byte[81920];
        long written = 0;
        // An empty append creates the empty-content partial too.
        await _inner.AppendPartialAsync(hash, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
        while (written < size)
        {
            var count = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - written)), ct).ConfigureAwait(false);
            if (count == 0) throw new BlobCorruptedException("Import source changed during reading.");
            await _inner.AppendPartialAsync(hash, buffer.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);
            written += count;
        }
        ct.ThrowIfCancellationRequested();
        if (!await _inner.CompletePartialAsync(hash, CancellationToken.None).ConfigureAwait(false)) throw new BlobCorruptedException("Import source changed during reading.");
        await using (var update = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false))
            await UpdateBytes(hash, ct).ConfigureAwait(false);
        return (hash, size);
        }
        finally { await UnpinAsync(hash, operationOwner, CancellationToken.None).ConfigureAwait(false); }
    }
    /// <inheritdoc />
    public async Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
        var entry = await Read(sha256, cancellationToken).ConfigureAwait(false);
        if (entry.Owners.Length > 0) throw new InvalidOperationException("Pinned content cannot be deleted.");
        cancellationToken.ThrowIfCancellationRequested();
        await _inner.DeleteAsync(sha256, CancellationToken.None).ConfigureAwait(false);
        await UpdateBytes(sha256, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

    /// <summary>Rotates an EncryptedBlobCache object while preserving pins and reserving space for both ciphertext generations.</summary>
    public async Task RotateEncryptionAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (_inner is not EncryptedBlobCache encrypted) throw new NotSupportedException("The managed cache is not encrypted.");
        await using var transfer = await AcquireTransferAsync(sha256, cancellationToken).ConfigureAwait(false);
        var lifetimeName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var owner = "reader-v2/" + lifetimeName;
        await using var lifetime = await _locks.AcquireAsync("lifetime-" + lifetimeName, cancellationToken).ConfigureAwait(false);
        await PinAsync(sha256, owner, cancellationToken).ConfigureAwait(false);
        try
        {
            var logical = checked((await _inner.SizeAsync(sha256, cancellationToken).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(sha256, cancellationToken).ConfigureAwait(false));
            await using (var reservation = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false))
                await Reserve(sha256, checked(2 * logical + 56), cancellationToken).ConfigureAwait(false);
            await encrypted.RotateAsync(sha256, cancellationToken).ConfigureAwait(false);
            await using var update = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
            await UpdateBytes(sha256, cancellationToken).ConfigureAwait(false);
        }
        finally { await UnpinAsync(sha256, owner, CancellationToken.None).ConfigureAwait(false); }
    }

    private sealed class PinnedStream(Stream inner, Func<Task> release) : Stream
    {
        private bool _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            // A synchronous browser disposal cannot block JS interop. Keep the safe durable pin; use await using to release it.
            if (disposing && !_disposed) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { await release().ConfigureAwait(false); }
            GC.SuppressFinalize(this);
        }
    }
}

[JsonSerializable(typeof(BlobCacheEntry))]
internal partial class CacheJson : JsonSerializerContext;
