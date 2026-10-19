using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Bsync.Client;
using Bsync.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Document = Bsync.Tests.SessionExecutionTests.Document;
using Store = Bsync.Tests.SessionExecutionTests.Store;
using Lease = Bsync.Tests.SessionExecutionTests.Lease;
using TestTransport = Bsync.Tests.SessionExecutionTests.Transport;

namespace Bsync.Tests;

[Collection("Execution budgets")]
public sealed class SessionOwnershipTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SyncSession<Document> Session(Store store, TestTransport transport,
        Func<string, CancellationToken, Task<IAsyncDisposable?>> acquire, TimeProvider? time = null) => new(new()
    {
        OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Document>(store, "node")),
        CreateTransport = _ => transport, AcquireLease = acquire,
        Cloner = d => new Document { Id = d.Id, UpdatedAt = d.UpdatedAt, Deleted = d.Deleted },
        TimeProvider = time ?? TimeProvider.System, Interval = TimeSpan.FromHours(1), FollowerRefresh = TimeSpan.FromHours(1),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleLossWakesWithoutAdvancingTimeAndReleasesBeforeReacquiring(bool denied)
    {
        using var metrics = new Stops(); var first = new Lease(); var next = new Lease(); var acquisitions = 0;
        var settled = Signal(); var replaced = Signal(); var time = new FakeTimeProvider();
        await using var session = Session(new(), new(), (_, _) =>
        {
            if (Interlocked.Increment(ref acquisitions) == 1) return Task.FromResult<IAsyncDisposable?>(first);
            Assert.True(first.Disposed); replaced.TrySetResult();
            return Task.FromResult<IAsyncDisposable?>(denied ? null : next);
        }, time);
        session.Changed += () => { if (session.Status.State == SyncState.Synced) settled.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.Cancellation.CancelAsync();
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();
        Assert.Equal(2, acquisitions); Assert.Equal(!denied, next.Disposed);
        Assert.Equal("lease_lost", Assert.Single(metrics.Reasons));
    }

    [Fact]
    public async Task PausedLossIsObservedOnceWithoutResumingOrPolling()
    {
        using var metrics = new Stops(); var first = new Lease(); var next = new Lease(); var acquisitions = 0;
        var synced = Signal(); var paused = Signal(); var lossRead = Signal(); var resumed = Signal(); var lost = false;
        var store = new Store { OnCount = () => { if (lost) lossRead.TrySetResult(); } }; var transport = new TestTransport();
        await using var session = Session(store, transport, (_, _) =>
        {
            var count = Interlocked.Increment(ref acquisitions);
            if (count > 1) resumed.TrySetResult();
            return Task.FromResult<IAsyncDisposable?>(count == 1 ? first : next);
        }, new FakeTimeProvider());
        session.Changed += () =>
        {
            if (session.Status.State == SyncState.Synced) synced.TrySetResult();
            if (session.Status.State == SyncState.Paused) paused.TrySetResult();
        };
        await session.GetEngineAsync("private-account"); await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Pause(); session.RequestSync(); await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pulls = transport.Pulls; lost = true; await first.Cancellation.CancelAsync();
        await lossRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var counts = Volatile.Read(ref store.Counts);
        await Task.Delay(100); // An incorrect repeated loss notification would spin while paused.
        Assert.Equal(counts, Volatile.Read(ref store.Counts)); Assert.Equal(pulls, transport.Pulls); Assert.Equal(1, acquisitions);
        Assert.Equal(SyncState.Paused, session.Status.State); Assert.Equal("lease_lost", Assert.Single(metrics.Reasons));
        session.Resume(); await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await session.StopAsync();
        Assert.True(first.Disposed); Assert.True(next.Disposed); Assert.Single(metrics.Reasons);
    }

    [Fact]
    public async Task OwnershipLossCannotBypassServerRetryAfter()
    {
        using var metrics = new Stops(); var lease = new Lease(); var time = new TimerClock(); var acquisitions = 0;
        var retried = Signal(); var retry = TimeSpan.FromMinutes(10);
        time.Observe = delay => delay == retry;
        var transport = new TestTransport { OnPull = _ => throw new SyncTransportException("unavailable", "retry later", true, retry) };
        await using var session = Session(new(), transport, (_, _) =>
        {
            if (Interlocked.Increment(ref acquisitions) == 1) return Task.FromResult<IAsyncDisposable?>(lease);
            retried.TrySetResult(); return Task.FromResult<IAsyncDisposable?>(null);
        }, time);
        await session.GetEngineAsync("private-account"); await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.Cancellation.CancelAsync(); time.Advance(TimeSpan.FromMinutes(9));
        Assert.False(retried.Task.IsCompleted); Assert.Equal(1, acquisitions); Assert.Equal(1, transport.Pulls);
        time.Advance(TimeSpan.FromMinutes(1)); await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync(); Assert.True(lease.Disposed); Assert.Equal("lease_lost", Assert.Single(metrics.Reasons));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalShutdownOrSuspensionDetachesBeforeLeaseDisposal(bool suspend)
    {
        using var metrics = new Stops(); var lease = new Lease(); var synced = Signal();
        await using var session = Session(new(), new(), (_, _) => Task.FromResult<IAsyncDisposable?>(lease));
        session.Changed += () => { if (session.Status.State == SyncState.Synced) synced.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (suspend) await session.SetReplicationEnabledAsync(false); else await session.StopAsync();
        Assert.True(lease.Disposed); Assert.True(lease.Lost.IsCancellationRequested); Assert.Empty(metrics.Reasons);
    }

    [Fact]
    public async Task ActiveRunLossHasSeparateOwnershipAndRunObservations()
    {
        using var metrics = new Stops(); var lease = new Lease(); var entered = Signal();
        var transport = new TestTransport { OnPull = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var acquisitions = 0;
        await using var session = Session(new(), transport, (_, _) => Task.FromResult<IAsyncDisposable?>(Interlocked.Increment(ref acquisitions) == 1 ? lease : null));
        await session.GetEngineAsync("private-account"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.Cancellation.CancelAsync(); await metrics.RunObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();
        Assert.Equal("lease_lost", Assert.Single(metrics.Reasons)); Assert.Equal("lease_lost", Assert.Single(metrics.RunReasons));
    }

    [Fact]
    public async Task AlreadyLostAcquisitionIsNotAnAdmittedOwnershipLifetime()
    {
        using var metrics = new Stops(); var lease = new Lease(); lease.Cancellation.Cancel(); var follower = Signal();
        await using var session = Session(new(), new(), (_, _) => Task.FromResult<IAsyncDisposable?>(lease));
        session.Changed += () => { if (session.Status.State == SyncState.Follower) follower.TrySetResult(); };
        await session.GetEngineAsync("private-account"); await follower.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync(); Assert.Empty(metrics.Reasons); Assert.True(lease.Disposed);
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
        public ConcurrentQueue<string> RunReasons { get; } = new();
        public TaskCompletionSource RunObserved { get; } = Signal();
        public Stops()
        {
            _listener.InstrumentPublished = (i, l) =>
            { if (i.Meter.Name == SyncDiagnostics.SourceName && i.Name == "bsync.execution.stops") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var all = tags.ToArray();
                var operation = all.Single(t => t.Key == "bsync.operation").Value;
                if (operation is not ("session_ownership" or "session_run")) return;
                Assert.Equal(1, value); Assert.Equal(2, all.Length);
                var reason = (string)all.Single(t => t.Key == "bsync.reason").Value!;
                if (Equals(operation, "session_ownership")) Reasons.Enqueue(reason);
                else { RunReasons.Enqueue(reason); RunObserved.TrySetResult(); }
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
