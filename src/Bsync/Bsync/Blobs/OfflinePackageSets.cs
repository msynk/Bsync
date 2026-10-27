using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>An exact child package revision and its canonical manifest SHA-256.</summary>
public sealed record OfflinePackageSetChild(string PackageId, string Revision, string ManifestSha256)
{
    /// <summary>Schema 2 only: missing content or revoked access does not block required-child activation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; init; }
}
/// <summary>Immutable flat root with 1..32 children. Schema 1 requires all children; schema 2 permits optional children. Nested sets are not supported.</summary>
public sealed record OfflinePackageSetManifest(int SchemaVersion, string SetId, string Revision,
    DateTimeOffset? PublishedUtc, OfflinePackageSetChild[] Children);
/// <summary>Missing required content in one child; identities are separate fields to avoid delimiter ambiguity.</summary>
public sealed record PackageSetMissingItem(string PackageId, string ItemId, string Sha256, long Size);
/// <summary>An optional child unavailable at observation/open time: incomplete or unauthorized.</summary>
public sealed record PackageSetOptionalFailure(string PackageId, string Revision, string Reason);
/// <summary>Fresh local verification of the latest known root, with active-root repair status reported separately. Completeness never asserts absence of newer server publications.</summary>
public sealed record OfflinePackageSetStatus(string SetId, string? ActiveRevision, string? LatestKnownRevision,
    PackageSetMissingItem[] MissingRequired, long BytesRemaining, DateTimeOffset VerifiedUtc)
{
    /// <summary>Missing content in the currently active root, including externally evicted or corrupted bytes.</summary>
    public PackageSetMissingItem[] ActiveMissingRequired { get; init; } = [];
    /// <summary>Candidate optional-child failures; these never contribute to required bytes remaining.</summary>
    public PackageSetOptionalFailure[] OptionalFailures { get; init; } = [];
    /// <summary>Optional-child failures in the active revision, observed separately from the candidate.</summary>
    public PackageSetOptionalFailure[] ActiveOptionalFailures { get; init; } = [];
}

public sealed partial class OfflinePackageManager
{
    private static string SetPrefix(string id) => "package-sets/v1/" + Hash(id) + "/";
    private static string SetKey(string id, string revision) => SetPrefix(id) + Hash(revision);
    private static string SetReferencePrefix(string package, string revision) => "package-set-refs/" + Hash(package) + "/" + Hash(revision) + "/";
    private static string SetReference(OfflinePackageSetChild child, OfflinePackageSetManifest root) =>
        SetReferencePrefix(child.PackageId, child.Revision) + Hash(Hash(root.SetId) + Hash(root.Revision));
    private static string EncodeSet(OfflinePackageSetManifest root) => JsonSerializer.Serialize(root, PackageSetJson.Default.OfflinePackageSetManifest);

    /// <summary>Canonical integrity identity for an exact child manifest; this is not an authorization grant.</summary>
    public static string GetManifestSha256(OfflinePackageManifest manifest) { Validate(manifest); return Hash(Encode(manifest)); }

