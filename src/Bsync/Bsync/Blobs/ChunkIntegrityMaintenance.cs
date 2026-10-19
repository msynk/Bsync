using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>One retained generation's key obligation. A null key means legacy/unknown: retain all potentially used keys.</summary>
/// <param name="Generation">Opaque immutable generation.</param>
/// <param name="Sha256">Logical plaintext identity.</param>
/// <param name="Key">Known key reference, or null when the generation predates the retention journal.</param>
public sealed record ChunkKeyRetention(string Generation, string Sha256, LocalKeyReference? Key);

/// <summary>A bounded observation, not permission to delete keys. Scan under exclusive account ownership; include v1 and other stores.</summary>
/// <param name="Generations">Obligations including partial, retired, reader-held and quarantined generations.</param>
/// <param name="Continuation">Last generation key, or null at end. Concurrent mutation requires a fresh scan.</param>
public sealed record ChunkKeyRetentionPage(IReadOnlyList<ChunkKeyRetention> Generations, string? Continuation);

/// <summary>Authenticated chunk scrub state; this does not recompute the full plaintext SHA-256.</summary>
public enum ChunkScrubStatus
{
    /// <summary>The complete object is absent.</summary>
    Missing,
    /// <summary>The invocation expired or could not fit the next chunk; persisted progress is retained.</summary>
    Deferred,
    /// <summary>All chunks in the sealed manifest have authenticated over this scan's successive invocations.</summary>
    Complete,
}

/// <summary>Durable authentication progress tied to one sealed generation. It is not a simultaneous snapshot of storage health.</summary>
/// <param name="Status">Completion or deferral state.</param>
/// <param name="Generation">Observed generation, or null when no object was observed.</param>
/// <param name="VerifiedChunks">Durably authenticated prefix length.</param>
/// <param name="TotalChunks">Chunks in the observed manifest.</param>
/// <param name="VerifiedBytes">Plaintext bytes in that prefix.</param>
/// <param name="TotalBytes">Plaintext size in the observed manifest.</param>
public sealed record ChunkScrubResult(ChunkScrubStatus Status, string? Generation, int VerifiedChunks, int TotalChunks, long VerifiedBytes, long TotalBytes);

public sealed partial class ChunkedEncryptedBlobCache
{
    private const string GenerationKeys = "encrypted-generation-key/v1/";
    private const string Scrubs = "encrypted-scrub/v1/";

    /// <summary>
    /// Reads at most limit generation records and their key journals without unlocking keys or reading ciphertext.
    /// Obligations disappear only after physical collection and reader release. Legacy generations without a journal
    /// conservatively report an unknown key. The caller owns key deletion policy and must quiesce all writers/readers,
    /// scan from the beginning to completion, and account for v1, protected metadata, backups and other key users.
    /// </summary>
    public async Task<ChunkKeyRetentionPage> GetKeyRetentionPageAsync(string? continuation = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Page(Generations, continuation, limit);
        if (continuation is not null && !continuation.StartsWith(Generations, StringComparison.Ordinal))
            throw new ArgumentException("Foreign generation continuation.", nameof(continuation));
        var page = await _store.ReadPageAsync(Generations, continuation, limit, cancellationToken).ConfigureAwait(false);
        var result = new List<ChunkKeyRetention>(page.Count);
        foreach (var generation in page)
        {
            var id = generation.Key[Generations.Length..];
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid generation identity.");
            _ = Key(generation.Payload);
            var record = await _store.ReadAsync(GenerationKeys + id, cancellationToken).ConfigureAwait(false);
            LocalKeyReference? reference = null;
            if (record is not null)
            {
                if (record.Payload.Length > 4096) throw new InvalidDataException("Invalid key retention journal bounds.");
                var key = JsonSerializer.Deserialize(record.Payload, ChunkJson.Default.ChunkGenerationKey);
                if (key is null || key.Version != 1) throw new InvalidDataException("Unsupported key retention journal.");
                reference = new(_writeKey.Account, key.KeyId, key.KeyVersion);
            }
            result.Add(new(id, generation.Payload, reference));
        }
        return new(result, page.Count == limit ? page[^1].Key : null);
    }

