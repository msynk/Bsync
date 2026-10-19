using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Diagnostics;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Document = Bsync.Tests.SessionExecutionTests.Document;
using Store = Bsync.Tests.SessionExecutionTests.Store;
using TestTransport = Bsync.Tests.SessionExecutionTests.Transport;

namespace Bsync.Tests;

[Collection("Execution budgets")]
public sealed class NestedStopCauseTests
{
    private static readonly AccountScope Account = new("app", "authority", "private-account");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SyncEngine<Document> Engine(Func<CancellationToken, Task> pull, TimeProvider? time = null) =>
        new(new Store(), new TestTransport { OnPull = pull }, new HybridLogicalClock("node"),
            d => new Document { Id = d.Id, UpdatedAt = d.UpdatedAt, Deleted = d.Deleted },
            options: new() { TimeProvider = time ?? new FakeTimeProvider() });

    private static async Task LoseLease(IOrchestrationStore store, FakeTimeProvider time, CancellationToken token)
    {
        var owner = Assert.Single(await store.ReadPageAsync("", null, 100), r => r.ExpiresAtUnixMilliseconds is not null);
        Assert.True(await store.TryWriteAsync([new(owner.Key, owner.Revision, "successor", (time.GetUtcNow() + TimeSpan.FromMinutes(2)).ToUnixTimeMilliseconds())]));
        time.Advance(TimeSpan.FromSeconds(10));
        try { await Task.Delay(Timeout.Infinite, token).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [Theory]
    [InlineData("budget", "budget_expired")]
    [InlineData("lease", "lease_lost")]
    [InlineData("caller", "caller_canceled")]
    [InlineData("caller_budget", "caller_canceled")]
    [InlineData("lease_budget", "lease_lost")]
    [InlineData("caller_lease", "caller_canceled")]
    public async Task WorkflowChildPreservesObservedRootCauseAndPrecedence(string signals, string expected)
    {
        using var metrics = new Stops(); using var caller = new CancellationTokenSource(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var budget = signals.Contains("lease") ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1);
        var engine = Engine(async ct =>
        {
            if (signals.Contains("lease")) await LoseLease(store, time, ct);
            if (signals.Contains("caller")) caller.Cancel();
            if (signals.Contains("budget")) time.Advance(budget);
        });
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("step", async (_, ct) => { await engine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); return WorkflowStepOutcome.Complete; })];
        var running = workflow.RunAsync("work", "1", steps, budget, caller.Token);
        if (signals.Contains("caller")) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else Assert.Equal(0, (await running)!.NextStep);
        Assert.Equal("running", (await workflow.GetAsync("work"))!.State);
        metrics.AssertOne("workflow", expected); metrics.AssertOne("sync_for", expected);
        Assert.Equal(2, metrics.Values.Count);
    }

