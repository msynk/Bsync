using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class BackgroundWorkTests
{
    private readonly InMemoryOrchestrationStore _store = new();
    private readonly AccountScope _account = new("app", "test", "alice");

    [Fact]
    public async Task UnsupportedSchedulingKeepsPendingRequestForForegroundFallback()
    {
        var dispatcher = new BackgroundWorkDispatcher(_store);
        var scheduler = new Scheduler(new(false, false, false));
        Assert.Equal(BackgroundWorkOutcome.Unsupported, await dispatcher.RequestAsync(new(_account, 0, new()), scheduler));
        Assert.Equal(0, scheduler.Calls);
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
        Assert.Equal(BackgroundWorkOutcome.Completed, await new BackgroundWorkDispatcher(_store).RunAsync(_account, TimeSpan.FromSeconds(5),
            (account, _) => { Assert.Equal(_account, account); return Task.FromResult(BackgroundWorkOutcome.Completed); }));
        Assert.False((await dispatcher.GetAsync(_account))!.Pending);
    }

    [Fact]
    public async Task NewRequestDuringExecutionCannotBeAcknowledgedByOldRun()
    {
        var dispatcher = new BackgroundWorkDispatcher(_store);
        var scheduler = new Scheduler(new(true, true, true));
        var request = new BackgroundWorkRequest(_account, 0, new());
        Assert.Equal(BackgroundWorkOutcome.Scheduled, await dispatcher.RequestAsync(request, scheduler));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = dispatcher.RunAsync(_account, TimeSpan.FromSeconds(5), async (_, _) => { entered.SetResult(); await finish.Task; return BackgroundWorkOutcome.Completed; });
        await entered.Task;
        await dispatcher.RequestAsync(request, scheduler);
        Assert.Equal(BackgroundWorkOutcome.RemainingWork, await dispatcher.RunAsync(_account, TimeSpan.FromSeconds(5), (_, _) => throw new Exception("Competing worker ran.")));
        finish.SetResult();
        Assert.Equal(BackgroundWorkOutcome.RemainingWork, await run);
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
    }

    [Fact]
    public async Task LockedKeysAndCancellationPreserveQueuedWork()
    {
        var dispatcher = new BackgroundWorkDispatcher(_store);
        await dispatcher.RequestAsync(new(_account, 0, new()), new Scheduler(new(true, true, true)));
        Assert.Equal(BackgroundWorkOutcome.KeyUnavailable, await dispatcher.RunAsync(_account, TimeSpan.FromSeconds(5), (_, _) => throw new ReplicaKeyUnavailableException(true)));
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var run = dispatcher.RunAsync(_account, TimeSpan.FromSeconds(5), async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return BackgroundWorkOutcome.Completed; }, stop.Token);
        await entered.Task; stop.Cancel();
        Assert.Equal(BackgroundWorkOutcome.Canceled, await run);
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
    }

    [Fact]
    public async Task CallbackThatIgnoresDeadlineCannotClearPendingRequest()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var store = new InMemoryOrchestrationStore(time);
        var dispatcher = new BackgroundWorkDispatcher(store, time);
        await dispatcher.RequestAsync(new(_account, 0, new()), new Scheduler(new(true, true, true)));
        var outcome = await dispatcher.RunAsync(_account, TimeSpan.FromSeconds(1), (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(BackgroundWorkOutcome.Completed);
        });
        Assert.Equal(BackgroundWorkOutcome.RemainingWork, outcome);
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
    }

    private sealed class Scheduler(BackgroundCapabilities capabilities) : IBackgroundWorkScheduler
    {
        public int Calls { get; private set; }
        public BackgroundCapabilities Capabilities => capabilities;
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(BackgroundWorkOutcome.Scheduled); }
    }
}
