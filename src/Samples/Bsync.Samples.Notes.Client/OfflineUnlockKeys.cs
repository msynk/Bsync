using Bsync.Client;

namespace Bsync.Samples.Notes.Client;

/// <summary>
/// A user-supplied recovery key held only for this tab's unlocked lifetime. No password derivation, key persistence,
/// credential renewal or remote revocation claim. Host must close encrypted stores before Lock/Dispose.
/// </summary>
public sealed class OfflineUnlockKeys(LocalKeyReference reference) : ILocalKeyProvider, IDisposable
{
    private LocalKeyAccess? _unlocked;
    private bool _disposed;

    public void Unlock(ReadOnlySpan<byte> recoveryKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replacement = LocalKeyAccess.Available(reference, recoveryKey);
        _unlocked?.Dispose();
        _unlocked = replacement;
    }

    public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference requested, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(requested != reference ? LocalKeyAccess.Unavailable(requested) : _unlocked is null
            ? LocalKeyAccess.Locked(reference) : LocalKeyAccess.Available(reference, _unlocked.Key.Span));
    }

    public void Lock() { _unlocked?.Dispose(); _unlocked = null; }
    public void Dispose() { Lock(); _disposed = true; }
}
