using Bsync.Client;

namespace Bsync.Samples.NativeBackground;

/// <summary>A fresh account lifetime. The host validates account availability, protected keys and credentials here.</summary>
public interface IBackgroundAccountScope : IAsyncDisposable
{
    AccountScope Account { get; }
    BackgroundWorkDispatcher Dispatcher { get; }
    Task<BackgroundWorkOutcome> ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Initialized from Application.OnCreate/AppDelegate launch on every process start, before scheduler callbacks.
/// Factories must not capture a page, WebView, UI session or access token. Only opaque account storage keys enter OS job data.
/// </summary>
public sealed class NativeBackgroundRuntime(
    Func<string, CancellationToken, Task<IBackgroundAccountScope?>> openAccount,
    Func<CancellationToken, Task<IReadOnlyList<string>>> pendingAccounts)
{
    // A host may replace this only during application bootstrap, never while jobs are running.
    public static NativeBackgroundRuntime? Current { get; set; }

    public async Task<BackgroundWorkOutcome> RunAsync(string storageKey, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        if (storageKey.Length != 64 || storageKey.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            return BackgroundWorkOutcome.Failed;
        using var deadline = new CancellationTokenSource(budget);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var scope = await openAccount(storageKey, stop.Token).ConfigureAwait(false);
            if (scope is null) return BackgroundWorkOutcome.DeferredByHost;
            if (scope.Account.StorageKey != storageKey) return BackgroundWorkOutcome.Failed;
            var remaining = budget - started.Elapsed;
            if (remaining <= TimeSpan.Zero || stop.IsCancellationRequested) return BackgroundWorkOutcome.Canceled;
            return await scope.Dispatcher.RunAsync(scope.Account, remaining, (_, ct) => scope.ExecuteAsync(ct), stop.Token).ConfigureAwait(false);
        }
        catch (ReplicaKeyUnavailableException) { return BackgroundWorkOutcome.KeyUnavailable; }
        catch (OperationCanceledException) { return BackgroundWorkOutcome.Canceled; }
        catch (Exception) { return BackgroundWorkOutcome.Failed; }
    }

    /// <summary>Bounded sequential processing for platforms with an application-wide job identifier and for foreground fallback.</summary>
    public async Task<bool> ReconcileAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(budget);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var accounts = await pendingAccounts(stop.Token).ConfigureAwait(false);
        if (accounts.Count > 100) throw new InvalidOperationException("Background account enumeration exceeds its declared bound.");
        var complete = true;
        foreach (var storageKey in accounts.Distinct(StringComparer.Ordinal))
        {
            var remaining = budget - started.Elapsed;
            if (remaining <= TimeSpan.Zero || stop.IsCancellationRequested) return false;
            var outcome = await RunAsync(storageKey, remaining, stop.Token).ConfigureAwait(false);
            complete &= outcome == BackgroundWorkOutcome.Completed;
        }
        return complete && !stop.IsCancellationRequested;
    }
}
