using Bsync.Server.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bsync.Tests.Http;

public sealed class WorkflowWorkerTests
{
    [Fact]
    public async Task ShutdownJoinsAdmittedBatchBeforeDisposingScopeDespiteCanceledStopWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Owned? owned = null; var calls = 0;
        var services = new ServiceCollection().AddLogging().AddScoped<Owned>();
        services.AddBsyncWorkflowWorker("projection", async (sp, ct) =>
        {
            Interlocked.Increment(ref calls); owned = sp.GetRequiredService<Owned>();
            using var registration = ct.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await release.Task;
            Assert.False(owned.Disposed);
        }, TimeSpan.FromMilliseconds(1));
        Assert.Throws<ArgumentException>(() => services.AddBsyncWorkflowWorker("projection", (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1)));
        await using var provider = services.BuildServiceProvider();
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = worker.StopAsync(new CancellationToken(true));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stop.IsCompleted); Assert.False(owned!.Disposed);
        release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(owned.Disposed); Assert.Equal(1, calls);
    }
    private sealed class Owned : IAsyncDisposable
    {
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
