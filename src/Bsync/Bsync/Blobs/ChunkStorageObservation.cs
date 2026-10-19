using System.Globalization;

namespace Bsync.Blobs;

/// <summary>A bounded journal observation, without decrypting content. These are reserved bytes, not a physical snapshot.</summary>
/// <param name="ExaminedChunks">Chunk journal records examined.</param>
/// <param name="ReservedBytes">Ciphertext bytes reserved by these records, including writes interrupted before storage.</param>
/// <param name="QuarantinedBytes">Subset reserved by generations protected by explicit recovery quarantine.</param>
/// <param name="Continuation">Last scanned chunk journal key, or null at end. Concurrent changes may require another scan.</param>
public sealed record ChunkStorageObservation(int ExaminedChunks, long ReservedBytes, long QuarantinedBytes, string? Continuation);

public sealed partial class ChunkedEncryptedBlobCache
{
    private static readonly System.Diagnostics.Metrics.Histogram<long> QuarantinePageBytes = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateHistogram<long>(
        "bsync.blob.quarantined_page_bytes", "By", "Reserved ciphertext bytes protected by quarantine in one bounded observation page; repeated samples must not be summed as cache size.");

    /// <summary>
    /// Reads at most limit chunk journal records and one protection marker per generation encountered. Works with locked
    /// keys and damaged ciphertext; never reads content or modifies storage. Rejects malformed inventory rather than
    /// underreporting it. The caller must hold account lifetime ownership. Page observations are not an atomic total;
    /// do not use them to authorize deletion or key retirement. Physical bytes remain available through GetStoredBytesAsync.
    /// </summary>
    public async Task<ChunkStorageObservation> ObserveStoragePageAsync(string? continuation = null, int limit = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (continuation is not null) _ = InventoryGeneration(continuation);
        var page = await _store.ReadPageAsync(Chunks, continuation, limit, cancellationToken).ConfigureAwait(false);
        long reserved = 0, quarantined = 0;
        string? previousGeneration = null; var protectedGeneration = false;
        foreach (var record in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = InventoryGeneration(record.Key);
            if (!int.TryParse(record.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size is < 28 or > 1048604)
                throw new InvalidDataException("Invalid chunk inventory; data retained.");
            if (generation != previousGeneration)
            {
                protectedGeneration = await _store.ReadAsync("encrypted-protected/v2/" + generation, cancellationToken).ConfigureAwait(false) is not null;
                previousGeneration = generation;
            }
            reserved = checked(reserved + size);
            if (protectedGeneration) quarantined = checked(quarantined + size);
        }
        cancellationToken.ThrowIfCancellationRequested();
        QuarantinePageBytes.Record(quarantined);
        return new(page.Count, reserved, quarantined, page.Count == limit ? page[^1].Key : null);
    }

    internal static string InventoryGeneration(string key)
    {
        if (!key.StartsWith(Chunks, StringComparison.Ordinal) || key.Length != Chunks.Length + 32 + 1 + 64 ||
            key[Chunks.Length + 32] != '/') throw new InvalidDataException("Invalid chunk inventory identity.");
        var generation = key.Substring(Chunks.Length, 32);
        if (!generation.All(char.IsAsciiHexDigitLower)) throw new InvalidDataException("Invalid chunk generation identity.");
        _ = Key(key[(Chunks.Length + 33)..]);
        return generation;
    }
}
