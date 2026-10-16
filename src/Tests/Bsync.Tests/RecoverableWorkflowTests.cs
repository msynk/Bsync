using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class RecoverableWorkflowTests
{
    private readonly InMemoryOrchestrationStore _store = new();
    private readonly AccountScope _account = new("app", "test", "alice");
    private RecoverableWorkflow Workflow() => new(_store, _account);

    [Fact]
    public async Task LostReplyReplaysStableOperationIdentityAndSkipsConfirmedSteps()
    {
        var effects = new HashSet<string>(StringComparer.Ordinal); var firstCalls = 0; var lastCalls = 0; var crash = true;
        WorkflowStep[] steps =
        [
            new("save-reference", (key, _) => { firstCalls++; effects.Add(key); return Task.FromResult(WorkflowStepOutcome.Complete); }),
            new("upload", async (key, _) =>
            {
                var progress = await Workflow().GetAsync("work");
                Assert.Equal("running", progress!.State); Assert.Equal(1, progress.NextStep);
                effects.Add(key);
                if (crash) { crash = false; throw new IOException("Simulated process loss after effect, before progress acknowledgement."); }
                return WorkflowStepOutcome.Complete;
            }),
            new("publish", (key, _) => { lastCalls++; effects.Add(key); return Task.FromResult(WorkflowStepOutcome.Complete); }),
        ];
        await Assert.ThrowsAsync<IOException>(() => Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, (await Workflow().GetAsync("work"))!.NextStep);
        Assert.Equal("complete", (await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)))!.State);
        Assert.Equal(3, effects.Count); Assert.Equal(1, firstCalls); Assert.Equal(1, lastCalls);
        await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5));
        Assert.Equal(1, lastCalls);
    }

    [Fact]
    public async Task DependencyFailureStopsDrainAndPermanentFailureRequiresExplicitRetry()
    {
        var outcome = WorkflowStepOutcome.RetryLater; var calls = 0;
        WorkflowStep[] steps = [new("dependency", (_, _) => { calls++; return Task.FromResult(outcome); })];
        Assert.Equal("pending", (await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)))!.State);
        Assert.Equal(1, calls);
        outcome = WorkflowStepOutcome.Failed;
        Assert.Equal("failed", (await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)))!.State);
        outcome = WorkflowStepOutcome.Complete;
        Assert.Equal("failed", (await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)))!.State);
        Assert.Equal(2, calls);
        Assert.True(await Workflow().RetryFailedAsync("work"));
        Assert.Equal("complete", (await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow().RunAsync("work", "2", steps, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CompetingExecutorDoesNotRunAndCancellationPreservesRecoverableStep()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowStep[] steps = [new("blocked", async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return WorkflowStepOutcome.Complete; })];
        using var stop = new CancellationTokenSource();
        var running = Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5), stop.Token);
        await entered.Task;
        Assert.Null(await Workflow().RunAsync("work", "1", steps, TimeSpan.FromSeconds(5)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("running", (await Workflow().GetAsync("work"))!.State);
    }
}
