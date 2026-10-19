using System.Buffers;
using System.Security.Cryptography;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>Optional provider contract for verified replacement through the ordinary partial-content operations.</summary>
/// <remarks>
/// Callers must serialize writers for the content identity. Appends must leave the existing object intact;
/// completion must verify the full hash before atomically replacing it, preserving already open readers.
/// Failed verification must retain the previous object. Providers without this capability are never automatically replaced.
/// </remarks>
public interface IBlobCacheReplacement : IBlobCache
{
    /// <summary>Whether this configured cache can safely replace existing content with a verified partial.</summary>
    bool SupportsVerifiedReplacement { get; }
}

/// <summary>Content verification outcome; only missing content and invalid plaintext identity are repair candidates.</summary>
public enum BlobVerificationResult
{
    /// <summary>Complete plaintext hash and length match.</summary>
    Verified,
    /// <summary>No complete logical object exists.</summary>
    Missing,
    /// <summary>The readable plaintext has an unexpected length or hash.</summary>
    InvalidContent,
    /// <summary>The host must unlock the existing key.</summary>
    KeyLocked,
    /// <summary>The existing key is unavailable.</summary>
    KeyUnavailable,
    /// <summary>Cryptographic verification failed; this does not distinguish wrong keys from damage.</summary>
    AuthenticationFailed,
    /// <summary>The stored format is not supported by this reader.</summary>
    UnsupportedFormat,
    /// <summary>Storage could not be read reliably; no destructive recovery is authorized.</summary>
    IoFailure,
    /// <summary>The declared content exceeds this invocation's byte allowance. Storage was not opened.</summary>
    ContentLimitExceeded,
}

/// <summary>Bounded-buffer full-content verification without changing the cache or interpreting I/O errors as corruption.</summary>
public static class BlobVerification
{
    /// <summary>
    /// Verifies only if the declared plaintext size fits maximumContentBytes, under one cooperative deadline including
    /// opening and joined disposal. Reads at most size bytes plus a one-byte EOF probe, with a 64 KiB working buffer.
    /// Provider-internal reads/allocations are outside this allowance. Expiry and caller cancellation throw; no partial
    /// hash is persisted, so retry requires a budget sufficient for a complete pass. ContentLimitExceeded does no I/O.
    /// </summary>
    public static async Task<BlobVerificationResult> VerifyAsync(IBlobCache cache, string sha256, long size, TimeSpan budget,
        long maximumContentBytes, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        Validate(cache, sha256, size);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumContentBytes);
        using var deadline = new CooperativeDeadline(budget, timeProvider ?? TimeProvider.System, cancellationToken, "blob_verification");
        _ = deadline.Remaining;
        if (size > maximumContentBytes) return BlobVerificationResult.ContentLimitExceeded;
        var result = await VerifyAsync(cache, sha256, size, deadline.Token).ConfigureAwait(false);
        _ = deadline.Remaining;
        return result;
    }

    /// <summary>Checks the exact plaintext identity. Caller cancellation propagates; keys and unreadable bytes are preserved.</summary>
    public static async Task<BlobVerificationResult> VerifyAsync(IBlobCache cache, string sha256, long size, CancellationToken cancellationToken = default)
    {
        Validate(cache, sha256, size);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = await cache.OpenReadAsync(sha256, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream is null) return BlobVerificationResult.Missing;
            if (stream.Length != size) return BlobVerificationResult.InvalidContent;
            using var hash = new StreamingSha256();
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                var remaining = size;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, 65536);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (read < 0 || read > count) throw new IOException("Invalid content stream read count.");
                    if (read == 0) return BlobVerificationResult.InvalidContent;
                    hash.AppendData(buffer, 0, read); remaining -= read;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var extra = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return extra == 0 && Convert.ToHexStringLower(hash.GetHashAndReset()) == sha256
                    ? BlobVerificationResult.Verified : BlobVerificationResult.InvalidContent;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (ReplicaKeyUnavailableException error) { return error.Locked ? BlobVerificationResult.KeyLocked : BlobVerificationResult.KeyUnavailable; }
        catch (CryptographicException) { return BlobVerificationResult.AuthenticationFailed; }
        catch (LocalStoreUnavailableException error) when (error.Reason == "outdated") { return BlobVerificationResult.UnsupportedFormat; }
        catch (LocalStoreUnavailableException) { return BlobVerificationResult.IoFailure; }
        catch (IOException) { return BlobVerificationResult.IoFailure; }
    }

    private static void Validate(IBlobCache cache, string sha256, long size)
    {
        ArgumentNullException.ThrowIfNull(cache);
        FileBlobCacheLock.ValidateName(sha256);
        if (sha256.Length != 64) throw new ArgumentException("Expected a content hash.", nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(size);
    }
}
