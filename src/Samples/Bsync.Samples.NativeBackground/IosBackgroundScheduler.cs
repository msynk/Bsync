#if IOS
using BackgroundTasks;
using Bsync.Client;
using Foundation;

namespace Bsync.Samples.NativeBackground;

/// <summary>Optional BGProcessingTask recipe. Register during application launch, not when a page becomes visible.</summary>
public sealed class IosBackgroundScheduler(string identifier) : IBackgroundWorkScheduler
{
    // This recipe does not promise per-account charging constraints within its application-wide job.
    public BackgroundCapabilities Capabilities { get; } = new(true, true, false);

    public bool Register() => BGTaskScheduler.Shared.Register(identifier, null, task => _ = RunAsync(task));

    public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Constraints.RequiresCharging) return Task.FromResult(BackgroundWorkOutcome.Unsupported);
        return Task.FromResult(Submit());
    }

    private BackgroundWorkOutcome Submit()
    {
        using var request = new BGProcessingTaskRequest(identifier)
        {
            RequiresNetworkConnectivity = true,
            RequiresExternalPower = false,
            EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(30),
        };
        var accepted = BGTaskScheduler.Shared.Submit(request, out var error);
        error?.Dispose();
        return accepted ? BackgroundWorkOutcome.Scheduled : BackgroundWorkOutcome.DeferredByHost;
    }

    private async Task RunAsync(BGTask task)
    {
        using var expiration = new CancellationTokenSource();
        task.ExpirationHandler = () =>
        {
            try { expiration.Cancel(); } catch (ObjectDisposedException) { }
        };
        var complete = false;
        try
        {
            if (NativeBackgroundRuntime.Current is { } runtime)
                complete = await runtime.ReconcileAsync(TimeSpan.FromSeconds(25), expiration.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* Durable requests stay pending; foreground reconciliation is always available. */ }
        finally
        {
            task.ExpirationHandler = null;
            var succeeded = complete && !expiration.IsCancellationRequested;
            task.SetTaskCompleted(succeeded);
            if (!succeeded)
            {
                try { _ = Submit(); }
                catch (Exception) { /* OS refusal leaves durable work for foreground reconciliation. */ }
            }
        }
    }
}
#endif
