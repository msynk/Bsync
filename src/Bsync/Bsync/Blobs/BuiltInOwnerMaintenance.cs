using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>One exclusive-account cache owner reconciliation page. Unknown owners and uncertain records are preserved.</summary>
/// <param name="Progress">Record-count and conservative cache-byte charge for the maintenance runner.</param>
/// <param name="ReleasedOwners">Proven abandoned pins removed, or removable during dry run.</param>
/// <param name="RetainedOwners">Pins still required or conservatively preserved.</param>
/// <param name="UnknownOwners">Subset retained because the owner or supporting record cannot be interpreted safely.</param>
/// <param name="Conflicts">Records changed by a nonparticipating writer and left unchanged.</param>
public sealed record BlobBuiltInMaintenanceResult(MaintenancePageResult Progress, int ReleasedOwners, int RetainedOwners, int UnknownOwners, int Conflicts);

public sealed partial class ManagedBlobCache
{
    /// <summary>
    /// Reconciles at most 16 cache records (at most 1000 owners each). Requires the exact cache account's exclusive
    /// scope and shared physical lock namespace. Bytes charge each record's conservative reservation once, including
    /// protected bytes; no blob content is read. An oversized next record is left untouched and requests a larger budget.
    /// Package/transfer pins consult durable records; missing records release only under exclusive account ownership.
    /// Unresolved repair journals and unknown/custom owners remain protected. Reader/snapshot/rotation lifetimes require
    /// a successful physical probe, never elapsed time. Reconcile durable package/transfer pins before this scan.
    /// </summary>
    public async Task<BlobBuiltInMaintenanceResult> ReconcileBuiltInOwnersAsync(AccountWorkScope scope, MaintenancePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.RequireMaintenance(); MaintenanceOwnerSupport.Validate(request);
        var after = request.Continuation;
        if (after is not null) _ = Key(after);
        int examined = 0, released = 0, retained = 0, unknown = 0, conflicts = 0; long bytes = 0;
        BlobBuiltInMaintenanceResult Result(string? next, bool larger = false) => new(new(examined, bytes, next, larger), released, retained, unknown, conflicts);
        while (examined < Math.Min(request.MaximumItems, 16))
        {
            cancellationToken.ThrowIfCancellationRequested(); scope.RequireMaintenance();
            var records = await _store.ReadPageAsync(Prefix, after is null ? null : Key(after), 1, cancellationToken).ConfigureAwait(false);
            if (records.Count == 0) return Result(null);
            var record = records[0]; var hash = record.Key[Prefix.Length..]; _ = Key(hash);
            var entry = Decode(record);
            if (entry.Bytes < 0 || entry.Owners is null || entry.Owners.Length > 1000 || entry.Owners.Any(o => string.IsNullOrEmpty(o) || o.Length > 256))
                throw new InvalidDataException("Invalid cache owner inventory; preserved.");
            if (entry.Bytes > request.MaximumBytes - bytes) return Result(after, true);
            var owners = new List<string>(); var removable = 0; var uncertain = 0;
            foreach (var owner in entry.Owners)
            {
                var resolution = await ResolveBuiltInOwner(hash, owner, cancellationToken).ConfigureAwait(false);
                if (resolution == BlobOwnerResolution.Released) removable++;
                else { owners.Add(owner); if (resolution == BlobOwnerResolution.Unknown) uncertain++; }
            }
            cancellationToken.ThrowIfCancellationRequested(); scope.RequireMaintenance();
            var applied = request.DryRun || removable == 0;
            if (!request.DryRun && removable > 0)
            {
                await using var held = await _locks.AcquireAsync("metadata", cancellationToken).ConfigureAwait(false);
                applied = await _store.TryWriteAsync([new(record.Key, record.Revision, JsonSerializer.Serialize(entry with { Owners = owners.ToArray() }, CacheJson.Default.BlobCacheEntry))], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            if (applied) { released += removable; retained += owners.Count; }
            else { conflicts++; retained += entry.Owners.Length; }
            unknown += uncertain; examined++; bytes += entry.Bytes; after = hash;
        }
        return Result(after);
    }

    private async Task<BlobOwnerResolution> ResolveBuiltInOwner(string hash, string owner, CancellationToken ct)
    {
        if (owner.StartsWith("reader-v2/", StringComparison.Ordinal) || owner.StartsWith("snapshot-v2/", StringComparison.Ordinal))
        {
            var id = owner[(owner.IndexOf('/') + 1)..];
            if (!MaintenanceOwnerSupport.Hash(id)) return BlobOwnerResolution.Unknown;
            await using var abandoned = await _locks.TryAcquireAsync("lifetime-" + id, ct).ConfigureAwait(false);
            return abandoned is null ? BlobOwnerResolution.Live : BlobOwnerResolution.Released;
        }
        try
        {
            var parts = owner.Split('/');
            if (parts is ["package", var package, var revision] && MaintenanceOwnerSupport.Hash(package) && MaintenanceOwnerSupport.Hash(revision))
            {
                var record = await _store.ReadAsync("packages/" + package + "/" + revision, ct).ConfigureAwait(false);
                if (record is null)
                {
                    foreach (var pointer in new[] { "active", "latest" })
                    {
                        var current = await _store.ReadAsync("packages/" + package + "/" + pointer, ct).ConfigureAwait(false);
                        if (current is not null && (string.IsNullOrWhiteSpace(current.Payload) || current.Payload.Length > 256 || MaintenanceOwnerSupport.Digest(current.Payload) == revision))
                            return BlobOwnerResolution.Unknown;
                    }
                    return BlobOwnerResolution.Released;
                }
                var manifest = JsonSerializer.Deserialize(record.Payload, PackageJson.Default.OfflinePackageManifest);
                if (manifest is null) return BlobOwnerResolution.Unknown;
                OfflinePackageManager.Validate(manifest);
                if (MaintenanceOwnerSupport.Digest(manifest.PackageId) != package || MaintenanceOwnerSupport.Digest(manifest.Revision) != revision)
                    return BlobOwnerResolution.Unknown;
                return manifest.Items.Any(i => i.Sha256 == hash) ? BlobOwnerResolution.Live : BlobOwnerResolution.Unknown;
            }
            if (parts is ["transfers", var authority, var direction, var content] && MaintenanceOwnerSupport.Hash(authority) && content == hash && direction is "up" or "down")
            {
                var record = await _store.ReadAsync(owner, ct).ConfigureAwait(false);
                if (record is not null)
                {
                    var job = JsonSerializer.Deserialize(record.Payload, TransferJson.Default.BlobTransferJob);
                    return job is not null && job.SchemaVersion == 1 && job.Sha256 == hash && job.Size >= 0 && Enum.IsDefined(job.State)
                        && job.Direction == (direction == "up" ? BlobTransferDirection.Upload : BlobTransferDirection.Download)
                        ? BlobOwnerResolution.Live : BlobOwnerResolution.Unknown;
                }
                var cleanup = await _store.ReadAsync("transfer-cleanup/v1/" + owner, ct).ConfigureAwait(false);
                if (cleanup is not null && cleanup.Payload != "1") return BlobOwnerResolution.Unknown;
                var repairRecord = await _store.ReadAsync("repair/v1/" + owner["transfers/".Length..], ct).ConfigureAwait(false);
                if (repairRecord is null) return BlobOwnerResolution.Released;
                var repair = JsonSerializer.Deserialize(repairRecord.Payload, RepairJson.Default.BlobRepairJob);
                return repair is { SchemaVersion: 1, State: BlobRepairState.Completed } && repair.Sha256 == hash
                    ? BlobOwnerResolution.Released : BlobOwnerResolution.Unknown;
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or LocalStoreUnavailableException or OverflowException) { return BlobOwnerResolution.Unknown; }
        return BlobOwnerResolution.Unknown;
    }
}

public sealed partial class OfflinePackageManager
{
    /// <summary>
    /// Restores missing durable package pins after interrupted staging, without network or content verification.
    /// Requires this manager's exact account under exclusive maintenance ownership. Scans at most 16 metadata records;
    /// Bytes charge the sum of manifest item sizes (including repeated hashes), and Items count metadata records.
    /// Unknown manifest schemas fail closed. Dry run reports the same work/continuation without changing pins.
    /// </summary>
    public async Task<MaintenancePageResult> ReconcilePinsAsync(AccountWorkScope scope, MaintenancePageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.RequireMaintenance(); MaintenanceOwnerSupport.Validate(request);
        const string prefix = "packages/";
        if (request.Continuation is not null && !request.Continuation.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Foreign package cursor.", nameof(request));
        var after = request.Continuation; var examined = 0; long bytes = 0;
        while (examined < Math.Min(request.MaximumItems, 16))
        {
            cancellationToken.ThrowIfCancellationRequested(); scope.RequireMaintenance();
            var page = await _store.ReadPageAsync(prefix, after, 1, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) return new(examined, bytes, null);
            var record = page[0];
            if (record.Key.Split('/') is ["packages", var package, var revision] && MaintenanceOwnerSupport.Hash(package) && MaintenanceOwnerSupport.Hash(revision))
            {
                var manifest = Decode(record);
                if (record.Key != RevisionKey(manifest.PackageId, manifest.Revision)) throw new InvalidDataException("Package inventory identity mismatch.");
                var size = checked(manifest.Items.Sum(i => i.Size));
                if (size > request.MaximumBytes - bytes) return new(examined, bytes, after, true);
                if (!request.DryRun)
                    foreach (var item in manifest.Items) await _cache.PinAsync(item.Sha256, Owner(manifest), cancellationToken).ConfigureAwait(false);
                bytes += size;
            }
            after = record.Key; examined++;
        }
        return new(examined, bytes, after);
    }
}

public sealed partial class BlobTransferManager
{
    /// <summary>
    /// Restores pins for durable jobs after interruption or a missing pin, without downloading or resuming terminal jobs.
    /// Requires this manager's account under exclusive maintenance ownership. At most 16 job records per page; each job's
    /// full expected size is charged once, even if bytes are missing. Terminal pins remain until explicit ReleaseTerminalAsync.
    /// </summary>
    public async Task<MaintenancePageResult> ReconcilePinsAsync(AccountWorkScope scope, MaintenancePageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.RequireMaintenance(); MaintenanceOwnerSupport.Validate(request);
        if (_cache is not ManagedBlobCache managed) throw new NotSupportedException("Transfer pin reconciliation requires managed content.");
        if (request.Continuation is not null && !request.Continuation.StartsWith(_prefix, StringComparison.Ordinal)) throw new ArgumentException("Foreign transfer cursor.", nameof(request));
        var after = request.Continuation; var examined = 0; long bytes = 0;
        while (examined < Math.Min(request.MaximumItems, 16))
        {
            cancellationToken.ThrowIfCancellationRequested(); scope.RequireMaintenance();
            var page = await _store.ReadPageAsync(_prefix, after, 1, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) return new(examined, bytes, null);
            var record = page[0]; var job = Decode(record);
            if (job.Size < 0 || !Enum.IsDefined(job.State) || record.Key != Key(job.Sha256, job.Direction)) throw new InvalidDataException("Transfer inventory identity mismatch.");
            if (job.Size > request.MaximumBytes - bytes) return new(examined, bytes, after, true);
            if (!request.DryRun) await managed.PinAsync(job.Sha256, record.Key, cancellationToken).ConfigureAwait(false);
            bytes += job.Size; examined++; after = record.Key;
        }
        return new(examined, bytes, after);
    }
}

internal static class MaintenanceOwnerSupport
{
    internal static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    internal static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void Validate(MaintenancePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumBytes, 1);
    }
}
