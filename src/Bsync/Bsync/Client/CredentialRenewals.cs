using System.Collections.Concurrent;

namespace Bsync.Client;

/// <summary>Helpers for <see cref="SyncSessionOptions{TDocument}.RenewCredentials"/>.</summary>
public static class CredentialRenewals
{
    /// <summary>
    /// Wraps <paramref name="renew"/> so concurrent calls for the same account share one renewal: several collections (one
    /// session each) that hit <c>unauthorized</c> together renew once, not once per collection. Give the returned delegate
    /// to every session of the account. The first caller's cancellation token applies to the shared renewal.
    /// </summary>
    public static Func<string, CancellationToken, Task<CredentialRenewal>> Coalesce(Func<string, CancellationToken, Task<CredentialRenewal>> renew)
    {
        ArgumentNullException.ThrowIfNull(renew);
        var inFlight = new ConcurrentDictionary<string, Task<CredentialRenewal>>(StringComparer.Ordinal);
        return (account, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(account);
            var started = new TaskCompletionSource<CredentialRenewal>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = inFlight.GetOrAdd(account, started.Task);
            if (shared != started.Task)
            {
                return shared;
            }

            _ = RunAsync();
            return shared;

            async Task RunAsync()
            {
                try
                {
                    started.TrySetResult(await renew(account, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    started.TrySetCanceled(cancellationToken);
                }
                catch (Exception error)
                {
                    started.TrySetException(error);
                }
                finally
                {
                    inFlight.TryRemove(new KeyValuePair<string, Task<CredentialRenewal>>(account, started.Task));
                }
            }
        };
    }
}
