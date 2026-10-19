using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Bsync.Client;
using Bsync.Blobs;
using Bsync.Diagnostics;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

[CollectionDefinition("Execution budgets", DisableParallelization = true)]
public sealed class ExecutionBudgetCollection;

[Collection("Execution budgets")]
public sealed class ExecutionBudgetTests
{
    private static readonly AccountScope Account = new("private-app", "private-authority", "private-account");

    [Fact]
    public async Task TransferAttemptExpiryAfterNoncooperativeSizeCheckDoesNotAcknowledgeCompletion()
    {
        using var metrics = new Stops();
        var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://private-authority.invalid/") };
        var cache = new LateSizeCache(time);
        var manager = new BlobTransferManager(store, cache, new(http), new() { TimeProvider = time, AttemptBudget = TimeSpan.FromSeconds(1) });
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        await manager.EnqueueAsync(hash, 3, BlobTransferDirection.Download);
        var drain = await manager.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, drain.Completed);
        Assert.True(drain.HasRemainingWork);
        Assert.Equal(BlobTransferState.Queued, (await manager.GetAsync(hash, BlobTransferDirection.Download))!.State);
        metrics.AssertOne("transfer_attempt", "budget_expired");
    }

    [Fact]
    public async Task WorkflowOwnershipAcquisitionUsesTheOriginalBudget()
    {
        using var metrics = new Stops();
        var time = new FakeTimeProvider();
        await using var inner = new InMemoryOrchestrationStore(time);
        var store = new OpeningStore(inner, () => time.Advance(TimeSpan.FromSeconds(2)));
        var calls = 0;
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] steps = [new("private-step", (_, _) => { calls++; return Task.FromResult(WorkflowStepOutcome.Complete); })];
        Assert.Null(await workflow.RunAsync("private-id", "1", steps, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, calls);
        Assert.Null(await workflow.GetAsync("private-id"));
        metrics.AssertOne("workflow", "budget_expired");
    }

    [Theory]
    [InlineData("workflow", "caller_canceled")]
    [InlineData("workflow", "budget_expired")]
    [InlineData("workflow", "lease_lost")]
    [InlineData("background", "caller_canceled")]
    [InlineData("background", "budget_expired")]
    [InlineData("background", "lease_lost")]
    [InlineData("read_model_rebuild", "caller_canceled")]
    [InlineData("read_model_rebuild", "budget_expired")]
    [InlineData("read_model_rebuild", "lease_lost")]
    public async Task InterruptedInvocationsReportTheirCauseAndPreserveUnacknowledgedWork(string operation, string reason)
    {
        using var metrics = new Stops();
        var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        using var caller = new CancellationTokenSource();
        var budget = reason == "budget_expired" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        async Task Interrupt(CancellationToken token)
        {
            if (reason == "caller_canceled") caller.Cancel();
            else if (reason == "budget_expired") time.Advance(TimeSpan.FromSeconds(2));
            else
            {
                var prefix = operation switch { "workflow" => "workflow-owner/", "background" => "background-owner/", _ => "read-model/" };
                var owner = Assert.Single(await store.ReadPageAsync(prefix, null, 100), r => r.ExpiresAtUnixMilliseconds is not null);
                Assert.True(await store.TryWriteAsync([new(owner.Key, owner.Revision, "successor", (time.GetUtcNow() + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds())]));
                time.Advance(TimeSpan.FromSeconds(10));
                // Renewal observes the successor. Join its cancellation instead of assuming timer callback ordering.
                try { await Task.Delay(Timeout.Infinite, token).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            // Deliberately return successfully despite the stop signal; it must not acknowledge work.
        }
        if (operation == "workflow")
        {
            var workflow = new RecoverableWorkflow(store, Account, time);
            WorkflowStep[] steps = [new("private-step", async (_, token) => { await Interrupt(token); return WorkflowStepOutcome.Complete; })];
            var run = workflow.RunAsync("private-id", "1", steps, budget, caller.Token);
            if (reason == "caller_canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else Assert.Equal(0, (await run)!.NextStep);
            Assert.Equal("running", (await workflow.GetAsync("private-id"))!.State);
        }
        else if (operation == "background")
        {
            var dispatcher = new BackgroundWorkDispatcher(store, time);
            await dispatcher.RequestAsync(new(Account, 0, new()), new Scheduler());
            var result = await dispatcher.RunAsync(Account, budget, async (_, token) => { await Interrupt(token); return BackgroundWorkOutcome.Completed; }, caller.Token);
            Assert.Equal(reason == "caller_canceled" ? BackgroundWorkOutcome.Canceled : BackgroundWorkOutcome.RemainingWork, result);
            Assert.True((await dispatcher.GetAsync(Account))!.Pending);
        }
        else
        {
            var model = new LocalReadModel(store, "private-model", time);
            async IAsyncEnumerable<ReadModelRow> Rows([EnumeratorCancellation] CancellationToken token = default)
            { await Interrupt(token); yield return new("private-row", "private-payload"); }
            var run = model.RebuildAsync("1", "private-source", Rows(), budget, caller.Token);
            if (reason == "caller_canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else Assert.False(await run);
            Assert.Null(await model.OpenActiveAsync());
        }
        metrics.AssertOne(operation, reason);
        if (reason == "lease_lost")
            Assert.Single(await store.ReadPageAsync("", null, 100), r => r.Payload == "successor");
    }

    [Fact]
    public async Task OrdinaryLeaseDisposalDoesNotReportLossAndCallerWinsSimultaneousSignals()
    {
        using var metrics = new Stops();
        var time = new FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var workflow = new RecoverableWorkflow(store, Account, time);
        WorkflowStep[] complete = [new("step", (_, _) => Task.FromResult(WorkflowStepOutcome.Complete))];
        Assert.Equal("complete", (await workflow.RunAsync("one", "1", complete, TimeSpan.FromSeconds(5)))!.State);
        Assert.Empty(metrics.Values);
        using var caller = new CancellationTokenSource();
        WorkflowStep[] interrupted = [new("step", (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2)); caller.Cancel();
            return Task.FromResult(WorkflowStepOutcome.Complete);
        })];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync("two", "1", interrupted, TimeSpan.FromSeconds(1), caller.Token));
        metrics.AssertOne("workflow", "caller_canceled");
    }

    private sealed class Stops : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<KeyValuePair<string, object?>[]> Values { get; } = new();
        internal Stops()
        {
            _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == SyncDiagnostics.SourceName && i.Name == "bsync.execution.stops") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => { Assert.Equal(1, value); Values.Enqueue(tags.ToArray()); });
            _listener.Start();
        }
        internal void AssertOne(string operation, string reason)
        {
            var tags = Assert.Single(Values);
            Assert.Equal(2, tags.Length);
            Assert.Contains(tags, t => t.Key == "bsync.operation" && Equals(t.Value, operation));
            Assert.Contains(tags, t => t.Key == "bsync.reason" && Equals(t.Value, reason));
        }
        public void Dispose() => _listener.Dispose();
    }
    private sealed class Scheduler : IBackgroundWorkScheduler
    {
        public BackgroundCapabilities Capabilities => new(true, true, true);
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkOutcome.Scheduled);
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Already cached content requires no network.");
    }
    private sealed class LateSizeCache(FakeTimeProvider time) : IBlobCache
    {
        private int _reads;
        public Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default)
        { if (++_reads == 2) time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult<long?>(3); }
        public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3]));
        public Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class OpeningStore(IOrchestrationStore inner, Action delay) : IOrchestrationStore
    {
        private bool _delayed;
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!_delayed && key.StartsWith("workflow-owner/", StringComparison.Ordinal)) { _delayed = true; delay(); }
            return inner.ReadAsync(key, cancellationToken);
        }
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default) => inner.TryWriteAsync(mutations, fence, cancellationToken);
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
