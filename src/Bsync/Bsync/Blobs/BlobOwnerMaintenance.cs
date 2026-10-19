using System.Text.Json;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>Host reconciliation of an immutable application pin identity; uncertainty always preserves content.</summary>
public enum BlobOwnerResolution
{
    /// <summary>The owner still needs the bytes.</summary>
    Live,
    /// <summary>The durable owner is permanently retired. Its identity must never be reused or pinned again.</summary>
    Released,
    /// <summary>The host cannot recognize the owner.</summary>
    Unknown,
    /// <summary>The authoritative owner registry is temporarily inaccessible.</summary>
    Unavailable,
}

/// <summary>Bounded host lookup. Released is permanent, backed by durable retirement; time/lease expiry alone is insufficient.</summary>
public interface IBlobOwnerResolver
{
    /// <summary>Resolves an app/ pin without holding cache metadata locks. Never return Released for a reusable identity.</summary>
    ValueTask<BlobOwnerResolution> ResolveAsync(string sha256, string owner, CancellationToken cancellationToken = default);
}

/// <summary>One bounded application-owner reconciliation page. Bytes are accounted payload bytes, not filesystem overhead.</summary>
/// <param name="ExaminedOwners">Application owners queried.</param>
/// <param name="ReleasedOwners">Proven retired owners removed (or removable in dry-run mode).</param>
/// <param name="UnknownOwners">Unrecognized owners retained.</param>
/// <param name="UnavailableOwners">Temporarily inaccessible owners retained.</param>
/// <param name="ProtectedBytes">Accounted bytes retained by at least one owner in the scanned page.</param>
/// <param name="Continuation">Last scanned hash, or null at the end.</param>
public sealed record BlobOwnerMaintenanceResult(int ExaminedOwners, int ReleasedOwners, int UnknownOwners, int UnavailableOwners, long ProtectedBytes, string? Continuation);

public sealed partial class ManagedBlobCache
{
    private static readonly System.Diagnostics.Metrics.Histogram<long> ProtectedPageBytes = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateHistogram<long>(
        "bsync.cache.protected_page_bytes", "By", "Accounted protected bytes in one bounded owner maintenance page; repeated samples must not be summed as cache size.");
    /// <summary>
    /// Reconciles app/ pins in at most 100 records under one cooperative budget. Other/legacy/built-in owner kinds are
    /// preserved for their own lifecycle protocols. The caller must hold account lifetime ownership. Callbacks run
    /// outside metadata locks; a changed record is conservatively revisited on a later scan. Dry run mutates nothing.
    /// A timeout throws cancellation; previously committed removals are idempotent and the page can be replayed.
    /// </summary>
    public async Task<BlobOwnerMaintenanceResult> MaintainOwnersAsync(IBlobOwnerResolver resolver, TimeSpan budget,
        string? afterHash = null, int recordLimit = 16, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordLimit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(recordLimit, 100);
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "cache_owner_maintenance");
        var token = deadline.Token;
        var records = await _store.ReadPageAsync(Prefix, afterHash is null ? null : Key(afterHash), recordLimit, token).ConfigureAwait(false);
        var examined = 0; var released = 0; var unknown = 0; var unavailable = 0; long protectedBytes = 0;
        foreach (var record in records)
        {
            var hash = record.Key[Prefix.Length..]; var entry = Decode(record);
            var owners = new List<string>(); var retired = 0;
            foreach (var owner in entry.Owners)
            {
                if (!owner.StartsWith("app/", StringComparison.Ordinal)) { owners.Add(owner); continue; }
                _ = deadline.Remaining;
                var resolution = await resolver.ResolveAsync(hash, owner, token).ConfigureAwait(false);
                _ = deadline.Remaining; examined++;
                if (!Enum.IsDefined(resolution)) throw new InvalidDataException("Unknown owner resolution.");
                if (resolution == BlobOwnerResolution.Released) { retired++; continue; }
                owners.Add(owner);
                if (resolution == BlobOwnerResolution.Unknown) unknown++;
                if (resolution == BlobOwnerResolution.Unavailable) unavailable++;
            }
            var applied = dryRun;
            if (!dryRun && retired > 0)
            {
                await using var metadata = await _locks.AcquireAsync("metadata", token).ConfigureAwait(false);
                applied = await _store.TryWriteAsync([new(record.Key, record.Revision,
                    JsonSerializer.Serialize(entry with { Owners = owners.ToArray() }, CacheJson.Default.BlobCacheEntry))], cancellationToken: token).ConfigureAwait(false);
            }
            if (applied) released += retired;
            if ((applied ? owners.Count : entry.Owners.Length) > 0) protectedBytes = checked(protectedBytes + entry.Bytes);
        }
        _ = deadline.Remaining;
        ProtectedPageBytes.Record(protectedBytes, new KeyValuePair<string, object?>("bsync.dry_run", dryRun));
        return new(examined, released, unknown, unavailable, protectedBytes, records.Count == recordLimit ? records[^1].Key[Prefix.Length..] : null);
    }
}
