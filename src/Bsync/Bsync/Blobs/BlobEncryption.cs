using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bsync.Blobs;

/// <summary>
/// Platform AES-256-GCM boundary. Format is a fresh 12-byte random nonce, ciphertext, then a 16-byte tag.
/// Implementations authenticate all associated data and return no plaintext on failure. Keys are exactly 32 bytes.
/// </summary>
public abstract class BlobEncryption
{
    /// <summary>Encrypts with a new cryptographically random nonce on every invocation, including retries.</summary>
    public abstract ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default);
    /// <summary>Authenticates the complete envelope before returning plaintext. Authentication failure throws CryptographicException.</summary>
    public abstract ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default);
}

/// <summary>Native platform AES-GCM. Browser hosts use BrowserBlobEncryption from Bsync.Blazor.</summary>
[UnsupportedOSPlatform("browser")]
public sealed class PlatformBlobEncryption : BlobEncryption
{
    /// <inheritdoc />
    public override ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit key.", nameof(key));
        var result = new byte[checked(plaintext.Length + 28)];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key.Span, 16);
        aes.Encrypt(result.AsSpan(0, 12), plaintext.Span, result.AsSpan(12, plaintext.Length), result.AsSpan(result.Length - 16), associatedData.Span);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit key.", nameof(key));
        if (envelope.Length < 28) throw new CryptographicException("Truncated blob envelope.");
        var result = new byte[envelope.Length - 28];
        try
        {
            using var aes = new AesGcm(key.Span, 16);
            aes.Decrypt(envelope.Span[..12], envelope.Span.Slice(12, result.Length), envelope.Span[^16..], result, associatedData.Span);
            return ValueTask.FromResult(result);
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }
}
