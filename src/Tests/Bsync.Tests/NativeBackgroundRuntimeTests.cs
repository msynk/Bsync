using Bsync.Client;
using Bsync.Samples.NativeBackground;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class NativeBackgroundRuntimeTests
{
    private readonly AccountScope _account = new("app", "test", "alice");
    private readonly InMemoryOrchestrationStore _catalog = new();

    [Fact]
    public async Task ForegroundFallbackOpensFreshScopesAndCompletesDurableDeferredWorkOnce()
    {
        var dispatcher = new BackgroundWorkDispatcher(_catalog);
        Assert.Equal(BackgroundWorkOutcome.Unsupported, await dispatcher.RequestAsync(new(_account, 0, new()), new Unsupported()));
        var opened = 0; var disposed = 0; var effects = 0;
        var runtime = new NativeBackgroundRuntime((key, _) =>
        {
            Assert.Equal(_account.StorageKey, key); opened++;
            return Task.FromResult<IBackgroundAccountScope?>(new Scope(_account, dispatcher,
                _ => { effects++; return Task.FromResult(BackgroundWorkOutcome.Completed); }, () => disposed++));
        }, _ => Task.FromResult<IReadOnlyList<string>>([_account.StorageKey]));
        Assert.True(await runtime.ReconcileAsync(TimeSpan.FromSeconds(5), default));
        Assert.False((await dispatcher.GetAsync(_account))!.Pending);
        Assert.True(await runtime.ReconcileAsync(TimeSpan.FromSeconds(5), default));
        Assert.Equal(2, opened); Assert.Equal(2, disposed); Assert.Equal(1, effects);
    }

    [Fact]
    public async Task ExpirationJoinsAccountScopeAndRetainsPendingWork()
    {
        var dispatcher = new BackgroundWorkDispatcher(_catalog);
        await dispatcher.RequestAsync(new(_account, 0, new()), new Unsupported());
        var stopped = false; var disposed = false;
        var runtime = new NativeBackgroundRuntime((_, _) => Task.FromResult<IBackgroundAccountScope?>(new Scope(_account, dispatcher, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); return BackgroundWorkOutcome.Completed; }
            finally { stopped = true; }
        }, () => { Assert.True(stopped); disposed = true; })), _ => Task.FromResult<IReadOnlyList<string>>([_account.StorageKey]));
        Assert.False(await runtime.ReconcileAsync(TimeSpan.FromMilliseconds(100), default));
        Assert.True(disposed);
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
    }

    [Fact]
    public async Task AccountOpeningSharesTheWorkerDeadline()
    {
        var stopped = false;
        var runtime = new NativeBackgroundRuntime(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); return null; }
            finally { stopped = true; }
        }, _ => Task.FromResult<IReadOnlyList<string>>([]));
        Assert.Equal(BackgroundWorkOutcome.Canceled, await runtime.RunAsync(_account.StorageKey, TimeSpan.FromMilliseconds(100), default));
        Assert.True(stopped);
    }

    [Fact]
    public async Task WrongAccountAndLockedKeysCannotExecuteOrClearWork()
    {
        var dispatcher = new BackgroundWorkDispatcher(_catalog);
        await dispatcher.RequestAsync(new(_account, 0, new()), new Unsupported());
        var calls = 0; var disposed = false;
        var runtime = new NativeBackgroundRuntime((_, _) => Task.FromResult<IBackgroundAccountScope?>(new Scope(new("app", "test", "bob"), dispatcher,
            _ => { calls++; return Task.FromResult(BackgroundWorkOutcome.Completed); }, () => disposed = true)), _ => Task.FromResult<IReadOnlyList<string>>([]));
        Assert.Equal(BackgroundWorkOutcome.Failed, await runtime.RunAsync(_account.StorageKey, TimeSpan.FromSeconds(1), default));
        Assert.True(disposed); Assert.Equal(0, calls);
        runtime = new((_, _) => throw new ReplicaKeyUnavailableException(true), _ => Task.FromResult<IReadOnlyList<string>>([]));
        Assert.Equal(BackgroundWorkOutcome.KeyUnavailable, await runtime.RunAsync(_account.StorageKey, TimeSpan.FromSeconds(1), default));
        Assert.True((await dispatcher.GetAsync(_account))!.Pending);
    }

    private sealed class Unsupported : IBackgroundWorkScheduler
    {
        public BackgroundCapabilities Capabilities => new(false, false, false);
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkOutcome.Unsupported);
    }
    private sealed class Scope(AccountScope account, BackgroundWorkDispatcher dispatcher,
        Func<CancellationToken, Task<BackgroundWorkOutcome>> execute, Action dispose) : IBackgroundAccountScope
    {
        public AccountScope Account => account;
        public BackgroundWorkDispatcher Dispatcher => dispatcher;
        public Task<BackgroundWorkOutcome> ExecuteAsync(CancellationToken cancellationToken) => execute(cancellationToken);
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
}
