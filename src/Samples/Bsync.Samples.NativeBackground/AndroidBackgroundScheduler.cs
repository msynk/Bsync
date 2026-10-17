#if ANDROID
using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Bsync.Client;
using System.Diagnostics.CodeAnalysis;

namespace Bsync.Samples.NativeBackground;

/// <summary>Optional Android WorkManager recipe. Requests are unique per account; timing and delivery belong to the OS.</summary>
public sealed class AndroidBackgroundScheduler(Context context) : IBackgroundWorkScheduler
{
    public BackgroundCapabilities Capabilities { get; } = new(true, true, true);

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AccountSyncWorker))]
    public async Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType((request.Constraints.RequiresNetwork ? NetworkType.Connected : NetworkType.NotRequired)!)
            .SetRequiresCharging(request.Constraints.RequiresCharging).Build();
        var data = new Data.Builder().PutString("account", request.Account.StorageKey).Build();
        var work = new OneTimeWorkRequest.Builder(typeof(AccountSyncWorker))
            .SetConstraints(constraints).SetInputData(data)
            .SetBackoffCriteria(BackoffPolicy.Exponential!, 30, Java.Util.Concurrent.TimeUnit.Seconds!).Build();
        // Joining the enqueue operation acknowledges scheduling, never execution. KEEP avoids cancellation storms.
        var operation = WorkManager.GetInstance(context.ApplicationContext!).EnqueueUniqueWork(
            "bsync/" + request.Account.StorageKey, ExistingWorkPolicy.Keep!, work);
        await Task.Run(() => operation.Result!.Get(), CancellationToken.None).ConfigureAwait(false);
        return BackgroundWorkOutcome.Scheduled;
    }

    /// <summary>Register in the account deletion callback before deleting its catalog, stores and keys.</summary>
    public async Task CancelAsync(AccountScope account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = WorkManager.GetInstance(context.ApplicationContext!).CancelUniqueWork("bsync/" + account.StorageKey);
        await Task.Run(() => operation.Result!.Get(), CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Stable Java name must remain available across app upgrades while persisted jobs reference it.</summary>
[Register("com.bsync.samples.background.AccountSyncWorker")]
public sealed class AccountSyncWorker : Worker
{
    private readonly CancellationTokenSource _stopped = new();
    public AccountSyncWorker(Context context, WorkerParameters parameters) : base(context, parameters) { }
    public override Result DoWork()
    {
        var runtime = NativeBackgroundRuntime.Current;
        var account = InputData?.GetString("account");
        if (runtime is null || account is null) return Result.InvokeRetry()!;
        var outcome = runtime.RunAsync(account, TimeSpan.FromSeconds(25), _stopped.Token).GetAwaiter().GetResult();
        return (outcome == BackgroundWorkOutcome.Completed ? Result.InvokeSuccess() : Result.InvokeRetry())!;
    }
    public override void OnStopped()
    {
        try { _stopped.Cancel(); } catch (ObjectDisposedException) { }
        base.OnStopped();
    }
    protected override void Dispose(bool disposing) { if (disposing) _stopped.Dispose(); base.Dispose(disposing); }
}
#endif
