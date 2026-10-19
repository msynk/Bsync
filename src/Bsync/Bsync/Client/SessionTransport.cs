using System.Runtime.CompilerServices;
using Bsync.Protocol;
using Bsync.Transport;

namespace Bsync.Client;

// Engines retained by an old UI cannot start a network request after their session is stopped/suspended.
internal sealed class SessionTransport<T> : ISyncTransport<T>
    where T : class, ISyncEntity
{
    private readonly Lazy<ISyncTransport<T>> _inner;
    private readonly CancellationToken _stopped;
    private readonly object _gate = new();
    private CancellationTokenSource _network = new();
    private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting;
    private int _running;

    public SessionTransport(Func<ISyncTransport<T>> create, CancellationToken stopped, bool enabled)
    {
        _inner = new(create);
        _stopped = stopped;
        _accepting = enabled;
        _drained.TrySetResult();
        if (enabled) _ = _inner.Value; // Preserve the established normal-open factory contract.
    }

    public void Resume()
    {
        lock (_gate)
        {
            _stopped.ThrowIfCancellationRequested();
            if (_network.IsCancellationRequested) { _network.Dispose(); _network = new(); }
            _accepting = true;
        }
    }

    public async Task SuspendAsync()
    {
        Task drained;
        lock (_gate) { _accepting = false; drained = _drained.Task; }
        await _network.CancelAsync().ConfigureAwait(false);
        await drained.ConfigureAwait(false);
    }

    private CancellationTokenSource Link(CancellationToken token, out IDisposable cause)
    {
        lock (_gate)
        {
            _stopped.ThrowIfCancellationRequested();
            if (!_accepting) throw new InvalidOperationException("Network replication is suspended for this session.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopped, _network.Token, token);
            cause = CooperativeDeadline.TrackLinkedToken(linked.Token, token, additionalCaller: _stopped, additionalCaller2: _network.Token);
            if (_running++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return linked;
        }
    }

    private void End()
    {
        lock (_gate) if (--_running == 0) _drained.TrySetResult();
    }

    public async Task<PullResult<T>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken, out var observation);
        using var cause = observation;
        try { return await _inner.Value.PullAsync(request, linked.Token).ConfigureAwait(false); }
        finally { End(); }
    }

    public async Task<PushResult<T>> PushAsync(PushRequest<T> request, CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken, out var observation);
        using var cause = observation;
        try { return await _inner.Value.PushAsync(request, linked.Token).ConfigureAwait(false); }
        finally { End(); }
    }

    public async IAsyncEnumerable<StreamEvent<T>> StreamAsync(Checkpoint since, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken, out var observation);
        using var cause = observation;
        try { await foreach (var item in _inner.Value.StreamAsync(since, linked.Token).ConfigureAwait(false)) yield return item; }
        finally { End(); }
    }
}
