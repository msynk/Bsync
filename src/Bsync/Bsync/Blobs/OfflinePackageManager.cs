using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>An immutable content snapshot; documents are serialized blobs, never mutable collection references.</summary>
/// <param name="Id">Item identity within the manifest.</param>
/// <param name="Sha256">Exact plaintext content hash.</param>
/// <param name="Size">Exact content length.</param>
/// <param name="Required">Whether activation requires this item.</param>
/// <param name="Dependencies">Other item IDs that must be present when this item is required.</param>
public sealed record OfflinePackageItem(string Id, string Sha256, long Size, bool Required, string[] Dependencies);

/// <summary>Publisher-created coherent immutable content revision. Collection checkpoints are not a global snapshot.</summary>
/// <param name="SchemaVersion">Manifest format; currently 1.</param>
/// <param name="PackageId">Package identity.</param>
/// <param name="Revision">Opaque immutable revision identity.</param>
/// <param name="PublishedUtc">Publisher freshness evidence, not proof that no newer revision exists.</param>
/// <param name="Items">At most 256 uniquely named items.</param>
public sealed record OfflinePackageManifest(int SchemaVersion, string PackageId, string Revision, DateTimeOffset? PublishedUtc, OfflinePackageItem[] Items);

/// <summary>Local readiness, distinct from synchronization completion or server acceptance.</summary>
public enum OfflinePackageReadiness
{
    /// <summary>No active revision exists.</summary>
    NoActiveRevision,
    /// <summary>A required item of the active revision is missing or corrupt.</summary>
    Incomplete,
    /// <summary>The active revision is locally complete; unseen server revisions remain unknown.</summary>
    ReadyFreshnessUnknown,
    /// <summary>The active revision is complete and a different revision is known locally.</summary>
    ReadyKnownOutdated,
}

/// <summary>Result of checking one bounded manifest's content.</summary>
/// <param name="PackageId">Package identity.</param>
/// <param name="ActiveRevision">Active pointer, if any.</param>
/// <param name="LatestKnownRevision">Last staged publisher revision, if any.</param>
/// <param name="Readiness">Completeness of the active revision.</param>
/// <param name="MissingRequired">Unavailable required item IDs of the checked revision.</param>
/// <param name="MissingOptional">Unavailable optional item IDs.</param>
/// <param name="BytesRemaining">Bytes required to repair missing required content.</param>
/// <param name="VerifiedUtc">Time of this local verification.</param>
public sealed record OfflinePackageStatus(string PackageId, string? ActiveRevision, string? LatestKnownRevision,
    OfflinePackageReadiness Readiness, string[] MissingRequired, string[] MissingOptional, long BytesRemaining, DateTimeOffset VerifiedUtc);