    [Fact]
    public async Task ThreeLevelsKeepBudgetCauseWithoutChangingCallerRelativeOutcomes()
    {
        using var metrics = new Stops(); var outerTime = new FakeTimeProvider();
        await using var workflows = new InMemoryOrchestrationStore(outerTime);
        var backgroundTime = new FakeTimeProvider();
        await using var catalog = new InMemoryOrchestrationStore(backgroundTime);
        var dispatcher = new BackgroundWorkDispatcher(catalog, backgroundTime);
        await dispatcher.RequestAsync(new(Account, 0, new()), new Scheduler());
        var engine = Engine(_ => { outerTime.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
        var workflow = new RecoverableWorkflow(workflows, Account, outerTime);
        var observed = BackgroundWorkOutcome.Completed;
        WorkflowStep[] steps = [new("step", async (_, parent) =>
        {
            observed = await dispatcher.RunAsync(Account, TimeSpan.FromMinutes(1), async (_, child) =>
            { await engine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: child); return BackgroundWorkOutcome.Completed; }, parent);
            return WorkflowStepOutcome.Complete;
        })];
        Assert.Equal(0, (await workflow.RunAsync("work", "1", steps, TimeSpan.FromSeconds(1)))!.NextStep);
        Assert.Equal(BackgroundWorkOutcome.Canceled, observed); // Existing public result semantics are caller-relative.
        Assert.True((await dispatcher.GetAsync(Account))!.Pending);
        foreach (var operation in new[] { "workflow", "background", "sync_for" }) metrics.AssertOne(operation, "budget_expired");
    }

    [Fact]
    public async Task InnerExpiryDoesNotInventAParentStop()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider(); var childTime = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var engine = Engine(_ => { childTime.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; }, childTime);
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("step", async (_, ct) =>
        { Assert.True((await engine.SyncForAsync(TimeSpan.FromSeconds(1), cancellationToken: ct)).HasRemainingWork); return WorkflowStepOutcome.RetryLater; })];
        Assert.Equal("pending", (await workflow.RunAsync("work", "1", steps, TimeSpan.FromMinutes(1)))!.State);
        metrics.AssertOne("sync_for", "budget_expired"); Assert.Single(metrics.Values);
    }

    [Fact]
    public async Task ApplicationLinkedTokenRetainsCallerRelativeClassification()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var engine = Engine(_ => { time.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("step", async (_, ct) =>
        {
            using var application = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await engine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: application.Token);
            return WorkflowStepOutcome.Complete;
        })];
        Assert.Equal(0, (await workflow.RunAsync("work", "1", steps, TimeSpan.FromSeconds(1)))!.NextStep);
        metrics.AssertOne("workflow", "budget_expired"); metrics.AssertOne("sync_for", "caller_canceled");
    }

    [Fact]
    public async Task CompletedScopesRemoveTokenAssociations()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider(); CancellationToken retained = default;
        await using var store = new InMemoryOrchestrationStore(time);
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("step", (_, ct) =>
        { retained = ct; time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(WorkflowStepOutcome.Complete); })];
        Assert.Equal(0, (await workflow.RunAsync("work", "1", steps, TimeSpan.FromSeconds(1)))!.NextStep);
        Assert.True(retained.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(_ => Task.CompletedTask).SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: retained));
        metrics.AssertOne("workflow", "budget_expired"); metrics.AssertOne("sync_for", "caller_canceled");
    }

    [Fact]
    public async Task ParallelUnrelatedTokensDoNotInheritAnotherInvocationsCause()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        await using var firstStore = new InMemoryOrchestrationStore(time);
        var otherTime = new FakeTimeProvider();
        await using var otherStore = new InMemoryOrchestrationStore(otherTime);
        var entered = Signal(); var release = Signal();
        var unaffected = new RecoverableWorkflow(otherStore, Account, otherTime);
        var otherEngine = Engine(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        var other = unaffected.RunAsync("other", "1", [new("step", async (_, ct) =>
        { await otherEngine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); return WorkflowStepOutcome.Complete; })], TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var failed = new RecoverableWorkflow(firstStore, Account, time);
            var expiring = Engine(_ => { time.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
            Assert.Equal(0, (await failed.RunAsync("first", "1", [new("step", async (_, ct) =>
            { await expiring.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); return WorkflowStepOutcome.Complete; })], TimeSpan.FromSeconds(1)))!.NextStep);
            Assert.False(other.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("complete", (await other)!.State);
        metrics.AssertOne("workflow", "budget_expired"); metrics.AssertOne("sync_for", "budget_expired"); Assert.Equal(2, metrics.Values.Count);
    }

    [Theory]
    [InlineData("budget", "budget_expired")]
    [InlineData("lease", "lease_lost")]
    [InlineData("caller", "caller_canceled")]
    public async Task TransferAttemptReportsDrainCause(string signal, string expected)
    {
        using var metrics = new Stops(); using var caller = new CancellationTokenSource(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://example.invalid/") };
        var cache = new CallbackCache(async ct =>
        {
            if (signal == "lease") await LoseLease(store, time, ct);
            else if (signal == "caller") caller.Cancel();
            else time.Advance(TimeSpan.FromSeconds(2));
        });
        var manager = new BlobTransferManager(store, cache, new(http), new() { TimeProvider = time, AttemptBudget = TimeSpan.FromMinutes(1) });
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        await manager.EnqueueAsync(hash, 3, BlobTransferDirection.Download);
        var drain = manager.DrainAsync(signal == "lease" ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1), caller.Token);
        if (signal == "caller") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        else { var result = await drain; Assert.Equal(0, result.Completed); Assert.True(result.HasRemainingWork); }
        Assert.NotEqual(BlobTransferState.Complete, (await manager.GetAsync(hash, BlobTransferDirection.Download))!.State);
        metrics.AssertOne("transfer_drain", expected); metrics.AssertOne("transfer_attempt", expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroBudgetChildUsesParentCause(bool goal)
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        await using var session = SessionExecutionTests.Session(new(), new());
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("step", async (_, ct) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            if (goal) await session.SyncAsync("account", SyncGoal.Background, TimeSpan.Zero, cancellationToken: ct);
            else await Engine(_ => Task.CompletedTask).SyncForAsync(TimeSpan.Zero, cancellationToken: ct);
            return WorkflowStepOutcome.Complete;
        })];
        Assert.Equal(0, (await workflow.RunAsync("work", "1", steps, TimeSpan.FromSeconds(1)))!.NextStep);
        metrics.AssertOne("workflow", "budget_expired"); metrics.AssertOne(goal ? "sync_goal" : "sync_for", "budget_expired");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadModelChildPreservesBudgetOrLeaseCause(bool loseLease)
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var engine = Engine(async ct =>
        { if (loseLease) await LoseLease(store, time, ct); else time.Advance(TimeSpan.FromSeconds(2)); });
        async IAsyncEnumerable<ReadModelRow> Rows([EnumeratorCancellation] CancellationToken ct = default)
        { await engine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); yield return new("row", "payload"); }
        var model = new LocalReadModel(store, "model", time);
        Assert.False(await model.RebuildAsync("1", "source", Rows(), loseLease ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1)));
        Assert.Null(await model.OpenActiveAsync());
        metrics.AssertOne("read_model_rebuild", loseLease ? "lease_lost" : "budget_expired");
        metrics.AssertOne("sync_for", loseLease ? "lease_lost" : "budget_expired");
    }

    [Fact]
    public async Task DirectDeadlineTokenPropagatesWithoutALeaseAlias()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        var child = Engine(_ => { time.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
        var parent = Engine(async ct => { await child.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); }, time);
        Assert.True((await parent.SyncForAsync(TimeSpan.FromSeconds(1))).HasRemainingWork);
        Assert.Equal(2, metrics.Values.Count);
        Assert.All(metrics.Values, v => Assert.Equal(("sync_for", "budget_expired"), v));
    }

    [Fact]
    public async Task TransferAttemptAliasPropagatesToItsOwnChild()
    {
        using var metrics = new Stops(); var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://example.invalid/") };
        var engine = Engine(_ => { time.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
        var cache = new CallbackCache(async ct => { await engine.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); });
        var manager = new BlobTransferManager(store, cache, new(http), new() { TimeProvider = time, AttemptBudget = TimeSpan.FromMinutes(1) });
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        await manager.EnqueueAsync(hash, 3, BlobTransferDirection.Download);
        Assert.Equal(0, (await manager.DrainAsync(TimeSpan.FromSeconds(1))).Completed);
        foreach (var operation in new[] { "transfer_drain", "transfer_attempt", "sync_for" }) metrics.AssertOne(operation, "budget_expired");
    }

    private sealed class Scheduler : IBackgroundWorkScheduler
    {
        public BackgroundCapabilities Capabilities => new(true, true, true);
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkOutcome.Scheduled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRunAliasCarriesLeaseOrLifecycleCauseToNestedWork(bool leaseLoss)
    {
        using var metrics = new Stops(); var entered = Signal(); var observed = Signal();
        var lease = new SessionExecutionTests.Lease();
        var child = Engine(async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var transport = new TestTransport { OnPull = async ct =>
        {
            try { await child.SyncForAsync(TimeSpan.FromMinutes(1), cancellationToken: ct); }
            finally { observed.TrySetResult(); }
        } };
        await using var session = SessionExecutionTests.Session(new Store(), transport, lease);
        await session.GetEngineAsync("private-account");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (leaseLoss) { lease.Cancellation.Cancel(); await metrics.RunObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        await session.SetReplicationEnabledAsync(false);
        metrics.AssertOne("sync_for", leaseLoss ? "lease_lost" : "caller_canceled");
        metrics.AssertOne("session_run", leaseLoss ? "lease_lost" : "caller_canceled");
    }

    private sealed class NoNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Cached content needs no network."); }
    private sealed class CallbackCache(Func<CancellationToken, Task> callback) : IBlobCache
    {
        private int _sizes;
        public async Task<long?> SizeAsync(string hash, CancellationToken cancellationToken = default)
        { if (++_sizes == 2) await callback(cancellationToken); return 3; }
        public Task<Stream?> OpenReadAsync(string hash, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3]));
        public Task<long> PartialLengthAsync(string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AppendPartialAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CompletePartialAsync(string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardPartialAsync(string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Stops : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<(string Operation, string Reason)> Values { get; } = new();
        public TaskCompletionSource RunObserved { get; } = Signal();
        public Stops()
        {
            _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == SyncDiagnostics.SourceName && i.Name == "bsync.execution.stops") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                Assert.Equal(1, value); var all = tags.ToArray(); Assert.Equal(2, all.Length);
                Values.Enqueue(((string)all.Single(t => t.Key == "bsync.operation").Value!, (string)all.Single(t => t.Key == "bsync.reason").Value!));
                if (all.Single(t => t.Key == "bsync.operation").Value is "session_run") RunObserved.TrySetResult();
            });
            _listener.Start();
        }
        public void AssertOne(string operation, string reason) => Assert.Equal(reason, Assert.Single(Values, v => v.Operation == operation).Reason);
        public void Dispose() => _listener.Dispose();
    }
}