    /// <summary>
    /// Authenticates a bounded prefix of remaining chunks and durably checkpoints each chunk under its generation key.
    /// Resumes after restart; replacement/rotation starts a new generation. restart discards this generation's previous
    /// progress before reading chunks. The byte allowance covers plaintext chunk reads, not manifest/checkpoint I/O.
    /// A chunk never starts unless it fits both limits. Keys, authentication and storage failures propagate without
    /// authorizing repair. Caller cancellation throws; cooperative expiry returns Deferred with last committed progress.
    /// Full plaintext identity was checked at publication; this scrub authenticates chunks against that sealed manifest.
    /// Use BlobVerification for a fresh full SHA-256. No portable digest state or instant whole-object health is claimed.
    /// </summary>
    public async Task<ChunkScrubResult> ScrubAsync(string sha256, TimeSpan budget, long maximumBytes, int maximumChunks = 128,
        bool restart = false, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        _ = Key(sha256);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChunks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumChunks, 1024);
        using var deadline = new CooperativeDeadline(budget, timeProvider ?? TimeProvider.System, cancellationToken, "chunk_scrub");
        var result = new ChunkScrubResult(ChunkScrubStatus.Deferred, null, 0, 0, 0, 0);
        try
        {
            var completed = await Run().ConfigureAwait(false);
            _ = deadline.Remaining; // Include joined operation-lock disposal in the original budget.
            return completed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.Expired) { return result; }

        async Task<ChunkScrubResult> Run()
        {
            await using var held = await Own(sha256, deadline.Token).ConfigureAwait(false);
            _ = deadline.Remaining;
            var state = Decode(await _store.ReadAsync(Key(sha256), deadline.Token).ConfigureAwait(false));
            if (state.Complete is not { } envelope) { _ = deadline.Remaining; return result with { Status = ChunkScrubStatus.Missing }; }
            var manifest = await OpenManifest(sha256, envelope, deadline.Token).ConfigureAwait(false);
            var stamp = Hash(Encoding.UTF8.GetBytes(envelope.Ciphertext));
            var key = Scrubs + manifest.Generation;
            var record = await _store.ReadAsync(key, deadline.Token).ConfigureAwait(false);
            var next = 0;
            if (restart && record is not null)
            {
                if (!await _store.TryWriteAsync([new(key, record.Revision, null)], cancellationToken: deadline.Token).ConfigureAwait(false))
                    throw new IOException("Scrub progress changed outside physical ownership.");
                record = null;
            }
            if (record is not null)
            {
                if (record.Payload.Length > 4096) throw new InvalidDataException("Invalid scrub checkpoint bounds.");
                using var access = await Access(Reference(envelope), deadline.Token).ConfigureAwait(false);
                var plaintext = await _encryption.OpenAsync(access.Key, Convert.FromBase64String(record.Payload),
                    Aad("scrub/1", sha256, manifest.Generation, Reference(envelope)), deadline.Token).ConfigureAwait(false);
                try
                {
                    var progress = JsonSerializer.Deserialize(plaintext, ChunkJson.Default.ChunkScrubCheckpoint);
                    if (progress is null || progress.Version != 1 || progress.Manifest != stamp || progress.Next < 0 || progress.Next > manifest.Chunks.Length)
                        throw new InvalidDataException("Invalid scrub checkpoint.");
                    next = progress.Next;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            result = new(ChunkScrubStatus.Deferred, manifest.Generation, next, manifest.Chunks.Length,
                manifest.Chunks.Take(next).Sum(c => (long)c.Length), manifest.Length);
            long readBytes = 0; var readChunks = 0;
            while (next < manifest.Chunks.Length && readChunks < maximumChunks && manifest.Chunks[next].Length <= maximumBytes - readBytes)
            {
                _ = deadline.Remaining;
                var bytes = await ReadChunk(manifest, envelope, next, deadline.Token).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(bytes);
                _ = deadline.Remaining;
                using var access = await Access(Reference(envelope), deadline.Token).ConfigureAwait(false);
                var progress = JsonSerializer.SerializeToUtf8Bytes(new ChunkScrubCheckpoint(1, stamp, next + 1), ChunkJson.Default.ChunkScrubCheckpoint);
                byte[] encrypted;
                try { encrypted = await _encryption.SealAsync(access.Key, progress, Aad("scrub/1", sha256, manifest.Generation, Reference(envelope)), deadline.Token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(progress); }
                _ = deadline.Remaining;
                if (!await _store.TryWriteAsync([new(key, record?.Revision, Convert.ToBase64String(encrypted))], cancellationToken: deadline.Token).ConfigureAwait(false))
                    throw new IOException("Scrub progress changed outside physical ownership.");
                readBytes += manifest.Chunks[next].Length; readChunks++; next++;
                result = result with { VerifiedChunks = next, VerifiedBytes = result.VerifiedBytes + manifest.Chunks[next - 1].Length };
                record = await _store.ReadAsync(key, deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException("Scrub checkpoint disappeared outside physical ownership.");
            }
            _ = deadline.Remaining;
            return next == manifest.Chunks.Length ? result with { Status = ChunkScrubStatus.Complete } : result;
        }
    }
}

internal sealed record ChunkGenerationKey(int Version, string KeyId, int KeyVersion);
internal sealed record ChunkScrubCheckpoint(int Version, string Manifest, int Next);
