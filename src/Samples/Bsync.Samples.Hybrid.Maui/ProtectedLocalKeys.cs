using System.Security.Cryptography;
using System.Text;
using Bsync.Client;
using Microsoft.Maui.Storage;

namespace Bsync.Samples.Hybrid.Maui;

/// <summary>
/// Read-only protected-store integration. Provision keys explicitly in the foreground under the application's
/// exclusive account-setup lock; never create a new key while reopening an existing encrypted resource.
/// SecureStorage does not expose a portable locked-state reason, so inaccessible keys conservatively report unavailable.
/// </summary>
public sealed class ProtectedLocalKeys(ISecureStorage storage) : ILocalKeyProvider
{
    /// <summary>Stable protected-store name. The resource's unencrypted header/catalog retains the same key ID/version.</summary>
    public static string StorageName(LocalKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return "bsync.local-key." + reference.Account.StorageKey + "." +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference.KeyId))) + "." +
            reference.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default)
    {
        var name = StorageName(reference);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? key = null;
        try
        {
            var encoded = await storage.GetAsync(name).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (encoded is null) return LocalKeyAccess.Unavailable(reference);
            key = Convert.FromBase64String(encoded);
            return key.Length == 32 ? LocalKeyAccess.Available(reference, key) : LocalKeyAccess.Unavailable(reference);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Includes unsupported storage, locked devices and invalid protected-store values. No destructive repair.
            // Do not include exception strings in logs: platform messages may contain sensitive context.
            return LocalKeyAccess.Unavailable(reference);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}
