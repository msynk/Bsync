using Bsync.Storage;

namespace Bsync.Client;

/// <summary>An execution lease whose loss cancels network work. Persistence writes must additionally use its fence.</summary>
public interface ISyncExecutionLease : IAsyncDisposable
{
    /// <summary>Canceled when this owner must stop. In-flight requests can still complete and must be replay-safe.</summary>
    CancellationToken Lost { get; }
}

/// <summary>Renewable metadata-backed cross-process/tab ownership with a unique fencing generation.</summary>
public sealed class ExecutionLease : ISyncExecutionLease
{
    private readonly IOrchestrationStore _store;
    private readonly TimeSpan _duration;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lost;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewing;
    private int _disposed;

    private ExecutionLease(IOrchestrationStore store, OrchestrationFence fence, TimeSpan duration, TimeSpan remaining, TimeProvider time)
    {
        _store = store;
        Fence = fence;
        _duration = duration;
        _time = time;
        _lost = new CancellationTokenSource(remaining, time);
        _renewing = RenewAsync();
    }

    /// <summary>Pass to every scheduler-owned metadata transaction; expired or superseded owners cannot commit.</summary>
    public OrchestrationFence Fence { get; }
    /// <inheritdoc />
    public CancellationToken Lost => _lost.Token;

    /// <summary>Acquires an absent/expired lease, or returns null. Duration must be between 1 second and 5 minutes.</summary>
    public static async Task<ExecutionLease?> TryAcquireAsync(IOrchestrationStore store, string key, TimeSpan duration,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        OrchestrationValidation.Key(key);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(duration));
        var time = timeProvider ?? TimeProvider.System;
        var existing = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (existing.ExpiresAtUnixMilliseconds is null
            || existing.ExpiresAtUnixMilliseconds > time.GetUtcNow().ToUnixTimeMilliseconds())) return null;
        var fence = new OrchestrationFence(key, Guid.NewGuid().ToString("N"));
        var expiry = time.GetUtcNow() + duration;
        if (!await store.TryWriteAsync([new(key, existing?.Revision, fence.Token, expiry.ToUnixTimeMilliseconds())],
            cancellationToken: cancellationToken).ConfigureAwait(false)) return null;
        var remaining = expiry - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return null;
        return new ExecutionLease(store, fence, duration, remaining, time);
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_duration / 3, _time, _stop.Token).ConfigureAwait(false);
                if (_lost.IsCancellationRequested) break;
                var expiry = (_time.GetUtcNow() + _duration).ToUnixTimeMilliseconds();
                var current = await _store.ReadAsync(Fence.Key, _stop.Token).ConfigureAwait(false);
                if (current is null || current.Payload != Fence.Token || !await _store.TryWriteAsync(
                    [new(Fence.Key, current.Revision, Fence.Token, expiry)], Fence, _stop.Token).ConfigureAwait(false)) break;
                var remaining = DateTimeOffset.FromUnixTimeMilliseconds(expiry) - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero || _lost.IsCancellationRequested) break;
                _lost.CancelAfter(remaining);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { /* Storage/renewal failure loses ownership; never keep working on an uncertain lease. */ }
        finally { await _lost.CancelAsync().ConfigureAwait(false); }
    }

    /// <summary>Stops renewal and removes only this generation's lease. Never removes a successor's ownership.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renewing.ConfigureAwait(false);
        var current = await _store.ReadAsync(Fence.Key).ConfigureAwait(false);
        if (current?.Payload == Fence.Token)
            await _store.TryWriteAsync([new(Fence.Key, current.Revision, null)]).ConfigureAwait(false);
        // Keep the cancellation sources usable for consumers holding Lost after disposal.
    }
}
