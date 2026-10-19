using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Bsync.Client;
using Bsync.Diagnostics;
using Bsync.Protocol;
using Bsync.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Document = Bsync.Tests.SessionExecutionTests.Document;
using Store = Bsync.Tests.SessionExecutionTests.Store;
using Lease = Bsync.Tests.SessionExecutionTests.Lease;

namespace Bsync.Tests;

[Collection("Execution budgets")]
public sealed class SessionLifetimeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SyncSession<Document> Session(Store store, HintTransport transport,
        Func<string, CancellationToken, Task<IAsyncDisposable?>>? acquire = null,
        SyncCoordinator? coordinator = null, TimeProvider? time = null, bool hints = false) => new(new()
    {
        OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Document>(store, "node")),
        Cloner = d => new Document { Id = d.Id, UpdatedAt = d.UpdatedAt, Deleted = d.Deleted },
        CreateTransport = _ => transport, AcquireLease = acquire, LiveHints = hints,
        Coordination = coordinator is null ? null : new(coordinator, "documents"),
        Interval = TimeSpan.FromHours(1), FollowerRefresh = TimeSpan.FromHours(1),
        MinBackoff = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1),
        TimeProvider = time ?? TimeProvider.System,
    });

    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    [InlineData("lost")]
    public async Task LateAcquisitionIsJoinedWithoutStartingStatusReadsOrReplication(string outcome)
    {
        using var metrics = new Stops("session_lease");
        var entered = Signal(); var release = Signal(); var canceled = Signal(); var store = new Store(); var transport = new HintTransport();
        var lease = new Lease(); CancellationToken callbackToken = default;
        await using var session = Session(store, transport, async (_, ct) =>
        {
            callbackToken = ct; using var registration = ct.Register(() => canceled.TrySetResult());
            entered.SetResult(); await release.Task;
            if (outcome == "lost") lease.Cancellation.Cancel();
            return outcome == "denied" ? null : lease;
        });
        await session.GetEngineAsync("private-account");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.StopAsync();
        try { await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(callbackToken.IsCancellationRequested); Assert.False(stopping.IsCompleted); Assert.False(store.Disposed); }
        finally { release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, store.Counts); Assert.Equal(0, store.IssueReads); Assert.Equal(0, transport.Pulls);
        Assert.Equal(outcome != "denied", lease.Disposed); Assert.True(store.Disposed);
        metrics.AssertOne("caller_canceled");
    }

    [Fact]
    public async Task CooperativeAcquisitionCancellationReportsOneStop()
    {
        using var metrics = new Stops("session_lease"); var entered = Signal();
        await using var session = Session(new(), new(), async (_, ct) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
        await session.GetEngineAsync("private-account"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        metrics.AssertOne("caller_canceled");
    }

    [Fact]
    public async Task AlreadyLostLeaseNeverStartsReplicationAndIsNotCountedAgainOnDisposal()
    {
        using var metrics = new Stops("session_lease"); var lease = new Lease(); lease.Cancellation.Cancel();
        var follower = Signal(); var transport = new HintTransport();
        await using var session = Session(new(), transport, (_, _) => Task.FromResult<IAsyncDisposable?>(lease));
        session.Changed += () => { if (session.Status.State == SyncState.Follower) follower.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await follower.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();
        Assert.Equal(0, transport.Pulls); Assert.True(lease.Disposed); metrics.AssertOne("lease_lost");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryGrantOrDenialDoesNotInventAStop(bool denied)
    {
        using var metrics = new Stops("session_lease"); var lease = new Lease(); var settled = Signal();
        await using var session = Session(new(), new(), (_, _) => Task.FromResult<IAsyncDisposable?>(denied ? null : lease));
        session.Changed += () => { if (session.Status.State == (denied ? SyncState.Follower : SyncState.Synced)) settled.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync(); Assert.Empty(metrics.Reasons); Assert.Equal(!denied, lease.Disposed);
    }

    [Fact]
    public async Task UnrelatedAcquisitionCancellationRemainsAnErrorWithoutAStopSignal()
    {
        using var metrics = new Stops("session_lease"); var failed = Signal();
        await using var session = Session(new(), new(), (_, _) => throw new OperationCanceledException("provider error"));
        session.Changed += () => { if (session.Status.State == SyncState.AttentionRequired) failed.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync(); Assert.Empty(metrics.Reasons);
    }

    [Fact]
    public async Task SessionJoinsLateHintAndDisposesIteratorWithoutConsumingMoreItems()
    {
        using var metrics = new Stops("session_hints");
        var entered = Signal(); var release = Signal(); var disposed = false; var afterYield = 0;
        async IAsyncEnumerable<StreamEvent<Document>> Hints([EnumeratorCancellation] CancellationToken ct)
        {
            try { entered.SetResult(); await release.Task; yield return StreamEvent<Document>.Resync(); afterYield++; }
            finally { disposed = true; }
        }
        var store = new Store();
        await using var session = Session(store, new() { Hints = Hints }, hints: true);
        await session.GetEngineAsync("private-account"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.StopAsync();
        try { Assert.False(stopping.IsCompleted); Assert.False(store.Disposed); }
        finally { release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed); Assert.True(store.Disposed); Assert.Equal(0, afterYield); metrics.AssertOne("caller_canceled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HintBackoffUsesInjectedTimeAndCancellationStopsWithoutReconnecting(bool multiplexed)
    {
        using var metrics = new Stops(multiplexed ? "coordinator_hints" : "session_hints");
        var time = new TimerClock(); var attempts = 0;
        async IAsyncEnumerable<string> Hints([EnumeratorCancellation] CancellationToken ct)
        { Interlocked.Increment(ref attempts); await Task.CompletedTask; if (attempts > 0) throw new OperationCanceledException("unrelated stream error"); yield break; }
        async IAsyncEnumerable<StreamEvent<Document>> OwnHints([EnumeratorCancellation] CancellationToken ct)
        { await foreach (var _ in Hints(ct)) yield return StreamEvent<Document>.Resync(); }
        await using var coordinator = new SyncCoordinator(new() { TimeProvider = time, Hints = multiplexed ? (_, _, ct) => Hints(ct) : null });
        await using var session = Session(new(), new() { Hints = OwnHints }, coordinator: multiplexed ? coordinator : null,
            time: time, hints: true, acquire: (_, _) => Task.FromResult<IAsyncDisposable?>(null));
        // Session follower waits are one hour; only the hint retry creates a timer within one minute.
        time.Observe = delay => delay <= TimeSpan.FromMinutes(1) && Volatile.Read(ref attempts) > 0;
        await session.GetEngineAsync("private-account"); await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts); Assert.Empty(metrics.Reasons);
        await session.StopAsync(); await coordinator.DisposeAsync();
        time.Advance(TimeSpan.FromDays(1)); Assert.Equal(1, attempts); metrics.AssertOne("caller_canceled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedHintsFinishWithoutInventingCancellation(bool multiplexed)
    {
        using var metrics = new Stops(multiplexed ? "coordinator_hints" : "session_hints"); var unsupported = Signal();
        async IAsyncEnumerable<string> Hints([EnumeratorCancellation] CancellationToken ct)
        { unsupported.TrySetResult(); await Task.FromException(new NotSupportedException()); yield break; }
        async IAsyncEnumerable<StreamEvent<Document>> OwnHints([EnumeratorCancellation] CancellationToken ct)
        { await foreach (var _ in Hints(ct)) yield return StreamEvent<Document>.Resync(); }
        await using var coordinator = new SyncCoordinator(new() { Hints = multiplexed ? (_, _, ct) => Hints(ct) : null });
        await using var session = Session(new(), new() { Hints = OwnHints }, coordinator: multiplexed ? coordinator : null, hints: true);
        await session.GetEngineAsync("private-account"); await unsupported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Completion of fallback is observable through the session's own iterator, after the shared catch has returned.
        await Task.Delay(50);
        Assert.Empty(metrics.Reasons);
        await session.StopAsync(); await coordinator.DisposeAsync(); Assert.Empty(metrics.Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementHintsJoinPredecessorAndIgnoreLateItemsOrUnsupported(bool unsupported)
    {
        // Exercise root CancelAsync racing a queued replacement's linked-token callback.
        // Both admitted listener lifetimes must still report shutdown, even if the provider never starts.
        for (var iteration = 0; iteration < 25; iteration++) await CheckReplacementShutdown(unsupported);
    }

    private static async Task CheckReplacementShutdown(bool unsupported)
    {
        using var metrics = new Stops("coordinator_hints");
        var entered = Signal(); var release = Signal(); var disposed = false; var afterYield = 0; var accounts = new ConcurrentQueue<string>();
        async IAsyncEnumerable<string> Hints(string account, [EnumeratorCancellation] CancellationToken ct)
        {
            accounts.Enqueue(account);
            try
            {
                entered.TrySetResult(); await release.Task;
                if (unsupported) throw new NotSupportedException();
                yield return "documents"; afterYield++;
            }
            finally { disposed = true; }
        }
        await using var coordinator = new SyncCoordinator(new() { Hints = (account, _, ct) => Hints(account, ct) });
        var transport = new HintTransport();
        await using var session = Session(new(), transport, coordinator: coordinator, hints: true);
        await session.GetEngineAsync("first-account"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.GetEngineAsync("second-account");
        var stopping = coordinator.DisposeAsync().AsTask();
        try { Assert.False(stopping.IsCompleted); Assert.False(disposed); }
        finally { release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first-account" }, accounts.ToArray()); Assert.True(disposed);
        Assert.Equal(0, afterYield); Assert.Equal(0, transport.Streams);
        Assert.Equal(new[] { "caller_canceled", "caller_canceled" }, metrics.Reasons.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountSwitchStartsReplacementOnlyAfterOldIteratorDisposal(bool unsupported)
    {
        using var metrics = new Stops("coordinator_hints");
        var firstEntered = Signal(); var release = Signal(); var nextEntered = Signal(); var firstDisposed = false; var afterYield = 0;
        async IAsyncEnumerable<string> Hints(string account, [EnumeratorCancellation] CancellationToken ct)
        {
            if (account == "first")
            {
                try
                {
                    firstEntered.TrySetResult(); await release.Task;
                    if (unsupported) throw new NotSupportedException();
                    yield return "documents"; afterYield++;
                }
                finally { firstDisposed = true; }
            }
            else
            {
                Assert.True(firstDisposed); nextEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct);
            }
        }
        await using var coordinator = new SyncCoordinator(new() { Hints = (account, _, ct) => Hints(account, ct) });
        var transport = new HintTransport();
        await using var session = Session(new(), transport, coordinator: coordinator, hints: true);
        await session.GetEngineAsync("first"); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await session.GetEngineAsync("second"); Assert.False(nextEntered.Task.IsCompleted); }
        finally { release.TrySetResult(); }
        await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, afterYield); Assert.Equal(0, transport.Streams); metrics.AssertOne("caller_canceled");
        await coordinator.DisposeAsync();
        Assert.Equal(new[] { "caller_canceled", "caller_canceled" }, metrics.Reasons.ToArray());
    }

    private sealed class HintTransport : ISyncTransport<Document>
    {
        private readonly SessionExecutionTests.Transport _inner = new();
        public Func<CancellationToken, IAsyncEnumerable<StreamEvent<Document>>>? Hints;
        public int Pulls => _inner.Pulls;
        public int Streams;
        public Task<PullResult<Document>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) => _inner.PullAsync(request, cancellationToken);
        public Task<PushResult<Document>> PushAsync(PushRequest<Document> request, CancellationToken cancellationToken = default) => _inner.PushAsync(request, cancellationToken);
        public IAsyncEnumerable<StreamEvent<Document>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Streams); return Hints?.Invoke(cancellationToken) ?? _inner.StreamAsync(since, cancellationToken); }
    }

    private sealed class TimerClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        public TaskCompletionSource TimerCreated { get; } = Signal();
        public Func<TimeSpan, bool>? Observe;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = _inner.CreateTimer(callback, state, dueTime, period); if (Observe?.Invoke(dueTime) == true) TimerCreated.TrySetResult(); return timer; }
        public void Advance(TimeSpan amount) => _inner.Advance(amount);
    }

    private sealed class Stops : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<string> Reasons { get; } = new();
        public Stops(string operation)
        {
            _listener.InstrumentPublished = (i, l) =>
            { if (i.Meter.Name == SyncDiagnostics.SourceName && i.Name == "bsync.execution.stops") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var all = tags.ToArray();
                if (!all.Any(t => t.Key == "bsync.operation" && Equals(t.Value, operation))) return;
                Assert.Equal(1, value); Assert.Equal(2, all.Length);
                Reasons.Enqueue((string)all.Single(t => t.Key == "bsync.reason").Value!);
            });
            _listener.Start();
        }
        public void AssertOne(string reason) => Assert.Equal(reason, Assert.Single(Reasons));
        public void Dispose() => _listener.Dispose();
    }
}
