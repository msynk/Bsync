using System.Collections.Concurrent;

namespace Bsync.Client;

// One monotonic deadline includes ownership acquisition and host callbacks. Disposal never abandons started work.
internal sealed class CooperativeDeadline : IDisposable
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _expiry;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _caller;
    private readonly string _operation;
    private readonly Func<StopSignals>? _parentSignals;
    private readonly IDisposable _tokenScope;
    private int _leaseLost;
    private int _finalSignals = -1;
    private static readonly ConcurrentDictionary<CancellationToken, Func<StopSignals>> Sources = new();
    private static readonly System.Diagnostics.Metrics.Counter<long> Stops = Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.execution.stops", "{invocation}", "Cancellation/deadline/lease signals observed by invocations and listener lifetimes; not business outcomes.");

    public CooperativeDeadline(TimeSpan budget, TimeProvider time, CancellationToken caller, string operation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        _time = time;
        _started = time.GetTimestamp();
        _budget = budget;
        _caller = caller;
        _operation = operation;
        Sources.TryGetValue(caller, out _parentSignals);
        _expiry = new(budget, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _expiry.Token);
        _tokenScope = TrackToken(_linked.Token);
    }
    public CancellationToken Token => _linked.Token;
    public bool Expired => _expiry.IsCancellationRequested || _time.GetElapsedTime(_started) >= _budget;
    // Dispose this registration BEFORE disposing the lease: ordinary lease disposal also cancels Lost.
    public CancellationTokenRegistration ObserveLease(CancellationToken lost) =>
        lost.UnsafeRegister(static state => Interlocked.Exchange(ref ((CooperativeDeadline)state!)._leaseLost, 1), this);
    // Register only library-owned aliases, and dispose the scope before disposing/canceling their underlying lease.
    // Reading the live lease token avoids depending on cancellation callback ordering in nested invocations.
    public IDisposable TrackToken(CancellationToken token, CancellationToken lost = default) =>
        Register(token, () => Signals() | (lost.IsCancellationRequested ? StopSignals.Lease : StopSignals.None));
    // Lifecycle aliases have no timer, but may inherit a deadline or lease from a coordinated parent run.
    internal static IDisposable TrackLinkedToken(CancellationToken token, CancellationToken caller,
        CancellationToken lost = default, CancellationToken additionalCaller = default, CancellationToken additionalCaller2 = default)
    {
        Sources.TryGetValue(caller, out var parent);
        Sources.TryGetValue(additionalCaller, out var additionalParent);
        Sources.TryGetValue(additionalCaller2, out var additionalParent2);
        return Register(token, () => CallerSignals(caller, parent) | CallerSignals(additionalCaller, additionalParent)
            | CallerSignals(additionalCaller2, additionalParent2)
            | (lost.IsCancellationRequested ? StopSignals.Lease : StopSignals.None));
    }
    public TimeSpan Remaining
    {
        get
        {
            var remaining = _budget - _time.GetElapsedTime(_started);
            if (remaining <= TimeSpan.Zero) _expiry.Cancel();
            Token.ThrowIfCancellationRequested();
            return remaining;
        }
    }
    public void Dispose()
    {
        var signals = Signals();
        if (Interlocked.CompareExchange(ref _finalSignals, (int)signals, -1) != -1) return;
        _tokenScope.Dispose();
        _linked.Dispose(); _expiry.Dispose();
        RecordStop(_operation, signals);
    }
    private StopSignals Signals()
    {
        var final = Volatile.Read(ref _finalSignals);
        if (final >= 0) return (StopSignals)final;
        return CallerSignals(_caller, _parentSignals)
            | (Volatile.Read(ref _leaseLost) != 0 ? StopSignals.Lease : StopSignals.None)
            | (Expired ? StopSignals.Budget : StopSignals.None);
    }

    internal static void RecordStop(string operation, CancellationToken caller, bool leaseLost, bool expired)
    {
        Sources.TryGetValue(caller, out var parent);
        RecordStop(operation, CallerSignals(caller, parent)
            | (leaseLost ? StopSignals.Lease : StopSignals.None) | (expired ? StopSignals.Budget : StopSignals.None));
    }
    internal static void RecordStop(string operation, bool callerCanceled, bool leaseLost, bool expired)
        => RecordStop(operation, (callerCanceled ? StopSignals.Caller : StopSignals.None)
            | (leaseLost ? StopSignals.Lease : StopSignals.None) | (expired ? StopSignals.Budget : StopSignals.None));

    private static void RecordStop(string operation, StopSignals signals)
    {
        var reason = signals.HasFlag(StopSignals.Caller) ? "caller_canceled"
            : signals.HasFlag(StopSignals.Lease) ? "lease_lost" : signals.HasFlag(StopSignals.Budget) ? "budget_expired" : null;
        if (reason is not null) Stops.Add(1, new KeyValuePair<string, object?>("bsync.operation", operation),
            new KeyValuePair<string, object?>("bsync.reason", reason));
    }

    private static StopSignals CallerSignals(CancellationToken caller, Func<StopSignals>? parent)
    {
        if (!caller.IsCancellationRequested) return StopSignals.None;
        var inherited = parent?.Invoke() ?? StopSignals.None;
        // A locally canceled alias with no parent signal is still caller-relative cancellation.
        return inherited == StopSignals.None ? StopSignals.Caller : inherited;
    }

    private static IDisposable Register(CancellationToken token, Func<StopSignals> signals)
    {
        var scope = new TokenScope(token, signals);
        if (!Sources.TryAdd(token, scope.Signals)) throw new InvalidOperationException("Cancellation token already has an observation scope.");
        return scope;
    }
    private sealed class TokenScope(CancellationToken token, Func<StopSignals> signals) : IDisposable
    {
        private int _final = -1;
        public StopSignals Signals()
        {
            var final = Volatile.Read(ref _final);
            return final >= 0 ? (StopSignals)final : signals();
        }
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _final, (int)signals(), -1) == -1) Sources.TryRemove(token, out _);
        }
    }
    [Flags]
    private enum StopSignals { None = 0, Caller = 1, Lease = 2, Budget = 4 }
}
