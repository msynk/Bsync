using Bsync.Client;

namespace Bsync.Blobs;

/// <summary>One bounded reconciliation page; continue until Continuation is null, then periodically repeat the scan.</summary>
/// <param name="Examined">Logical records examined.</param>
/// <param name="AccountedBytes">Conservative payload bytes for this page, not a whole-cache total.</param>
/// <param name="Continuation">Opaque continuation for the next page, or null when both inventories are exhausted.</param>
public sealed record BlobReconciliationResult(int Examined, long AccountedBytes, string? Continuation);

public sealed partial class ManagedBlobCache
{
    /// <summary>
    /// Reconciles a bounded metadata/provider page under one cooperative deadline. Requires optional paged inventory;
    /// third-party providers can retain the legacy ReconcileAsync path. Hold the account lifetime around the scan.
    /// Unknown objects receive a conservative owner; unknown partials remain counted physically and are never deleted.
    /// A cursor is valid only for this cache and is not a snapshot. Repeat scans to revisit protected readers.
    /// </summary>
    public async Task<BlobReconciliationResult> ReconcilePageAsync(TimeSpan budget, string? continuation = null,
        int limit = 128, CancellationToken cancellationToken = default)
    {
        if (_inner is not IBlobCachePagedInventory inventory) throw new NotSupportedException("Provider lacks bounded completed-object inventory.");
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (continuation is not null && !(continuation.StartsWith("m:", StringComparison.Ordinal) || continuation.StartsWith("p:", StringComparison.Ordinal)))
            throw new ArgumentException("Invalid reconciliation continuation.", nameof(continuation));
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "cache_inventory");
        var token = deadline.Token;
        var provider = continuation?.StartsWith("p:", StringComparison.Ordinal) == true;
        var after = continuation is { Length: > 2 } ? continuation[2..] : null;
        IReadOnlyList<string> hashes = provider
            ? await inventory.ListCompletedPageAsync(after, limit, token).ConfigureAwait(false)
            : (await _store.ReadPageAsync(Prefix, after is null ? null : Key(after), limit, token).ConfigureAwait(false)).Select(r => r.Key[Prefix.Length..]).ToArray();
        long accounted = 0;
        foreach (var hash in hashes)
        {
            _ = deadline.Remaining;
            // Match import/repair lock order; never wait for content ownership while holding metadata.
            await using var content = await AcquireTransferAsync(hash, token).ConfigureAwait(false);
            await using var held = await _locks.AcquireAsync("metadata", token).ConfigureAwait(false);
            var recognized = await _store.ReadAsync(Key(hash), token).ConfigureAwait(false) is not null;
            var entry = await Read(hash, token).ConfigureAwait(false);
            var owners = new List<string>();
            foreach (var owner in entry.Owners)
            {
                if (owner.StartsWith("reader-v2/", StringComparison.Ordinal) || owner.StartsWith("snapshot-v2/", StringComparison.Ordinal))
                {
                    var name = owner[(owner.IndexOf('/') + 1)..];
                    if (name.Length == 64 && name.All(char.IsAsciiHexDigitLower))
                    {
                        await using var abandoned = await _locks.TryAcquireAsync("lifetime-" + name, token).ConfigureAwait(false);
                        if (abandoned is not null) continue;
                    }
                }
                owners.Add(owner);
            }
            var bytes = checked((await _inner.SizeAsync(hash, token).ConfigureAwait(false) ?? 0) + await _inner.PartialLengthAsync(hash, token).ConfigureAwait(false));
            if (owners.Any(o => o.StartsWith("reader-v2/", StringComparison.Ordinal))) bytes = Math.Max(bytes, entry.Bytes);
            await Save(hash, entry with { Bytes = bytes, Owners = recognized ? owners.ToArray() : ["recovered/unknown"] }, token).ConfigureAwait(false);
            accounted = checked(accounted + bytes);
        }
        _ = deadline.Remaining;
        var next = hashes.Count == limit ? (provider ? "p:" : "m:") + hashes[^1] : provider ? null : "p:";
        return new(hashes.Count, accounted, next);
    }
}