/// <summary>
/// Account-scoped staged packages over durable metadata and managed content. Authorization is supplied by the host and
/// evaluated before staging/opening/activation; an offline policy may permit previously granted local access. No network
/// is required to inspect or open prepared content. Retired revision pins are released only by explicit removal.
/// </summary>
public sealed class OfflinePackageManager
{
    private readonly IOrchestrationStore _store;
    private readonly ManagedBlobCache _cache;
    private readonly BlobTransferManager _transfers;
    private readonly Func<OfflinePackageManifest, CancellationToken, Task<bool>> _authorize;
    private readonly IBlobCacheLock _locks;
    private readonly TimeProvider _time;
    /// <summary>Creates a manager bound to one account and endpoint, sharing the cache's cross-process lock namespace.</summary>
    public OfflinePackageManager(IOrchestrationStore store, ManagedBlobCache cache, BlobTransferManager transfers, IBlobCacheLock locks,
        Func<OfflinePackageManifest, CancellationToken, Task<bool>> authorize, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store)); _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers)); _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize)); _time = timeProvider ?? TimeProvider.System;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Prefix(string package) => "packages/" + Hash(package) + "/";
    private static string RevisionKey(string package, string revision) => Prefix(package) + Hash(revision);
    private static string Owner(OfflinePackageManifest manifest) => "package/" + Hash(manifest.PackageId) + "/" + Hash(manifest.Revision);
    private static string Encode(OfflinePackageManifest manifest) => JsonSerializer.Serialize(manifest, PackageJson.Default.OfflinePackageManifest);
    private static OfflinePackageManifest Decode(OrchestrationRecord record)
    {
        var manifest = JsonSerializer.Deserialize(record.Payload, PackageJson.Default.OfflinePackageManifest)!;
        Validate(manifest);
        return manifest;
    }
    /// <summary>Validates bounded manifests, exact identities, dependencies and cycles before any durable mutation.</summary>
    public static void Validate(OfflinePackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported package manifest; previous content retained.");
        if (string.IsNullOrWhiteSpace(manifest.PackageId) || manifest.PackageId.Length > 256 || string.IsNullOrWhiteSpace(manifest.Revision) || manifest.Revision.Length > 256)
            throw new ArgumentException("Package and revision identities must contain 1..256 characters.", nameof(manifest));
        if (manifest.Items is null || manifest.Items.Length > 256) throw new ArgumentException("A manifest has at most 256 items.", nameof(manifest));
        var items = new Dictionary<string, OfflinePackageItem>(StringComparer.Ordinal);
        foreach (var item in manifest.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 || item.Size < 0 || item.Dependencies is null || item.Dependencies.Length > 256
                || !items.TryAdd(item.Id, item)) throw new ArgumentException("Invalid or duplicate package item.", nameof(manifest));
            FileBlobCacheLock.ValidateName(item.Sha256);
            if (item.Sha256 == "metadata") throw new ArgumentException("Expected item content hash.", nameof(manifest));
        }
        foreach (var group in manifest.Items.GroupBy(i => i.Sha256))
            if (group.Select(i => i.Size).Distinct().Count() != 1) throw new ArgumentException("Identical hashes must have identical lengths.", nameof(manifest));
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!items.TryGetValue(id, out var item)) throw new ArgumentException("Missing package dependency.", nameof(manifest));
            if (!visiting.Add(id)) throw new ArgumentException("Package dependency cycle.", nameof(manifest));
            foreach (var dependency in item.Dependencies) Visit(dependency);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in items.Keys) Visit(id);
        if (Encode(manifest).Length > 524288) throw new ArgumentException("Manifest exceeds persistence bounds.", nameof(manifest));
        _ = checked(manifest.Items.Sum(i => i.Size));
    }
    private async Task Authorize(OfflinePackageManifest manifest, CancellationToken ct)
    { if (!await _authorize(manifest, ct).ConfigureAwait(false)) throw new UnauthorizedAccessException("Local package access is not permitted by the host policy."); }
    private static HashSet<string> Required(OfflinePackageManifest manifest)
    {
        var items = manifest.Items.ToDictionary(i => i.Id, StringComparer.Ordinal); var result = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id) { if (result.Add(id)) foreach (var dependency in items[id].Dependencies) Add(dependency); }
        foreach (var item in manifest.Items.Where(i => i.Required)) Add(item.Id);
        return result;
    }
    /// <summary>Persists preparation before pins/jobs, resuming idempotently after interruption. Does not contact the server.</summary>
    public async Task StageAsync(OfflinePackageManifest manifest, CancellationToken cancellationToken = default)
    {
        Validate(manifest);
        // Copy caller-owned arrays before awaiting or retaining them.
        manifest = Decode(new("manifest", "copy", Encode(manifest)));
        await Authorize(manifest, cancellationToken).ConfigureAwait(false);
        await using var held = await _locks.AcquireAsync(Hash("package/" + manifest.PackageId), cancellationToken).ConfigureAwait(false);
        var key = RevisionKey(manifest.PackageId, manifest.Revision);
        var existing = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Payload != Encode(manifest)) throw new InvalidOperationException("A package revision is immutable; publish a new revision identity.");
        var latestKey = Prefix(manifest.PackageId) + "latest";
        var latest = await _store.ReadAsync(latestKey, cancellationToken).ConfigureAwait(false);
        if (!await _store.TryWriteAsync([new(key, existing?.Revision, Encode(manifest)), new(latestKey, latest?.Revision, manifest.Revision)], cancellationToken: cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Package state changed outside its ownership boundary.");
        await Queue(manifest, cancellationToken).ConfigureAwait(false);
    }
    private async Task Queue(OfflinePackageManifest manifest, CancellationToken ct)
    {
        foreach (var item in manifest.Items)
        {
            await _cache.PinAsync(item.Sha256, Owner(manifest), ct).ConfigureAwait(false);
            var job = await _transfers.EnqueueAsync(item.Sha256, item.Size, BlobTransferDirection.Download, item.Required ? 1 : 0, ct).ConfigureAwait(false);
            if (job.State == BlobTransferState.Complete && await _cache.SizeAsync(item.Sha256, ct).ConfigureAwait(false) != item.Size)
                await _transfers.SetStateAsync(item.Sha256, BlobTransferDirection.Download, BlobTransferState.Queued, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Resumes the latest staged candidate, drains transfers within budget, and activates only verified required content.</summary>
    public async Task<OfflinePackageStatus> PrepareAsync(string packageId, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var latest = await _store.ReadAsync(Prefix(packageId) + "latest", cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("No staged package.");
        var manifest = Decode(await _store.ReadAsync(RevisionKey(packageId, latest.Payload), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing staged manifest."));
        await Authorize(manifest, cancellationToken).ConfigureAwait(false);
        await Queue(manifest, cancellationToken).ConfigureAwait(false);
        await _transfers.DrainAsync(budget, cancellationToken).ConfigureAwait(false);
        await TryActivateAsync(packageId, manifest.Revision, cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(packageId, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Atomically activates a fully verified candidate only while it remains the latest staged revision.</summary>
    public async Task<bool> TryActivateAsync(string packageId, string revision, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(Hash("package/" + packageId), cancellationToken).ConfigureAwait(false);
        var latestKey = Prefix(packageId) + "latest"; var activeKey = Prefix(packageId) + "active";
        var latest = await _store.ReadAsync(latestKey, cancellationToken).ConfigureAwait(false);
        if (latest?.Payload != revision) return false;
        var manifest = Decode(await _store.ReadAsync(RevisionKey(packageId, revision), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing staged manifest."));
        await Authorize(manifest, cancellationToken).ConfigureAwait(false);
        foreach (var item in manifest.Items) await _cache.PinAsync(item.Sha256, Owner(manifest), cancellationToken).ConfigureAwait(false);
        var missing = await Missing(manifest, cancellationToken).ConfigureAwait(false);
        if (missing.Required.Length > 0) return false;
        var active = await _store.ReadAsync(activeKey, cancellationToken).ConfigureAwait(false);
        return await _store.TryWriteAsync([new(activeKey, active?.Revision, revision), new(latestKey, latest.Revision, revision)], cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    private async Task<(string[] Required, string[] Optional, long Bytes)> Missing(OfflinePackageManifest manifest, CancellationToken ct)
    {
        var required = Required(manifest); var missing = new List<string>(); var optional = new List<string>(); long bytes = 0;
        foreach (var item in manifest.Items)
        {
            await using var stream = await _cache.OpenReadAsync(item.Sha256, ct).ConfigureAwait(false);
            var valid = stream is not null && stream.Length == item.Size
                && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) == item.Sha256;
            if (valid) continue;
            if (required.Contains(item.Id)) { missing.Add(item.Id); bytes = checked(bytes + item.Size); }
            else optional.Add(item.Id);
        }
        return (missing.ToArray(), optional.ToArray(), bytes);
    }
    /// <summary>Verifies at most 256 active items. This performs content I/O; hosts may cache displayed results but must label their age.</summary>
    public async Task<OfflinePackageStatus> GetStatusAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var active = await _store.ReadAsync(Prefix(packageId) + "active", cancellationToken).ConfigureAwait(false);
        var latest = await _store.ReadAsync(Prefix(packageId) + "latest", cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            if (latest is null) return await Remember(new(packageId, null, null, OfflinePackageReadiness.NoActiveRevision, [], [], 0, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            var candidate = Decode(await _store.ReadAsync(RevisionKey(packageId, latest.Payload), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing candidate manifest."));
            await Authorize(candidate, cancellationToken).ConfigureAwait(false);
            var unavailable = await Missing(candidate, cancellationToken).ConfigureAwait(false);
            return await Remember(new(packageId, null, latest.Payload, OfflinePackageReadiness.NoActiveRevision, unavailable.Required, unavailable.Optional, unavailable.Bytes, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        var manifest = Decode(await _store.ReadAsync(RevisionKey(packageId, active.Payload), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing active manifest."));
        await Authorize(manifest, cancellationToken).ConfigureAwait(false);
        var missing = await Missing(manifest, cancellationToken).ConfigureAwait(false);
        return await Remember(new(packageId, active.Payload, latest?.Payload, missing.Required.Length > 0 ? OfflinePackageReadiness.Incomplete
            : latest?.Payload != active.Payload ? OfflinePackageReadiness.ReadyKnownOutdated : OfflinePackageReadiness.ReadyFreshnessUnknown,
            missing.Required, missing.Optional, missing.Bytes, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    private async Task<OfflinePackageStatus> Remember(OfflinePackageStatus status, CancellationToken ct)
    {
        var key = Prefix(status.PackageId) + "verification";
        var previous = await _store.ReadAsync(key, ct).ConfigureAwait(false);
        if (previous is not null) _ = DecodeVerification(previous);
        // A concurrent verifier may win. Its valid observation is retained; the caller still receives this observation.
        await _store.TryWriteAsync([new(key, previous?.Revision, JsonSerializer.Serialize(new PackageVerification(1, status), PackageJson.Default.PackageVerification))], cancellationToken: ct).ConfigureAwait(false);
        return status;
    }

    private static OfflinePackageStatus DecodeVerification(OrchestrationRecord record)
    {
        var verification = JsonSerializer.Deserialize(record.Payload, PackageJson.Default.PackageVerification)!;
        if (verification.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported package verification schema; nothing was changed.");
        return verification.Status;
    }

    /// <summary>
    /// Returns the last durable verification for unchanged active/latest pointers, or null when absent/changed. Reads
    /// bounded metadata only and rechecks host authorization; does not prove bytes still exist. Display VerifiedUtc/age.
    /// Use GetStatusAsync for fresh verification; OpenActiveAsync and item reads always verify actual content.
    /// </summary>
    public async Task<OfflinePackageStatus?> GetLastVerificationAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(Hash("package/" + packageId), cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(Prefix(packageId) + "verification", cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var status = DecodeVerification(record);
        var active = await _store.ReadAsync(Prefix(packageId) + "active", cancellationToken).ConfigureAwait(false);
        var latest = await _store.ReadAsync(Prefix(packageId) + "latest", cancellationToken).ConfigureAwait(false);
        if (status.PackageId != packageId || status.ActiveRevision != active?.Payload || status.LatestKnownRevision != latest?.Payload) return null;
        if ((active?.Payload ?? latest?.Payload) is { } revision)
        {
            var manifest = await _store.ReadAsync(RevisionKey(packageId, revision), cancellationToken).ConfigureAwait(false);
            if (manifest is null) return null;
            await Authorize(Decode(manifest), cancellationToken).ConfigureAwait(false);
        }
        return status;
    }
    /// <summary>Opens one immutable active revision and pins its content until asynchronous disposal.</summary>
    public async Task<PackageSnapshot> OpenActiveAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(Hash("package/" + packageId), cancellationToken).ConfigureAwait(false);
        var active = await _store.ReadAsync(Prefix(packageId) + "active", cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No complete package revision is active.");
        var manifest = Decode(await _store.ReadAsync(RevisionKey(packageId, active.Payload), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing active manifest."));
        await Authorize(manifest, cancellationToken).ConfigureAwait(false);
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var owner = "snapshot-v2/" + name;
        var lifetime = await _locks.AcquireAsync("lifetime-" + name, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in manifest.Items) await _cache.PinAsync(item.Sha256, owner, cancellationToken).ConfigureAwait(false);
            if ((await Missing(manifest, cancellationToken).ConfigureAwait(false)).Required.Length > 0) throw new FileNotFoundException("The active package is incomplete; repair is required.");
            return new(manifest, _cache, owner, lifetime);
        }
        catch
        {
            try { foreach (var item in manifest.Items) await _cache.UnpinAsync(item.Sha256, owner).ConfigureAwait(false); }
            finally { await lifetime.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }
    /// <summary>Removes a retired manifest and its package pins. Active/latest revisions require explicit replacement first.</summary>
    public async Task RemoveRetiredAsync(string packageId, string revision, CancellationToken cancellationToken = default)
    {
        await using var held = await _locks.AcquireAsync(Hash("package/" + packageId), cancellationToken).ConfigureAwait(false);
        foreach (var pointer in new[] { "active", "latest" })
            if ((await _store.ReadAsync(Prefix(packageId) + pointer, cancellationToken).ConfigureAwait(false))?.Payload == revision)
                throw new InvalidOperationException("The active or preparing revision cannot be retired.");
        var key = RevisionKey(packageId, revision); var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        var manifest = Decode(record);
        foreach (var item in manifest.Items) await _cache.UnpinAsync(item.Sha256, Owner(manifest), cancellationToken).ConfigureAwait(false);
        await _store.TryWriteAsync([new(key, record.Revision, null)], cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An immutable revision handle. Use await using; cache reconciliation reclaims its lifetime pins after process/context termination releases its physical lock.</summary>
public sealed class PackageSnapshot : IAsyncDisposable
{
    private readonly OfflinePackageManifest _manifest;
    private readonly ManagedBlobCache _cache;
    private readonly string _owner;
    private readonly IAsyncDisposable _lifetime;
    private bool _disposed;
    internal PackageSnapshot(OfflinePackageManifest manifest, ManagedBlobCache cache, string owner, IAsyncDisposable lifetime)
    { _manifest = manifest; _cache = cache; _owner = owner; _lifetime = lifetime; }
    /// <summary>The fixed revision identity of this reader.</summary>
    public string Revision => _manifest.Revision;
    /// <summary>Opens an item from this revision, verifying identity again in case of external storage loss or tampering.</summary>
    public async Task<Stream?> OpenAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = _manifest.Items.SingleOrDefault(i => i.Id == itemId) ?? throw new KeyNotFoundException("Item is not in this revision.");
        var stream = await _cache.OpenReadAsync(item.Sha256, cancellationToken).ConfigureAwait(false);
        if (stream is null) return null;
        try
        {
            if (stream.Length != item.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != item.Sha256)
                throw new BlobCorruptedException("Prepared content is no longer verified.");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { foreach (var item in _manifest.Items) await _cache.UnpinAsync(item.Sha256, _owner).ConfigureAwait(false); }
        finally { await _lifetime.DisposeAsync().ConfigureAwait(false); }
    }
}

[JsonSerializable(typeof(OfflinePackageManifest))]
[JsonSerializable(typeof(PackageVerification))]
internal partial class PackageJson : JsonSerializerContext;
internal sealed record PackageVerification(int SchemaVersion, OfflinePackageStatus Status);
