using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Diagnostics;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

[Collection("Execution budgets")]
public sealed class SessionExecutionTests
{
    [Fact]
    public async Task PausedSessionSleepsUntilTriggeredAndCanResumeOrStop()
    {
        var store = new Store(); var transport = new Transport();
        await using var session = Session(store, transport);
        var synced = Signal(); var paused = Signal();
        session.Changed += () =>
        {
            if (session.Status.State == SyncState.Synced) synced.TrySetResult();
            if (session.Status.State == SyncState.Paused) paused.TrySetResult();
        };
        await session.GetEngineAsync("private-account");
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Pause(); session.RequestSync();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var counts = Volatile.Read(ref store.Counts); var pulls = transport.Pulls;
        await Task.Delay(100); // Let an erroneous tight loop perform observable storage work.
        Assert.Equal(counts, Volatile.Read(ref store.Counts));
        Assert.Equal(pulls, transport.Pulls);
        synced = Signal(); session.Resume();
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transport.Pulls > pulls);
        paused = Signal(); session.Pause(); session.RequestSync();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroBudgetDoesNotOpenStorageAndCallerCancellationWins(bool canceled)
    {
        using var metrics = new Stops();
        using var caller = new CancellationTokenSource();
        if (canceled) caller.Cancel();
        var store = new Store(); var transport = new Transport();
        var engine = Engine(store, transport, new FakeTimeProvider());
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SyncForAsync(TimeSpan.Zero, cancellationToken: caller.Token));
        else Assert.True((await engine.SyncForAsync(TimeSpan.Zero)).HasRemainingWork);
        Assert.Equal(0, store.Openings); Assert.Equal(0, transport.Pulls);
        metrics.AssertOne("sync_for", canceled ? "caller_canceled" : "budget_expired");
    }

    [Fact]
    public async Task UnrelatedCancellationIsNotReportedAsBudgetExpiry()
    {
        using var metrics = new Stops();
        var transport = new Transport { OnPull = _ => throw new OperationCanceledException("provider-specific stop") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(new Store(), transport, new FakeTimeProvider()).SyncForAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(metrics.Values);
    }

    [Fact]
    public async Task DeadlineIncludesTheReplicationGateAndDoesNotCancelItsOwner()
    {
        using var metrics = new Stops();
        var entered = Signal(); var release = Signal(); var time = new FakeTimeProvider();
        var transport = new Transport { OnPull = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var engine = Engine(new Store(), transport, time);
        var owner = engine.SyncAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = engine.SyncForAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).HasRemainingWork);
            Assert.False(owner.IsCompleted);
            Assert.Equal(1, transport.Pulls);
        }
        finally { release.TrySetResult(); await owner; }
        metrics.AssertOne("sync_for", "budget_expired");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeNetworkStopUsesInjectedTimeAndPreservesCallerReason(bool callerCanceled)
    {
        using var metrics = new Stops(); using var caller = new CancellationTokenSource();
        var entered = Signal(); var time = new FakeTimeProvider();
        var transport = new Transport { OnPull = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var running = Engine(new Store(), transport, time).SyncForAsync(TimeSpan.FromSeconds(1), cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCanceled) caller.Cancel(); else time.Advance(TimeSpan.FromSeconds(2));
        if (callerCanceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else Assert.True((await running.WaitAsync(TimeSpan.FromSeconds(5))).HasRemainingWork);
        metrics.AssertOne("sync_for", callerCanceled ? "caller_canceled" : "budget_expired");
    }

    [Fact]
    public async Task LateDiagnosticCallbackCannotTurnExpiredRunIntoCompletion()
    {
        using var metrics = new Stops(measureQueue: true);
        var time = new FakeTimeProvider();
        var store = new Store { OnCount = () => time.Advance(TimeSpan.FromSeconds(2)) };
        var result = await Engine(store, new Transport(), time).SyncForAsync(TimeSpan.FromSeconds(1));
        Assert.True(result.HasRemainingWork);
        Assert.Equal(1, store.Counts);
        metrics.AssertOne("sync_for", "budget_expired");
    }

    [Fact]
    public async Task CompleteAndRunLimitedResultsDoNotInventStopSignals()
    {
        using var metrics = new Stops();
        Assert.False((await Engine(new Store(), new Transport(), new FakeTimeProvider()).SyncForAsync(TimeSpan.FromSeconds(10))).HasRemainingWork);
        var transport = new Transport { More = true };
        var engine = new SyncEngine<Document>(new Store(), transport, new HybridLogicalClock("node"), Clone,
            options: new() { TimeProvider = new FakeTimeProvider(), MaxPullPages = 1 });
        Assert.True((await engine.SyncForAsync(TimeSpan.FromSeconds(10), maxRuns: 2)).HasRemainingWork);
        Assert.Equal(2, transport.Pulls);
        Assert.Empty(metrics.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRunReportsLeaseLossOrLifecycleStop(bool leaseLoss)
    {
        using var metrics = new Stops();
        var entered = Signal(); var lease = new Lease();
        var transport = new Transport { OnPull = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using var session = Session(new Store(), transport, lease);
        await session.GetEngineAsync("private-account");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (leaseLoss)
        {
            lease.Cancellation.Cancel();
            await metrics.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await session.SetReplicationEnabledAsync(false);
        Assert.True(lease.Disposed);
        metrics.AssertOne("session_run", leaseLoss ? "lease_lost" : "caller_canceled");
    }

    [Fact]
    public async Task NormalSessionLeaseDisposalDoesNotReportLoss()
    {
        using var metrics = new Stops(); var lease = new Lease(); var synced = Signal();
        await using var session = Session(new Store(), new Transport(), lease);
        session.Changed += () => { if (session.Status.State == SyncState.Synced) synced.TrySetResult(); };
        await session.GetEngineAsync("private-account");
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetReplicationEnabledAsync(false);
        Assert.True(lease.Disposed);
        Assert.Empty(metrics.Values);
    }

    internal static SyncSession<Document> Session(Store store, Transport transport, Lease? lease = null) => new(new()
    {
        Cloner = Clone, OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Document>(store, "node")),
        CreateTransport = _ => transport, Interval = TimeSpan.FromHours(1), FollowerRefresh = TimeSpan.FromHours(1),
        AcquireLease = lease is null ? null : (_, _) => Task.FromResult<IAsyncDisposable?>(lease),
    });
    private static SyncEngine<Document> Engine(Store store, Transport transport, TimeProvider time) =>
        new(store, transport, new HybridLogicalClock("node"), Clone, options: new() { TimeProvider = time });
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Document : ISyncEntity
    {
        public string Id { get; set; } = "doc";
        public HlcTimestamp UpdatedAt { get; set; }
        public bool Deleted { get; set; }
    }
    private static Document Clone(Document doc) => new() { Id = doc.Id, UpdatedAt = doc.UpdatedAt, Deleted = doc.Deleted };
    internal sealed class Transport : ISyncTransport<Document>
    {
        public int Pulls;
        public bool More;
        public Func<CancellationToken, Task>? OnPull;
        public async Task<PullResult<Document>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            var page = Interlocked.Increment(ref Pulls);
            if (OnPull is not null) await OnPull(cancellationToken);
            return new([], new Checkpoint($"page-{page}"), More);
        }
        public Task<PushResult<Document>> PushAsync(PushRequest<Document> request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No pending documents.");
        public async IAsyncEnumerable<StreamEvent<Document>> StreamAsync(Checkpoint since, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }
    internal sealed class Lease : ISyncExecutionLease
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Disposed;
        public CancellationToken Lost => Cancellation.Token;
        public ValueTask DisposeAsync() { Disposed = true; Cancellation.Cancel(); return ValueTask.CompletedTask; }
    }
    internal sealed class Store : ILocalStore<Document>, IAsyncDisposable
    {
        private readonly InMemoryLocalStore<Document> _inner = new(Clone);
        public int Counts; public int Openings;
        public Action? OnCount;
        public Func<CancellationToken, Task>? CountAsync;
        public int IssueReads;
        public bool Disposed;
        public async Task<int> CountDirtyAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Counts); OnCount?.Invoke();
            if (CountAsync is not null) await CountAsync(cancellationToken);
            return await _inner.CountDirtyAsync(CancellationToken.None);
        }
        public Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref IssueReads); return _inner.CountIssuesAsync(cancellationToken); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Openings); return _inner.GetClockHighWaterAsync(cancellationToken); }
        public Task<SyncRecord<Document>?> GetAsync(string id, CancellationToken cancellationToken = default) => _inner.GetAsync(id, cancellationToken);
        public Task<IReadOnlyList<RecordUpdateResult<Document>>> UpdateAsync(IReadOnlyList<RecordUpdate<Document>> updates, ReplicaCursor? cursor = null, CancellationToken cancellationToken = default) => _inner.UpdateAsync(updates, cursor, cancellationToken);
        public Task<IReadOnlyList<SyncRecord<Document>>> GetPendingAsync(int limit, IReadOnlySet<string>? exclude = null, CancellationToken cancellationToken = default) => _inner.GetPendingAsync(limit, exclude, cancellationToken);
        public Task<IReadOnlyList<SyncRecord<Document>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default) => _inner.GetStaleAsync(generation, limit, cancellationToken);
        public Task<IReadOnlyList<SyncRecord<Document>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default) => _inner.GetConflictsAsync(limit, cancellationToken);
        public Task<IReadOnlyList<SyncRecord<Document>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default) => _inner.GetRejectedAsync(limit, cancellationToken);
        public Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default) => _inner.PurgeAsync(ids, generation, cancellationToken);
        public Task<IReadOnlyList<Document>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) => _inner.QueryAsync(includeDeleted, cancellationToken);
        public Task<IReadOnlyList<Document>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default) => _inner.QueryPageAsync(afterId, limit, includeDeleted, cancellationToken);
        public Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default) => _inner.GetCursorAsync(cancellationToken);
    }
    private sealed class Stops : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<KeyValuePair<string, object?>[]> Values { get; } = new();
        public TaskCompletionSource Observed { get; } = Signal();
        public Stops(bool measureQueue = false)
        {
            _listener.InstrumentPublished = (i, l) =>
            { if (i.Meter.Name == SyncDiagnostics.SourceName && (i.Name == "bsync.execution.stops" || measureQueue && i.Name == "bsync.queue.depth")) l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var values = tags.ToArray();
                if (instrument.Name == "bsync.execution.stops" && !values.Any(t => t.Key == "bsync.operation" &&
                    t.Value is "sync_for" or "session_run")) return;
                Assert.Equal(1, value); Values.Enqueue(values); Observed.TrySetResult();
            });
            _listener.Start();
        }
        public void AssertOne(string operation, string reason)
        {
            var tags = Assert.Single(Values); Assert.Equal(2, tags.Length);
            Assert.Contains(tags, t => t.Key == "bsync.operation" && Equals(t.Value, operation));
            Assert.Contains(tags, t => t.Key == "bsync.reason" && Equals(t.Value, reason));
        }
        public void Dispose() => _listener.Dispose();
    }
}
