using System.Security.Cryptography;

namespace Bsync.Client;

/// <summary>Identifies an existing local encryption key. It is not a credential, authorization claim or key material.</summary>
public sealed record LocalKeyReference
{
    /// <summary>Creates an account-scoped, explicitly versioned key reference.</summary>
    public LocalKeyReference(AccountScope account, string keyId, int version)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length > 128 || keyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Key identifiers must be at most 128 ASCII letters, digits, dots, underscores or hyphens.", nameof(keyId));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Account = account; KeyId = keyId; Version = version;
    }

    /// <summary>Local account namespace.</summary>
    public AccountScope Account { get; }
    /// <summary>Host-owned key identifier; never infer it from server access tokens.</summary>
    public string KeyId { get; }
    /// <summary>Version required by the existing encrypted resource.</summary>
    public int Version { get; }
}

/// <summary>Local key availability, independent of network credential validity.</summary>
public enum LocalKeyState
{
    /// <summary>The requested existing key is available.</summary>
    Available,
    /// <summary>Local interaction or device unlock is needed. Background work should defer.</summary>
    Locked,
    /// <summary>The requested key cannot be obtained. Preserve ciphertext; never generate a replacement implicitly.</summary>
    Unavailable,
}

/// <summary>
/// Host boundary for existing local keys. Acquisition must not provision, rotate, erase, or fetch a replacement key.
/// Foreground unlock/provisioning is an explicit host operation. The caller disposes each returned access handle.
/// </summary>
public interface ILocalKeyProvider
{
    /// <summary>Requests the exact account/key/version. Offline opening must not require network renewal.</summary>
    ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// A disposable copy of a 256-bit local key, or an explicit unavailable outcome. Dispose zeroes this owned copy only;
/// host/provider/runtime copies and keys already imported by a storage engine have separate lifetimes.
/// Not thread-safe with concurrent disposal. Never serialize, log or persist Key bytes beside encrypted resources.
/// </summary>
public sealed class LocalKeyAccess : IDisposable
{
    private byte[]? _key;
    private bool _disposed;

    private LocalKeyAccess(LocalKeyReference reference, LocalKeyState state, byte[]? key)
    { ArgumentNullException.ThrowIfNull(reference); Reference = reference; State = state; _key = key; }

    /// <summary>The exact requested key identity.</summary>
    public LocalKeyReference Reference { get; }
    /// <summary>Availability at acquisition time; this is not a remote revocation check.</summary>
    public LocalKeyState State { get; }

    /// <summary>Creates available access by copying exactly 32 bytes. The caller remains responsible for its original.</summary>
    public static LocalKeyAccess Available(LocalKeyReference reference, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (key.Length != 32) throw new ArgumentException("Local keys must contain exactly 32 bytes.", nameof(key));
        return new(reference, LocalKeyState.Available, key.ToArray());
    }

    /// <summary>Reports that unlocking the existing key requires host interaction.</summary>
    public static LocalKeyAccess Locked(LocalKeyReference reference) => new(reference, LocalKeyState.Locked, null);
    /// <summary>Reports missing/inaccessible key material without creating a substitute.</summary>
    public static LocalKeyAccess Unavailable(LocalKeyReference reference) => new(reference, LocalKeyState.Unavailable, null);

    /// <summary>Gets the owned key or throws ReplicaKeyUnavailableException for a locked/unavailable outcome.</summary>
    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_key is null) throw new ReplicaKeyUnavailableException(State == LocalKeyState.Locked);
            return _key;
        }
    }

    /// <summary>Zeroes this handle's owned key bytes. Idempotent; retained memory views also observe zeros.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_key is { } key) CryptographicOperations.ZeroMemory(key);
        _key = null;
    }
}
