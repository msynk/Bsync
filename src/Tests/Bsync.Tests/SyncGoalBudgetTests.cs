using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Bsync.Client;
using Bsync.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Document = Bsync.Tests.SessionExecutionTests.Document;
using Store = Bsync.Tests.SessionExecutionTests.Store;
using GoalTransport = Bsync.Tests.SessionExecutionTests.Transport;

namespace Bsync.Tests;

[Collection("Execution budgets")]
public sealed class SyncGoalBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SyncSession<Document> Session(Store store, FakeTimeProvider time,
        Func<SyncSessionOptions<Document>, SyncSessionOptions<Document>>? configure = null)
    {
        var options = new SyncSessionOptions<Document>
        {
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Document>(store, "node")),
            CreateTransport = _ => new GoalTransport(), StartReplication = false, TimeProvider = time,
            Cloner = d => new Document { Id = d.Id, UpdatedAt = d.UpdatedAt, Deleted = d.Deleted },
            Interval = TimeSpan.FromHours(1),
        };
        return new(configure is null ? options : configure(options));
    }

    [Fact]
    public async Task ZeroBudgetDoesNotResolveOrOpenAndReportsUnknownObservation()
    {
        var store = new Store(); var resolves = 0; var opens = 0;
        await using var session = Session(store, new(), o => o with
        { OpenReplica = (_, _) => { opens++; return Task.FromResult(new LocalReplica<Document>(store, "node")); } });
        var collection = new LocalSyncCollection<Document>(session, _ => { resolves++; return Task.FromResult("account"); });
        var result = await collection.SyncAsync(SyncGoal.Accepted("wanted", 1), TimeSpan.Zero);
        Assert.False(result.Reached); Assert.False(result.HasObservation);
        Assert.Single(result.Unaccepted);
        Assert.Equal(0, resolves); Assert.Equal(0, opens); Assert.Equal(0, store.Counts);
    }

    [Fact]
    public async Task AccountResolutionConsumesOriginalBudgetBeforeOpening()
    {
        var time = new FakeTimeProvider(); var opens = 0;
        await using var session = Session(new(), time, o => o with
        { OpenReplica = (_, _) => { opens++; return Task.FromResult(new LocalReplica<Document>(new Store(), "node")); } });
        var collection = new LocalSyncCollection<Document>(session, _ => { time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult("account"); });
        var waiting = collection.SyncAsync(SyncGoal.Background, Budget);
        // Also complete the old implementation's incorrectly restarted timer for the immutable baseline reproducer.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Reached);
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task LateReplicaOpeningIsJoinedAndClosedWithoutCreatingTransport()
    {
        var store = new Store(); var time = new FakeTimeProvider(); var transports = 0;
        var entered = Signal(); var release = Signal();
        await using var session = Session(store, time, o => o with
        {
            StartReplication = true,
            OpenReplica = async (_, _) => { entered.SetResult(); await release.Task; return new(store, "node"); },
            CreateTransport = _ => { transports++; return new GoalTransport(); },
        });
        var waiting = session.SyncAsync("account", SyncGoal.Background, Budget);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted); // provider is still owned, not abandoned
        release.SetResult();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Reached); Assert.False(result.HasObservation);
        Assert.True(store.Disposed); Assert.Equal(0, transports); Assert.Null(session.Account);
    }

    [Fact]
    public async Task LifecycleGateWaitConsumesBudgetWithoutCancelingTheOpeningOwner()
    {
        var entered = Signal(); var release = Signal(); var time = new FakeTimeProvider(); var store = new Store();
        await using var session = Session(store, time, o => o with
        { OpenReplica = async (_, _) => { entered.SetResult(); await release.Task; return new(store, "node"); } });
        var opening = session.GetEngineAsync("account");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = session.SyncAsync("account", SyncGoal.Background, Budget);
        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).HasObservation);
            Assert.False(opening.IsCompleted);
        }
        finally { release.TrySetResult(); await opening; }
    }

    [Fact]
    public async Task ExpiryReturnsLastObservationWithoutNewStorageReads()
    {
        var store = new Store(); var time = new FakeTimeProvider();
        await using var session = Session(store, time);
        var waiting = session.SyncAsync("account", SyncGoal.Accepted("wanted", 1), Budget);
        Assert.Equal(1, store.Counts); Assert.Equal(1, store.IssueReads);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Reached); Assert.True(result.HasObservation); Assert.Single(result.Unaccepted);
        Assert.Equal(1, store.Counts); Assert.Equal(1, store.IssueReads);
    }

    [Fact]
    public async Task LateGoalReadIsJoinedAndCannotStartTheNextReadOrClaimSuccess()
    {
        var store = new Store(); var time = new FakeTimeProvider(); var entered = Signal(); var release = Signal();
        store.CountAsync = async _ => { entered.SetResult(); await release.Task; };
        await using var session = Session(store, time);
        var waiting = session.SyncAsync("account", new SyncGoal(AllPendingAccepted: true), Budget);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Reached); Assert.False(result.HasObservation); Assert.Equal(0, store.IssueReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeReadStopDistinguishesCallerAndBudget(bool callerCanceled)
    {
        var store = new Store(); var time = new FakeTimeProvider(); var entered = Signal();
        using var caller = new CancellationTokenSource(); using var metrics = new Stops();
        store.CountAsync = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        await using var session = Session(store, time);
        var waiting = session.SyncAsync("account", SyncGoal.Background, Budget, cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCanceled) caller.Cancel(); else time.Advance(TimeSpan.FromSeconds(2));
        if (callerCanceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        else Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).HasObservation);
        Assert.Equal(0, store.IssueReads);
        Assert.Equal(callerCanceled ? "caller_canceled" : "budget_expired", Assert.Single(metrics.Reasons));
    }

    [Fact]
    public async Task StopJoinsGoalObservationBeforeClosingItsStore()
    {
        var store = new Store(); var entered = Signal(); var release = Signal();
        store.CountAsync = async _ => { entered.SetResult(); await release.Task; Assert.False(store.Disposed); };
        await using var session = Session(store, new());
        var waiting = session.SyncAsync("account", SyncGoal.Background, TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = session.StopAsync();
        Assert.False(stopping.IsCompleted); Assert.False(store.Disposed);
        release.SetResult();
        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Reached);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.Disposed); Assert.Equal(0, store.IssueReads);
    }

    [Fact]
    public async Task ExpiringOneWaitDoesNotCancelSharedReplicationOrAnotherWait()
    {
        var entered = Signal(); var release = Signal(); var time = new FakeTimeProvider(); var canceled = false;
        var transport = new GoalTransport { OnPull = async ct =>
        { using var registration = ct.Register(() => canceled = true); entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var session = Session(new(), time, o => o with { StartReplication = true, CreateTransport = _ => transport });
        var shortWait = session.SyncAsync("account", SyncGoal.Background, Budget);
        var longWait = session.SyncAsync("account", SyncGoal.Background, TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            Assert.False((await shortWait.WaitAsync(TimeSpan.FromSeconds(5))).Reached);
            Assert.False(canceled); Assert.False(longWait.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await longWait.WaitAsync(TimeSpan.FromSeconds(5))).Reached);
    }

    [Fact]
    public async Task ThrowingProgressAffectsOnlyItsWaitAndGoalInputsAreCopied()
    {
        await using var session = Session(new(), new(), o => o with { StartReplication = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SyncAsync("account", SyncGoal.Background,
            TimeSpan.FromSeconds(10), new ThrowingProgress()));
        Assert.True((await session.SyncAsync("account", SyncGoal.Background, TimeSpan.FromSeconds(10))).Reached);
        var time = new FakeTimeProvider();
        await using var offline = Session(new(), time);
        var documents = new List<SyncDocumentRevision> { new("wanted", 1) };
        var waiting = offline.SyncAsync("account", new(Documents: documents), Budget);
        documents.Clear(); time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("wanted", Assert.Single((await waiting).Unaccepted).Id);
    }

    [Fact]
    public async Task UnrelatedGoalReadCancellationPropagatesWithoutInventingAStopReason()
    {
        var store = new Store { CountAsync = _ => throw new OperationCanceledException("provider stop") };
        using var metrics = new Stops();
        await using var session = Session(store, new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SyncAsync("account", SyncGoal.Background, TimeSpan.FromSeconds(10)));
        Assert.Empty(metrics.Reasons);
    }

    [Fact]
    public void AggregatePreservesUnknownObservation()
    {
        var known = new SyncGoalResult(false, false, 3, [], default);
        var unknown = new SyncGoalResult(false, false, 0, [], default) { HasObservation = false };
        Assert.False(SyncGoalResult.Combine([known, unknown]).HasObservation);
        Assert.Equal(3, SyncGoalResult.Combine([known, unknown]).Pending);
    }

    private sealed class ThrowingProgress : IProgress<SyncProgress>
    { public void Report(SyncProgress value) => throw new InvalidOperationException("consumer progress error"); }
    private sealed class Stops : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<string> Reasons { get; } = new();
        public Stops()
        {
            _listener.InstrumentPublished = (i, l) =>
            { if (i.Meter.Name == SyncDiagnostics.SourceName && i.Name == "bsync.execution.stops") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var values = tags.ToArray();
                if (values.Any(t => t.Key == "bsync.operation" && Equals(t.Value, "sync_goal")))
                { Assert.Equal(2, values.Length); Reasons.Enqueue((string)values.Single(t => t.Key == "bsync.reason").Value!); }
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
