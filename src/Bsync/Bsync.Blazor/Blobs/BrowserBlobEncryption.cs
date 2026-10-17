using System.Security.Cryptography;
using Bsync.Blobs;
using Microsoft.JSInterop;

namespace Bsync.Blazor.Blobs;

/// <summary>Web Crypto AES-256-GCM for EncryptedBlobCache. Requires a secure browser context. Dispose after operations finish.</summary>
public sealed class BrowserBlobEncryption : BlobEncryption, IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private BrowserBlobEncryption(IJSObjectReference module) => _module = module;

    /// <summary>Loads the Web Crypto implementation without persisting or provisioning keys.</summary>
    public static async Task<BrowserBlobEncryption> OpenAsync(IJSRuntime js, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        return new(await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/Bsync.Blazor/bsync-blob-encryption.js").ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
        CallAsync("seal", key, plaintext, associatedData, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
        CallAsync("open", key, envelope, associatedData, cancellationToken);

    private async ValueTask<byte[]> CallAsync(string method, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> aad, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit key.", nameof(key));
        var keyCopy = key.ToArray(); var inputCopy = input.ToArray();
        try
        {
            // Join Web Crypto completion even after cancellation; never release resources while the JS operation is active.
            return await _module.InvokeAsync<byte[]>(method, keyCopy, inputCopy, aad.ToArray()).ConfigureAwait(false);
        }
        catch (JSException error) { throw new CryptographicException("Browser blob encryption/authentication failed.", error); }
        finally { CryptographicOperations.ZeroMemory(keyCopy); CryptographicOperations.ZeroMemory(inputCopy); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();
}