    /// <summary>Validates flat bounded roots. Child manifests retain their existing item/dependency bounds.</summary>
    public static void ValidateSet(OfflinePackageSetManifest root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.SchemaVersion is not (1 or 2)) throw new LocalStoreUnavailableException("outdated", "Unsupported package set; content retained.");
        if (string.IsNullOrWhiteSpace(root.SetId) || root.SetId.Length > 256 || string.IsNullOrWhiteSpace(root.Revision) || root.Revision.Length > 256
            || root.Children is null || root.Children.Length is < 1 or > 32) throw new ArgumentException("Invalid package set bounds.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in root.Children)
            if (child is null || string.IsNullOrWhiteSpace(child.PackageId) || child.PackageId.Length > 256 || !ids.Add(child.PackageId)
                || string.IsNullOrWhiteSpace(child.Revision) || child.Revision.Length > 256 || child.ManifestSha256 is null
                || child.ManifestSha256.Length != 64 || child.ManifestSha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                throw new ArgumentException("Invalid or duplicate package-set child.");
        if (root.SchemaVersion == 1 && root.Children.Any(c => c.Optional)) throw new ArgumentException("Optional children require root schema 2.");
        if (EncodeSet(root).Length > 32768) throw new ArgumentException("Package-set root exceeds 64 KiB UTF-16.");
    }
    private static OfflinePackageSetManifest DecodeSet(OrchestrationRecord record)
    {
        var root = JsonSerializer.Deserialize(record.Payload, PackageSetJson.Default.OfflinePackageSetManifest)
            ?? throw new InvalidDataException("Missing package-set root.");
        ValidateSet(root);
        if (record.Key != "copy" && record.Key != SetKey(root.SetId, root.Revision)) throw new InvalidDataException("Package-set root identity mismatch.");
        return root;
    }
    private async Task<OfflinePackageManifest> SetChild(OfflinePackageSetChild child, CancellationToken ct)
    {
        var manifest = Decode(await _store.ReadAsync(RevisionKey(child.PackageId, child.Revision), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing package-set child manifest."));
        if (manifest.PackageId != child.PackageId || manifest.Revision != child.Revision || GetManifestSha256(manifest) != child.ManifestSha256)
            throw new InvalidDataException("Package-set child identity mismatch.");
        await Authorize(manifest, ct).ConfigureAwait(false);
        return manifest;
    }
    private async Task<List<IAsyncDisposable>> OwnChildren(OfflinePackageSetManifest root, CancellationToken ct)
    {
        var held = new List<IAsyncDisposable>();
        try
        {
            foreach (var id in root.Children.Select(c => c.PackageId).Order(StringComparer.Ordinal))
                held.Add(await _locks.AcquireAsync(Hash("package/" + id), ct).ConfigureAwait(false));
            return held;
        }
        catch { await ReleaseChildren(held).ConfigureAwait(false); throw; }
    }
    private static async Task ReleaseChildren(List<IAsyncDisposable> held)
    { for (var i = held.Count - 1; i >= 0; i--) await held[i].DisposeAsync().ConfigureAwait(false); }

    /// <summary>
    /// Stages existing immutable package manifests and a root. Raises the permanent orchestration feature barrier to 3;
    /// close older processes/tabs under exclusive account ownership first. Every manifest must be authorized at staging;
    /// optional-child content may finish later. Aggregate child metadata is bounded to 4 MiB UTF-16.
    /// </summary>
    public async Task StageSetAsync(OfflinePackageSetManifest root, IReadOnlyList<OfflinePackageManifest> children, CancellationToken cancellationToken = default)
    {
        ValidateSet(root); ArgumentNullException.ThrowIfNull(children);
        root = DecodeSet(new("copy", "copy", EncodeSet(root)));
        if (children.Count != root.Children.Length) throw new ArgumentException("Supply every exact child once.", nameof(children));
        var copies = children.Select(c => { Validate(c); return Decode(new("copy", "copy", Encode(c))); }).ToArray();
        if (copies.Sum(c => (long)Encode(c).Length) > 2097152) throw new ArgumentException("Aggregate child metadata exceeds bounds.");
        foreach (var child in root.Children)
        {
            var matches = copies.Where(c => c.PackageId == child.PackageId && c.Revision == child.Revision).ToArray();
            if (matches.Length != 1 || GetManifestSha256(matches[0]) != child.ManifestSha256) throw new ArgumentException("Missing or substituted child.");
            await Authorize(matches[0], cancellationToken).ConfigureAwait(false);
        }
        foreach (var group in copies.SelectMany(c => c.Items).GroupBy(i => i.Sha256))
            if (group.Select(i => i.Size).Distinct().Count() != 1) throw new ArgumentException("Shared content has conflicting lengths.");
        if (_store is not IOrchestrationFeatureStore features) throw new NotSupportedException("Package sets require a downgrade barrier.");
        await features.RequireFeatureVersionAsync(3, cancellationToken).ConfigureAwait(false);
        await using var ownRoot = await _locks.AcquireAsync(Hash("package-set/" + root.SetId), cancellationToken).ConfigureAwait(false);
        var key = SetKey(root.SetId, root.Revision);
        var existing = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Payload != EncodeSet(root)) throw new InvalidOperationException("A package-set revision is immutable.");
        foreach (var manifest in copies) await StageAsync(manifest, cancellationToken).ConfigureAwait(false);
        var held = await OwnChildren(root, cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = await _store.ReadAsync(SetPrefix(root.SetId) + "latest", cancellationToken).ConfigureAwait(false);
            var mutations = new List<OrchestrationMutation> { new(key, existing?.Revision, EncodeSet(root)), new(SetPrefix(root.SetId) + "latest", latest?.Revision, root.Revision) };
            foreach (var child in root.Children)
            {
                _ = await SetChild(child, cancellationToken).ConfigureAwait(false);
                var reference = await _store.ReadAsync(SetReference(child, root), cancellationToken).ConfigureAwait(false);
                mutations.Add(new(SetReference(child, root), reference?.Revision, "1"));
            }
            if (!await _store.TryWriteAsync(mutations, cancellationToken: cancellationToken).ConfigureAwait(false)) throw new IOException("Package-set ownership changed.");
        }
        finally { await ReleaseChildren(held).ConfigureAwait(false); }
    }

    /// <summary>Runs existing durable transfers within a cooperative budget, then attempts activation of the latest root.</summary>
    public async Task<bool> PrepareSetAsync(string setId, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "package_set_prepare");
        var latest = await _store.ReadAsync(SetPrefix(setId) + "latest", deadline.Token).ConfigureAwait(false) ?? throw new KeyNotFoundException("No staged set.");
        var root = DecodeSet(await _store.ReadAsync(SetKey(setId, latest.Payload), deadline.Token).ConfigureAwait(false) ?? throw new InvalidDataException("Missing root."));
        foreach (var child in root.Children.OrderBy(c => c.Optional))
        {
            try { await Queue(await SetChild(child, deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) when (child.Optional) { /* Revocation never blocks the required set. */ }
        }
        await _transfers.DrainAsync(deadline.Remaining, deadline.Token).ConfigureAwait(false);
        return await TryActivateSetAsync(setId, root.Revision, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Publishes one active-root pointer after verifying all required children. A failed attempt preserves the previous root.</summary>
    public async Task<bool> TryActivateSetAsync(string setId, string revision, CancellationToken cancellationToken = default)
    {
        await using var ownRoot = await _locks.AcquireAsync(Hash("package-set/" + setId), cancellationToken).ConfigureAwait(false);
        var latest = await _store.ReadAsync(SetPrefix(setId) + "latest", cancellationToken).ConfigureAwait(false);
        if (latest?.Payload != revision) return false;
        var root = DecodeSet(await _store.ReadAsync(SetKey(setId, revision), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing root."));
        foreach (var child in root.Children.Where(c => !c.Optional))
            if ((await Missing(await SetChild(child, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)).Required.Length != 0) return false;
        var key = SetPrefix(setId) + "active";
        var active = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return await _store.TryWriteAsync([new(key, active?.Revision, revision), new(latest.Key, latest.Revision, revision)], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens all children through one immutable root, holding physical snapshot pins through replacement and retirement.</summary>
    public async Task<PackageSetSnapshot> OpenActiveSetAsync(string setId, CancellationToken cancellationToken = default)
    {
        await using var ownRoot = await _locks.AcquireAsync(Hash("package-set/" + setId), cancellationToken).ConfigureAwait(false);
        var active = await _store.ReadAsync(SetPrefix(setId) + "active", cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No active set.");
        var root = DecodeSet(await _store.ReadAsync(SetKey(setId, active.Payload), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing root."));
        var snapshots = new Dictionary<string, PackageSnapshot>(StringComparer.Ordinal);
        var unavailable = new List<PackageSetOptionalFailure>();
        try
        {
            foreach (var child in root.Children)
            {
                try { snapshots.Add(child.PackageId, await OpenSnapshotAsync(await SetChild(child, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)); }
                catch (UnauthorizedAccessException) when (child.Optional) { unavailable.Add(new(child.PackageId, child.Revision, "unauthorized")); }
                catch (FileNotFoundException) when (child.Optional) { unavailable.Add(new(child.PackageId, child.Revision, "incomplete")); }
            }
            return new(root.Revision, snapshots, unavailable.ToArray());
        }
        catch { foreach (var snapshot in snapshots.Values) await snapshot.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Verifies active and latest roots. Candidate missing bytes are counted once across shared child content.</summary>
    public async Task<OfflinePackageSetStatus> GetSetStatusAsync(string setId, CancellationToken cancellationToken = default)
    {
        await using var ownRoot = await _locks.AcquireAsync(Hash("package-set/" + setId), cancellationToken).ConfigureAwait(false);
        var active = await _store.ReadAsync(SetPrefix(setId) + "active", cancellationToken).ConfigureAwait(false);
        var latest = await _store.ReadAsync(SetPrefix(setId) + "latest", cancellationToken).ConfigureAwait(false);
        async Task<(PackageSetMissingItem[] Missing, PackageSetOptionalFailure[] Optional)> Verify(string? revision)
        {
            if (revision is null) return ([], []);
            var missing = new List<PackageSetMissingItem>();
            var optional = new List<PackageSetOptionalFailure>();
            var root = DecodeSet(await _store.ReadAsync(SetKey(setId, revision), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing root."));
            foreach (var child in root.Children)
            {
                OfflinePackageManifest manifest;
                try { manifest = await SetChild(child, cancellationToken).ConfigureAwait(false); }
                catch (UnauthorizedAccessException) when (child.Optional) { optional.Add(new(child.PackageId, child.Revision, "unauthorized")); continue; }
                var unavailable = (await Missing(manifest, cancellationToken).ConfigureAwait(false)).Required.ToHashSet(StringComparer.Ordinal);
                if (child.Optional)
                {
                    if (unavailable.Count != 0) optional.Add(new(child.PackageId, child.Revision, "incomplete"));
                    continue;
                }
                missing.AddRange(manifest.Items.Where(i => unavailable.Contains(i.Id)).Select(i => new PackageSetMissingItem(child.PackageId, i.Id, i.Sha256, i.Size)));
            }
            return (missing.ToArray(), optional.ToArray());
        }
        var candidate = await Verify(latest?.Payload ?? active?.Payload).ConfigureAwait(false);
        var activeMissing = active?.Payload == latest?.Payload ? candidate : await Verify(active?.Payload).ConfigureAwait(false);
        return new(setId, active?.Payload, latest?.Payload, candidate.Missing, candidate.Missing.DistinctBy(i => i.Sha256).Sum(i => i.Size), _time.GetUtcNow())
        { ActiveMissingRequired = activeMissing.Missing, OptionalFailures = candidate.Optional, ActiveOptionalFailures = activeMissing.Optional };
    }

    /// <summary>Releases a retired root's child references atomically. Child pins use existing explicit package retirement.</summary>
    public async Task RemoveRetiredSetAsync(string setId, string revision, CancellationToken cancellationToken = default)
    {
        await using var ownRoot = await _locks.AcquireAsync(Hash("package-set/" + setId), cancellationToken).ConfigureAwait(false);
        foreach (var pointer in new[] { "active", "latest" })
            if ((await _store.ReadAsync(SetPrefix(setId) + pointer, cancellationToken).ConfigureAwait(false))?.Payload == revision) throw new InvalidOperationException("Set remains active or latest.");
        var record = await _store.ReadAsync(SetKey(setId, revision), cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        var root = DecodeSet(record);
        var held = await OwnChildren(root, cancellationToken).ConfigureAwait(false);
        try
        {
            var mutations = new List<OrchestrationMutation> { new(record.Key, record.Revision, null) };
            foreach (var child in root.Children)
            {
                var reference = await _store.ReadAsync(SetReference(child, root), cancellationToken).ConfigureAwait(false);
                if (reference is not null) mutations.Add(new(reference.Key, reference.Revision, null));
            }
            if (!await _store.TryWriteAsync(mutations, cancellationToken: cancellationToken).ConfigureAwait(false)) throw new IOException("Package-set ownership changed.");
        }
        finally { await ReleaseChildren(held).ConfigureAwait(false); }
    }
}

/// <summary>A fixed package-set revision. Child streams retain existing verified managed-cache reader ownership.</summary>
public sealed class PackageSetSnapshot : IAsyncDisposable
{
    private readonly Dictionary<string, PackageSnapshot> _children;
    private bool _disposed;
    internal PackageSetSnapshot(string revision, Dictionary<string, PackageSnapshot> children, PackageSetOptionalFailure[] unavailable)
    { Revision = revision; _children = children; UnavailableOptionalChildren = Array.AsReadOnly(unavailable); }
    /// <summary>Optional children excluded when this snapshot was opened. Later repairs require opening a new snapshot.</summary>
    public IReadOnlyList<PackageSetOptionalFailure> UnavailableOptionalChildren { get; }
    /// <summary>Root revision captured when this snapshot was opened.</summary>
    public string Revision { get; }
    /// <summary>Opens an item through its exact child revision, verifying content on every open.</summary>
    public Task<Stream?> OpenAsync(string packageId, string itemId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (UnavailableOptionalChildren.Any(c => c.PackageId == packageId)) return Task.FromResult<Stream?>(null);
        return (_children.TryGetValue(packageId, out var child) ? child : throw new KeyNotFoundException("Unknown child package.")).OpenAsync(itemId, cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var child in _children.Values) await child.DisposeAsync().ConfigureAwait(false);
    }
}

[JsonSerializable(typeof(OfflinePackageSetManifest))]
internal partial class PackageSetJson : JsonSerializerContext;
