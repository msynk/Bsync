using Bsync.Client;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;

namespace Bsync.Maui;

/// <summary>
/// Wires a <see cref="SyncSession{TDocument}"/> to a .NET MAUI app (task G1): it syncs when the device regains internet
/// access, pauses when the app's window stops (goes to the background) and resumes, syncing at once, when it comes back.
/// </summary>
public static class MauiSync
{
    /// <summary>
    /// Returns <paramref name="options"/> with <see cref="SyncSessionOptions{TDocument}.AttachLifecycle"/> set to
    /// <see cref="AttachAsync"/>, keeping a lifecycle the options already had (it runs too).
    /// </summary>
    public static SyncSessionOptions<TDocument> UseMauiLifecycle<TDocument>(this SyncSessionOptions<TDocument> options)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(options);
        var previous = options.AttachLifecycle;
        return options with
        {
            AttachLifecycle = async (session, account, cancellationToken) =>
            {
                var maui = await AttachAsync(session, cancellationToken).ConfigureAwait(false);
                var other = previous is null ? null : await previous(session, account, cancellationToken).ConfigureAwait(false);
                return other is null ? maui : new Both(maui, other);
            },
        };
    }

    /// <summary>
    /// Subscribes <paramref name="session"/> to connectivity changes and to the app's windows stopping and resuming.
    /// Disposing the result unsubscribes. Connectivity is a hint only: the session still reconciles on its interval (I13).
    /// </summary>
    public static Task<IAsyncDisposable?> AttachAsync<TDocument>(SyncSession<TDocument> session, CancellationToken cancellationToken = default)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(session);

        void OnConnectivity(object? sender, ConnectivityChangedEventArgs e)
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                session.RequestSync();
            }
        }

        void OnStopped(object? sender, EventArgs e) => session.Pause();

        void OnResumed(object? sender, EventArgs e) => session.Resume();

        Connectivity.Current.ConnectivityChanged += OnConnectivity;
        var windows = Application.Current?.Windows.ToList() ?? [];
        foreach (var window in windows)
        {
            window.Stopped += OnStopped;
            window.Resumed += OnResumed;
        }

        return Task.FromResult<IAsyncDisposable?>(new Subscription(() =>
        {
            Connectivity.Current.ConnectivityChanged -= OnConnectivity;
            foreach (var window in windows)
            {
                window.Stopped -= OnStopped;
                window.Resumed -= OnResumed;
            }
        }));
    }

    /// <summary>
    /// One budgeted sync for a background task (Android <c>WorkManager</c>, iOS <c>BGTaskScheduler</c>): opens the
    /// account's replica if needed and syncs until <paramref name="budget"/> runs out, keeping everything committed so
    /// far. Call it from the platform's background entry point and pass <see cref="SyncResult.HasRemainingWork"/> on to
    /// the scheduler (for example to ask for another run).
    /// </summary>
    public static async Task<SyncResult> RunInBackgroundAsync<TDocument>(SyncSession<TDocument> session, string account, TimeSpan budget, CancellationToken cancellationToken = default)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(session);
        var engine = await session.GetEngineAsync(account, cancellationToken).ConfigureAwait(false);
        return await engine.SyncForAsync(budget, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed class Subscription(Action dispose) : IAsyncDisposable
    {
        private Action? _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Both(IAsyncDisposable? first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (first is not null)
            {
                await first.DisposeAsync().ConfigureAwait(false);
            }

            await second.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Keys kept in the platform's protected store through MAUI <see cref="SecureStorage"/> (Keychain, Keystore, DPAPI),
/// for example the 32-byte key of an encrypted replica (ADR-016), one per account.
/// </summary>
public static class SecureReplicaKeys
{
    private const string Prefix = "bsync.replica-key.";

    /// <summary>Returns the account's key, creating and storing a random 32-byte key on first use.</summary>
    public static async Task<byte[]> GetOrCreateAsync(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (await SecureStorage.Default.GetAsync(Prefix + account).ConfigureAwait(false) is { } stored)
        {
            return Convert.FromBase64String(stored);
        }

        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(Prefix + account, Convert.ToBase64String(key)).ConfigureAwait(false);
        return key;
    }

    /// <summary>Forgets the account's key (a sign-out wipe; an encrypted replica becomes unreadable).</summary>
    public static void Delete(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        SecureStorage.Default.Remove(Prefix + account);
    }
}
